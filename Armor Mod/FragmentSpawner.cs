using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    internal static class FragmentSpawner
    {
        private const int FragmentCount = 20;      // rays per shell hit
        private const float ConeHalfAngle = 35f;   // spread, in degrees
        private const float MaxRange = 30f;        // total path length of one ray, in meters
        private const float SurfaceOffset = 0.02f; // pushes a ricochet start point off the surface

        private static readonly RaycastHit[] Hits = new RaycastHit[64];

        private struct TraceResult
        {
            public bool HitPart;
            public int Ricochets;
            public float Damage;
        }

        // angle: 0 = head-on, 90 = grazing (same convention as the hit log).
        public static void Spawn(PartScript armor, Vector3 point, Vector3 direction,
                                 Vector3 normal, float angle, bool applyDamage)
        {
            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            bool draw = ArmorFragmentsPlugin.DrawRays.Value;

            // 1) Grazing hit on the armor: no penetration, one ricocheted ray.
            float armorTolerated = ToleratedAngle(
                ArmorFragmentsPlugin.ArmorRicochetAngle.Value,
                ArmorFragmentsPlugin.ArmorRicochetMultiplier.Value);

            if (ArmorFragmentsPlugin.MaxRicochets.Value > 0 && angle >= armorTolerated)
            {
                // Make the normal face the shell, then mirror the direction around it.
                Vector3 n = Vector3.Dot(normal, direction) > 0f ? -normal : normal;
                Vector3 reflected = Vector3.Reflect(direction, n).normalized;

                // The armor bounce counts as the ray's first ricochet.
                TraceResult r = TraceRay(point + n * SurfaceOffset, reflected, 0f, 1,
                                         null, applyDamage, draw, mask);

                ArmorFragmentsPlugin.Log.LogInfo(
                    "Armor ricochet: angle " + angle.ToString("F0") + " >= tolerated " +
                    armorTolerated.ToString("F0") + " deg. Single ray: " +
                    (r.HitPart ? "hit a part" : "hit no part") + ", extra ricochets " +
                    r.Ricochets + ", total damage " + r.Damage.ToString("F1") +
                    (applyDamage ? "" : " (damage off)"));
                return;
            }

            // 2) Otherwise the shell "penetrates": fan of fragment rays behind the armor.
            int partsHit = 0;
            int ricochets = 0;
            float totalDamage = 0f;

            for (int i = 0; i < FragmentCount; i++)
            {
                Vector3 dir = RandomInCone(direction, ConeHalfAngle);
                TraceResult r = TraceRay(point, dir, 0f, 0, armor, applyDamage, draw, mask);
                if (r.HitPart) partsHit++;
                ricochets += r.Ricochets;
                totalDamage += r.Damage;
            }

            ArmorFragmentsPlugin.Log.LogInfo(
                "Fragments: " + partsHit + " of " + FragmentCount + " rays hit a part, " +
                ricochets + " ricochets, total damage " + totalDamage.ToString("F1") +
                (applyDamage ? "" : " (damage off)"));
        }

        // Follows one ray. It stops at the first thing it hits, unless the hit is
        // grazing enough to ricochet; then it bounces and keeps going. Each bounce
        // consumes one of MaxRicochets, so the loop always ends.
        //   ignore:   a part to skip on the first segment only (the armor itself)
        //   bounces:  ricochets this ray already had
        //   traveled: distance already covered, counts toward MaxRange and damage falloff
        private static TraceResult TraceRay(Vector3 origin, Vector3 dir, float traveled, int bounces,
                                            PartScript ignore, bool applyDamage, bool draw, int mask)
        {
            TraceResult result = default;
            int maxBounces = ArmorFragmentsPlugin.MaxRicochets.Value;
            float tolerated = ToleratedAngle(
                ArmorFragmentsPlugin.FragmentRicochetAngle.Value,
                ArmorFragmentsPlugin.FragmentRicochetMultiplier.Value);

            while (true)
            {
                float remaining = MaxRange - traveled;
                if (remaining <= 0.01f) break;

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
                    if (draw) RayDrawer.Draw(origin, origin + dir * remaining, RayKind.Miss);
                    break;
                }

                RaycastHit hit = Hits[best];   // copy: Hits is reused by the next ray
                traveled += hit.distance;
                ignore = null;                 // only the first segment skips the armor

                float angle = ImpactAngle(dir, hit.normal);
                if (bounces < maxBounces && angle >= tolerated)
                {
                    if (draw) RayDrawer.Draw(origin, hit.point, RayKind.Ricochet);
                    dir = Vector3.Reflect(dir, hit.normal).normalized;
                    origin = hit.point + hit.normal * SurfaceOffset;
                    bounces++;
                    result.Ricochets++;
                    continue;
                }

                PartScript part = hit.collider.GetComponentInParent<PartScript>();
                if (draw) RayDrawer.Draw(origin, hit.point, part != null ? RayKind.Hit : RayKind.Miss);

                if (part != null)
                {
                    result.HitPart = true;
                    if (applyDamage)
                    {
                        float falloff = 1f - traveled / MaxRange;
                        float damage = ArmorFragmentsPlugin.DamagePerFragment.Value * falloff;
                        part.OnDamaged(null, damage, hit.point, dir);
                        result.Damage += damage;
                    }
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
