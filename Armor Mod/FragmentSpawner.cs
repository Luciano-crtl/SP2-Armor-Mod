using System.Collections.Generic;
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
        public const float ShellLeadIn = 4f;

        private const float ConeHalfAngle = 35f;   // spread, in degrees
        private const float MaxRange = 30f;        // total path length of one ray, in meters
        private const float SurfaceOffset = 0.02f; // pushes a ricochet start point off the surface

        private static readonly RaycastHit[] Hits = new RaycastHit[64];

        // While not null, the damage every fragment WOULD do is added up per part here,
        // together with the moment the first fragment touches the part.
        // The shot simulator sets it for one shot, then clears it.
        public static Dictionary<PartScript, DamageRecord> DamageSink;

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
                leadTime = BallisticRay.DrawInitialRay(new[] { point - direction * ShellLeadIn, point });
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
                TraceResult r = TraceRay(path, point + n * SurfaceOffset, reflected, 0f, 1,
                                         null, applyDamage, mask, leadTime);
                if (draw) RayDrawer.Animate(path, leadTime);

                // What the shell keeps after the bounce; the rest stays in the armor.
                float energyOut = EnergyModel.RicochetEnergyOut(shell.Energy, angle);
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

            // 3) It penetrates: fan of fragment rays behind the armor.
            int partsHit = 0;
            int ricochets = 0;
            float totalDamage = 0f;

            for (int i = 0; i < fragmentCount; i++)
            {
                Vector3 dir = RandomInCone(direction, ConeHalfAngle);

                RayPath path = new RayPath();
                path.Add(point, RayKind.None);
                TraceResult r = TraceRay(path, point, dir, 0f, 0, armor, applyDamage, mask, leadTime);
                if (draw) RayDrawer.Animate(path, leadTime);

                if (r.HitPart) partsHit++;
                ricochets += r.Ricochets;
                totalDamage += r.Damage;
            }

            LastImpact.Record(tag + "Penetrated (" + comparison + "), " + fragmentCount + " rays", angle, Mathf.Min(armorTolerated, 90f),
                              partsHit, ricochets, totalDamage);

            ArmorFragmentsPlugin.Log.LogInfo(
                tag + "Fragments: " + partsHit + " of " + fragmentCount + " rays hit a part, " +
                ricochets + " ricochets, total damage " + totalDamage.ToString("F1") +
                (applyDamage ? "" : " (not applied)"));
            return ImpactOutcome.Penetrated;
        }

        // Follows one ray and records its route in 'path'. It stops at the first thing it
        // hits, unless the hit is grazing enough to ricochet; then it bounces and keeps
        // going. Each bounce consumes one of MaxRicochets, so the loop always ends.
        //   origin:   where the first segment starts
        //   ignore:   a part to skip on the first segment only (the armor itself)
        //   bounces:  ricochets this ray already had
        //   traveled: distance already covered, counts toward MaxRange and damage falloff
        //   timeOffset: seconds from the start of the shot until this ray starts moving; with the
        //               distance flown it gives the moment each part is touched (for the X-ray view)
        private static TraceResult TraceRay(RayPath path, Vector3 origin, Vector3 dir,
                                            float traveled, int bounces, PartScript ignore,
                                            bool applyDamage, int mask, float timeOffset)
        {
            TraceResult result = default;
            int maxBounces = ArmorFragmentsPlugin.MaxRicochets.Value;
            float tolerated = ToleratedAngle(
                ArmorFragmentsPlugin.FragmentRicochetAngle.Value,
                ArmorFragmentsPlugin.FragmentRicochetMultiplier.Value);

            while (true)
            {
                float remaining = MaxRange - traveled;
                if (remaining <= 0.01f) { path.Final = RayKind.Miss; break; }

                int count = Physics.RaycastNonAlloc(
                    origin, dir, Hits, remaining, mask, QueryTriggerInteraction.Ignore);

                // Closest hit of ANY object, except shells and the ignored part.
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int h = 0; h < count; h++)
                {
                    Collider c = Hits[h].collider;
                    if (c.GetComponentInParent<CannonProjectileScript>() != null) continue;
                    if (ignore != null && c.GetComponentInParent<PartScript>() == ignore) continue;
                    if (Hits[h].distance < bestDistance)
                    {
                        bestDistance = Hits[h].distance;
                        best = h;
                    }
                }

                if (best < 0)
                {
                    path.Add(origin + dir * remaining, RayKind.None);   // fades out in the air
                    path.Final = RayKind.Miss;
                    break;
                }

                RaycastHit hit = Hits[best];   // copy: Hits is reused by the next ray
                traveled += hit.distance;
                ignore = null;                 // only the first segment skips the armor

                float angle = ImpactAngle(dir, hit.normal);
                if (bounces < maxBounces && angle >= tolerated)
                {
                    path.Add(hit.point, RayKind.Ricochet);
                    dir = Vector3.Reflect(dir, hit.normal).normalized;
                    origin = hit.point + hit.normal * SurfaceOffset;
                    bounces++;
                    result.Ricochets++;
                    continue;
                }

                PartScript part = hit.collider.GetComponentInParent<PartScript>();
                if (part != null)
                {
                    path.Add(hit.point, RayKind.Hit);
                    path.Final = RayKind.Hit;
                    result.HitPart = true;

                    float falloff = 1f - traveled / MaxRange;
                    float damage = ArmorFragmentsPlugin.DamagePerFragment.Value * falloff;

                    // A part marked [MOD d=...] ignores fragments weaker than that.
                    ModuleTag module = TagCodec.Parse(part.Part.Name).Module;
                    if (module == null || damage >= module.MinDamage)
                    {
                        if (applyDamage) part.OnDamaged(null, damage, hit.point, dir);
                        result.Damage += damage;   // counted even when not applied: "would-be" damage

                        if (DamageSink != null)
                        {
                            // When the drawn ray's head reaches this point (same speed as drawn).
                            float arrival = timeOffset + traveled / Mathf.Max(ArmorFragmentsPlugin.RaySpeed.Value, 0.1f);

                            DamageRecord record;
                            if (!DamageSink.TryGetValue(part, out record))
                                record.FirstTime = arrival;
                            record.Total += damage;
                            record.FirstTime = Mathf.Min(record.FirstTime, arrival);
                            DamageSink[part] = record;
                        }
                    }
                }
                else
                {
                    path.Add(hit.point, RayKind.Miss);
                    path.Final = RayKind.Miss;
                }
                break;
            }

            return result;
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

        private static Vector3 RandomInCone(Vector3 axis, float halfAngle)
        {
            Vector3 side = Vector3.Cross(axis, Random.onUnitSphere).normalized;
            return (Quaternion.AngleAxis(Random.Range(0f, halfAngle), side) * axis).normalized;
        }
    }
}
