using System;
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight.Cameras;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ArmorFragments
{
    // What the damage maps know about one plate's mesh.
    internal sealed class PlateInfo
    {
        public PartScript Part;
        public PlateGeometry Geometry;
        public PartMaterialScript.RendererMaterialMap Map;
        public Mesh Mesh;
        public Matrix4x4 ToPart;    // the mesh's vertices to the part's local frame
        public float ThicknessM;    // the plate's thickness from its tag, meters

        // A block thick enough also has marks on its sides: they are plates of their own (PlateGeometry.Side), for the two pairs of faces
        // across its length and its width. Null for a thin slab (a side of a few centimeters is not worth a map).
        public const float MinSideDepth = 0.05f;
        public readonly PlateGeometry[] Sides = new PlateGeometry[2];
        private readonly PlateInfo[] sideInfos = new PlateInfo[2];

        public PlateGeometry GeometryOf(int axis)
        {
            return axis == 0 || Sides[axis - 1] == null ? Geometry : Sides[axis - 1];
        }

        // The same plate seen through the geometry of one of its sides (what that side's tile and marks are made from).
        public PlateInfo ForAxis(int axis)
        {
            if (axis == 0) return this;
            PlateInfo side = sideInfos[axis - 1];
            if (side == null && Sides[axis - 1] != null)
                sideInfos[axis - 1] = side = new PlateInfo { Part = Part, Geometry = Sides[axis - 1], Map = Map, Mesh = Mesh, ToPart = ToPart, ThicknessM = ThicknessM };
            return side;
        }
    }

    // DAMAGE MAPS. Every shot that resolves on an armor plate leaves a mark on it (a dent, a hole, a groove, pits), and
    // the mark counts: a hole lets shells and fragments through, a dent makes the plate thinner there.
    //
    // Two layers that never wait for each other:
    //  1. The MODEL (here, instant): a short list of holes and dents per plate, in plate coordinates. The energy model
    //     asks it ("is this point a hole?", "how thin is the plate here?") and nothing else decides the game.
    //  2. The LOOK (VehicleMaps, DamageWorker): the same impacts, drawn into the plate's normal and hole maps by a worker
    //     thread, and shown a small rectangle at a time when they are ready (and the shell's ray has arrived).
    internal static class PlateDamageMaps
    {
        private const string Tag = "[DMAP] ";
        private const int MaxRecords = 64;      // holes and dents kept per plate (the oldest go)
        private const int MaxPitsPerCloud = 96; // fragment marks drawn per plate for one burst

        private struct Hole { public int Axis; public bool Front; public float U, V, R; }
        private struct Dent { public int Axis; public float U, V, R, D; }

        private sealed class PlateState
        {
            public PlateInfo Info;
            public readonly List<Hole> Holes = new List<Hole>();
            public readonly List<Dent> Dents = new List<Dent>();
        }

        private static readonly Dictionary<PartScript, PlateInfo> Infos = new Dictionary<PartScript, PlateInfo>();
        private static readonly HashSet<PartScript> NoInfo = new HashSet<PartScript>();
        private static readonly Dictionary<PartScript, PlateState> States = new Dictionary<PartScript, PlateState>();
        private static readonly Dictionary<AircraftScript, VehicleMaps> Vehicles = new Dictionary<AircraftScript, VehicleMaps>();
        private static readonly HashSet<AircraftScript> Refused = new HashSet<AircraftScript>();
        private static readonly List<Delivery> Holding = new List<Delivery>();
        private static readonly List<PlateVisual> LitWanted = new List<PlateVisual>();   // blocks that are to be drawn with the Lit shader once all their rectangles are in
        private static int counter;

        public static void WantLit(PlateVisual block)
        {
            if (block != null && !block.HasHole && !LitWanted.Contains(block)) LitWanted.Add(block);
        }

        // The family of jobs the worker still has for a block (its own and its sides').
        private static bool WorkerBusy(PlateVisual block)
        {
            int pending = System.Threading.Volatile.Read(ref block.PendingJobs);
            foreach (PlateVisual side in block.Sides) if (side != null) pending += System.Threading.Volatile.Read(ref side.PendingJobs);
            return pending > 0;
        }

        private static bool HoldingFor(PlateVisual block)
        {
            foreach (Delivery d in Holding)
                if (d.Plate != null && (d.Plate == block || d.Plate.Main == block)) return true;
            return false;
        }
        private static int frame;
        private static double lastDeliverMs;

        public static bool Visuals
        {
            get { return ArmorFragmentsPlugin.DamageMapsEnabled.Value; }
        }

        // A mark goes on the two big faces of a plate (the map has a tile for each): a hit whose surface normal is
        // less aligned than this with the plate's thickness axis (the side of a block, an edge) leaves no mark.
        private const float BigFaceCos = 0.5f;

        public static bool Weakens
        {
            get { return ArmorFragmentsPlugin.DamageMapsWeakenArmor.Value; }
        }

        // What the panel shows.
        public static string Message = "";

        public static string SimStatus
        {
            get
            {
                if (ArmorFragmentsPlugin.SimQuality.Value == 0) return "off (the quick marks)";
                string last = DamageWorker.LastSimQuality < 0
                    ? "no simulation yet"
                    : "last " + DamageWorker.LastSimMs + " ms, " + ArmorFragments.Sim.ImpactSim.QualityName(DamageWorker.LastSimQuality) +
                      ", " + (DamageWorker.LastSimGpu ? "graphics card" : DamageWorker.LastSimThreads + " thread(s)");
                return last + "; " + DamageWorker.SimsDone + " done, " + DamageWorker.Fallbacks + " fell back, waiting " +
                       DamageWorker.PendingSims + ", best quality now " + ArmorFragments.Sim.ImpactSim.QualityName(DamageWorker.TunedQuality) +
                       ", threads in use up to " + DamageWorker.WorkersNow;
            }
        }

        public static string Status
        {
            get
            {
                int plates = 0;
                foreach (VehicleMaps v in Vehicles.Values) plates += v.Plates.Count;
                return Vehicles.Count + " vehicle(s), " + plates + " plate(s), " + States.Count + " damaged, queue " +
                       (DamageWorker.Pending + Holding.Count) + ", last job " + (DamageWorker.LastMs10 / 10f).ToString("F1") +
                       " ms (worker), " + lastDeliverMs.ToString("F2") + " ms (main thread)";
            }
        }

        // ---- The plate's mesh -------------------------------------------------------------------

        public static PlateInfo InfoOf(PartScript part)
        {
            PlateInfo info;
            if (Infos.TryGetValue(part, out info)) return info;
            if (NoInfo.Contains(part)) return null;

            info = Build(part);
            if (info == null) NoInfo.Add(part);
            else Infos[part] = info;
            return info;
        }

        private static PlateInfo Build(PartScript part)
        {
            PartMaterialScript materials = part.PartMaterialScript;
            if (materials == null) return null;

            foreach (PartMaterialScript.RendererMaterialMap map in materials.RendererMaps)
            {
                Renderer renderer = map.Renderer;
                if (renderer == null || !map.MeshIsUnique) continue;   // a shared mesh is never touched
                Mesh mesh = map.Mesh;
                if (mesh == null || !mesh.isReadable || mesh.vertexCount < 3) continue;

                Matrix4x4 toPart = part.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                PlateGeometry geometry = PlateGeometry.Build(mesh, toPart);
                if (geometry == null) continue;

                ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;
                if (geometry.Curved)
                    ArmorFragmentsPlugin.Log.LogInfo(Tag + "plate '" + part.Part.Name + "' is not a slab (depth " + geometry.Depth.ToString("F2") +
                        " m of " + geometry.SizeU.ToString("F2") + " x " + geometry.SizeV.ToString("F2") + "): its maps are flat approximations");
                PlateInfo made = new PlateInfo
                {
                    Part = part, Geometry = geometry, Map = map, Mesh = mesh, ToPart = toPart,
                    ThicknessM = (armor != null ? armor.Thickness : 20f) * 0.001f,
                };
                if (geometry.Depth >= PlateInfo.MinSideDepth)
                {
                    made.Sides[0] = PlateGeometry.Side(geometry, 1);
                    made.Sides[1] = PlateGeometry.Side(geometry, 2);
                }
                return made;
            }
            return null;
        }

        // ---- What the model asks ------------------------------------------------------------------

        // Is this point of the plate a hole? 'worldNormal' is the surface normal on the side that was hit.
        public static bool InHole(PartScript part, Vector3 worldPoint, Vector3 worldNormal)
        {
            if (States.Count == 0 || !Weakens) return false;
            PlateState state;
            if (!States.TryGetValue(part, out state) || state.Holes.Count == 0) return false;

            int axis;
            float facing;
            if (!ChooseAxis(state.Info, part.transform.InverseTransformDirection(worldNormal).normalized, out axis, out facing)) return false;   // an edge: no map there
            PlateGeometry g = state.Info.GeometryOf(axis);
            bool front = facing > 0f;
            Vector2 at = g.ToPlane(part.transform.InverseTransformPoint(worldPoint));
            foreach (Hole hole in state.Holes)
            {
                if (hole.Axis != axis || hole.Front != front) continue;
                float du = at.x - hole.U, dv = at.y - hole.V;
                if (du * du + dv * dv < hole.R * hole.R) return true;
            }
            return false;
        }

        // Which of the block's faces a surface normal (in the part's frame) looks at: 0 = the plate's own two big faces, 1 and 2 = the sides of a
        // thick block. False if it lines up with none of them well enough (an edge or a corner, which has no map).
        private static bool ChooseAxis(PlateInfo info, Vector3 normalLocal, out int axis, out float facing)
        {
            axis = 0;
            facing = Vector3.Dot(info.Geometry.N, normalLocal);
            float best = Mathf.Abs(facing);
            for (int a = 1; a <= 2; a++)
            {
                PlateGeometry side = info.Sides[a - 1];
                if (side == null) continue;
                float f = Vector3.Dot(side.N, normalLocal);
                if (Mathf.Abs(f) > best + 1e-4f) { best = Mathf.Abs(f); axis = a; facing = f; }
            }
            return best >= BigFaceCos;
        }

        // The plate as it is at this point: thinner where it is dented. Returns the same tag if nothing changed.
        public static ArmorTag Weakened(PartScript part, ArmorTag tag, Vector3 worldPoint)
        {
            float factor = ThicknessFactor(part, tag.Thickness, worldPoint);
            if (factor > 0.999f) return tag;
            return new ArmorTag { Thickness = tag.Thickness * factor, Hardness = tag.Hardness, Brittleness = tag.Brittleness };
        }

        // The same for a plain thickness (a face of a per-face block): the thickness left, in mm.
        public static float WeakenedMm(PartScript part, float thicknessMm, Vector3 worldPoint)
        {
            return thicknessMm * ThicknessFactor(part, thicknessMm, worldPoint);
        }

        // 1 = untouched, down to 0.4 where the plate is dented.
        private static float ThicknessFactor(PartScript part, float thicknessMm, Vector3 worldPoint)
        {
            if (States.Count == 0 || !Weakens) return 1f;
            PlateState state;
            if (!States.TryGetValue(part, out state) || state.Dents.Count == 0) return 1f;

            PlateGeometry g = state.Info.Geometry;
            Vector2 at = g.ToPlane(part.transform.InverseTransformPoint(worldPoint));
            float thickness = Mathf.Max(thicknessMm * 0.001f, 0.001f);
            float lost = 0f;
            foreach (Dent dent in state.Dents)
            {
                if (dent.Axis != 0) continue;   // a dent in a side thins the block across its width, not the plate across its thickness
                float du = at.x - dent.U, dv = at.y - dent.V;
                float rho = Mathf.Sqrt(du * du + dv * dv) / dent.R;
                if (rho >= 1f) continue;
                float bowl = 1f - rho * rho;
                lost += dent.D * bowl * bowl;
            }
            return Mathf.Clamp(1f - lost / thickness, 0.4f, 1f);
        }

        // ---- What the model tells ------------------------------------------------------------------

        // A shot has been resolved on 'part'.
        //   direction: the way the shell was going (for a ricochet: the way it bounced off)
        //   strength:  0..1, how hard it was compared with what the plate could take
        //   delay:     seconds until the shell's ray arrives (the mark appears then)
        //   shellMass, speed, keptEnergy: the shell (at the plate), for the simulation; without them the quick marks are used
        //   visualOnly: a simulated shot's mark: drawn, but not recorded in the armor model (no hole to shoot through, no thinner plate)
        public static void OnImpact(PartScript part, ArmorTag armor, DamageKind kind, Vector3 point, Vector3 normalFacing,
                                    Vector3 direction, float caliberMeters, float strength, float angle, float delay,
                                    float shellMass = 0f, float speed = 0f, float keptEnergy = 1f, bool visualOnly = false, float deflection = 0f,
                                    ShellProfile profile = default(ShellProfile))
        {
            // A real shell has already arrived: its marks and effects come now, not when the (slowed) picture of its ray gets there. Only the
            // simulated shot, which is a replay, waits for its ray.
            delay = RealTime(delay, visualOnly);

            // The sparks and smoke come with the hit, whether or not the plate can be marked (a simulated shot is imaginary: no effects).
            if (!visualOnly && part != null && part.Aircraft != null && part.Aircraft.LoadContext == CraftLoadContext.Flight)
                ImpactEffects.Play(part, kind, point, normalFacing, direction, caliberMeters, strength, angle,
                                   armor != null ? armor.Thickness * 0.001f : 0.02f, delay);

            if (visualOnly ? !Visuals : !Visuals && !Weakens) return;
            if (part == null || part.Aircraft == null || part.Aircraft.LoadContext != CraftLoadContext.Flight) return;

            PlateInfo info = InfoOf(part);
            if (info == null)
            {
                NoMark(part, "its mesh cannot be marked (it is shared, unreadable or not a single mesh of its own)");
                return;
            }

            Vector3 normalLocal = part.transform.InverseTransformDirection(normalFacing).normalized;
            int axis;
            float signedFacing;
            if (!ChooseAxis(info, normalLocal, out axis, out signedFacing))   // an edge or a corner: it has no map
            {
                NoMark(part, "the shell hit an edge or a corner of the block, which has no map (no face of the block is within " +
                             (Mathf.Acos(BigFaceCos) * Mathf.Rad2Deg).ToString("F0") + " deg of the surface there)");
                return;
            }
            PlateGeometry g = info.GeometryOf(axis);
            Vector3 directionLocal = part.transform.InverseTransformDirection(direction).normalized;
            Vector2 at = g.ToPlane(part.transform.InverseTransformPoint(point));
            Vector2 along = g.DirectionToPlane(directionLocal);

            float thickness = armor != null ? armor.Thickness * 0.001f : info.ThicknessM;
            PlateImpact impact = new PlateImpact
            {
                Kind = kind,
                Axis = axis,
                Front = signedFacing > 0f,
                U = at.x, V = at.y,
                DirU = along.x, DirV = along.y,
                Angle = angle,
                Caliber = caliberMeters,
                Strength = strength,
                Thickness = thickness,
                Seed = unchecked(part.Part.Id * 7919 + (++counter) * 104729),
                ShellMass = shellMass,
                Speed = speed,
                PlateHardness = armor != null ? armor.Hardness : 100f,
                PlateBrittleness = armor != null ? armor.Brittleness / 100f : 0.5f,
                KeptEnergy = keptEnergy,
                Deflection = deflection,
                Profile = profile,
            };

            if (Weakens && !visualOnly) Record(part, info, impact);   // a simulated shot only draws: it never changes the armor model
            if (Visuals) Show(part, new[] { impact }, delay);
            if (!visualOnly && ArmorFragmentsPlugin.DamageMapsShareMarks.Value) MarksSync.Send(part, impact, delay);   // the other players see it too
        }

        // A mark that another player's game worked out (MarksSync): drawn here the same way, and recorded in the armor model of this game.
        public static void OnRemoteImpact(PartScript part, PlateImpact impact, float delay)
        {
            delay = RealTime(delay, false);
            if (part == null || part.Aircraft == null || part.Aircraft.LoadContext != CraftLoadContext.Flight) return;
            PlateInfo remoteInfo = InfoOf(part);
            if (remoteInfo != null) PlayRemoteEffects(part, remoteInfo, impact, delay);
            if (!Visuals && !Weakens) return;
            PlateInfo info = remoteInfo;
            if (info == null) return;
            if (Weakens) Record(part, info, impact);
            if (Visuals) Show(part, new[] { impact }, delay);
        }

        // When a mark or an effect shows: at once for a real shot (its time is the real one: the shell is already there), after the ray's own
        // time for the simulated shot, which is drawn to be watched.
        private static float RealTime(float delay, bool simulated)
        {
            return simulated || ShotSimulator.Active ? delay : 0f;
        }

        // The sparks and smoke of another player's hit: its place and direction are rebuilt from the plate coordinates the message carries.
        private static void PlayRemoteEffects(PartScript part, PlateInfo info, PlateImpact impact, float delay)
        {
            if (!ArmorFragmentsPlugin.ImpactEffectsEnabled.Value) return;
            PlateGeometry g = info.GeometryOf(impact.Axis);
            Vector3 normalLocal = impact.Front ? g.N : -g.N;
            Vector3 local = g.Origin + g.T1 * impact.U + g.T2 * impact.V + normalLocal * (g.Depth * 0.5f);
            Vector3 planar = g.T1 * impact.DirU + g.T2 * impact.DirV;
            planar = planar.sqrMagnitude > 0.0001f ? planar.normalized : g.T1;
            float angle = Mathf.Clamp(impact.Angle, 0f, 85f) * Mathf.Deg2Rad;
            // The way the shell went (into the plate), or for a ricochet the way it bounced (away from it).
            Vector3 into = impact.Kind == DamageKind.Ricochet ? normalLocal : -normalLocal;
            Vector3 directionLocal = (planar * Mathf.Sin(angle) + into * Mathf.Cos(angle)).normalized;

            Transform t = part.transform;
            ImpactEffects.Play(part, impact.Kind, t.TransformPoint(local), t.TransformDirection(normalLocal), t.TransformDirection(directionLocal),
                               impact.Caliber, impact.Strength, impact.Angle, impact.Thickness, delay);
        }

        // Why a shell left no mark on a plate, told once per plate and reason (so the log does not stay silent).
        private static readonly HashSet<string> NoMarkTold = new HashSet<string>();

        private static void NoMark(PartScript part, string reason)
        {
            if (NoMarkTold.Add(part.GetInstanceID() + reason))
                ArmorFragmentsPlugin.Log.LogInfo(Tag + "no mark on '" + part.Part.Name + "': " + reason);
        }

        // A fragment stopped in a plate (a pit) or went through it (a small hole). Gathered until the next frame.
        private sealed class PitBuffer
        {
            public readonly List<PlateImpact> Impacts = new List<PlateImpact>();
            public float Delay = float.MaxValue;
        }

        private static readonly Dictionary<PartScript, PitBuffer> Pits = new Dictionary<PartScript, PitBuffer>();

        public static void OnFragment(PartScript part, bool passed, Vector3 point, Vector3 normalFacing, Vector3 direction,
                                      float diameterMeters, float strength, float delay)
        {
            delay = RealTime(delay, false);
            if (!Visuals || part == null || part.Aircraft == null || part.Aircraft.LoadContext != CraftLoadContext.Flight) return;

            // A fragment's pit is small and there are hundreds: it goes only on a plate that a shell has already marked. A plate that only a fragment
            // touched is left as the game draws it (setting it up for a pit gave it a tile and switched it to the Lit material, and the atlas filled up
            // with plates nobody could tell had been hit).
            if (!AlreadyMarked(part)) return;
            PlateInfo info = InfoOf(part);
            if (info == null) return;

            PitBuffer buffer;
            if (!Pits.TryGetValue(part, out buffer)) Pits[part] = buffer = new PitBuffer();
            if (buffer.Impacts.Count >= MaxPitsPerCloud) return;

            Vector3 normalLocal = part.transform.InverseTransformDirection(normalFacing).normalized;
            int axis;
            float signedFacing;
            if (!ChooseAxis(info, normalLocal, out axis, out signedFacing)) return;   // an edge or a corner: it has no map
            PlateGeometry g = info.GeometryOf(axis);
            Vector2 at = g.ToPlane(part.transform.InverseTransformPoint(point));
            buffer.Delay = Mathf.Min(buffer.Delay, delay);
            buffer.Impacts.Add(new PlateImpact
            {
                Kind = passed ? DamageKind.SmallHole : DamageKind.Pit,
                Axis = axis,
                Front = signedFacing > 0f,
                U = at.x, V = at.y,
                Caliber = diameterMeters,
                Strength = strength,
                Thickness = info.ThicknessM,
                Seed = unchecked(part.Part.Id * 7919 + (++counter) * 104729),
            });
        }

        // The model's record of the impact: holes let things through, dents make the plate thinner there.
        private static void Record(PartScript part, PlateInfo info, PlateImpact impact)
        {
            PlateState state;
            if (!States.TryGetValue(part, out state)) States[part] = state = new PlateState { Info = info };

            float caliber = Mathf.Max(impact.Caliber, 0.002f);
            switch (impact.Kind)
            {
                case DamageKind.Blocked:
                    AddDent(state, impact.Axis, impact.U, impact.V, caliber * (0.85f + 0.5f * impact.Strength), caliber * (0.08f + 0.32f * impact.Strength));
                    break;

                case DamageKind.Penetrated:
                {
                    AddDent(state, impact.Axis, impact.U, impact.V, caliber * 0.85f, caliber * 0.10f);
                    AddHole(state, impact.Axis, impact.Front, impact.U, impact.V, caliber * 0.5f);

                    Vector2 dir = new Vector2(impact.DirU, impact.DirV);
                    dir = dir.sqrMagnitude < 0.0001f ? new Vector2(1f, 0f) : dir.normalized;
                    float lateral = Mathf.Min(impact.Thickness * Mathf.Tan(Mathf.Clamp(impact.Angle, 0f, 85f) * Mathf.Deg2Rad), 0.6f);
                    AddHole(state, impact.Axis, !impact.Front, impact.U + dir.x * lateral, impact.V + dir.y * lateral, caliber * 0.5f * 1.15f);
                    break;
                }
            }
        }

        private static void AddDent(PlateState state, int axis, float u, float v, float radius, float depth)
        {
            if (state.Dents.Count >= MaxRecords) state.Dents.RemoveAt(0);
            state.Dents.Add(new Dent { Axis = axis, U = u, V = v, R = radius, D = depth });
        }

        private static void AddHole(PlateState state, int axis, bool front, float u, float v, float radius)
        {
            if (state.Holes.Count >= MaxRecords) state.Holes.RemoveAt(0);
            state.Holes.Add(new Hole { Axis = axis, Front = front, U = u, V = v, R = radius });
        }

        // The look: hand the impacts to the worker thread, if the plate can be shown.
        private static void Show(PartScript part, PlateImpact[] impacts, float delay)
        {
            // The impacts of one burst can be on different faces of a thick block (the plate's own faces and its sides): each goes to its own plate.
            bool mixed = false;
            for (int i = 1; i < impacts.Length; i++) if (impacts[i].Axis != impacts[0].Axis) mixed = true;
            if (!mixed)
            {
                ShowOn(part, impacts, delay, impacts.Length > 0 ? impacts[0].Axis : 0);
                return;
            }
            for (int axis = 0; axis <= 2; axis++)
            {
                List<PlateImpact> own = new List<PlateImpact>();
                foreach (PlateImpact impact in impacts) if (impact.Axis == axis) own.Add(impact);
                if (own.Count > 0) ShowOn(part, own.ToArray(), delay, axis);
            }
        }

        private static void ShowOn(PartScript part, PlateImpact[] impacts, float delay, int axis)
        {
            VehicleMaps maps = VehicleFor(part.Aircraft);
            if (maps == null) return;
            PlateVisual plate = maps.PlateFor(part, axis);
            if (plate == null) return;

            DamageWorker.Job job = new DamageWorker.Job
            {
                Owner = maps, Plate = plate, Impacts = impacts, DeliverAt = Time.time + Mathf.Max(delay, 0f),
                Height = ArmorFragmentsPlugin.DamageMapsRelief.Value != 0 || maps.Gloss,
                Damage = ArmorFragmentsPlugin.ImpactCameraDamageView.Value,
                Everything = plate.TakeFull(),   // its tiles were just given out: they are filled with its paint and flat normals first
                ClearPatches = PlateClearance.Prepare(plate, impacts),   // plates that overlap this one: the marks stay inside the room there is
            };
            WantGeometry(job, plate);

            // The sides of a block got their tiles with it: they are filled with the block's paint now, hit or not (an untouched tile is white, shiny
            // metal in the atlas, and the block's side showed it).
            // (Whichever face was hit: a hit on a side must fill the block's big faces too, or the top of the block stays white.)
            PlateVisual block = plate.Main ?? plate;
            for (int k = -1; k < 2; k++)
            {
                PlateVisual other = k < 0 ? block : block.Sides[k];
                if (other == null || other == plate || other.Cpu.Width <= 0 || !other.TakeFull()) continue;
                DamageWorker.Job fill = new DamageWorker.Job
                {
                    Owner = maps, Plate = other, Impacts = new PlateImpact[0], DeliverAt = job.DeliverAt,
                    Height = job.Height, Damage = job.Damage, Everything = true,
                };
                WantGeometry(fill, other);   // the block's mesh is built by whichever of its jobs is the last
                DamageWorker.Enqueue(fill);
            }

            // A shell's impact is simulated if the setting says so and the simulations are not piling up; the settings are taken
            // here, on the main thread, and travel with the job.
            int quality = Math.Min(ArmorFragmentsPlugin.SimQuality.Value - 1, ArmorFragments.Sim.ImpactSim.MaxQuality);
            if (quality >= 0 && impacts.Length == 1 && SimBridge.Wants(impacts[0]))
            {
                if (DamageWorker.PendingSims < QueueLimit())
                {
                    job.SimQuality = quality;
                    job.AutoQuality = ArmorFragmentsPlugin.SimAutoQuality.Value;
                    job.TimeLimitMs = Mathf.RoundToInt(ArmorFragmentsPlugin.SimTimeLimit.Value * 1000f);
                    job.ResistanceJPerM3 = ArmorFragmentsPlugin.ArmorResistance.Value * 1e7f;   // J per mm per cm2 -> J per m3
                    job.Threads = ArmorFragmentsPlugin.SimThreads.Value;
                    job.UseGpu = ArmorFragmentsPlugin.UseGpu.Value;
                    job.SimCracks = ArmorFragmentsPlugin.SimCracks.Value;
                    job.SimSmooth = ArmorFragmentsPlugin.SimSmoothEdges.Value;
                }
                else
                {
                    ArmorFragmentsPlugin.Log.LogInfo("[SIMFX] " + DamageWorker.PendingSims + " simulations are waiting (the limit is " + QueueLimit() +
                                                     "): this impact gets the quick marks");
                }
            }
            PlateStamps.FlexScale = ArmorFragmentsPlugin.DamageMapsFlex.Value ? 1f : 0f;
            DamageWorker.Enqueue(job);
            FlexNeighbors(part, maps, plate, impacts);
        }

        // A heavy blow bends the plates it is joined to a little too: each armor plate that touches the hit one gets a dish of its own, at the place
        // nearest the hit, smaller the further it is (none beyond 30 cm, none that would be under 0.8 mm deep). Only a look; nothing is recorded.
        private static void FlexNeighbors(PartScript part, VehicleMaps maps, PlateVisual plate, PlateImpact[] impacts)
        {
            if (!ArmorFragmentsPlugin.DamageMapsFlex.Value) return;
            foreach (PlateImpact impact in impacts)
            {
                if (impact.Kind != DamageKind.Blocked && impact.Kind != DamageKind.Penetrated) continue;
                PlateGeometry g = plate.Info.Geometry;
                float radius, depth;
                PlateStamps.FlexOf(impact, g.SizeU, g.SizeV, out radius, out depth);
                if (depth * 0.5f < 0.0008f) continue;

                System.Collections.Generic.List<Collider> neighbors = PlateClearance.NeighborsOf(plate);
                if (neighbors.Count == 0) continue;
                Vector3 hit = part.transform.TransformPoint(g.Origin + g.T1 * impact.U + g.T2 * impact.V);
                Vector3 struck = part.transform.TransformDirection(g.N * (impact.Front ? 1f : -1f));

                Dictionary<PartScript, Vector3> nearest = new Dictionary<PartScript, Vector3>();
                Dictionary<PartScript, float> distance = new Dictionary<PartScript, float>();
                foreach (Collider c in neighbors)
                {
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    PartScript other = c.GetComponentInParent<PartScript>();
                    if (other == null || other == part) continue;
                    Vector3 q = c.ClosestPoint(hit);
                    float d = (q - hit).magnitude;
                    float known;
                    if (d < 0.3f && (!distance.TryGetValue(other, out known) || d < known)) { distance[other] = d; nearest[other] = q; }
                }

                // Only the three nearest plates bend (a vehicle packed with plates must not set up dozens of them for one hit).
                List<PartScript> order = new List<PartScript>(nearest.Keys);
                order.Sort((a, b) => distance[a].CompareTo(distance[b]));
                if (order.Count > 3) order.RemoveRange(3, order.Count - 3);
                foreach (PartScript other in order)
                {
                    float share = 0.5f * (1f - distance[other] / 0.3f);
                    float neighborDepth = depth * share;
                    if (neighborDepth < 0.0008f) continue;
                    if (!AlreadyMarked(other)) continue;   // a plate nobody has marked is not set up just to bend a little
                    PlateInfo info = InfoOf(other);
                    if (info == null) continue;

                    Vector2 at = info.Geometry.ToPlane(other.transform.InverseTransformPoint(nearest[other]));
                    Vector3 normal = other.transform.TransformDirection(info.Geometry.N);
                    PlateImpact flex = new PlateImpact
                    {
                        Kind = DamageKind.Flex, Axis = 0, Front = Vector3.Dot(normal, struck) >= 0f,
                        U = at.x, V = at.y, Caliber = radius * 0.6f, Depth = neighborDepth, Strength = impact.Strength,
                        Thickness = info.ThicknessM, Seed = unchecked(other.Part.Id * 7919 + (++counter) * 104729),
                    };
                    ShowOn(other, new[] { flex }, 0f, 0);   // a Flex asks for no flexing of its own neighbors
                }
            }
        }

        // Has this plate been set up for marks already (a shell hit it)?
        private static bool AlreadyMarked(PartScript part)
        {
            VehicleMaps maps;
            PlateVisual plate;
            return part != null && part.Aircraft != null && Vehicles.TryGetValue(part.Aircraft, out maps) && !maps.Disposed &&
                   maps.Plates.TryGetValue(part, out plate) && plate.Applied && !plate.Failed;
        }

        private static VehicleMaps VehicleFor(AircraftScript aircraft)
        {
            VehicleMaps maps;
            if (Vehicles.TryGetValue(aircraft, out maps)) return maps;
            if (Refused.Contains(aircraft)) return null;

            // Not too many at once: the vehicle that has been quiet longest gives its atlas back.
            // At least two: with one, the vehicle you shoot and your own (which also gets marks, from fragments or when you leave it) took the
            // atlas from each other over and over, and every time a vehicle gave its atlas back its plates lost their marks.
            int most = Mathf.Max(ArmorFragmentsPlugin.DamageMapsMaxVehicles.Value, 2);
            while (Vehicles.Count >= most)
            {
                AircraftScript oldest = null;
                float oldestTime = float.MaxValue;
                foreach (KeyValuePair<AircraftScript, VehicleMaps> pair in Vehicles)
                {
                    if (pair.Value.LastUsed < oldestTime) { oldestTime = pair.Value.LastUsed; oldest = pair.Key; }
                }
                if (oldest == null) break;
                ArmorFragmentsPlugin.Log.LogInfo(Tag + "too many vehicles with damage maps: '" + oldest.name + "' gives its atlas back");
                Vehicles[oldest].Dispose(oldest != null);
                Vehicles.Remove(oldest);
            }

            maps = VehicleMaps.Create(aircraft);
            if (maps == null)
            {
                Refused.Add(aircraft);
                ArmorFragmentsPlugin.Log.LogInfo(Tag + "no damage maps for '" + aircraft.name + "': " + VehicleMaps.LastFailure);
                return null;
            }
            Vehicles[aircraft] = maps;
            return maps;
        }

        // ---- Every frame ------------------------------------------------------------------------

        public static void Tick()
        {
            string message;
            while (DamageWorker.Messages.TryDequeue(out message)) ArmorFragmentsPlugin.Log.LogWarning(message);
            while (DamageWorker.Infos.TryDequeue(out message)) ArmorFragmentsPlugin.Log.LogInfo(message);
            ShedLoad();

            // The atlases a hit vehicle will need are made one a frame, not all in the frame of its first impact.
            foreach (VehicleMaps vehicle in Vehicles.Values)
                if (vehicle.WarmUp()) break;

            // The fragment marks go to the worker. A plate's first mark makes its setup (its mesh gets the atlas UVs and tangents, which can
            // take milliseconds on a big mesh), so a burst that reaches dozens of plates at once would be one very long frame: at most
            // MaxSetupsPerFrame plates are set up a frame, and the marks of the others wait for the next frames.
            if (Pits.Count > 0)
            {
                int setups = 0;
                List<PartScript> shown = null;
                foreach (KeyValuePair<PartScript, PitBuffer> pair in Pits)
                {
                    if (pair.Key == null || pair.Value.Impacts.Count == 0) { (shown ?? (shown = new List<PartScript>())).Add(pair.Key); continue; }
                    if (NeedsSetup(pair.Key))
                    {
                        if (setups >= MaxSetupsPerFrame) continue;   // the next frame
                        setups++;
                    }
                    Show(pair.Key, pair.Value.Impacts.ToArray(), pair.Value.Delay);
                    (shown ?? (shown = new List<PartScript>())).Add(pair.Key);
                }
                if (shown != null) foreach (PartScript part in shown) Pits.Remove(part);
            }

            // What the worker has finished goes into the atlases, a few rectangles a frame (and not before the shell's
            // ray has arrived), never more than a couple of milliseconds.
            // Which blocks have the worker done with (read BEFORE the finished rectangles are taken: the worker queues them, then counts the job off).
            List<PlateVisual> settled = null;
            for (int i = LitWanted.Count - 1; i >= 0; i--)
            {
                PlateVisual block = LitWanted[i];
                if (block.Owner == null || block.Owner.Disposed || block.HasHole) { LitWanted.RemoveAt(i); continue; }
                if (!WorkerBusy(block)) (settled ?? (settled = new List<PlateVisual>())).Add(block);
            }

            Delivery done;
            while (DamageWorker.Done.TryDequeue(out done)) Holding.Add(done);
            if (Holding.Count > 0)
            {
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                float now = Time.time;
                int delivered = 0;
                for (int i = 0; i < Holding.Count && delivered < 16; )
                {
                    Delivery d = Holding[i];
                    if (d.Owner.Disposed) { Holding.RemoveAt(i); continue; }
                    if (d.DeliverAt > now) { i++; continue; }

                    try
                    {
                        lastDeliverMs = d.Owner.Deliver(d);
                    }
                    catch (Exception e)
                    {
                        ArmorFragmentsPlugin.Log.LogWarning(Tag + "a rectangle could not be delivered: " + e.Message);
                    }
                    Holding.RemoveAt(i);
                    delivered++;
                    if (watch.Elapsed.TotalMilliseconds > 2.0) break;
                }
            }

            // The Lit material goes on a block only when everything of it is in the atlas (nothing queued, nothing waiting for the shell's ray).
            if (settled != null)
            {
                foreach (PlateVisual block in settled)
                {
                    if (HoldingFor(block)) continue;
                    if (block.Owner.Gloss && block.Owner.Height == null) continue;   // the shine map is not made yet
                    try { block.EnsureLit(); }
                    catch (Exception e) { ArmorFragmentsPlugin.Log.LogWarning(Tag + "a plate could not be switched to the Lit shader: " + e.Message); }
                    LitWanted.Remove(block);
                }
            }

            if (++frame % 120 == 0) Prune();
            TickSettings();
            TickGeometrySettings();
            TickMeshes();
            GeometryView.Tick(Vehicles.Values);
        }

        private const int MaxSetupsPerFrame = 2;

        // Would marking this plate now have to set it up first (make the vehicle's maps, or the plate's own mesh and material)?
        private static bool NeedsSetup(PartScript part)
        {
            if (part.Aircraft == null) return false;
            VehicleMaps maps;
            if (!Vehicles.TryGetValue(part.Aircraft, out maps)) return !Refused.Contains(part.Aircraft);
            PlateVisual plate;
            return maps.Plates.TryGetValue(part, out plate) && !plate.Applied && !plate.Failed;
        }

        // ---- Settings that change what is drawn ---------------------------------------------------

        private static int shownRelief;
        private static bool shownDamageView;

        // Real geometry (experimental): the job also rewrites the plate's mesh, if the setting is on and the plate is a flat rectangle.
        private static void WantGeometry(DamageWorker.Job job, PlateVisual plate)
        {
            if (!ArmorFragmentsPlugin.DamageMapsGeometry.Value || !plate.CanRemesh()) return;
            job.Geometry = true;
            job.GridMeters = ArmorFragmentsPlugin.DamageMapsGridMm.Value * 0.001f;
            job.FineMeters = ArmorFragmentsPlugin.DamageMapsFineGridMm.Value * 0.001f;
            job.MaxVertices = VertexRoom(plate);
        }

        // How many vertices this plate's geometry may have: its own limit, but never more than half the vehicle's budget, and never more than
        // what is left of that budget after the vehicle's other plates (a floor keeps a late plate from being left flat).
        private static int VertexRoom(PlateVisual plate)
        {
            int own = ArmorFragmentsPlugin.DamageMapsMaxKiloVertices.Value * 1000;
            int budget = ArmorFragmentsPlugin.DamageMapsBudgetKiloVertices.Value * 1000;
            int others = 0;
            foreach (PlateVisual other in plate.Owner.Plates.Values)
                if (other != plate) others += other.GeometryVertices;
            return Math.Max(3000, Math.Min(own, Math.Min(budget / 2, budget - others)));
        }

        // How many simulations may wait their turn. One shot that goes through several plates asks for one simulation per plate, so a fixed
        // small number sent the last plates of a chain to the quick marks. The setting is the least; while simulations are quick (the graphics
        // card: tens of milliseconds) more may wait, as many as fit in about 0.8 s of work, up to 12. A slow machine keeps the setting.
        private static int QueueLimit()
        {
            int least = ArmorFragmentsPlugin.SimMaxQueue.Value;
            int typical = Math.Max(DamageWorker.LastSimMs, 20);
            return Math.Max(least, Math.Min(12, 800 / typical));
        }

        private static readonly List<MeshData> HoldingMeshes = new List<MeshData>();
        private static bool shownGeometry;
        private static float shownGrid;

        // What the worker has rewritten goes into the plates' meshes, one at a time and not before the shell's ray has arrived.
        private static void TickMeshes()
        {
            MeshData data;
            while (DamageWorker.Meshes.TryDequeue(out data)) HoldingMeshes.Add(data);
            if (HoldingMeshes.Count == 0) return;

            float now = Time.time;
            for (int i = 0; i < HoldingMeshes.Count; i++)
            {
                data = HoldingMeshes[i];
                if (data.Plate.Owner.Disposed || !ArmorFragmentsPlugin.DamageMapsGeometry.Value) { HoldingMeshes.RemoveAt(i); i--; continue; }
                if (data.DeliverAt > now) continue;

                HoldingMeshes.RemoveAt(i);
                try
                {
                    double ms = data.Plate.ApplyGeometry(data, GeometryView.Need(data.Plate, data.MaxHeight));
                    if (data.Plate.GeometryApplied <= 3)
                        ArmorFragmentsPlugin.Log.LogInfo("[GEOM] plate '" + data.Plate.Info.Part.Part.Name + "': big-face triangles " + data.Before + " -> " + data.After +
                            " around the damage, " + data.Vertices.Length + " vertices, " + (data.Triangles.Length / 3) + " triangles, up to " +
                            (data.MaxHeight * 1000f).ToString("F0") + " mm of push; built in " +
                            data.BuildMs.ToString("F1") + " ms (worker), put in the mesh in " + ms.ToString("F1") + " ms");
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("[GEOM] a plate's mesh could not be rewritten: " + e.Message);
                }
                return;   // one a frame
            }
        }

        // The geometry setting (or its grid) changed: every damaged plate is rewritten, or given its own mesh back.
        private static void TickGeometrySettings()
        {
            bool on = ArmorFragmentsPlugin.DamageMapsGeometry.Value;
            float grid = ArmorFragmentsPlugin.DamageMapsGridMm.Value + 1000f * ArmorFragmentsPlugin.DamageMapsFineGridMm.Value;   // either one changing rewrites the plates
            if (on == shownGeometry && (!on || Mathf.Approximately(grid, shownGrid))) return;

            bool turnedOff = shownGeometry && !on;
            shownGeometry = on;
            shownGrid = grid;

            foreach (VehicleMaps maps in Vehicles.Values)
            {
                if (maps.Disposed) continue;
                foreach (PlateVisual plate in maps.AllPlates())
                {
                    if (!plate.Applied || (plate.Cpu.Faces[0] == null && plate.Cpu.Faces[1] == null)) continue;
                    if (turnedOff) plate.RestoreGeometry();

                    DamageWorker.Job job = new DamageWorker.Job
                    {
                        Owner = maps, Plate = plate, Impacts = new PlateImpact[0], DeliverAt = Time.time,
                        Height = ArmorFragmentsPlugin.DamageMapsRelief.Value != 0 || maps.Gloss, Damage = ArmorFragmentsPlugin.ImpactCameraDamageView.Value,
                        Everything = turnedOff,   // without geometry the holes need the hole atlas again
                    };
                    WantGeometry(job, plate);
                    DamageWorker.Enqueue(job);
                }
            }
        }

        // For the performance report: how many plates carry real geometry, and how many vertices and triangles that is in all.
        internal static string GeometryTotals()
        {
            int plates = 0, vertices = 0, triangles = 0, biggest = 0;
            foreach (VehicleMaps maps in Vehicles.Values)
            {
                if (maps.Disposed) continue;
                foreach (PlateVisual plate in maps.AllPlates())
                {
                    if (!plate.HasGeometry) continue;
                    plates++;
                    vertices += plate.GeometryVertices;
                    triangles += plate.GeometryTriangles;
                    if (plate.GeometryTriangles > biggest) biggest = plate.GeometryTriangles;
                }
            }
            return plates + " plates with geometry, " + (vertices / 1000) + "k vertices, " + (triangles / 1000) + "k triangles (the biggest " + (biggest / 1000) + "k)";
        }

        // The vehicle's maps, if it has any (the impact camera's damage view draws from them).
        internal static VehicleMaps MapsFor(AircraftScript aircraft)
        {
            VehicleMaps maps;
            return aircraft != null && Vehicles.TryGetValue(aircraft, out maps) && !maps.Disposed ? maps : null;
        }

        // The relief mode or the damage view was switched: the materials are set again, and a stream that was off until now gets
        // everything the plates already have (they are marked on the CPU; the worker draws them into the new atlas).
        private static void TickSettings()
        {
            int relief = Mathf.Clamp(ArmorFragmentsPlugin.DamageMapsRelief.Value, 0, 2);
            bool view = ArmorFragmentsPlugin.ImpactCameraDamageView.Value;
            if (relief == shownRelief && view == shownDamageView) return;

            bool catchUpHeight = relief != 0 && shownRelief == 0;
            bool catchUpView = view && !shownDamageView;
            bool reliefChanged = relief != shownRelief;
            shownRelief = relief;
            shownDamageView = view;

            foreach (VehicleMaps maps in Vehicles.Values)
            {
                if (maps.Disposed) continue;
                if (reliefChanged) maps.RefreshRelief();
                if (!catchUpHeight && !catchUpView) continue;

                foreach (PlateVisual plate in maps.AllPlates())
                {
                    if (!plate.Applied || (plate.Cpu.Faces[0] == null && plate.Cpu.Faces[1] == null)) continue;
                    DamageWorker.Job job = new DamageWorker.Job
                    {
                        Owner = maps, Plate = plate, Impacts = new PlateImpact[0], DeliverAt = Time.time,
                        Height = relief != 0 || maps.Gloss, Damage = view, Everything = true,
                    };
                    WantGeometry(job, plate);
                    DamageWorker.Enqueue(job);
                }
            }
        }

        // ---- The simulation's load ------------------------------------------------------------------

        private static float fastFrame, slowFrame, lastLoadChange;
        private static int allowedWorkers = int.MaxValue;

        // If the frame rate drops while the simulation works, it lends threads back (one at a time), and takes them again
        // after a calm moment. "Dropped" = the last few frames are both slow (under 40 fps) and clearly slower than usual.
        private static void ShedLoad()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || dt > 1f) return;
            fastFrame = fastFrame <= 0f ? dt : Mathf.Lerp(fastFrame, dt, 0.25f);
            slowFrame = slowFrame <= 0f ? dt : Mathf.Lerp(slowFrame, dt, 0.02f);

            int configured = DamageWorker.ConfiguredWorkers(ArmorFragmentsPlugin.SimThreads.Value);
            if (!ArmorFragmentsPlugin.SimShedLoad.Value || DamageWorker.PendingSims == 0)
            {
                if (allowedWorkers != int.MaxValue) { allowedWorkers = int.MaxValue; DamageWorker.SetAllowedWorkers(int.MaxValue); }
                return;
            }

            float now = Time.unscaledTime;
            bool pressure = fastFrame > 0.025f && fastFrame > slowFrame * 1.35f;
            int current = Mathf.Min(allowedWorkers, configured);
            if (pressure && now - lastLoadChange > 0.5f && current > 0)
            {
                allowedWorkers = current - 1;
                lastLoadChange = now;
                DamageWorker.SetAllowedWorkers(allowedWorkers);
            }
            else if (!pressure && now - lastLoadChange > 1.5f && allowedWorkers < configured)
            {
                allowedWorkers++;
                lastLoadChange = now;
                DamageWorker.SetAllowedWorkers(allowedWorkers);
            }
        }

        // Aircraft that are gone: free their atlases and forget their plates.
        private static void Prune()
        {
            List<AircraftScript> gone = null;
            foreach (AircraftScript aircraft in Vehicles.Keys)
                if (aircraft == null) (gone ?? (gone = new List<AircraftScript>())).Add(aircraft);
            if (gone != null)
                foreach (AircraftScript aircraft in gone)
                {
                    Vehicles[aircraft].Dispose(false);
                    Vehicles.Remove(aircraft);
                }

            List<PartScript> parts = null;
            foreach (PartScript part in States.Keys)
                if (part == null) (parts ?? (parts = new List<PartScript>())).Add(part);
            if (parts != null)
                foreach (PartScript part in parts) States.Remove(part);

            parts = null;
            foreach (PartScript part in Infos.Keys)
                if (part == null) (parts ?? (parts = new List<PartScript>())).Add(part);
            if (parts != null)
                foreach (PartScript part in parts) Infos.Remove(part);
            NoInfo.RemoveWhere(p => p == null);
            Refused.RemoveWhere(a => a == null);
        }

        // Everything back as it was: the plates' looks and the model's holes and dents (the armor is whole again).
        public static void Clear()
        {
            foreach (VehicleMaps maps in Vehicles.Values) maps.Dispose(maps.Aircraft != null);
            Vehicles.Clear();
            States.Clear();
            Refused.Clear();
            Pits.Clear();
            Holding.Clear();
            LitWanted.Clear();
            HoldingMeshes.Clear();
            PartDecals.Shutdown();   // the decals on the other parts too
            Message = "Cleared";
        }

        // The plugin is being unloaded: the plates look as the game made them, the worker thread and the simulation's threads stop
        // (without waiting for them) and every texture is freed. The model's holes and dents are forgotten with the rest.
        public static void Shutdown()
        {
            Clear();                     // restores every plate and frees the atlases
            DamageWorker.Shutdown();
            VehicleMaps.ReleaseStaging();
            Infos.Clear();
            NoInfo.Clear();
        }

        // ---- Test impacts (panel) ----------------------------------------------------------------

    }

    // In flight a group of parts merges the meshes of its parts into one when it initializes. A plate that is merged has
    // no renderer of its own, so its look cannot be changed afterwards. With the setting on, armor plates are marked
    // "do not merge" just before that, and keep their own renderer (a few more draw calls).
    // The group's own list is not always ready when it initializes (an AI vehicle's group may have no body yet, a part's renderer may be
    // added later), and a plate that slips through is merged for good: "is merged into its group's mesh" in the log, and no marks. So the
    // plate is also marked at the source: the moment its renderer is registered with its part's materials, an armor plate's renderer is
    // registered as "do not merge".
    [HarmonyPatch(typeof(PartMaterialScript), "AddRenderer", new[] { typeof(Renderer), typeof(Material[]), typeof(PartRendererMaterialType[]),
        typeof(Texture2D[]), typeof(Texture2D[]), typeof(Texture2D[]), typeof(int[]), typeof(bool), typeof(bool), typeof(bool) })]
    internal static class PlateRendererPatch
    {
        private static void Prefix(PartMaterialScript __instance, ref bool excludeFromCombine)
        {
            try
            {
                if (excludeFromCombine || !ArmorFragmentsPlugin.KeepPlatesApart.Value) return;
                PartScript part = __instance.GetComponent<PartScript>();
                if (part == null || part.Part == null) return;
                AircraftScript aircraft = part.Aircraft;
                if (aircraft != null && aircraft.LoadContext != CraftLoadContext.Flight) return;
                if (TagCodec.Parse(part.Part.Name).Armor != null) excludeFromCombine = true;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] plate renderer patch failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(PartGroupScript), "Initialize")]
    internal static class PlateMergePatch
    {
        private static void Prefix(PartGroupScript __instance)
        {
            try
            {
                if (!ArmorFragmentsPlugin.KeepPlatesApart.Value) return;
                BodyScript body = __instance.Body;
                AircraftScript aircraft = body != null ? body.Aircraft : null;
                if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight) return;

                int plates = 0;
                foreach (PartScript part in __instance.Parts)
                {
                    if (part == null || part.Part == null || part.PartMaterialScript == null) continue;
                    if (TagCodec.Parse(part.Part.Name).Armor == null) continue;
                    foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
                        map.ExcludeFromMeshCombine = true;
                    plates++;
                }
                if (plates > 0)
                    ArmorFragmentsPlugin.Log.LogInfo("[DMAP] " + plates + " armor plate(s) of a group kept out of the mesh merge");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] plate merge patch failed: " + e.Message);
            }
        }
    }
}
