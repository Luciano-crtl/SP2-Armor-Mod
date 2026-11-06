using Assets.Scripts.Craft.Parts;
using UnityEngine;

namespace ArmorFragments
{
    internal static class FragmentSpawner
    {
        private const int FragmentCount = 20;      // rays per shell hit
        private const float ConeHalfAngle = 35f;   // spread, in degrees
        private const float MaxRange = 30f;         // meters
        private const float DamagePerFragment = 5f; // tune this by testing

        private static readonly RaycastHit[] Hits = new RaycastHit[32];

        public static void Spawn(PartScript armor, Vector3 point, Vector3 direction, bool applyDamage)
        {
            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            int partsHit = 0;

            for (int i = 0; i < FragmentCount; i++)
            {
                Vector3 dir = RandomInCone(direction, ConeHalfAngle);
                int count = Physics.RaycastNonAlloc(
                    point, dir, Hits, MaxRange, mask, QueryTriggerInteraction.Ignore);

                // Take the closest hit that belongs to a different part.
                float best = float.MaxValue;
                PartScript target = null;
                Vector3 targetPoint = point;
                for (int h = 0; h < count; h++)
                {
                    PartScript p = Hits[h].collider.GetComponentInParent<PartScript>();
                    if (p == null || p == armor) continue;
                    if (Hits[h].distance < best)
                    {
                        best = Hits[h].distance;
                        target = p;
                        targetPoint = Hits[h].point;
                    }
                }
                if (target == null) continue;

                partsHit++;
                if (applyDamage)
                {
                    float falloff = 1f - best / MaxRange;
                    target.OnDamaged(null, DamagePerFragment * falloff, targetPoint, dir);
                }
            }

            ArmorFragmentsPlugin.Log.LogInfo(
                "Fragments: " + partsHit + " of " + FragmentCount + " rays hit a part");
        }

        private static Vector3 RandomInCone(Vector3 axis, float halfAngle)
        {
            Vector3 side = Vector3.Cross(axis, Random.onUnitSphere).normalized;
            return (Quaternion.AngleAxis(Random.Range(0f, halfAngle), side) * axis).normalized;
        }
    }
}