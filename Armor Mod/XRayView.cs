using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight.Cameras;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // "X-ray" view: the real parts near the impact are hidden and replaced by see-through
    // copies of their meshes. Every copy starts faint and neutral; the moment the first
    // fragment ray reaches a part, its copy turns dark and red, the more so the more damage it
    // would take. Purely visual: nothing about the real parts is changed except that their
    // rendering is switched off while the view is on.
    internal static class XRayView
    {
        private const int Levels = 12;

        private static readonly List<Renderer> HiddenRenderers = new List<Renderer>();
        private static readonly List<GameObject> Ghosts = new List<GameObject>();
        internal static Material[] normalMaterials;   // one per damage level
        internal static Material[] armorMaterials;    // same, for parts tagged [ARM]
        internal static Material[] whiteMaterials;    // for rim-lit ghosts: color comes from the mesh
        private static readonly List<RimGhost> RimList = new List<RimGhost>();
        private static readonly List<Reveal> Reveals = new List<Reveal>();
        private static readonly List<LiveEntry> LiveList = new List<LiveEntry>();
        private static bool liveMode;
        private static float nextLiveUpdate;
        private const float LiveInterval = 0.5f;   // seconds between looks at the vehicle's damage
        private static float hideAtTime;
        private static int rimRoundRobin;   // where the next frame continues in RimList

        // True while the vehicle view (which stays on until switched off) is showing.
        public static bool IsLive
        {
            get { return liveMode && IsOn; }
        }

        public static bool IsOn
        {
            get { return Ghosts.Count > 0 || HiddenRenderers.Count > 0; }
        }

        // damage: simulated damage per part. center: where the shot hit.
        public static void Show(Vector3 center, Dictionary<PartScript, DamageRecord> damage)
        {
            Show(center, damage, null);
        }

        // The player's own vehicle: every part is shown, coloured by the damage it has really taken, and
        // the colours follow new damage until the view is switched off.
        public static void ShowVehicle(AircraftScript aircraft)
        {
            List<PartScript> all = new List<PartScript>();
            Dictionary<PartScript, DamageRecord> damage = new Dictionary<PartScript, DamageRecord>();
            foreach (PartData data in aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;
                all.Add(part);
                if (part.PartDamage > 0f) damage[part] = new DamageRecord { Total = part.PartDamage };
            }
            if (all.Count == 0) return;

            Show(aircraft.CalculateBounds(false).center, damage, all);
        }

        // vehicleParts: null for a shot (the parts near the impact); a list for the vehicle view.
        private static void Show(Vector3 center, Dictionary<PartScript, DamageRecord> damage,
                                 List<PartScript> vehicleParts)
        {
            bool live = vehicleParts != null;
            Hide();

            Shader shader = RayDrawer.GetShader();
            if (shader == null)
            {
                ArmorFragmentsPlugin.Log.LogWarning("X-ray view unavailable: no usable shader in this build");
                return;
            }
            if (normalMaterials == null) CreateMaterials(shader);
            if (whiteMaterials == null) CreateWhiteMaterials(shader);

            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            XRayStats.ResetView();

            // Every part near the impact (and every part that took simulated damage).
            HashSet<PartScript> parts = new HashSet<PartScript>();
            if (live)
            {
                foreach (PartScript part in vehicleParts) parts.Add(part);
            }
            else
            {
                int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
                Collider[] colliders = Physics.OverlapSphere(
                    center, ArmorFragmentsPlugin.XRayRadius.Value, mask, QueryTriggerInteraction.Ignore);
                foreach (Collider collider in colliders)
                {
                    PartScript part = collider.GetComponentInParent<PartScript>();
                    if (part != null) parts.Add(part);
                }
            }
            foreach (PartScript part in damage.Keys) parts.Add(part);

            // Most damaged first, then nearest first. When the vertex limit for the rim look is
            // reached, the parts that matter least are the ones that get the cheap flat look.
            List<PartEntry> entries = new List<PartEntry>();
            foreach (PartScript part in parts)
            {
                if (part == null) continue;
                DamageRecord record;
                damage.TryGetValue(part, out record);
                entries.Add(new PartEntry
                {
                    Part = part,
                    Taken = record.Total,
                    FirstTime = record.FirstTime,
                    DistanceSqr = (part.transform.position - center).sqrMagnitude,
                });
            }
            entries.Sort((x, y) =>
            {
                int byDamage = y.Taken.CompareTo(x.Taken);
                return byDamage != 0 ? byDamage : x.DistanceSqr.CompareTo(y.DistanceSqr);
            });

            float fullDark = Mathf.Max(ArmorFragmentsPlugin.XRayDarkPercent.Value / 100f, 0.01f);
            bool rimLook = ArmorFragmentsPlugin.XRayRimLook.Value;
            bool showUndamaged = live || ArmorFragmentsPlugin.XRayShowUndamaged.Value;   // the vehicle view needs every part
            int rimVertexLimit = ArmorFragmentsPlugin.XRayMaxRimKiloVertices.Value * 1000;
            HashSet<Renderer> alreadyHidden = new HashSet<Renderer>();
            float showTime = Time.time;
            float lastReveal = 0f;

            foreach (PartEntry entry in entries)
            {
                PartScript part = entry.Part;
                bool isArmor = TagCodec.Parse(part.Part.Name).Armor != null;

                // Optionally leave undamaged, non-armor parts alone (fewer ghosts to draw).
                if (!showUndamaged && entry.Taken <= 0f && !isArmor) continue;

                // How dark it ends up: damage as a fraction of the part's health, scaled so that
                // 'fullDark' of the health lost means fully dark.
                float t = Mathf.Clamp01(entry.Taken / Mathf.Max(part.MaxHealth, 1f) / fullDark);
                int level = LevelFor(t, entry.Taken);

                // The ghost starts neutral. A part that takes damage changes look when the
                // first fragment reaches it.
                Reveal reveal = null;
                if (entry.Taken > 0f)
                {
                    reveal = new Reveal { At = showTime + entry.FirstTime, T = t, Level = level, IsArmor = isArmor };
                    Reveals.Add(reveal);
                    lastReveal = Mathf.Max(lastReveal, entry.FirstTime);
                }

                // Parts can be drawn as one combined mesh per group: hide that too.
                PartGroupScript group = part.PartGroup;
                if (group != null && group.Renderer != null) HideRenderer(group.Renderer, alreadyHidden);

                List<GhostInfo> partGhosts = new List<GhostInfo>();
                foreach (MeshRenderer source in GhostSources(part))
                {
                    MeshFilter filter = source.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;

                    GhostInfo info = MakeGhost(source, filter.sharedMesh, isArmor, rimLook, rimVertexLimit);
                    partGhosts.Add(info);
                    if (reveal != null) reveal.Ghosts.Add(info);
                    HideRenderer(source, alreadyHidden);
                }
                if (live && partGhosts.Count > 0)
                    LiveList.Add(new LiveEntry { Part = part, Ghosts = partGhosts, IsArmor = isArmor, Level = entry.Taken > 0f ? level : 0 });
            }

            rimRoundRobin = 0;
            // The view stays on for its duration counted from the last part being hit.
            liveMode = live;
            nextLiveUpdate = Time.time + LiveInterval;
            hideAtTime = live ? float.MaxValue : showTime + lastReveal + ArmorFragmentsPlugin.XRayDuration.Value;

            XRayStats.BuildMs = (float)timer.Elapsed.TotalMilliseconds;
            ArmorFragmentsPlugin.Log.LogInfo(
                "X-ray view on: " + entries.Count + " parts, " + XRayStats.Ghosts + " ghosts (" +
                XRayStats.RimGhosts + " rim-lit, " + XRayStats.FlatGhosts + " flat), " +
                XRayStats.TotalVertices + " vertices (" + XRayStats.RimVertices + " rim-lit), built in " +
                XRayStats.BuildMs.ToString("F0") + " ms");
        }

        private struct PartEntry
        {
            public PartScript Part;
            public float Taken;
            public float FirstTime;     // seconds until the first fragment touches it
            public float DistanceSqr;
        }

        // A group of ghosts (one part's) waiting to show that the part was hit.
        private sealed class Reveal
        {
            public float At;            // Time.time when the first fragment reaches the part
            public float T;             // final darkness, 0..1
            public int Level;
            public bool IsArmor;
            public readonly List<GhostInfo> Ghosts = new List<GhostInfo>();
        }

        // A part of the vehicle view: its ghosts and the damage level they currently show.
        private sealed class LiveEntry
        {
            public PartScript Part;
            public List<GhostInfo> Ghosts;
            public bool IsArmor;
            public int Level;
        }

        private sealed class GhostInfo
        {
            public MeshRenderer Renderer;
            public RimGhost Rim;        // null for flat ghosts
            public int SubMeshes;
        }

        internal struct Look
        {
            public Color Body;
            public float BodyAlpha;
            public Color Rim;
            public float RimAlpha;
        }

        // The damage scale: lightly damaged = yellow, damaged = orange, badly damaged = red.
        // t: 0..1, where 1 means 'fully dark' (the setting) of the part's health lost.
        private static Color DamageColor(float t)
        {
            Color yellow = new Color(1.00f, 0.88f, 0.10f);
            Color orange = new Color(1.00f, 0.50f, 0.05f);
            Color red = new Color(0.95f, 0.06f, 0.03f);
            t = Mathf.Clamp01(t);
            return t < 0.5f ? Color.Lerp(yellow, orange, t * 2f) : Color.Lerp(orange, red, (t - 0.5f) * 2f);
        }

        // The material level of a part: 0 is the neutral look, so any damage at all gets at least 1.
        internal static int LevelFor(float t, float taken)
        {
            int level = Mathf.RoundToInt(t * (Levels - 1));
            return taken > 0f ? Mathf.Max(1, level) : level;
        }

        // t: 0 = no damage (neutral, bluish), 1 = fully dark. A damaged part is yellow, orange or red.
        internal static Look ComputeLook(float t, bool isArmor)
        {
            Look look = new Look();
            if (t <= 0f)
            {
                look.Body = isArmor ? new Color(0.55f, 0.85f, 1.00f) : new Color(0.78f, 0.82f, 0.88f);
                look.BodyAlpha = isArmor ? 0.06f : 0.02f;
                look.Rim = new Color(0.90f, 0.95f, 1.00f);
                look.RimAlpha = isArmor ? 0.45f : 0.22f;
                return look;
            }

            Color damage = DamageColor(t);
            look.Body = damage * 0.75f;
            look.BodyAlpha = Mathf.Lerp(0.18f, 0.55f, t);
            look.Rim = damage;
            look.RimAlpha = Mathf.Lerp(0.70f, 0.95f, t);
            return look;
        }

        // Switches everything back. Safe to call at any time.
        public static void Hide()
        {
            foreach (Renderer renderer in HiddenRenderers)
                if (renderer != null) renderer.forceRenderingOff = false;
            HiddenRenderers.Clear();

            foreach (GameObject ghost in Ghosts)
                if (ghost != null) Object.Destroy(ghost);
            Ghosts.Clear();

            Reveals.Clear();
            LiveList.Clear();
            liveMode = false;

            // The rim ghosts own copies of meshes: free them.
            foreach (RimGhost rim in RimList)
                if (rim.Mesh != null) Object.Destroy(rim.Mesh);
            RimList.Clear();
        }

        // Called every frame by the plugin: turns the view off when its time is up and
        // spends a limited amount of work on rim colors.
        public static void Tick()
        {
            XRayStats.OnFrame();

            if (!IsOn)
            {
                XRayStats.RimMs = 0f;
                return;
            }
            if (Time.time >= hideAtTime)
            {
                Hide();
                return;
            }

            ApplyDueReveals();
            if (liveMode && Time.time >= nextLiveUpdate)
            {
                nextLiveUpdate = Time.time + LiveInterval;
                UpdateLive();
            }
            UpdateRimColors();
        }

        // Parts whose time has come get their damaged look.
        private static void ApplyDueReveals()
        {
            for (int i = Reveals.Count - 1; i >= 0; i--)
            {
                Reveal reveal = Reveals[i];
                if (Time.time < reveal.At) continue;

                ApplyLook(reveal.Ghosts, reveal.T, reveal.Level, reveal.IsArmor);
                Reveals.RemoveAt(i);
            }
        }

        // Gives a part's ghosts the look of 'level' (t is the matching 0..1 darkness).
        private static void ApplyLook(List<GhostInfo> ghosts, float t, int level, bool isArmor)
        {
            Look look = ComputeLook(t, isArmor);
            foreach (GhostInfo ghost in ghosts)
            {
                if (ghost == null || ghost.Renderer == null) continue;

                Material material = ghost.Rim != null
                    ? whiteMaterials[level]
                    : (isArmor ? armorMaterials : normalMaterials)[level];
                SetMaterial(ghost.Renderer, ghost.SubMeshes, material);
                if (ghost.Rim != null) ghost.Rim.SetLook(look.Body, look.BodyAlpha, look.Rim, look.RimAlpha);
            }
        }

        // Vehicle view: look at each part's real damage and restyle the ones whose level changed.
        // Switches itself off when the vehicle is gone.
        private static void UpdateLive()
        {
            float fullDark = Mathf.Max(ArmorFragmentsPlugin.XRayDarkPercent.Value / 100f, 0.01f);
            int alive = 0;
            foreach (LiveEntry entry in LiveList)
            {
                if (entry.Part == null) continue;
                alive++;

                float taken = entry.Part.PartDamage;
                float t = Mathf.Clamp01(taken / Mathf.Max(entry.Part.MaxHealth, 1f) / fullDark);
                int level = LevelFor(t, taken);
                if (level == entry.Level) continue;

                entry.Level = level;
                ApplyLook(entry.Ghosts, t, level, entry.IsArmor);
            }
            if (alive == 0) Hide();
        }

        internal static void SetMaterial(MeshRenderer renderer, int subMeshes, Material material)
        {
            Material[] materials = new Material[subMeshes];
            for (int i = 0; i < materials.Length; i++) materials[i] = material;
            renderer.sharedMaterials = materials;
        }

        // Gives this frame's vertex budget to the ghosts whose camera view has changed.
        // Ghosts take turns, so one big mesh cannot starve the others.
        private static void UpdateRimColors()
        {
            int ghostCount = RimList.Count;
            if (ghostCount == 0) return;

            CameraManagerScript manager = CameraManagerScript.Instance;
            Camera camera = (manager != null && manager.MainCamera != null) ? manager.MainCamera : Camera.main;
            if (camera == null) return;
            Vector3 cameraPosition = camera.transform.position;

            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            int budget = ArmorFragmentsPlugin.XRayBudgetKiloVertices.Value * 1000;
            int next = rimRoundRobin;

            for (int k = 0; k < ghostCount && budget > 0; k++)
            {
                int index = (rimRoundRobin + k) % ghostCount;
                RimGhost ghost = RimList[index];
                if (ghost.Transform == null) continue;   // destroyed with its part

                if (ghost.NeedsWork(cameraPosition))
                {
                    budget -= ghost.Step(budget);
                    next = (index + 1) % ghostCount;
                }
            }

            rimRoundRobin = next;
            XRayStats.OnRimCost((float)timer.Elapsed.TotalMilliseconds);
        }

        // The renderers that make up a part's visible mesh.
        internal static IEnumerable<MeshRenderer> GhostSources(PartScript part)
        {
            HashSet<MeshRenderer> seen = new HashSet<MeshRenderer>();

            // The game's own list of the part's renderers (includes ones that are switched off
            // because the part is drawn as part of a combined mesh).
            if (part.PartMaterialScript != null)
            {
                foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
                    if (map.Renderer != null && seen.Add(map.Renderer)) yield return map.Renderer;
            }

            // Any other mesh that is currently visible (propeller blades, for example).
            foreach (MeshRenderer renderer in part.GetComponentsInChildren<MeshRenderer>())
            {
                if (renderer.name == "ArmorXRayGhost") continue;
                if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                if (renderer.enabled && seen.Add(renderer)) yield return renderer;
            }
        }

        // A see-through copy of 'mesh', attached to the same transform as the real renderer.
        // It starts with the neutral (undamaged) look.
        private static GhostInfo MakeGhost(MeshRenderer source, Mesh mesh, bool isArmor,
                                           bool rimLook, int rimVertexLimit)
        {
            GameObject ghost = new GameObject("ArmorXRayGhost");
            ghost.layer = source.gameObject.layer;
            ghost.transform.SetParent(source.transform, false);

            MeshFilter filter = ghost.AddComponent<MeshFilter>();
            MeshRenderer renderer = ghost.AddComponent<MeshRenderer>();
            GhostInfo info = new GhostInfo { Renderer = renderer };
            Material material;

            // Rim-lit look needs a readable mesh and room left in the vertex limit.
            bool useRim = rimLook && mesh.isReadable &&
                          XRayStats.RimVertices + mesh.vertexCount <= rimVertexLimit;

            if (useRim)
            {
                // Its own copy of the mesh, whose vertex colors are updated so that the edges
                // (seen at a grazing angle) glow and the middle stays transparent.
                Mesh copy = Object.Instantiate(mesh);
                filter.sharedMesh = copy;

                Look look = ComputeLook(0f, isArmor);
                info.Rim = new RimGhost(ghost.transform, copy, look.Body, look.BodyAlpha, look.Rim, look.RimAlpha);
                RimList.Add(info.Rim);
                material = whiteMaterials[0];

                XRayStats.RimGhosts++;
                XRayStats.RimVertices += mesh.vertexCount;
            }
            else
            {
                // Flat, uniform color: shares the game's mesh, so no CPU work and no memory.
                filter.sharedMesh = mesh;
                material = (isArmor ? armorMaterials : normalMaterials)[0];
                XRayStats.FlatGhosts++;
            }

            info.SubMeshes = Mathf.Max(1, filter.sharedMesh.subMeshCount);
            SetMaterial(renderer, info.SubMeshes, material);

            // The material is unlit, so everything the renderer would do for lighting is wasted.
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            Ghosts.Add(ghost);
            XRayStats.Ghosts++;
            XRayStats.TotalVertices += mesh.vertexCount;
            return info;
        }

        // forceRenderingOff does not touch 'enabled', so the game's own show/hide logic
        // keeps working and nothing is lost when we switch it back.
        private static void HideRenderer(Renderer renderer, HashSet<Renderer> alreadyHidden)
        {
            if (renderer == null || !alreadyHidden.Add(renderer)) return;
            if (renderer.forceRenderingOff) return;   // someone else hid it: leave it alone

            renderer.forceRenderingOff = true;
            HiddenRenderers.Add(renderer);
        }

        // Transparent things are drawn in render queue order, later = on top. Giving
        // more damaged levels a later queue draws them over the fainter parts.
        private static Material MakeMaterial(Shader shader, Color color, int level)
        {
            Material material = new Material(shader) { color = color };
            material.renderQueue = 3000 + level;
            return material;
        }

        // White materials: the visible color comes from each ghost's vertex colors.
        internal static void CreateWhiteMaterials(Shader shader)
        {
            whiteMaterials = new Material[Levels];
            for (int i = 0; i < Levels; i++)
                whiteMaterials[i] = MakeMaterial(shader, Color.white, i);
        }

        internal static void CreateMaterials(Shader shader)
        {
            normalMaterials = new Material[Levels];
            armorMaterials = new Material[Levels];

            // Level 0: almost invisible (no damage). Levels 1..: yellow, orange, red, more solid as the damage grows.
            Color normalLight = new Color(0.70f, 0.80f, 0.95f, 0.04f);
            Color armorLight = new Color(0.45f, 0.85f, 1.00f, 0.09f);

            for (int i = 0; i < Levels; i++)
            {
                if (i == 0)
                {
                    normalMaterials[i] = MakeMaterial(shader, normalLight, i);
                    armorMaterials[i] = MakeMaterial(shader, armorLight, i);
                    continue;
                }
                float t = i / (float)(Levels - 1);
                Color damage = DamageColor(t);
                damage.a = Mathf.Lerp(0.30f, 0.85f, t);
                normalMaterials[i] = MakeMaterial(shader, damage, i);
                armorMaterials[i] = MakeMaterial(shader, damage, i);
            }
        }
    }
}
