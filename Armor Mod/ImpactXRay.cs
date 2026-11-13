using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // The impact camera's own X-ray: see-through copies of the hit vehicle's meshes, coloured by damage
    // (yellow, orange, red; almost black red for a destroyed part). They live on a layer only the impact
    // camera draws (the main camera never sees them), so the real vehicle is left alone. When the camera
    // wants the X-ray look it stops drawing the real vehicle's layers and draws these instead.
    //
    // While a part is alive its copies are children of its meshes, so they follow it (wheels turn, turrets
    // swing: the picture stays true to the vehicle). When a part is destroyed the game switches off its
    // whole body, and anything attached would vanish just when it matters; so the moment before the game
    // does that (it announces it: see PartDisconnectPatch) its copies are moved to a holder that follows the
    // vehicle (its main cockpit), exactly where they were. They stay there, shown as destroyed.
    // A part destroyed before this camera opened is put where it was the last time it was seen alive.
    //
    // It is built a little at a time while the camera flies toward the impact, so there is no hitch, and
    // the rim glow is worked out once, because the camera stays still relative to the vehicle.
    // A part shows damage from the moment the shell's ray reaches it (the sink times), on top of the
    // damage it already had when the camera opened.
    internal sealed class ImpactXRay
    {
        // The layer the ghosts are on (a setting): one only the impact camera is to draw. The main camera is
        // told to leave it out while the impact camera is open (see ImpactCamera).
        public int GhostLayer { get; private set; }

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
            public bool WasDestroyed;       // already switched off (destroyed earlier) when the camera opened
            public float Baseline;          // damage the part had when the camera opened
            public float Health;
            public bool Frozen;             // its copies hang from the holder (the part is destroyed), not from its meshes
            public Matrix4x4 ToHolder;      // the part's place in the holder's space (only when Frozen)
            public int Level;
            public bool Built;
            public readonly List<GhostInfo> Ghosts = new List<GhostInfo>();
            public readonly List<DamageEvent> Events = new List<DamageEvent>();
        }

        // Where each part was, in the vehicle's frame, the last time it was seen alive: a part that was
        // destroyed before this camera opened is shown there.
        private static readonly Dictionary<PartScript, Matrix4x4> Remembered = new Dictionary<PartScript, Matrix4x4>();

        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<PartScript, Entry> byPart = new Dictionary<PartScript, Entry>();
        private readonly List<Entry> withEvents = new List<Entry>();
        private readonly List<GameObject> ghostObjects = new List<GameObject>();
        private readonly List<RimGhost> rims = new List<RimGhost>();

        private Transform reference;   // what the holder follows (the vehicle's main cockpit)
        private GameObject holder;

        private int nextBuild;
        private int partsBuilt;
        private int rimVertices;
        private int rimRound;
        private bool rimIdle;
        private bool buildErrorLogged;

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

        // fallbackReference: what to follow if the vehicle has no main cockpit (the part that was hit).
        public ImpactXRay(AircraftScript aircraft, Vector3 impact, Transform fallbackReference)
        {
            GhostLayer = Mathf.Clamp(ArmorFragmentsPlugin.ImpactCameraGhostLayer.Value, 0, 31);

            Shader shader = RayDrawer.GetShader();
            if (shader == null) return;
            if (XRayView.normalMaterials == null) XRayView.CreateMaterials(shader);
            if (XRayView.whiteMaterials == null) XRayView.CreateWhiteMaterials(shader);

            Transform cockpit = aircraft.MainCockpit != null ? aircraft.MainCockpit.transform : null;
            reference = cockpit != null ? cockpit : fallbackReference;
            if (reference == null) return;

            holder = new GameObject("ArmorImpactXRay");
            holder.transform.SetPositionAndRotation(reference.position, reference.rotation);

            ForgetGoneParts();

            foreach (PartData data in aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;

                // Alive: its place now (and remembered). Switched off (destroyed): where it was last seen.
                bool alive = part.gameObject.activeInHierarchy;
                Matrix4x4 toHolder;
                if (alive)
                {
                    toHolder = holder.transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
                    Remembered[part] = toHolder;
                }
                else if (!Remembered.TryGetValue(part, out toHolder))
                {
                    continue;   // never seen alive: nothing says where it was
                }

                Entry entry = new Entry
                {
                    Part = part,
                    IsArmor = TagCodec.Parse(data.Name).Armor != null,
                    WasDestroyed = !alive,
                    Frozen = !alive,
                    Baseline = part.PartDamage,   // before this impact's damage is applied
                    Health = Mathf.Max(part.MaxHealth, 1f),
                    ToHolder = toHolder,
                };
                entries.Add(entry);
                byPart[part] = entry;
            }

            // Nearest to the impact first: they get the rim look when the vertex limit runs out.
            entries.Sort((a, b) =>
                (a.Part.transform.position - impact).sqrMagnitude.CompareTo((b.Part.transform.position - impact).sqrMagnitude));

            Available = true;
        }

        // Parts that no longer exist at all are dropped from the memory.
        private static void ForgetGoneParts()
        {
            if (Remembered.Count < 2000) return;

            List<PartScript> gone = new List<PartScript>();
            foreach (PartScript part in Remembered.Keys)
                if (part == null) gone.Add(part);
            foreach (PartScript part in gone) Remembered.Remove(part);

            if (Remembered.Count > 8000) Remembered.Clear();
        }

        // ---- Building ----------------------------------------------------------------

        // Makes ghosts for as many parts as fit in 'budgetMs' milliseconds.
        public void BuildStep(float budgetMs)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            while (nextBuild < entries.Count && timer.Elapsed.TotalMilliseconds < budgetMs)
            {
                Entry entry = entries[nextBuild];
                nextBuild++;   // first: a part that fails must not stop the others
                try
                {
                    Build(entry);
                }
                catch (System.Exception e)
                {
                    if (!buildErrorLogged)
                    {
                        buildErrorLogged = true;
                        ArmorFragmentsPlugin.Log.LogWarning("Impact X-ray: a part could not be built (shown once): " + e);
                    }
                }
            }
        }

        private void Build(Entry entry)
        {
            if (entry.Part == null) return;

            float taken = DamageAt(entry, Time.time);
            float t;
            int level = LevelOf(entry, taken, out t);

            // The meshes come from the game's own list for the part; this works even if the part has been
            // switched off, because it only reads data and positions.
            foreach (MeshRenderer source in XRayView.GhostSources(entry.Part))
            {
                MeshFilter filter = source.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;

                if (entry.Frozen)
                {
                    // Destroyed: in the holder, where the part was.
                    Matrix4x4 sourceToPart = entry.Part.transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
                    entry.Ghosts.Add(MakeGhost(filter.sharedMesh, entry, holder.transform, entry.ToHolder * sourceToPart, t, level));
                }
                else
                {
                    // Alive: a child of the mesh, so it goes where the part goes.
                    entry.Ghosts.Add(MakeGhost(filter.sharedMesh, entry, source.transform, null, t, level));
                }
            }
            entry.Level = level;
            entry.Built = true;
            partsBuilt++;
        }

        private GhostInfo MakeGhost(Mesh mesh, Entry entry, Transform parent, Matrix4x4? place, float t, int level)
        {
            GameObject ghost = new GameObject("ArmorXRayGhost");
            ghost.layer = GhostLayer;
            ghost.transform.SetParent(parent, false);
            if (place.HasValue) Place(ghost.transform, place.Value);

            MeshFilter filter = ghost.AddComponent<MeshFilter>();
            MeshRenderer renderer = ghost.AddComponent<MeshRenderer>();
            GhostInfo info = new GhostInfo { Renderer = renderer };
            Material material;

            bool useRim = ArmorFragmentsPlugin.XRayRimLook.Value && mesh.isReadable &&
                          rimVertices + mesh.vertexCount <= ArmorFragmentsPlugin.XRayMaxRimKiloVertices.Value * 1000;
            XRayView.Look look = XRayView.ComputeLook(t, entry.IsArmor, level == XRayView.DestroyedLevel);

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

        // Puts a transform at the place (position, rotation and scale, mirrored or not) a matrix describes.
        private static void Place(Transform target, Matrix4x4 m)
        {
            Vector3 c0 = m.GetColumn(0);
            Vector3 c1 = m.GetColumn(1);
            Vector3 c2 = m.GetColumn(2);

            float sy = Mathf.Max(c1.magnitude, 0.000001f);
            float sz = Mathf.Max(c2.magnitude, 0.000001f);
            Vector3 up = c1 / sy;
            Vector3 forward = c2 / sz;
            Vector3 right = Vector3.Cross(up, forward);   // the third axis of a right-handed rotation
            float sx = Vector3.Dot(c0, right);            // negative if the part is mirrored

            target.localPosition = m.GetColumn(3);
            target.localRotation = Quaternion.LookRotation(forward, up);
            target.localScale = new Vector3(sx, sy, sz);
        }

        // ---- Destroyed parts ---------------------------------------------------------

        // The game is about to switch this part's body off (it is destroyed): its copies stop following it and
        // stay where they are, in the holder. Called BEFORE the game does it, so the part still has its place.
        public void Freeze(PartScript part)
        {
            Entry entry;
            if (holder == null || !byPart.TryGetValue(part, out entry) || entry.Frozen) return;

            // The holder is brought up to date first, so the place stored is exact.
            if (reference != null) holder.transform.SetPositionAndRotation(reference.position, reference.rotation);

            entry.Frozen = true;
            entry.ToHolder = holder.transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
            Remembered[part] = entry.ToHolder;

            // Copies already made move from the part's meshes to the holder, keeping their world place.
            foreach (GhostInfo ghost in entry.Ghosts)
            {
                if (ghost.Renderer == null) continue;
                Transform t = ghost.Renderer.transform;
                Matrix4x4 world = t.localToWorldMatrix;
                t.SetParent(holder.transform, false);
                Place(t, holder.transform.worldToLocalMatrix * world);
            }
        }

        // Safety net: a part whose body was switched off without being announced (its copies are already
        // hidden with it) is frozen where it stands. Slightly late, so slightly less exact.
        public void CatchSwitchedOff()
        {
            foreach (Entry entry in entries)
            {
                if (entry.Frozen || !entry.Built || entry.Part == null) continue;
                if (!entry.Part.gameObject.activeInHierarchy) Freeze(entry.Part);
            }
        }

        // A line for the log: what was built, and the state of a few ghosts (to find out why they would not show).
        public string Describe()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            int destroyed = 0;
            foreach (Entry entry in entries) if (entry.WasDestroyed) destroyed++;

            text.Append(partsBuilt).Append(" of ").Append(entries.Count).Append(" parts (")
                .Append(destroyed).Append(" already destroyed), ")
                .Append(ghostObjects.Count).Append(" ghosts (").Append(rims.Count).Append(" rim-lit, ")
                .Append(rimVertices).Append(" rim vertices), on layer ").Append(GhostLayer);

            int shown = 0;
            foreach (GameObject ghost in ghostObjects)
            {
                if (ghost == null || shown >= 3) continue;
                Renderer renderer = ghost.GetComponent<Renderer>();
                text.Append("; sample: layer ").Append(ghost.layer)
                    .Append(", active ").Append(ghost.activeInHierarchy)
                    .Append(", enabled ").Append(renderer != null && renderer.enabled)
                    .Append(", queue ").Append(renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.renderQueue : -1)
                    .Append(", bounds ").Append(renderer != null ? renderer.bounds.size.magnitude.ToString("F1") : "?");
                shown++;
            }
            return text.ToString();
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

        // The look level of a part with this much damage: 0..1 darkness ('fully dark at' of its health lost),
        // and the special level for a destroyed part (100% or more of its health lost, or already switched off).
        private static int LevelOf(Entry entry, float taken, out float t)
        {
            float fullDark = Mathf.Max(ArmorFragmentsPlugin.XRayDarkPercent.Value / 100f, 0.01f);
            t = Mathf.Clamp01(taken / entry.Health / fullDark);
            bool destroyed = entry.WasDestroyed || taken >= entry.Health;
            return XRayView.LevelFor(t, taken, destroyed);
        }

        // ---- Every frame -------------------------------------------------------------

        // Follows the vehicle, shows damage as it arrives and, once the camera is still ('settled'),
        // brings the rim glow up to date.
        public void Tick(Vector3 cameraWorld, bool settled)
        {
            if (reference != null && holder != null)
                holder.transform.SetPositionAndRotation(reference.position, reference.rotation);

            if (Time.frameCount % 8 == 0) CatchSwitchedOff();

            float now = Time.time;
            foreach (Entry entry in withEvents)
            {
                if (!entry.Built) continue;

                float taken = DamageAt(entry, now);
                float t;
                int level = LevelOf(entry, taken, out t);
                if (level == entry.Level) continue;

                entry.Level = level;
                ApplyLook(entry, t, level);
            }

            if (settled) UpdateRim(cameraWorld);
        }

        private static void ApplyLook(Entry entry, float t, int level)
        {
            XRayView.Look look = XRayView.ComputeLook(t, entry.IsArmor, level == XRayView.DestroyedLevel);
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

                if (ghost.NeedsWork(cameraWorld, false))
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
            // The holder owns every ghost.
            if (holder != null) Object.Destroy(holder);
            holder = null;
            ghostObjects.Clear();

            foreach (RimGhost rim in rims)
                if (rim.Mesh != null) Object.Destroy(rim.Mesh);
            rims.Clear();
        }
    }

    // The game switches off the whole body of a destroyed part inside BodyScript.DisconnectParts. Ahead of it,
    // each part about to go is announced to the impact camera, so its X-ray copies can be frozen in place.
    // (The list is of a type private to the game, so it is read as plain objects.)
    [HarmonyLib.HarmonyPatch(typeof(BodyScript), "DisconnectParts")]
    internal static class PartDisconnectPatch
    {
        private static System.Reflection.PropertyInfo partProperty;

        private static void Prefix(object __0)
        {
            if (!ImpactCamera.IsActive) return;

            try
            {
                System.Collections.IEnumerable items = __0 as System.Collections.IEnumerable;
                if (items == null) return;

                foreach (object item in items)
                {
                    if (item == null) continue;
                    if (partProperty == null || partProperty.DeclaringType != item.GetType())
                        partProperty = item.GetType().GetProperty("Part");
                    PartScript part = partProperty != null ? partProperty.GetValue(item, null) as PartScript : null;
                    if (part != null) ImpactCamera.OnPartsDisconnecting(part);
                }
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Could not follow a part being destroyed: " + e.Message);
            }
        }
    }
}
