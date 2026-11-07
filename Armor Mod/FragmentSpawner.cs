using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    internal static class FragmentSpawner
    {
        private const float ConeHalfAngle = 35f;   // spread, in degrees
        private const float MaxRange = 30f;        // total path length of one ray, in meters
        private const float SurfaceOffset = 0.02f; // pushes a ricochet start point off the surface
        private const float ShellLeadIn = 4f;      // length of the drawn shell track before the impact

        private static readonly RaycastHit[] Hits = new RaycastHit[64];

        // While not null, the damage every fragment WOULD do is added up per part here.
        // The shot simulator sets it for one shot, then clears it.
        public static Dictionary<PartScript, float> DamageSink;

        private struct TraceResult
        {
            public bool HitPart;
            public int Ricochets;
            public float Damage;
        }

        // angle: 0 = head-on, 90 = grazing (same convention as the hit log).
        public static void Spawn(PartScript armor, ArmorTag armorTag, CannonTag cannonTag,
                                 Vector3 point, Vector3 direction,
                                 Vector3 normal, float angle, bool applyDamage,
                                 bool simulated = false)
        {
            string tag = simulated ? "[SIM] " : "";

            // How many fragment rays a penetrating shell makes: from the cannon's [CAN f=] tag.
            int fragmentCount = Mathf.Clamp(
                cannonTag != null ? cannonTag.Fragments : ArmorFragmentsPlugin.DefaultFragmentCount.Value,
                1, 500);
            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            bool draw = ArmorFragmentsPlugin.DrawRays.Value;

            // The drawn shell track: from behind the impact point up to it.
            Vector3 shellStart = point - direction * ShellLeadIn;

            // 1) Grazing hit on the armor: no penetration, one ricocheted ray.
            float armorTolerated = ToleratedAngle(
                ArmorFragmentsPlugin.ArmorRicochetAngle.Value,
                ArmorFragmentsPlugin.ArmorRicochetMultiplier.Value);

            if (ArmorFragmentsPlugin.MaxRicochets.Value > 0 && angle >= armorTolerated)
            {
                // Make the normal face the shell, then mirror the direction around it.
                Vector3 n = Vector3.Dot(normal, direction) > 0f ? -normal : normal;
                Vector3 reflected = Vector3.Reflect(direction, n).normalized;

                // One path: shell -> armor impact -> ricochets.
                RayPath path = new RayPath();
                path.Add(shellStart, RayKind.None);
                path.Add(point, RayKind.ArmorImpact);

                // The armor bounce counts as the ray's first ricochet.
                TraceResult r = TraceRay(path, point + n * SurfaceOffset, reflected, 0f, 1,
                                         null, applyDamage, mask);
                if (draw) RayDrawer.Animate(path, 0f);

                LastImpact.Record(tag + "Armor ricochet (single ray)", angle, armorTolerated,
                                  r.HitPart ? 1 : 0, 1 + r.Ricochets, r.Damage);

                ArmorFragmentsPlugin.Log.LogInfo(
                    tag + "Armor ricochet: angle " + angle.ToString("F0") + " >= tolerated " +
                    armorTolerated.ToString("F0") + " deg. Single ray: " +
                    (r.HitPart ? "hit a part" : "hit no part") + ", extra ricochets " +
                    r.Ricochets + ", total damage " + r.Damage.ToString("F1") +
                    (applyDamage ? "" : " (not applied)"));
                return;
            }

            // 2) Can the shell defeat the armor? Compare its penetration with the armor's
            //    effective thickness, which grows as the hit gets more slanted.
            float penetration = cannonTag != null
                ? cannonTag.Penetration
                : ArmorFragmentsPlugin.DefaultPenetration.Value;
            float slant = Mathf.Max(Mathf.Cos(angle * Mathf.Deg2Rad), 0.1f);
            float resistance = armorTag.Thickness * (armorTag.Hardness / 100f) / slant;
            string comparison = penetration.ToString("F0") + " vs " + resistance.ToString("F0") + " mm";

            if (penetration < resistance)
            {
                if (draw)
                {
                    RayPath blockedShell = new RayPath { Final = RayKind.ArmorImpact };
                    blockedShell.Add(shellStart, RayKind.None);
                    blockedShell.Add(point, RayKind.ArmorImpact);
                    RayDrawer.Animate(blockedShell, 0f);
                }

                LastImpact.Record(tag + "Blocked (penetration " + comparison + ")", angle, armorTolerated, 0, 0, 0f);
                ArmorFragmentsPlugin.Log.LogInfo(tag + "Shell blocked by armor: penetration " + comparison);
                return;
            }

            // 3) It penetrates: fan of fragment rays behind the armor.
            float rayDelay = 0f;
            if (draw)
            {
                RayPath shell = new RayPath { Final = RayKind.ArmorImpact };
                shell.Add(shellStart, RayKind.None);
                shell.Add(point, RayKind.ArmorImpact);
                RayDrawer.Animate(shell, 0f);

                // Fragments leave once the shell has arrived.
                rayDelay = ShellLeadIn / Mathf.Max(ArmorFragmentsPlugin.RaySpeed.Value, 0.1f);
            }

            int partsHit = 0;
            int ricochets = 0;
            float totalDamage = 0f;

            for (int i = 0; i < fragmentCount; i++)
            {
                Vector3 dir = RandomInCone(direction, ConeHalfAngle);

                RayPath path = new RayPath();
                path.Add(point, RayKind.None);
                TraceResult r = TraceRay(path, point, dir, 0f, 0, armor, applyDamage, mask);
                if (draw) RayDrawer.Animate(path, rayDelay);

                if (r.HitPart) partsHit++;
                ricochets += r.Ricochets;
                totalDamage += r.Damage;
            }

            LastImpact.Record(tag + "Penetrated (" + comparison + "), " + fragmentCount + " rays", angle, armorTolerated,
                              partsHit, ricochets, totalDamage);

            ArmorFragmentsPlugin.Log.LogInfo(
                tag + "Fragments: " + partsHit + " of " + fragmentCount + " rays hit a part, " +
                ricochets + " ricochets, total damage " + totalDamage.ToString("F1") +
                (applyDamage ? "" : " (not applied)"));
        }

        // Follows one ray and records its route in 'path'. It stops at the first thing it
        // hits, unless the hit is grazing enough to ricochet; then it bounces and keeps
        // going. Each bounce consumes one of MaxRicochets, so the loop always ends.
        //   origin:   where the first segment starts
        //   ignore:   a part to skip on the first segment only (the armor itself)
        //   bounces:  ricochets this ray already had
        //   traveled: distance already covered, counts toward MaxRange and damage falloff
        private static TraceResult TraceRay(RayPath path, Vector3 origin, Vector3 dir,
                                            float traveled, int bounces, PartScript ignore,
                                            bool applyDamage, int mask)
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
                            float sum;
                            DamageSink.TryGetValue(part, out sum);
                            DamageSink[part] = sum + damage;
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
