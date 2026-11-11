using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // The impact camera's own X-ray: see-through copies of the hit vehicle's meshes, coloured by damage
    // (yellow, orange, red). They live on a layer only the impact camera draws (the main camera never
    // sees them), so the real vehicle is left alone. When the camera wants the X-ray look it stops
    // drawing the real vehicle's layers and draws these instead.
    //
    // It is built a little at a time while the camera flies toward the impact, so there is no hitch, and
    // the rim glow is worked out once, because the camera stays still relative to the vehicle.
    // A part shows damage from the moment the shell's ray reaches it (the sink times), on top of the
    // damage it already had when the camera opened.
    internal sealed class ImpactXRay
    {
        // Layer 3 is unused by the game and not drawn by the main camera.
        public const int GhostLayer = 3;

        private sealed class GhostInfo
        {
            public MeshRenderer Renderer;
            public RimGhost Rim;       // null for flat ghosts
            public int SubMeshes;
        }

        private struct DamageEvent
        {
            public float Time;         // Time.time at which the damage shows
            public float Damage;
        }

        private sealed class Entry
        {
            public PartScript Part;
            public bool IsArmor;
            public float Baseline;     // damage the part had when the camera opened
            public float Health;
            public int Level;
            public bool Built;
            public readonly List<GhostInfo> Ghosts = new List<GhostInfo>();
            public readonly List<DamageEvent> Events = new List<DamageEvent>();
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<PartScript, Entry> byPart = new Dictionary<PartScript, Entry>();
        private readonly List<Entry> withEvents = new List<Entry>();
        private readonly List<GameObject> ghostObjects = new List<GameObject>();
        private readonly List<RimGhost> rims = new List<RimGhost>();

        private int nextBuild;
        private int rimVertices;
        private int rimRound;
        private bool rimIdle;

        // False if no see-through shader exists in this build (then there is no X-ray).
        public bool Available { get; private set; }

        public bool BuildDone
        {
            get { return nextBuild >= entries.Count; }
        }

        // True when the rim glow of every ghost is up to date.
        public bool RimIdle
        {
            get { return rimIdle; }
        }

        public ImpactXRay(AircraftScript aircraft, Vector3 impact)
        {
            Shader shader = RayDrawer.GetShader();
            if (shader == null) return;
            if (XRayView.normalMaterials == null) XRayView.CreateMaterials(shader);
            if (XRayView.whiteMaterials == null) XRayView.CreateWhiteMaterials(shader);

            foreach (PartData data in aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;

                Entry entry = new Entry
                {
                    Part = part,
                    IsArmor = TagCodec.Parse(data.Name).Armor != null,
                    Baseline = part.PartDamage,   // before this impact's damage is applied
                    Health = Mathf.Max(part.MaxHealth, 1f),
                };
                entries.Add(entry);
                byPart[part] = entry;
            }

            // Nearest to the impact first: they get the rim look when the vertex limit runs out.
            entries.Sort((a, b) =>
                (a.Part.transform.position - impact).sqrMagnitude.CompareTo((b.Part.transform.position - impact).sqrMagnitude));

            Available = true;
        }

        // ---- Building ----------------------------------------------------------------

        // Makes ghosts for as many parts as fit in 'budgetMs' milliseconds.
        public void BuildStep(float budgetMs)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            while (nextBuild < entries.Count && timer.Elapsed.TotalMilliseconds < budgetMs)
            {
                Build(entries[nextBuild]);
                nextBuild++;
            }
        }

        private void Build(Entry entry)
        {
            if (entry.Part == null) return;

            float now = Time.time;
            float taken = DamageAt(entry, now);
            float t = Level01(entry, taken);
            int level = XRayView.LevelFor(t, taken);

            foreach (MeshRenderer source in XRayView.GhostSources(entry.Part))
            {
                MeshFilter filter = source.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                entry.Ghosts.Add(MakeGhost(source, filter.sharedMesh, entry, t, level));
            }
            entry.Level = level;
            entry.Built = true;
        }

        private GhostInfo MakeGhost(MeshRenderer source, Mesh mesh, Entry entry, float t, int level)
        {
            GameObject ghost = new GameObject("ArmorXRayGhost");
            ghost.layer = GhostLayer;
            ghost.transform.SetParent(source.transform, false);

            MeshFilter filter = ghost.AddComponent<MeshFilter>();
            MeshRenderer renderer = ghost.AddComponent<MeshRenderer>();
            GhostInfo info = new GhostInfo { Renderer = renderer };
            Material material;

            bool useRim = ArmorFragmentsPlugin.XRayRimLook.Value && mesh.isReadable &&
                          rimVertices + mesh.vertexCount <= ArmorFragmentsPlugin.XRayMaxRimKiloVertices.Value * 1000;
            XRayView.Look look = XRayView.ComputeLook(t, entry.IsArmor);

            if (useRim)
            {
                Mesh copy = Object.Instantiate(mesh);   // its own copy: the vertex colors change
                filter.sharedMesh = copy;
                info.Rim = new RimGhost(ghost.transform, copy, look.Body, look.BodyAlpha, look.Rim, look.RimAlpha);
                rims.Add(info.Rim);
                rimVertices += mesh.vertexCount;
                material = XRayView.whiteMaterials[level];
            }
            else
            {
                filter.sharedMesh = mesh;   // flat color: shares the game's mesh
                material = (entry.IsArmor ? XRayView.armorMaterials : XRayView.normalMaterials)[level];
            }

            info.SubMeshes = Mathf.Max(1, filter.sharedMesh.subMeshCount);
            XRayView.SetMaterial(renderer, info.SubMeshes, material);

            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            ghostObjects.Add(ghost);
            return info;
        }

        // ---- Damage ------------------------------------------------------------------

        // 'sink' holds what each part took in one impact; each part shows it when the shell's ray reaches it.
        // 'baseTime' is the Time.time the impact was worked out at (the sink's times are relative to it).
        public void AddEvents(Dictionary<PartScript, DamageRecord> sink, float baseTime)
        {
            foreach (KeyValuePair<PartScript, DamageRecord> pair in sink)
            {
                Entry entry;
                if (!byPart.TryGetValue(pair.Key, out entry)) continue;   // not a part of this vehicle

                entry.Events.Add(new DamageEvent { Time = baseTime + pair.Value.FirstTime, Damage = pair.Value.Total });
                if (!withEvents.Contains(entry)) withEvents.Add(entry);
            }
        }

        private static float DamageAt(Entry entry, float now)
        {
            float damage = entry.Baseline;
            foreach (DamageEvent e in entry.Events)
                if (e.Time <= now) damage += e.Damage;
            return damage;
        }

        // 0..1: how dark the part is shown ('fully dark at' of its health lost).
        private static float Level01(Entry entry, float taken)
        {
            float fullDark = Mathf.Max(ArmorFragmentsPlugin.XRayDarkPercent.Value / 100f, 0.01f);
            return Mathf.Clamp01(taken / entry.Health / fullDark);
        }

        // ---- Every frame -------------------------------------------------------------

        // Shows damage as it arrives and, once the camera is still ('settled'), brings the rim glow up to date.
        public void Tick(Vector3 cameraWorld, bool settled)
        {
            float now = Time.time;
            foreach (Entry entry in withEvents)
            {
                if (!entry.Built || entry.Part == null) continue;

                float taken = DamageAt(entry, now);
                float t = Level01(entry, taken);
                int level = XRayView.LevelFor(t, taken);
                if (level == entry.Level) continue;

                entry.Level = level;
                ApplyLook(entry, t, level);
            }

            if (settled) UpdateRim(cameraWorld);
        }

        private static void ApplyLook(Entry entry, float t, int level)
        {
            XRayView.Look look = XRayView.ComputeLook(t, entry.IsArmor);
            foreach (GhostInfo ghost in entry.Ghosts)
            {
                if (ghost.Renderer == null) continue;

                Material material = ghost.Rim != null
                    ? XRayView.whiteMaterials[level]
                    : (entry.IsArmor ? XRayView.armorMaterials : XRayView.normalMaterials)[level];
                XRayView.SetMaterial(ghost.Renderer, ghost.SubMeshes, material);
                if (ghost.Rim != null) ghost.Rim.SetLook(look.Body, look.BodyAlpha, look.Rim, look.RimAlpha);
            }
        }

        // The same budgeted rim update as the main X-ray view, but for this camera.
        private void UpdateRim(Vector3 cameraWorld)
        {
            int count = rims.Count;
            if (count == 0)
            {
                rimIdle = true;
                return;
            }

            int budget = ArmorFragmentsPlugin.XRayBudgetKiloVertices.Value * 1000;
            int next = rimRound;
            bool busy = false;

            for (int k = 0; k < count; k++)
            {
                if (budget <= 0)
                {
                    busy = true;   // ran out of budget before looking at every ghost
                    break;
                }

                int index = (rimRound + k) % count;
                RimGhost ghost = rims[index];
                if (ghost.Transform == null) continue;

                if (ghost.NeedsWork(cameraWorld))
                {
                    busy = true;
                    budget -= ghost.Step(budget);
                    next = (index + 1) % count;
                }
            }

            rimRound = next;
            rimIdle = !busy;
        }

        // ---- Cleaning up -------------------------------------------------------------

        public void Dispose()
        {
            foreach (GameObject ghost in ghostObjects)
                if (ghost != null) Object.Destroy(ghost);
            ghostObjects.Clear();

            foreach (RimGhost rim in rims)
                if (rim.Mesh != null) Object.Destroy(rim.Mesh);
            rims.Clear();
        }
    }
}
