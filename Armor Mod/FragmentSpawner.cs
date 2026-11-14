using System.Collections.Generic;
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

        private static readonly RaycastHit[] Hits = new RaycastHit[64];

        // While not null, the damage every fragment WOULD do is added up per part here,
        // together with the moment the first fragment touches the part.
        // The shot simulator sets it for one shot, then clears it.
        public static Dictionary<PartScript, DamageRecord> DamageSink;

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
                BurstOnSurface(shell, point, direction, normal, angle, applyDamage, simulated, leadTime,
                               shell.HasFuse ? "its fuse ran out at the plate" : "it burst on touching the plate (no fuse)");
                return ImpactOutcome.Blocked;
            }

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
                ShellFlight.Continue(point + n * SurfaceOffset, reflected, bounced, cannonTag, null, armor,
                                     leadTime, 0f, RayKind.Ricochet, applyDamage, simulated, plate, true, bounces + 1);
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
            Launch(fragments, point, armor, null, applyDamage, mask, leadTime, RayKind.None,
                   out partsHit, out ricochets, out totalDamage);

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
                (applyDamage ? "" : " (not applied)"));

            // 4) The shell is not spent by the plate: what the plate did not take and did not turn into
            //    fragments goes on, bent a little toward the plate's normal. (An explosive shell with a fuse
            //    carries its fuse in with it, and may burst inside; one without a fuse never gets here.)
            float spalled = fragments.Length > 0 ? cloud.TotalEnergy : 0f;
            ContinueAfterPlate(armor, armorTag, cannonTag, shell, point, direction, normal, angle,
                               shellEnergy, shellEnergy - absorbed - spalled, effectiveThickness,
                               applyDamage, simulated, leadTime, plate, tag, bounces);
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
                                               int bounces)
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

            // Where it leaves the plate: how far the line goes inside the plate's colliders.
            const float stepBack = 0.05f;
            float entry;
            float exit;
            float chord = PartPhysics.Chord(armor, point - direction * stepBack, direction, 5f, out entry, out exit);
            float inside = chord > 0.001f
                ? Mathf.Max(exit - stepBack, 0.005f)
                : Mathf.Max(effectiveThicknessMm * 0.001f, 0.005f);   // the colliders gave nothing: use the tag's thickness
            Vector3 exitPoint = point + direction * inside;

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
                    Detonate(burstShell, point + direction * into, direction, armor, applyDamage, simulated,
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
            if (shell.HasFuse) next.FuseLeft = Mathf.Max(shell.FuseLeft - inside, 0f);

            LastImpact.ShellAfter = energyAfter;
            LastImpact.Deflection = Vector3.Angle(direction, newDirection);

            ArmorFragmentsPlugin.Log.LogInfo(
                tag + "Shell goes through plate " + plate + " with " + EnergyModel.Kilojoules(energyAfter) + " of " +
                EnergyModel.Kilojoules(energyIn) + ", turned " + LastImpact.Deflection.ToString("F1") + " deg");

            // The ray starts a little BACK inside the plate (which it ignores), not past its back face: a part
            // touching that face (an ammo rack behind the armor) has its surface right there, and a ray that
            // starts inside a collider never sees it (Jakniphe's test: a 100 mm through a 64 mm plate "went
            // through 0 parts" with the rack right behind it).
            float backOff = Mathf.Min(0.02f, inside * 0.5f);
            ShellFlight.Continue(exitPoint - newDirection * backOff, newDirection, next, cannonTag,
                                 null, armor, timeAtPlate + insideTime, 0f, RayKind.None,
                                 applyDamage, simulated, plate + 1, true, bounces);
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
                (applyDamage ? "" : " (not applied)"));
        }

        // Sends every fragment of a cloud flying from 'point' and follows each one.
        //   ignore:      a part the fragments do not meet on their first stretch (the plate they came out of)
        //   inside:      a part the point is inside of: every fragment crosses it first
        //   delay:       seconds after the impact at which they leave
        //   firstMarker: the bubble drawn at 'point' (once: on the first fragment's ray)
        private static void Launch(FragmentSpec[] fragments, Vector3 point, PartScript ignore, PartScript inside,
                                   bool applyDamage, int mask, float delay, RayKind firstMarker,
                                   out int partsHit, out int ricochets, out float totalDamage)
        {
            partsHit = 0;
            ricochets = 0;
            totalDamage = 0f;
            bool draw = ArmorFragmentsPlugin.DrawRays.Value;

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
                                         mask, delay, slug);
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
        // It flies straight; the first thing it meets either throws it back (a ricochet, if the hit is
        // grazing enough) or lets it in:
        //   - not a part (the ground...): it stops;
        //   - a part: it crosses it (the distance inside comes from the colliders), leaving energy
        //     behind (that energy is the part's damage), and carries on with what is left, up to
        //     MaxPartsPerFragment parts, until its energy runs out.
        // Each bounce consumes one of MaxRicochets, and each part crossed one of the parts allowed,
        // so the loop always ends.
        // The path also gets a drawn speed for every stretch (the real speed from the energy times the
        // visual multiplier), so the ray slows down as it loses energy; the same speeds give the moment
        // each part is touched, for the X-ray view.
        //   origin:   where the first segment starts
        //   ignore:   a part to skip on the first segment only (the armor itself)
        //   startInside: a part the ray starts inside (a burst inside it): the ray crosses it first
        //   bounces:  ricochets this ray already had
        //   traveled: distance already covered, counts toward MaxRange
        //   timeOffset: seconds from the start of the shot until this ray starts moving
        private static TraceResult TraceRay(RayPath path, Vector3 origin, Vector3 dir,
                                            float traveled, int bounces, PartScript ignore, PartScript startInside,
                                            bool applyDamage, int mask, float timeOffset, Slug slug)
        {
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

            while (true)
            {
                float remaining = MaxRange - traveled;
                float speed = BallisticRay.VisualSpeed(slug.Energy, slug.Mass);
                if (remaining <= 0.01f) { path.Final = RayKind.Miss; break; }

                int count = Physics.RaycastNonAlloc(
                    origin, dir, Hits, remaining, mask, QueryTriggerInteraction.Ignore);

                // Closest hit of ANY object, except shells and the part to skip.
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int h = 0; h < count; h++)
                {
                    Collider c = Hits[h].collider;
                    if (c.GetComponentInParent<CannonProjectileScript>() != null) continue;
                    if (skip != null && c.GetComponentInParent<PartScript>() == skip) continue;
                    if (Hits[h].distance < bestDistance)
                    {
                        bestDistance = Hits[h].distance;
                        best = h;
                    }
                }
                skip = null;

                if (best < 0)
                {
                    path.Add(origin + dir * remaining, RayKind.None, speed);   // fades out in the air
                    path.Final = RayKind.Miss;
                    break;
                }

                RaycastHit hit = Hits[best];   // copy: Hits is reused by the next ray
                traveled += hit.distance;
                time += hit.distance / speed;  // the head reaches the hit now
                PartScript part = hit.collider.GetComponentInParent<PartScript>();

                // A grazing hit throws it back. What it keeps is sin^2 of the angle; the rest stays
                // in whatever it hit.
                float angle = ImpactAngle(dir, hit.normal);
                if (bounces < maxBounces && angle >= tolerated)
                {
                    float kept = EnergyModel.RicochetEnergyOut(slug.Energy, angle);
                    if (part != null)
                        Deposit(part, (slug.Energy - kept) * bundle, hit.point, dir, applyDamage, time, ref result);

                    slug.Energy = kept;
                    path.Add(hit.point, RayKind.Ricochet, speed);
                    dir = Vector3.Reflect(dir, hit.normal).normalized;
                    origin = hit.point + hit.normal * SurfaceOffset;
                    bounces++;
                    result.Ricochets++;

                    if (slug.Energy <= floor)
                    {
                        path.Add(hit.point, stopMarker, speed);
                        path.Final = RayKind.Miss;
                        break;
                    }
                    continue;
                }

                // Not a part (the ground, for example): it stops there.
                if (part == null)
                {
                    path.Add(hit.point, DamageSink != null ? RayKind.Landed : RayKind.Miss, speed);
                    path.Final = RayKind.Miss;
                    break;
                }

                // A part: how far does it travel inside, and what does that cost?
                float entry;
                float exit;
                float chord = PartPhysics.Chord(part, origin, dir, remaining, out entry, out exit);
                if (chord <= 0.001f)
                {
                    // The collider test found nothing (a graze): treat it as a thin wall.
                    entry = hit.distance;
                    exit = hit.distance + 0.01f;
                    chord = 0.01f;
                }

                // Armor charges a fragment what it would charge a shell of the same cross-section: its resistance
                // per mm of thickness (the tag's, longer the more slanted the hit) and per cm2, times the
                // fragment factor. Any other part slows it by its density.
                ArmorTag partArmor = TagCodec.Parse(part.Part.Name).Armor;
                float armorMm = partArmor != null
                    ? partArmor.Thickness / Mathf.Max(Mathf.Cos(angle * Mathf.Deg2Rad), 0.1f)
                    : 0f;

                float density = PartPhysics.Density(part);
                float energyIn = slug.Energy;
                float energyOut = partArmor != null
                    ? PartPhysics.EnergyAfterArmor(energyIn, slug.AreaM2, armorMm)
                    : PartPhysics.EnergyAfter(energyIn, slug.Mass, slug.AreaM2, density, chord);
                result.HitPart = true;
                partsCrossed++;

                // The drawn ray reaches the part's surface at the speed it flies with.
                path.Add(origin + dir * entry, RayKind.None, speed);

                if (energyOut <= floor)
                {
                    // It stops inside the part and leaves everything there; it slows to a halt on the way.
                    Deposit(part, energyIn * bundle, hit.point, dir, applyDamage, time, ref result);
                    float into = partArmor != null
                        ? PartPhysics.ArmorStopDistance(energyIn, floor, slug.AreaM2, armorMm, chord)
                        : PartPhysics.StopDistance(energyIn, floor, slug.Mass, slug.AreaM2, density, chord);
                    path.Add(origin + dir * (entry + into), stopMarker, speed * 0.5f);
                    path.Final = RayKind.Hit;
                    break;
                }

                Deposit(part, (energyIn - energyOut) * bundle, hit.point, dir, applyDamage, time, ref result);
                slug.Energy = energyOut;

                // Inside the part the speed goes from what it was to what it becomes.
                float speedOut = BallisticRay.VisualSpeed(slug.Energy, slug.Mass);
                float averageSpeed = 0.5f * (speed + speedOut);
                time += Mathf.Max(exit - entry, 0f) / averageSpeed;

                if (partsCrossed >= maxParts)
                {
                    // It has gone through as many parts as allowed: it ends where it leaves the last one.
                    path.Add(origin + dir * exit, stopMarker, averageSpeed);
                    path.Final = RayKind.Hit;
                    break;
                }

                // Out the other side: carry on from there.
                path.Add(origin + dir * exit, RayKind.None, averageSpeed);
                float past = exit + 0.002f;
                origin = origin + dir * past;
                traveled += past - hit.distance;
                skip = part;
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
