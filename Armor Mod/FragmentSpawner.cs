using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    // What a simulated shot did to one part.
    internal struct DamageRecord
    {
        public float Total;       // damage all fragments together would do
        public float FirstTime;   // seconds after the shot starts until the first fragment touches it
    }

    // How an impact on armor was resolved.
    internal enum ImpactOutcome
    {
        Ricochet,    // the shell bounced off (its own physics can carry it on)
        Blocked,     // the armor stopped it
        Penetrated,  // it went through and made fragments
    }

    internal static class FragmentSpawner
    {
        // Length of the drawn shell track before the impact, in meters.
        // Meters of the shell's flight drawn before an impact (also where the impact camera starts).
        public static float ShellLeadIn
        {
            get { return ArmorFragmentsPlugin.ShellLeadIn.Value; }
        }

        private const float MaxRange = 30f;        // total path length of one ray, in meters
        private const float SurfaceOffset = 0.02f; // pushes a ricochet start point off the surface
        private const int MaxBubblesPerCloud = 80;  // most rays of one cloud that get their end bubbles

        private static readonly List<RayEvent> Events = new List<RayEvent>();   // what one stretch of a fragment ray meets

        // While not null, the damage every fragment WOULD do is added up per part here,
        // together with the moment the first fragment touches the part.
        // The shot simulator sets it for one shot, then clears it.
        public static Dictionary<PartScript, DamageRecord> DamageSink;

        // Does this shot mark the plates it hits? A real one that applies damage does. A simulated one does only when the
        // setting asks for it, and then the marks are only a look (PlateDamageMaps keeps them out of the armor model).
        private static bool LeavesMarks(bool simulated, bool applyDamage)
        {
            return simulated ? ArmorFragmentsPlugin.DamageMapsSimulatedShots.Value : applyDamage;
        }

        // Same idea for a REAL shot, for the impact camera's X-ray: what each part took, and when the
        // drawn ray reaches it. (A separate one: DamageSink also changes how the rays are drawn.)
        public static Dictionary<PartScript, DamageRecord> ImpactSink;

        // Who fired the shell being resolved (the game's player id; null for the simulated shot or when
        // unknown). Set by the cannon hit patch around each resolution, as the game does for its own shells.
        public static int? Attacker;

        private struct TraceResult
        {
            public bool HitPart;
            public int Ricochets;
            public float Damage;
        }

        // angle: 0 = head-on, 90 = grazing (same convention as the hit log).
        // leadTime: seconds the initial ray (the shell) takes to reach the armor, when the caller
        //           has already drawn it. Negative = draw the usual short shell track here.
        // plate: which plate of a chain this is (an armor-piercing shell that goes through a plate flies on
        //        and may meet another one; 1 = the first).
        // bounces: ricochets this shell has had already (it may bounce off armor up to MaxRicochets times).
        public static ImpactOutcome Spawn(PartScript armor, ArmorTag armorTag, CannonTag cannonTag, ShellInfo shell,
                                 Vector3 point, Vector3 direction,
                                 Vector3 normal, float angle, bool applyDamage,
                                 bool simulated = false, float leadTime = -1f, int plate = 1, int bounces = 0)
        {
            BeginBatch();
            try
            {
                return SpawnCore(armor, armorTag, cannonTag, shell, point, direction, normal, angle, applyDamage,
                                 simulated, leadTime, plate, bounces);
            }
            finally
            {
                EndBatch();
            }
        }

        private static ImpactOutcome SpawnCore(PartScript armor, ArmorTag armorTag, CannonTag cannonTag, ShellInfo shell,
                                 Vector3 point, Vector3 direction,
                                 Vector3 normal, float angle, bool applyDamage,
                                 bool simulated, float leadTime, int plate, int bounces)
        {
            string tag = simulated ? "[SIM] " : "";

            // A per-face block's plate is one face of a hollow block: what comes off it (a bounced shell, the shell
            // going on, the fragments) meets the block's other faces. Whole-block armor is left behind altogether.
            bool perFace = ArmorFaces.IsPerFace(armor);
            PartScript leaveOut = perFace ? null : armor;
            PartScript resolved = perFace ? armor : null;

            // Energy bookkeeping shown in the panel.
            LastImpact.ShellEnergy = shell.Energy;
            LastImpact.Absorbed = 0f;
            LastImpact.Residual = 0f;
            LastImpact.ShellAfter = 0f;
            LastImpact.Deflection = 0f;
            LastImpact.Plate = plate;
            LastImpact.ClearCloud();

            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");

            // The shell's last few meters before the impact, drawn as the (thicker) initial ray.
            // Everything that happens after the impact waits for it to arrive.
            if (leadTime < 0f)
            {
                leadTime = BallisticRay.DrawSimpleTrack(point - direction * ShellLeadIn, point, shell.Energy, shell.Mass,
                                                        shell.CaliberMm * 0.001f);
            }

            // 0) An explosive shell without a fuse (HE), or whose fuse has run out, bursts on the plate's
            //    surface: it never gets in, the plate sheds nothing, and the fragments start outside the vehicle.
            if (shell.Explosive && (!shell.HasFuse || shell.FuseLeft <= 0f))
            {
                if (LeavesMarks(simulated, applyDamage))
                    PlateDamageMaps.OnImpact(armor, armorTag, DamageKind.Burst, point, Vector3.Dot(normal, direction) > 0f ? -normal : normal, direction,
                                             shell.CaliberMm * 0.001f, Mathf.Clamp01(shell.FillerFraction * 6f), angle, leadTime,
                                             0f, 0f, 1f, simulated, 0f, shell.Profile);
                BurstOnSurface(shell, point, direction, normal, angle, applyDamage, simulated, leadTime,
                               shell.HasFuse ? "its fuse ran out at the plate" : "it burst on touching the plate (no fuse)");
                return ImpactOutcome.Blocked;
            }

            // The marks of earlier shots: a hole lets the shell through as if the plate were not there, and a dent
            // makes the plate thinner where it was hit (see DamageMaps/).
            Vector3 facingNormal = Vector3.Dot(normal, direction) > 0f ? -normal : normal;
            if (PlateDamageMaps.InHole(armor, point, facingNormal))
            {
                ArmorFragmentsPlugin.Log.LogInfo(tag + "The shell hit a hole in the plate and flies on");
                LastImpact.Record(tag + "Through a hole", angle, 0f, 0, 0, 0f);
                ShellFlight.Continue(point + direction * SurfaceOffset, direction, shell, cannonTag, null, leaveOut, leadTime, 0f,
                                     RayKind.None, applyDamage, simulated, plate, true, bounces, resolved);
                return ImpactOutcome.Penetrated;
            }
            armorTag = PlateDamageMaps.Weakened(armor, armorTag, point);

            // 1) Ricochet: does this shell bounce off this armor at this angle? The critical angle
            //    depends on the hardnesses, the speed and the caliber against the thickness.
            string criticalBreakdown;
            float armorTolerated = EnergyModel.RicochetCriticalAngle(armorTag, shell, out criticalBreakdown);
            ArmorFragmentsPlugin.Log.LogInfo(
                tag + "Ricochet check: angle " + angle.ToString("F0") + " deg, critical " +
                Mathf.Min(armorTolerated, 90f).ToString("F1") + " deg (" + criticalBreakdown + ")");

            if (ArmorFragmentsPlugin.MaxRicochets.Value > 0 && bounces < ArmorFragmentsPlugin.MaxRicochets.Value &&
                angle >= armorTolerated)
            {
                // Make the normal face the shell, then mirror the direction around it.
                Vector3 n = Vector3.Dot(normal, direction) > 0f ? -normal : normal;
                Vector3 reflected = Vector3.Reflect(direction, n).normalized;

                // What the shell keeps after the bounce; the rest stays in the armor.
                float energyOut = EnergyModel.RicochetEnergyOut(shell.Energy, angle);
                LastImpact.Absorbed = shell.Energy - energyOut;
                LastImpact.Residual = energyOut;

                if (LeavesMarks(simulated, applyDamage))
                    PlateDamageMaps.OnImpact(armor, armorTag, DamageKind.Ricochet, point, n, reflected, shell.CaliberMm * 0.001f,
                                             1f - energyOut / Mathf.Max(shell.Energy, 1f), angle, leadTime,
                                             shell.Mass, shell.Speed, energyOut / Mathf.Max(shell.Energy, 1f), simulated, 0f, shell.Profile);
                LastImpact.Record(tag + "Armor ricochet", angle, Mathf.Min(armorTolerated, 90f), 0, 1, 0f);
                ArmorFragmentsPlugin.Log.LogInfo(
                    tag + "Armor ricochet: angle " + angle.ToString("F0") + " >= critical " +
                    armorTolerated.ToString("F0") + " deg, shell keeps " + EnergyModel.Kilojoules(energyOut) +
                    " of " + EnergyModel.Kilojoules(shell.Energy) + " and flies on");

                // The bounced shell is the same shell: it flies on as a ballistic ray (through the parts it meets,
                // paying energy in each; onto other armor, which is resolved in turn; a shell with a fuse still
                // counts it down). The physical shell is retired by the hit patch.
                ShellInfo bounced = shell;
                bounced.Speed = Mathf.Sqrt(2f * energyOut / Mathf.Max(shell.Mass, 0.000001f));
                ShellFlight.Continue(point + n * SurfaceOffset, reflected, bounced, cannonTag, null, leaveOut,
                                     leadTime, 0f, RayKind.Ricochet, applyDamage, simulated, plate, true, bounces + 1,
                                     resolved);
                return ImpactOutcome.Ricochet;
            }

            // 2) Does the shell have the energy to get through? The armor takes energy in proportion
            //    to its thickness (which grows as the hit gets more slanted) and to the shell's
            //    cross-section. What the armor does not take is left for the fragments.
            float shellEnergy = shell.Energy;
            float effectiveThickness;
            float absorbed = EnergyModel.ArmorAbsorption(armorTag, shell, angle, out effectiveThickness);
            string comparison = "shell " + EnergyModel.Kilojoules(shellEnergy) + " vs armor " +
                                EnergyModel.Kilojoules(absorbed) + " (" + effectiveThickness.ToString("F0") + " mm effective)";
            LastImpact.Absorbed = Mathf.Min(absorbed, shellEnergy);

            if (shellEnergy < absorbed)
            {
                LastImpact.Record(tag + "Blocked (" + comparison + ")", angle, Mathf.Min(armorTolerated, 90f), 0, 0, 0f);
                ArmorFragmentsPlugin.Log.LogInfo(tag + "Shell blocked by armor: " + comparison);
                if (LeavesMarks(simulated, applyDamage))
                    PlateDamageMaps.OnImpact(armor, armorTag, DamageKind.Blocked, point, facingNormal, direction,
                                             shell.CaliberMm * 0.001f, Mathf.Clamp01(shellEnergy / Mathf.Max(absorbed, 1f)), angle, leadTime,
                                             shell.Mass, shell.Speed, 1f, simulated, 0f, shell.Profile);

                // A shell with a fuse that the plate stops bursts where it stopped: on the plate's surface.
                if (shell.HasFuse)
                {
                    ShellInfo stopped = shell;
                    stopped.Speed = 0f;
                    BurstOnSurface(stopped, point, direction, normal, angle, applyDamage, simulated, leadTime,
                                   "the plate stopped it");
                }
                return ImpactOutcome.Blocked;
            }
            LastImpact.Residual = shellEnergy - absorbed;

            // 3) It penetrates. The plate's brittleness decides how much of the leftover energy becomes
            //    fragments; the cloud decides where they go, how heavy each is, and how much each carries.
            CloudSummary cloud;
            FragmentSpec[] fragments = FragmentCloud.Generate(
                armorTag, shell, direction, normal, angle, shellEnergy - absorbed,
                absorbed, effectiveThickness, ArmorFragmentsPlugin.SimpleFragments.Value, out cloud);
            LastImpact.SetCloud(cloud);

            int partsHit;
            int ricochets;
            float totalDamage;
            Launch(fragments, point, leaveOut, null, applyDamage, mask, leadTime, RayKind.None,
                   out partsHit, out ricochets, out totalDamage, resolved);

            if (fragments.Length == 0)
                ArmorFragmentsPlugin.Log.LogInfo(tag + "No fragments: the plate sheds none " +
                    "(brittleness, thickness or leftover energy too low) (" +
                    EnergyModel.Kilojoules(shellEnergy - absorbed) + ")");
            else
                ArmorFragmentsPlugin.Log.LogInfo(
                    tag + "Fragment cloud: " + cloud.Count + " fragments in " + fragments.Length + " rays" +
                    (cloud.Multiplicity > 1.001f ? " (" + cloud.Multiplicity.ToString("F1") + " per ray)" : "") + ", " +
                    EnergyModel.Kilojoules(cloud.TotalEnergy) +
                    " and " + (cloud.TotalMass * 1000f).ToString("F0") + " g in total, speed ~" + cloud.Speed.ToString("F0") +
                    " m/s, spread " + cloud.SigmaX.ToString("F1") + " x " + cloud.SigmaY.ToString("F1") +
                    " deg, axis leaning " + cloud.AxisTiltDegrees.ToString("F1") + " deg");

            LastImpact.Record(tag + "Penetrated (" + comparison + "), " + fragments.Length + " rays", angle, Mathf.Min(armorTolerated, 90f),
                              partsHit, ricochets, totalDamage);

            ArmorFragmentsPlugin.Log.LogInfo(
                tag + "Fragments: " + partsHit + " of " + fragments.Length + " rays hit a part, " +
                ricochets + " ricochets, total damage " + totalDamage.ToString("F1") +
                (applyDamage ? "" : " (not applied)") + BounceText());

            if (LeavesMarks(simulated, applyDamage))
                PlateDamageMaps.OnImpact(armor, armorTag, DamageKind.Penetrated, point, facingNormal, direction,
                                         shell.CaliberMm * 0.001f, Mathf.Clamp01(absorbed / Mathf.Max(shellEnergy, 1f)), angle, leadTime,
                                         shell.Mass, shell.Speed, 1f, simulated, EnergyModel.DeflectionDegrees(armorTag, shell, angle), shell.Profile);

            // 4) The shell is not spent by the plate: what the plate did not take and did not turn into
            //    fragments goes on, bent a little toward the plate's normal. (An explosive shell with a fuse
            //    carries its fuse in with it, and may burst inside; one without a fuse never gets here.)
            float spalled = fragments.Length > 0 ? cloud.TotalEnergy : 0f;
            ContinueAfterPlate(armor, armorTag, cannonTag, shell, point, direction, normal, angle,
                               shellEnergy, shellEnergy - absorbed - spalled, effectiveThickness,
                               applyDamage, simulated, leadTime, plate, tag, bounces, perFace);
            return ImpactOutcome.Penetrated;
        }

        // The shell has gone through 'armor' with 'energyAfter' joules left. It leaves the far side of the
        // plate (the collider's own thickness along its line), turned by the plate's deflection, and flies
        // on as a ballistic ray; if that meets another plate, the next one of the chain is resolved.
        //   timeAtPlate: seconds after the impact at which the shell reached this plate (the drawn ray's time)
        private static void ContinueAfterPlate(PartScript armor, ArmorTag armorTag, CannonTag cannonTag, ShellInfo shell,
                                               Vector3 point, Vector3 direction, Vector3 normal, float angle,
                                               float energyIn, float energyAfter, float effectiveThicknessMm,
                                               bool applyDamage, bool simulated, float timeAtPlate, int plate, string tag,
                                               int bounces, bool perFace)
        {
            // Does it stop here? (Too many plates already, or nothing left of its energy.)
            int maxPlates = Mathf.Max(1, ArmorFragmentsPlugin.MaxPlates.Value);
            string stoppedText = null;
            if (plate >= maxPlates)
                stoppedText = "Shell stopped after " + plate + " plate" + (plate == 1 ? "" : "s") +
                              " (the limit): " + EnergyModel.Kilojoules(energyAfter) + " left";
            else if (energyAfter <= energyIn * EnergyModel.StopFraction)
                stoppedText = "Shell spent in the plate (" + EnergyModel.Kilojoules(energyAfter) + " left)";

            // A solid shell that stops is done. One with a fuse still bursts where it stopped (below).
            if (stoppedText != null && !shell.HasFuse)
            {
                ArmorFragmentsPlugin.Log.LogInfo(tag + stoppedText);
                return;
            }

            // Where it leaves the plate: how far the line goes inside the plate's colliders. A face of a per-face
            // block is a plate on the block's surface, as thick as its tag says (the block itself is hollow).
            const float stepBack = 0.05f;
            float inside;
            if (perFace)
            {
                inside = Mathf.Max(effectiveThicknessMm * 0.001f, 0.005f);
            }
            else
            {
                float entry;
                float exit;
                float chord = PartPhysics.Chord(armor, point - direction * stepBack, direction, 5f, out entry, out exit);
                inside = chord > 0.001f
                    ? Mathf.Max(exit - stepBack, 0.005f)
                    : Mathf.Max(effectiveThicknessMm * 0.001f, 0.005f);   // the colliders gave nothing: use the tag's thickness
            }

            // The shell's own time to cross the plate: slowing down, from what it had to what it keeps.
            float insideTime = inside / BallisticRay.VisualSpeed(0.5f * (energyIn + Mathf.Max(energyAfter, 0f)), shell.Mass);

            // The fuse counts the way the shell flies inside the plate too. If it runs out in there, or the shell
            // stops in there, the shell bursts inside the plate.
            if (shell.HasFuse)
            {
                bool stopped = stoppedText != null;
                if (stopped || shell.FuseLeft <= inside)
                {
                    float into = stopped ? Mathf.Max(inside - 0.01f, 0f) : Mathf.Max(shell.FuseLeft, 0f);
                    float share = inside > 0.0001f ? Mathf.Clamp01(into / inside) : 1f;
                    float energyThere = Mathf.Lerp(energyIn, Mathf.Max(energyAfter, 0f), share);

                    ShellInfo burstShell = shell;
                    burstShell.Speed = Mathf.Sqrt(2f * Mathf.Max(energyThere, 0f) / Mathf.Max(shell.Mass, 0.000001f));
                    burstShell.FuseLeft = 0f;

                    ArmorFragmentsPlugin.Log.LogInfo(tag + (stopped ? stoppedText + "; " : "") + "it bursts inside plate " + plate);
                    // (A per-face plate has no collider of its own: the burst is just past its surface, and the
                    // fragments flying back meet the face like any other.)
                    Detonate(burstShell, point + direction * into, direction, perFace ? null : armor, applyDamage, simulated,
                             timeAtPlate + insideTime * share,
                             stopped ? "it stopped in the plate" : "its fuse ran out inside the plate");
                    return;
                }
            }

            // The plate turns it toward the plate's normal (into the plate, away from the shooter).
            float deflection = EnergyModel.DeflectionDegrees(armorTag, shell, angle);
            Vector3 facing = Vector3.Dot(normal, direction) > 0f ? -normal : normal;   // normal facing the shell
            Vector3 newDirection = Vector3.RotateTowards(direction, -facing, deflection * Mathf.Deg2Rad, 0f).normalized;

            ShellInfo next = shell;
            next.Speed = Mathf.Sqrt(2f * energyAfter / Mathf.Max(shell.Mass, 0.000001f));
            // (The ray below starts on the plate's front surface, so its fuse counts from there: the plate's own
            // thickness is part of the ray's length, not taken off first.)
            if (shell.HasFuse) next.FuseLeft = Mathf.Max(shell.FuseLeft, 0f);

            LastImpact.ShellAfter = energyAfter;
            LastImpact.Deflection = Vector3.Angle(direction, newDirection);

            ArmorFragmentsPlugin.Log.LogInfo(
                tag + "Shell goes through plate " + plate + " with " + EnergyModel.Kilojoules(energyAfter) + " of " +
                EnergyModel.Kilojoules(energyIn) + ", turned " + LastImpact.Deflection.ToString("F1") + " deg");

            // The ray restarts on the plate's FRONT surface, where the shell went in, with the plate ignored, not
            // past its back face. A part touching the back face (an ammo rack behind the armor) has its surface
            // right there, and a ray that starts inside a collider never sees it. Starting on the front surface
            // works whatever the plate's thickness, in the 3D model or in its tag. A per-face block is not ignored:
            // only the face just gone through is, and the shell goes on to meet its other faces.
            ShellFlight.Continue(point, newDirection, next, cannonTag,
                                 null, perFace ? null : armor, timeAtPlate, 0f, RayKind.None,
                                 applyDamage, simulated, plate + 1, true, bounces, perFace ? armor : null);
        }

        // An explosive shell bursts on the surface it touches (HE, or an APHE whose fuse has run out or that the
        // armor has stopped): just outside it, so the fragments that fly inward have to get through it.
        //   speed of 'shell' is its speed at the touch (0 if the armor stopped it)
        public static void BurstOnSurface(ShellInfo shell, Vector3 point, Vector3 direction, Vector3 normal,
                                          float angle, bool applyDamage, bool simulated, float leadTime, string why)
        {
            Vector3 facing = Vector3.Dot(normal, direction) > 0f ? -normal : normal;   // the side the shell came from
            Detonate(shell, point + facing * SurfaceOffset, direction, null, applyDamage, simulated, leadTime, why, angle);
        }

        // The shell bursts at 'point', flying along 'direction' at shell.Speed (0 if it had stopped): its casing
        // goes out as fragments in all directions, each carrying the shell's own motion plus the explosion's
        // (see DetonationCloud).
        //   inside: the part the burst is inside of, if any: the fragments cross it on their way out
        //   delay:  seconds after the impact at which it happens (when the shell's ray gets there)
        //   angle:  the hit angle to show in the panel (negative: keep what is there)
        public static void Detonate(ShellInfo shell, Vector3 point, Vector3 direction, PartScript inside,
                                    bool applyDamage, bool simulated, float delay, string why, float angle = -1f)
        {
            BeginBatch();
            try
            {
                DetonateCore(shell, point, direction, inside, applyDamage, simulated, delay, why, angle);
            }
            finally
            {
                EndBatch();
            }
        }

        private static void DetonateCore(ShellInfo shell, Vector3 point, Vector3 direction, PartScript inside,
                                         bool applyDamage, bool simulated, float delay, string why, float angle)
        {
            string tag = simulated ? "[SIM] " : "";
            CloudSummary cloud;
            FragmentSpec[] fragments = DetonationCloud.Generate(shell, direction, out cloud);
            LastImpact.SetCloud(cloud);

            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            int partsHit;
            int ricochets;
            float totalDamage;
            Launch(fragments, point, null, inside, applyDamage, mask, delay, RayKind.Detonation,
                   out partsHit, out ricochets, out totalDamage);

            string kind = Ammunition.NameOf(shell.Kind);
            LastImpact.Record(tag + "Burst (" + kind + ": " + why + "), " + fragments.Length + " rays",
                              angle >= 0f ? angle : LastImpact.Angle, angle >= 0f ? 0f : LastImpact.Tolerated,
                              partsHit, ricochets, totalDamage);

            ArmorFragmentsPlugin.Log.LogInfo(
                tag + kind + " burst (" + why + "): " + cloud.Count + " fragments in " + fragments.Length + " rays" +
                (cloud.Multiplicity > 1.001f ? " (" + cloud.Multiplicity.ToString("F1") + " per ray)" : "") + ", " +
                EnergyModel.Kilojoules(cloud.TotalEnergy) + " and " + (cloud.TotalMass * 1000f).ToString("F0") +
                " g of casing, speed ~" + cloud.Speed.ToString("F0") + " m/s, filler " +
                (shell.FillerFraction * 100f).ToString("F1") + " %, casing brittleness " +
                (shell.Brittleness * 100f).ToString("F0") + " %, shell speed " + shell.Speed.ToString("F0") + " m/s");
            ArmorFragmentsPlugin.Log.LogInfo(
                tag + "Burst fragments: " + partsHit + " of " + fragments.Length + " rays hit a part, " +
                ricochets + " ricochets, total damage " + totalDamage.ToString("F1") +
                (applyDamage ? "" : " (not applied)") + BounceText());
        }

        // What the fragments of the cloud being launched bounced off: one entry per kind of surface, with how many
        // bounces and their steepest and shallowest angles (for the log).
        private sealed class BounceCount
        {
            public int Count;
            public float MinAngle = 90f, MaxAngle;
        }

        private static readonly Dictionary<string, BounceCount> Bounces = new Dictionary<string, BounceCount>();

        private static void CountBounce(RayEvent e, float angle)
        {
            string label;
            if (e.Part == null)
                label = "not a part (ground or scenery)";
            else if (e.Face)
                label = ArmorFaces.Names[e.FaceIndex] + " face (" + e.Mm.ToString("0", CultureInfo.InvariantCulture) +
                        " mm) of '" + PlainName(e.Part) + "' from " + (e.Leaving ? "inside" : "outside");
            else if (e.Armor != null)
                label = "whole-block armor '" + PlainName(e.Part) + "' (" +
                        e.Armor.Thickness.ToString("0", CultureInfo.InvariantCulture) + " mm)";
            else
                label = "part '" + PlainName(e.Part) + "'";

            BounceCount count;
            if (!Bounces.TryGetValue(label, out count))
            {
                count = new BounceCount();
                Bounces[label] = count;
            }
            count.Count++;
            count.MinAngle = Mathf.Min(count.MinAngle, angle);
            count.MaxAngle = Mathf.Max(count.MaxAngle, angle);
        }

        // The part's name without its tags.
        private static string PlainName(PartScript part)
        {
            string name = part.Part != null ? part.Part.Name : part.name;
            if (string.IsNullOrEmpty(name)) return "?";
            int tag = name.IndexOf('[');
            string plain = tag > 0 ? name.Substring(0, tag).Trim() : name;
            return plain.Length > 0 ? plain : name;
        }

        // "; bounced off: Right face (20 mm) of 'Fuselage' from inside x14 at 81-88 deg, part 'Fuel Tank' x3 at 64-70 deg"
        private static string BounceText()
        {
            if (Bounces.Count == 0) return "";
            StringBuilder text = new StringBuilder("; bounced off: ");
            bool first = true;
            foreach (KeyValuePair<string, BounceCount> entry in Bounces.OrderByDescending(x => x.Value.Count))
            {
                if (!first) text.Append(", ");
                first = false;
                BounceCount c = entry.Value;
                text.Append(entry.Key).Append(" x").Append(c.Count).Append(" at ")
                    .Append(c.MinAngle.ToString("F0", CultureInfo.InvariantCulture));
                if (c.MaxAngle - c.MinAngle >= 0.5f)
                    text.Append('-').Append(c.MaxAngle.ToString("F0", CultureInfo.InvariantCulture));
                text.Append(" deg");
            }
            return text.ToString();
        }

        // Sends every fragment of a cloud flying from 'point' and follows each one.
        //   ignore:      a part the fragments do not meet on their first stretch (the whole-block plate they came out of)
        //   inside:      a part the point is inside of: every fragment crosses it first
        //   delay:       seconds after the impact at which they leave
        //   firstMarker: the bubble drawn at 'point' (once: on the first fragment's ray)
        //   resolved:    a per-face block whose face the fragments came out of (that face is not met again)
        private static void Launch(FragmentSpec[] fragments, Vector3 point, PartScript ignore, PartScript inside,
                                   bool applyDamage, int mask, float delay, RayKind firstMarker,
                                   out int partsHit, out int ricochets, out float totalDamage,
                                   PartScript resolved = null)
        {
            partsHit = 0;
            ricochets = 0;
            totalDamage = 0f;
            Bounces.Clear();
            bool draw = RayDrawer.Wanted;

            // Every ray ends in a bubble (a little sphere object). With hundreds of rays that would be hundreds of
            // objects at once, so a big cloud only gives some of its rays their bubbles.
            int bubbleStride = Mathf.Max(1, Mathf.CeilToInt(fragments.Length / (float)MaxBubblesPerCloud));

            for (int i = 0; i < fragments.Length; i++)
            {
                // As thick as the fragment is wide.
                RayPath path = new RayPath { Width = RayPath.DiameterOf(fragments[i].AreaCm2) };
                path.Add(point, i == 0 ? firstMarker : RayKind.None);

                // The fragment flies with its own energy, mass and size.
                Slug slug = new Slug
                {
                    Energy = fragments[i].Energy,
                    Mass = fragments[i].Mass,
                    AreaM2 = fragments[i].AreaCm2 * 0.0001f,
                    Multiplicity = Mathf.Max(fragments[i].Multiplicity, 1f),
                };
                TraceResult r = TraceRay(path, point, fragments[i].Direction, 0f, 0, ignore, inside, applyDamage,
                                         mask, delay, slug, resolved);
                if (draw)
                {
                    if (i % bubbleStride != 0)
                        for (int m = 1; m < path.Markers.Count; m++) path.Markers[m] = RayKind.None;
                    RayDrawer.Animate(path, delay);
                }

                if (r.HitPart) partsHit++;
                ricochets += r.Ricochets;
                totalDamage += r.Damage;
            }
        }

        // The shell itself leaves energy in every ordinary part it flew through: that is their damage.
        // 'skip' is a part that already took the game's own impact damage (a real shell's first contact).
        // Returns the damage (applied or not). startDelay: seconds after the impact at which the ray started.
        // shellCaliberMm: the shell's width, so an ammo rack sees it as a tube, not a thin line.
        public static float DepositCrossings(BallisticRay.Result ray, Vector3 from, Vector3 direction,
                                             PartScript skip, bool applyDamage, float startDelay, float shellCaliberMm = 0f)
        {
            TraceResult total = default;
            if (applyDamage) PartDecals.FromCrossings(ray, from, direction, shellCaliberMm);   // the holes it leaves in the parts it goes through
            BeginBatch();
            depositingShell = true;   // an ammo rack treats the shell itself as a direct hit
            shellRadius = Mathf.Max(shellCaliberMm, 0f) * 0.0005f;   // mm of caliber -> m of radius
            try
            {
                foreach (BallisticRay.Crossed crossing in ray.Crossings)
                {
                    if (crossing.Part == null || crossing.Part == skip) continue;
                    shellEnergyIn = crossing.EnergyIn;   // what the shell carries into this part (an ammo rack's direct hit)
                    Deposit(crossing.Part, crossing.Dissipated, from + direction * crossing.Distance, direction,
                            applyDamage, startDelay + crossing.ArrivalTime, ref total);
                }
            }
            finally
            {
                depositingShell = false;
                shellEnergyIn = 0f;
                shellRadius = 0f;
                EndBatch();
            }
            return total.Damage;
        }

        // What a flying piece of metal carries: its energy, mass and cross-section.
        private struct Slug
        {
            public float Energy;   // joules
            public float Mass;     // kilograms
            public float AreaM2;   // square meters
            // How many real fragments the ray stands for (1 = just this one). Energy, mass and area are of ONE
            // of them (that is what decides how far it goes); what it leaves in the parts is times this.
            public float Multiplicity;
        }

        // Follows one piece (a fragment, or a ricocheted shell) and records its route in 'path'.
        // It flies straight; each surface it meets either throws it back (a ricochet, if the hit is
        // grazing enough) or lets it in:
        //   - not a part (the ground...): it stops;
        //   - a part: it crosses it (the distance inside comes from the colliders), leaving energy
        //     behind (that energy is the part's damage), and carries on with what is left, up to
        //     MaxPartsPerFragment parts, until its energy runs out;
        //   - an armored face of a per-face block, from outside or from inside: it pays that plate's thickness
        //     (longer the more slanted) and carries on. An open face is not a surface: the piece flies through
        //     it and through the inside of the block without touching anything.
        // Each bounce consumes one of MaxRicochets, and each part or plate crossed one of the parts allowed,
        // so the loop always ends.
        // The path also gets a drawn speed for every stretch (the real speed from the energy times the
        // visual multiplier), so the ray slows down as it loses energy; the same speeds give the moment
        // each part is touched, for the X-ray view.
        //   origin:   where the first segment starts
        //   ignore:   a part to skip on the first segment only (whole-block armor itself)
        //   startInside: a part the ray starts inside (a burst inside it): the ray crosses it first. (Inside a
        //               per-face block there is nothing to cross: its faces are met like any others.)
        //   bounces:  ricochets this ray already had
        //   traveled: distance already covered, counts toward MaxRange
        //   timeOffset: seconds from the start of the shot until this ray starts moving
        //   resolved: a per-face block whose face the ray starts on (that face is not met again)
        private static TraceResult TraceRay(RayPath path, Vector3 origin, Vector3 dir,
                                            float traveled, int bounces, PartScript ignore, PartScript startInside,
                                            bool applyDamage, int mask, float timeOffset, Slug slug,
                                            PartScript resolved = null)
        {
            if (ArmorFaces.IsPerFace(startInside)) startInside = null;

            TraceResult result = default;
            int maxBounces = ArmorFragmentsPlugin.MaxRicochets.Value;
            int maxParts = ArmorFragmentsPlugin.MaxPartsPerFragment.Value;
            float tolerated = ToleratedAngle(
                ArmorFragmentsPlugin.FragmentRicochetAngle.Value,
                ArmorFragmentsPlugin.FragmentRicochetMultiplier.Value);
            float floor = Mathf.Max(1f, slug.Energy * EnergyModel.StopFraction);   // of one fragment
            RayKind stopMarker = DamageSink != null ? RayKind.Landed : RayKind.Hit;
            float bundle = Mathf.Max(slug.Multiplicity, 1f);   // fragments this ray stands for

            int partsCrossed = 0;
            PartScript skip = ignore;
            float time = timeOffset;   // seconds since the shot started, at the head of this ray

            // Starting inside a part (a shell that burst in it): the way out through it comes first. A ray
            // starting there does not "hit" the part from outside, so it has to be charged here.
            if (startInside != null)
            {
                float insideEntry;
                float insideExit;
                float insideChord = PartPhysics.Chord(startInside, origin, dir, MaxRange, out insideEntry, out insideExit);
                if (insideChord > 0.001f)
                {
                    // Armor charges a fragment as it does a shell; anything else by its density.
                    bool insideIsArmor = TagCodec.Parse(startInside.Part.Name).Armor != null;
                    float insideDensity = PartPhysics.Density(startInside);
                    float energyIn = slug.Energy;
                    float energyOut = insideIsArmor
                        ? PartPhysics.EnergyAfterArmor(energyIn, slug.AreaM2, insideExit * 1000f)
                        : PartPhysics.EnergyAfter(energyIn, slug.Mass, slug.AreaM2, insideDensity, insideExit);
                    float startSpeed = BallisticRay.VisualSpeed(energyIn, slug.Mass);
                    result.HitPart = true;

                    if (energyOut <= floor)
                    {
                        // It does not get out: everything stays in the part.
                        Deposit(startInside, energyIn * bundle, origin, dir, applyDamage, time, ref result);
                        float into = insideIsArmor
                            ? PartPhysics.ArmorStopDistance(energyIn, floor, slug.AreaM2, insideExit * 1000f, insideExit)
                            : PartPhysics.StopDistance(energyIn, floor, slug.Mass, slug.AreaM2, insideDensity, insideExit);
                        path.Add(origin + dir * into, stopMarker, startSpeed * 0.5f);
                        path.Final = RayKind.Hit;
                        return result;
                    }

                    Deposit(startInside, (energyIn - energyOut) * bundle, origin, dir, applyDamage, time, ref result);
                    slug.Energy = energyOut;
                    float average = 0.5f * (startSpeed + BallisticRay.VisualSpeed(energyOut, slug.Mass));
                    time += insideExit / average;
                    path.Add(origin + dir * insideExit, RayKind.None, average);

                    float pastExit = insideExit + 0.002f;
                    origin = origin + dir * pastExit;
                    traveled += pastExit;
                    skip = startInside;
                    partsCrossed = 1;
                }
            }

            PartScript resolvedNow = resolved;

            while (true)
            {
                float remaining = MaxRange - traveled;
                float speed = BallisticRay.VisualSpeed(slug.Energy, slug.Mass);
                if (remaining <= 0.01f) { path.Final = RayKind.Miss; break; }

                // Everything on this straight stretch, nearest first (the way into and out of per-face blocks too).
                RayEvents.Gather(origin, dir, remaining, skip, resolvedNow, Events);
                skip = null;
                resolvedNow = null;

                float at = 0f;                                  // how far along this stretch the head is
                float crossedUntil = float.NegativeInfinity;    // the part just crossed reaches this far
                bool ended = false;
                bool turned = false;

                for (int k = 0; k < Events.Count; k++)
                {
                    RayEvent e = Events[k];
                    if (e.Open) continue;                                     // an open face: empty space
                    if (!e.Face && e.Distance < crossedUntil) continue;       // inside the part just crossed

                    float d = Mathf.Max(e.Distance, at);
                    time += (d - at) / speed;   // the head reaches the surface now
                    at = d;
                    PartScript part = e.Part;

                    // A hole in an armor plate (see DamageMaps/): the fragment goes through as if the plate were not there.
                    if (part != null && e.Armor != null && PlateDamageMaps.InHole(part, e.Point, e.Normal)) continue;

                    // A grazing hit throws it back. What it keeps is sin^2 of the angle; the rest stays
                    // in whatever it hit.
                    float angle = ImpactAngle(dir, e.Normal);
                    if (bounces < maxBounces && angle >= tolerated && !CrewBodies.IsBody(part))   // bodies don't throw it back
                    {
                        float kept = EnergyModel.RicochetEnergyOut(slug.Energy, angle);
                        if (part != null)
                            Deposit(part, (slug.Energy - kept) * bundle, e.Point, dir, applyDamage, time, ref result);

                        slug.Energy = kept;
                        path.Add(e.Point, RayKind.Ricochet, speed);
                        CountBounce(e, angle);
                        Vector3 facing = Vector3.Dot(e.Normal, dir) > 0f ? -e.Normal : e.Normal;   // the side it came from
                        dir = Vector3.Reflect(dir, facing).normalized;
                        origin = e.Point + facing * SurfaceOffset;
                        traveled += d;
                        bounces++;
                        result.Ricochets++;
                        if (e.Face) resolvedNow = part;

                        if (slug.Energy <= floor)
                        {
                            path.Add(e.Point, stopMarker, speed);
                            path.Final = RayKind.Miss;
                            ended = true;
                        }
                        turned = true;
                        break;
                    }

                    // Not a part (the ground, for example): it stops there.
                    if (part == null)
                    {
                        path.Add(e.Point, DamageSink != null ? RayKind.Landed : RayKind.Miss, speed);
                        path.Final = RayKind.Miss;
                        ended = true;
                        break;
                    }

                    result.HitPart = true;
                    partsCrossed++;

                    if (e.Face)
                    {
                        // An armored face of a per-face block: a plate on the block's surface, charged as a shell
                        // of the fragment's cross-section would be (its thickness, longer the more slanted).
                        float plateMm = PlateDamageMaps.WeakenedMm(part, e.Mm, e.Point) / Mathf.Max(Mathf.Cos(angle * Mathf.Deg2Rad), 0.1f);   // dents make it thinner
                        bool faceMarks = LeavesMarks(DamageSink != null, applyDamage);   // a real shot leaves marks (the simulated one only if asked)
                        float plateIn = slug.Energy;
                        float plateOut = PartPhysics.EnergyAfterArmor(plateIn, slug.AreaM2, plateMm);

                        if (plateOut <= floor)
                        {
                            Deposit(part, plateIn * bundle, e.Point, dir, applyDamage, time, ref result);
                            if (faceMarks)
                                PlateDamageMaps.OnFragment(part, false, e.Point, e.Normal, dir,
                                                           RayPath.DiameterOf(slug.AreaM2 * 10000f), 0.6f, time);
                            path.Add(origin + dir * d, stopMarker, speed);
                            path.Final = RayKind.Hit;
                            ended = true;
                            break;
                        }

                        Deposit(part, (plateIn - plateOut) * bundle, e.Point, dir, applyDamage, time, ref result);
                        if (faceMarks)
                            PlateDamageMaps.OnFragment(part, true, e.Point, e.Normal, dir,
                                                       RayPath.DiameterOf(slug.AreaM2 * 10000f), 0.6f, time);
                        slug.Energy = plateOut;

                        if (partsCrossed >= maxParts)
                        {
                            path.Add(origin + dir * d, stopMarker, speed);
                            path.Final = RayKind.Hit;
                            ended = true;
                            break;
                        }

                        path.Add(origin + dir * d, RayKind.None, speed);
                        speed = BallisticRay.VisualSpeed(slug.Energy, slug.Mass);
                        continue;
                    }

                    // A part: how far does it travel inside, and what does that cost?
                    float entry;
                    float exit;
                    float chord = PartPhysics.Chord(part, origin, dir, remaining, out entry, out exit);
                    if (chord <= 0.001f)
                    {
                        // The collider test found nothing (a graze): treat it as a thin wall.
                        entry = d;
                        exit = d + 0.01f;
                        chord = 0.01f;
                    }
                    entry = Mathf.Max(entry, d);
                    exit = Mathf.Max(exit, entry);

                    // Whole-block armor charges a fragment what it would charge a shell of the same cross-section: its
                    // resistance per mm of thickness (the tag's, longer the more slanted the hit) and per cm2, times the
                    // fragment factor. Any other part slows it by its density.
                    ArmorTag partArmor = e.Armor;
                    if (partArmor != null) partArmor = PlateDamageMaps.Weakened(part, partArmor, e.Point);   // dents make it thinner
                    bool marks = partArmor != null && LeavesMarks(DamageSink != null, applyDamage);   // a real shot leaves marks (the simulated one only if asked)
                    float armorMm = partArmor != null
                        ? partArmor.Thickness / Mathf.Max(Mathf.Cos(angle * Mathf.Deg2Rad), 0.1f)
                        : 0f;

                    float density = PartPhysics.Density(part);
                    float energyIn = slug.Energy;
                    float energyOut = partArmor != null
                        ? PartPhysics.EnergyAfterArmor(energyIn, slug.AreaM2, armorMm)
                        : PartPhysics.EnergyAfter(energyIn, slug.Mass, slug.AreaM2, density, chord);

                    // The drawn ray reaches the part's surface at the speed it flies with.
                    path.Add(origin + dir * entry, RayKind.None, speed);

                    if (energyOut <= floor)
                    {
                        // It stops inside the part and leaves everything there; it slows to a halt on the way.
                        Deposit(part, energyIn * bundle, e.Point, dir, applyDamage, time, ref result);
                        if (marks)
                            PlateDamageMaps.OnFragment(part, false, e.Point, e.Normal, dir,
                                                       RayPath.DiameterOf(slug.AreaM2 * 10000f), 0.6f, time);
                        float into = partArmor != null
                            ? PartPhysics.ArmorStopDistance(energyIn, floor, slug.AreaM2, armorMm, chord)
                            : PartPhysics.StopDistance(energyIn, floor, slug.Mass, slug.AreaM2, density, chord);
                        path.Add(origin + dir * (entry + into), stopMarker, speed * 0.5f);
                        path.Final = RayKind.Hit;
                        ended = true;
                        break;
                    }

                    Deposit(part, (energyIn - energyOut) * bundle, e.Point, dir, applyDamage, time, ref result);
                    if (marks)
                        PlateDamageMaps.OnFragment(part, true, e.Point, e.Normal, dir,
                                                   RayPath.DiameterOf(slug.AreaM2 * 10000f), 0.6f, time);
                    slug.Energy = energyOut;

                    // Inside the part the speed goes from what it was to what it becomes.
                    float speedOut = BallisticRay.VisualSpeed(slug.Energy, slug.Mass);
                    float averageSpeed = 0.5f * (speed + speedOut);
                    time += (exit - at) / averageSpeed;

                    if (partsCrossed >= maxParts)
                    {
                        // It has gone through as many parts as allowed: it ends where it leaves the last one.
                        path.Add(origin + dir * exit, stopMarker, averageSpeed);
                        path.Final = RayKind.Hit;
                        ended = true;
                        break;
                    }

                    // Out the other side: carry on from there.
                    path.Add(origin + dir * exit, RayKind.None, averageSpeed);
                    at = exit;
                    crossedUntil = exit;
                    speed = speedOut;
                }

                if (ended) break;
                if (turned) continue;

                // Nothing more ahead: it flies off into the air.
                path.Add(origin + dir * remaining, RayKind.None, speed);
                path.Final = RayKind.Miss;
                break;
            }

            return result;
        }

        // The energy a piece leaves in a part is that part's damage. Armor takes none for now (the
        // game's own damage to armor is ignored too), and a part marked [MOD d=...] ignores pieces
        // that would hurt it less than that.
        private static void Deposit(PartScript part, float joules, Vector3 point, Vector3 dir,
                                    bool applyDamage, float arrival, ref TraceResult result)
        {
            if (joules <= 0f) return;

            PartTags tags = TagCodec.Parse(part.Part.Name);
            // Armor takes none; on an armored vehicle, neither does the structure (only modules and racks).
            if (!VehicleRules.TakesModDamage(part, tags)) return;

            float damage = joules / 1000f * ArmorFragmentsPlugin.DamagePerKiloJoule.Value;

            if (applyDamage) Accumulate(part, damage, point, dir);
            result.Damage += damage;   // counted even when not applied: "would-be" damage

            // 'arrival' is when the drawn ray's head reaches this point.
            if (DamageSink != null) Record(DamageSink, part, damage, arrival);
            if (ImpactSink != null) Record(ImpactSink, part, damage, arrival);
        }

        // ---- Damage, added up per part -------------------------------------------------------
        // A burst can have a thousand fragments hitting the same few parts. Calling the game (or sending a network
        // message) for each would be a waste: while a shot is being resolved (between BeginBatch and the
        // EndBatch that closes the outermost one) the damage is added up per part and applied once at the end.
        // Everything happens in the same frame, so the result is the same.
        private struct Pending
        {
            public float Damage;
            public float Biggest;    // the biggest single piece so far: its point and direction are the hit's
            public Vector3 Point;
            public Vector3 Dir;
        }

        private static readonly Dictionary<PartScript, Pending> PendingDamage = new Dictionary<PartScript, Pending>();
        private static int batchDepth;

        public static void BeginBatch()
        {
            batchDepth++;
        }

        public static void EndBatch()
        {
            batchDepth--;
            if (batchDepth > 0) return;
            batchDepth = 0;
            Flush();
        }

        private static bool depositingShell;   // true while DepositCrossings lays down the shell's own path
        private static float shellEnergyIn;    // J the shell carries into the part being deposited
        private static float shellRadius;      // m: half the shell's caliber

        private static void Accumulate(PartScript part, float damage, Vector3 point, Vector3 dir)
        {
            // An ammo rack judges each piece's path against its rounds before the pieces are summed up.
            AmmoRacks.NotePiece(part, damage, point, dir, depositingShell,
                                depositingShell ? shellEnergyIn : 0f, depositingShell ? shellRadius : 0f);

            if (batchDepth <= 0)
            {
                ApplyDamage(part, damage, point, dir);   // nobody will flush: apply it now
                return;
            }

            Pending pending;
            PendingDamage.TryGetValue(part, out pending);
            pending.Damage += damage;
            if (damage >= pending.Biggest)
            {
                pending.Biggest = damage;
                pending.Point = point;
                pending.Dir = dir;
            }
            PendingDamage[part] = pending;
        }

        private static void Flush()
        {
            if (PendingDamage.Count == 0) return;

            List<KeyValuePair<PartScript, Pending>> all = new List<KeyValuePair<PartScript, Pending>>(PendingDamage);
            PendingDamage.Clear();
            foreach (KeyValuePair<PartScript, Pending> pair in all)
            {
                if (pair.Key == null) continue;   // destroyed meanwhile
                try
                {
                    ApplyDamage(pair.Key, pair.Value.Damage, pair.Value.Point, pair.Value.Dir);
                }
                catch (System.Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Applying damage to a part failed: " + e.Message);
                }
            }
        }

        private static void Record(Dictionary<PartScript, DamageRecord> sink, PartScript part, float damage, float arrival)
        {
            DamageRecord record;
            if (!sink.TryGetValue(part, out record))
                record.FirstTime = arrival;
            record.Total += damage;
            record.FirstTime = Mathf.Min(record.FirstTime, arrival);
            sink[part] = record;
        }

        // Damages a part the way the game's own shells do. A part of a REMOTE aircraft (another player's)
        // ignores OnDamaged; its owner has to be told over the network, which is what DamagePart does.
        private static void ApplyDamage(PartScript part, float damage, Vector3 point, Vector3 dir)
        {
            AircraftScript aircraft = part.Aircraft;
            if (aircraft != null && aircraft.RemoteAircraft)
            {
                if (aircraft.NetworkAircraft == null)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Remote damage not sent: '" + part.Part.Name + "' has no network aircraft");
                    return;
                }

                // In peaceful mode the server throws away all damage between players.
                if (Assets.Scripts.Flight.FlightSceneScript.IsPeacefulMode)
                    ArmorFragmentsPlugin.Log.LogWarning(
                        "PEACEFUL MODE is on: the server ignores damage to other players' aircraft (turn it off in the multiplayer settings)");

                if (ArmorFragmentsPlugin.LogNetworkDamage.Value)
                    ArmorFragmentsPlugin.Log.LogInfo(
                    "Remote damage sent: " + damage.ToString("F1") + " to '" + part.Part.Name + "' (part id " +
                    part.Part.Id + ") of player " + aircraft.NetworkAircraft.PlayerId +
                    ", attacker " + (Attacker.HasValue ? Attacker.Value.ToString() : "none"));
                aircraft.NetworkAircraft.DamagePart(Attacker, part, damage, point, dir);
                return;
            }
            part.OnDamaged(null, damage, point, dir);
        }

        // 0 = head-on, 90 = grazing.
        private static float ImpactAngle(Vector3 direction, Vector3 normal)
        {
            float a = Vector3.Angle(direction, normal);
            return Mathf.Min(a, 180f - a);
        }

        // Base angle times the material multiplier, kept inside 0..90 degrees.
        private static float ToleratedAngle(float baseAngle, float multiplier)
        {
            return Mathf.Clamp(baseAngle * multiplier, 0f, 90f);
        }
    }
}
