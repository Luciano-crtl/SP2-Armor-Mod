using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight.Cameras;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // "X-ray" view: the real parts near the impact are hidden and replaced by see-through
    // copies of their meshes. The more simulated damage a part would take, the darker and
    // more solid its copy is. Purely visual: nothing about the real parts is changed
    // except that their rendering is switched off while the view is on.
    internal static class XRayView
    {
        private const int Levels = 12;

        private static readonly List<Renderer> HiddenRenderers = new List<Renderer>();
        private static readonly List<GameObject> Ghosts = new List<GameObject>();
        private static Material[] normalMaterials;   // one per damage level
        private static Material[] armorMaterials;    // same, for parts tagged [ARM]
        private static Material[] whiteMaterials;    // for rim-lit ghosts: color comes from the mesh
        private static readonly List<RimGhost> RimList = new List<RimGhost>();
        private static float hideAtTime;
        private static int rimRoundRobin;   // where the next frame continues in RimList

        public static bool IsOn
        {
            get { return Ghosts.Count > 0 || HiddenRenderers.Count > 0; }
        }

        // damage: simulated damage per part. center: where the shot hit.
        public static void Show(Vector3 center, Dictionary<PartScript, float> damage)
        {
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
            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            Collider[] colliders = Physics.OverlapSphere(
                center, ArmorFragmentsPlugin.XRayRadius.Value, mask, QueryTriggerInteraction.Ignore);
            foreach (Collider collider in colliders)
            {
                PartScript part = collider.GetComponentInParent<PartScript>();
                if (part != null) parts.Add(part);
            }
            foreach (PartScript part in damage.Keys) parts.Add(part);

            // Most damaged first, then nearest first. When the vertex limit for the rim look is
            // reached, the parts that matter least are the ones that get the cheap flat look.
            List<PartEntry> entries = new List<PartEntry>();
            foreach (PartScript part in parts)
            {
                if (part == null) continue;
                float taken;
                damage.TryGetValue(part, out taken);
                entries.Add(new PartEntry
                {
                    Part = part,
                    Taken = taken,
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
            bool showUndamaged = ArmorFragmentsPlugin.XRayShowUndamaged.Value;
            int rimVertexLimit = ArmorFragmentsPlugin.XRayMaxRimKiloVertices.Value * 1000;
            HashSet<Renderer> alreadyHidden = new HashSet<Renderer>();

            foreach (PartEntry entry in entries)
            {
                PartScript part = entry.Part;
                bool isArmor = TagCodec.Parse(part.Part.Name).Armor != null;

                // Optionally leave undamaged, non-armor parts alone (fewer ghosts to draw).
                if (!showUndamaged && entry.Taken <= 0f && !isArmor) continue;

                // How dark: damage as a fraction of the part's health, scaled so that
                // 'fullDark' of the health lost means fully dark.
                float t = Mathf.Clamp01(entry.Taken / Mathf.Max(part.MaxHealth, 1f) / fullDark);
                int level = Mathf.RoundToInt(t * (Levels - 1));

                // Parts can be drawn as one combined mesh per group: hide that too.
                PartGroupScript group = part.PartGroup;
                if (group != null && group.Renderer != null) HideRenderer(group.Renderer, alreadyHidden);

                foreach (MeshRenderer source in GhostSources(part))
                {
                    MeshFilter filter = source.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;

                    MakeGhost(source, filter.sharedMesh, level, t, isArmor, rimLook, rimVertexLimit);
                    HideRenderer(source, alreadyHidden);
                }
            }

            rimRoundRobin = 0;
            hideAtTime = Time.time + ArmorFragmentsPlugin.XRayDuration.Value;

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
            public float DistanceSqr;
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

            UpdateRimColors();
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
        private static IEnumerable<MeshRenderer> GhostSources(PartScript part)
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
        // t (0..1) is how much simulated damage the part takes: 0 = none, 1 = "fully dark".
        private static void MakeGhost(MeshRenderer source, Mesh mesh, int level, float t, bool isArmor,
                                      bool rimLook, int rimVertexLimit)
        {
            GameObject ghost = new GameObject("ArmorXRayGhost");
            ghost.layer = source.gameObject.layer;
            ghost.transform.SetParent(source.transform, false);

            MeshFilter filter = ghost.AddComponent<MeshFilter>();
            MeshRenderer renderer = ghost.AddComponent<MeshRenderer>();
            Material material;

            // Rim-lit look needs a readable mesh and room left in the vertex limit.
            bool useRim = rimLook && mesh.isReadable &&
                          XRayStats.RimVertices + mesh.vertexCount <= rimVertexLimit;

            if (useRim)
            {
                // Its own copy of the mesh, whose vertex colors are updated so that the edges
                // (seen at a grazing angle) glow and the middle stays transparent.
                // Damaged parts get a dark red body and a bright red rim.
                Mesh copy = Object.Instantiate(mesh);
                filter.sharedMesh = copy;

                Color body = Color.Lerp(isArmor ? new Color(0.55f, 0.85f, 1.00f) : new Color(0.78f, 0.82f, 0.88f),
                                        new Color(0.30f, 0.03f, 0.03f), t);
                float bodyAlpha = Mathf.Lerp(isArmor ? 0.06f : 0.02f, 0.60f, t);
                Color rim = Color.Lerp(new Color(0.90f, 0.95f, 1.00f), new Color(1.00f, 0.12f, 0.05f), t);
                float rimAlpha = Mathf.Lerp(isArmor ? 0.45f : 0.22f, 0.95f, t);

                RimList.Add(new RimGhost(ghost.transform, copy, body, bodyAlpha, rim, rimAlpha));
                material = whiteMaterials[level];

                XRayStats.RimGhosts++;
                XRayStats.RimVertices += mesh.vertexCount;
            }
            else
            {
                // Flat, uniform color: shares the game's mesh, so no CPU work and no memory.
                filter.sharedMesh = mesh;
                material = (isArmor ? armorMaterials : normalMaterials)[level];
                XRayStats.FlatGhosts++;
            }

            Material[] materials = new Material[Mathf.Max(1, filter.sharedMesh.subMeshCount)];
            for (int i = 0; i < materials.Length; i++) materials[i] = material;
            renderer.sharedMaterials = materials;

            // The material is unlit, so everything the renderer would do for lighting is wasted.
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            Ghosts.Add(ghost);
            XRayStats.Ghosts++;
            XRayStats.TotalVertices += mesh.vertexCount;
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
        private static void CreateWhiteMaterials(Shader shader)
        {
            whiteMaterials = new Material[Levels];
            for (int i = 0; i < Levels; i++)
                whiteMaterials[i] = MakeMaterial(shader, Color.white, i);
        }

        private static void CreateMaterials(Shader shader)
        {
            normalMaterials = new Material[Levels];
            armorMaterials = new Material[Levels];

            // From almost invisible (no damage) to dark and nearly solid (a lot of damage).
            Color normalLight = new Color(0.70f, 0.80f, 0.95f, 0.04f);
            Color armorLight = new Color(0.45f, 0.85f, 1.00f, 0.09f);
            Color dark = new Color(0.10f, 0.00f, 0.00f, 0.92f);

            for (int i = 0; i < Levels; i++)
            {
                float t = i / (float)(Levels - 1);
                normalMaterials[i] = MakeMaterial(shader, Color.Lerp(normalLight, dark, t), i);
                armorMaterials[i] = MakeMaterial(shader, Color.Lerp(armorLight, dark, t), i);
            }
        }
    }
}
