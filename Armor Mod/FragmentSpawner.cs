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

        private static readonly RaycastHit[] Hits = new RaycastHit[64];

        // While not null, the damage every fragment WOULD do is added up per part here,
        // together with the moment the first fragment touches the part.
        // The shot simulator sets it for one shot, then clears it.
        public static Dictionary<PartScript, DamageRecord> DamageSink;

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
        public static ImpactOutcome Spawn(PartScript armor, ArmorTag armorTag, CannonTag cannonTag, ShellInfo shell,
                                 Vector3 point, Vector3 direction,
                                 Vector3 normal, float angle, bool applyDamage,
                                 bool simulated = false, float leadTime = -1f)
        {
            string tag = simulated ? "[SIM] " : "";

            // Energy bookkeeping shown in the panel.
            LastImpact.ShellEnergy = shell.Energy;
            LastImpact.Absorbed = 0f;
            LastImpact.Residual = 0f;
            LastImpact.ClearCloud();

            // How many fragment rays a penetrating shell makes: from the cannon's [CAN f=] tag.
            int fragmentCount = Mathf.Clamp(
                cannonTag != null ? cannonTag.Fragments : ArmorFragmentsPlugin.DefaultFragmentCount.Value,
                1, 500);
            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            bool draw = ArmorFragmentsPlugin.DrawRays.Value;

            // The shell's last few meters before the impact, drawn as the (thicker) initial ray.
            // Everything that happens after the impact waits for it to arrive.
            if (leadTime < 0f)
            {
                leadTime = BallisticRay.DrawSimpleTrack(point - direction * ShellLeadIn, point, shell.Energy, shell.Mass);
            }

            // 1) Ricochet: does this shell bounce off this armor at this angle? The critical angle
            //    depends on the hardnesses, the speed and the caliber against the thickness.
            string criticalBreakdown;
            float armorTolerated = EnergyModel.RicochetCriticalAngle(armorTag, shell, out criticalBreakdown);
            ArmorFragmentsPlugin.Log.LogInfo(
                tag + "Ricochet check: angle " + angle.ToString("F0") + " deg, critical " +
                Mathf.Min(armorTolerated, 90f).ToString("F1") + " deg (" + criticalBreakdown + ")");

            if (ArmorFragmentsPlugin.MaxRicochets.Value > 0 && angle >= armorTolerated)
            {
                // Make the normal face the shell, then mirror the direction around it.
                Vector3 n = Vector3.Dot(normal, direction) > 0f ? -normal : normal;
                Vector3 reflected = Vector3.Reflect(direction, n).normalized;

                // The ricocheted ray starts at the impact point.
                RayPath path = new RayPath();
                path.Add(point, RayKind.None);

                // The armor bounce counts as the ray's first ricochet.
                // What the shell keeps after the bounce; the rest stays in the armor.
                float energyOut = EnergyModel.RicochetEnergyOut(shell.Energy, angle);
                Slug bounced = new Slug
                {
                    Energy = energyOut,
                    Mass = Mathf.Max(shell.Mass, 0.000001f),
                    AreaM2 = shell.AreaCm2 * 0.0001f,
                };
                TraceResult r = TraceRay(path, point + n * SurfaceOffset, reflected, 0f, 1,
                                         null, applyDamage, mask, leadTime, bounced);
                if (draw) RayDrawer.Animate(path, leadTime);

                LastImpact.Absorbed = shell.Energy - energyOut;
                LastImpact.Residual = energyOut;

                LastImpact.Record(tag + "Armor ricochet (single ray)", angle, Mathf.Min(armorTolerated, 90f),
                                  r.HitPart ? 1 : 0, 1 + r.Ricochets, r.Damage);

                ArmorFragmentsPlugin.Log.LogInfo(
                    tag + "Armor ricochet: angle " + angle.ToString("F0") + " >= critical " +
                    armorTolerated.ToString("F0") + " deg, shell keeps " + EnergyModel.Kilojoules(energyOut) +
                    " of " + EnergyModel.Kilojoules(shell.Energy) + ". Single ray: " +
                    (r.HitPart ? "hit a part" : "hit no part") + ", extra ricochets " +
                    r.Ricochets + ", total damage " + r.Damage.ToString("F1") +
                    (applyDamage ? "" : " (not applied)"));
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
                return ImpactOutcome.Blocked;
            }
            LastImpact.Residual = shellEnergy - absorbed;

            // 3) It penetrates. The plate's brittleness decides how much of the leftover energy becomes
            //    fragments; the cloud decides where they go, how heavy each is, and how much each carries.
            CloudSummary cloud;
            FragmentSpec[] fragments = FragmentCloud.Generate(
                fragmentCount, armorTag, shell, direction, normal, angle, shellEnergy - absorbed,
                absorbed, effectiveThickness, ArmorFragmentsPlugin.SimpleFragments.Value, out cloud);
            LastImpact.SetCloud(cloud);

            int partsHit = 0;
            int ricochets = 0;
            float totalDamage = 0f;

            for (int i = 0; i < fragments.Length; i++)
            {
                RayPath path = new RayPath();
                path.Add(point, RayKind.None);

                // The fragment flies with its own energy, mass and size.
                Slug slug = new Slug
                {
                    Energy = fragments[i].Energy,
                    Mass = fragments[i].Mass,
                    AreaM2 = fragments[i].AreaCm2 * 0.0001f,
                };
                TraceResult r = TraceRay(path, point, fragments[i].Direction, 0f, 0, armor, applyDamage,
                                         mask, leadTime, slug);
                if (draw) RayDrawer.Animate(path, leadTime);

                if (r.HitPart) partsHit++;
                ricochets += r.Ricochets;
                totalDamage += r.Damage;
            }

            if (fragments.Length == 0)
                ArmorFragmentsPlugin.Log.LogInfo(tag + "No fragments: too little energy left over after the armor (" +
                                                 EnergyModel.Kilojoules(shellEnergy - absorbed) + ")");
            else
                ArmorFragmentsPlugin.Log.LogInfo(
                    tag + "Fragment cloud: " + fragments.Length + " fragments, " + EnergyModel.Kilojoules(cloud.TotalEnergy) +
                    " and " + (cloud.TotalMass * 1000f).ToString("F0") + " g in total, speed ~" + cloud.Speed.ToString("F0") +
                    " m/s, spread " + cloud.SigmaX.ToString("F1") + " x " + cloud.SigmaY.ToString("F1") +
                    " deg, axis leaning " + cloud.AxisTiltDegrees.ToString("F1") + " deg");

            LastImpact.Record(tag + "Penetrated (" + comparison + "), " + fragments.Length + " rays", angle, Mathf.Min(armorTolerated, 90f),
                              partsHit, ricochets, totalDamage);

            ArmorFragmentsPlugin.Log.LogInfo(
                tag + "Fragments: " + partsHit + " of " + fragments.Length + " rays hit a part, " +
                ricochets + " ricochets, total damage " + totalDamage.ToString("F1") +
                (applyDamage ? "" : " (not applied)"));
            return ImpactOutcome.Penetrated;
        }

        // What a flying piece of metal carries: its energy, mass and cross-section.
        private struct Slug
        {
            public float Energy;   // joules
            public float Mass;     // kilograms
            public float AreaM2;   // square meters
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
        //   bounces:  ricochets this ray already had
        //   traveled: distance already covered, counts toward MaxRange
        //   timeOffset: seconds from the start of the shot until this ray starts moving
        private static TraceResult TraceRay(RayPath path, Vector3 origin, Vector3 dir,
                                            float traveled, int bounces, PartScript ignore,
                                            bool applyDamage, int mask, float timeOffset, Slug slug)
        {
            TraceResult result = default;
            int maxBounces = ArmorFragmentsPlugin.MaxRicochets.Value;
            int maxParts = ArmorFragmentsPlugin.MaxPartsPerFragment.Value;
            float tolerated = ToleratedAngle(
                ArmorFragmentsPlugin.FragmentRicochetAngle.Value,
                ArmorFragmentsPlugin.FragmentRicochetMultiplier.Value);
            float floor = Mathf.Max(1f, slug.Energy * EnergyModel.StopFraction);
            RayKind stopMarker = DamageSink != null ? RayKind.Landed : RayKind.Hit;

            int partsCrossed = 0;
            PartScript skip = ignore;
            float time = timeOffset;   // seconds since the shot started, at the head of this ray

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
                        Deposit(part, slug.Energy - kept, hit.point, dir, applyDamage, time, ref result);

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

                float density = PartPhysics.Density(part);
                float energyIn = slug.Energy;
                float energyOut = PartPhysics.EnergyAfter(energyIn, slug.Mass, slug.AreaM2, density, chord);
                result.HitPart = true;
                partsCrossed++;

                // The drawn ray reaches the part's surface at the speed it flies with.
                path.Add(origin + dir * entry, RayKind.None, speed);

                if (energyOut <= floor)
                {
                    // It stops inside the part and leaves everything there; it slows to a halt on the way.
                    Deposit(part, energyIn, hit.point, dir, applyDamage, time, ref result);
                    float into = PartPhysics.StopDistance(energyIn, floor, slug.Mass, slug.AreaM2, density, chord);
                    path.Add(origin + dir * (entry + into), stopMarker, speed * 0.5f);
                    path.Final = RayKind.Hit;
                    break;
                }

                Deposit(part, energyIn - energyOut, hit.point, dir, applyDamage, time, ref result);
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
            if (tags.Armor != null) return;

            float damage = joules / 1000f * ArmorFragmentsPlugin.DamagePerKiloJoule.Value;

            if (applyDamage) ApplyDamage(part, damage, point, dir);
            result.Damage += damage;   // counted even when not applied: "would-be" damage

            if (DamageSink != null)
            {
                // 'arrival' is when the drawn ray's head reaches this point.
                DamageRecord record;
                if (!DamageSink.TryGetValue(part, out record))
                    record.FirstTime = arrival;
                record.Total += damage;
                record.FirstTime = Mathf.Min(record.FirstTime, arrival);
                DamageSink[part] = record;
            }
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
