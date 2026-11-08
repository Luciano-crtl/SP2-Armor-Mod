using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    // What a part is made of, as far as stopping a flying piece of metal goes.
    //  - Chord: how far a ray travels INSIDE the part (method A: for each collider of the part, a ray
    //    forward gives where it enters and a ray cast backward from beyond gives where it leaves;
    //    the stretches of the several colliders of a part are joined so none is counted twice).
    //  - Density: the part's mass over its volume. The volume is estimated from its colliders.
    //  - Loss: how much energy a fragment leaves behind in a part.
    internal static class PartPhysics
    {
        // The game stores every mass at 1/100 of its real value.
        private const float GameMassScale = 0.01f;

        private static readonly Dictionary<PartScript, Collider[]> CollidersOf = new Dictionary<PartScript, Collider[]>();
        private static readonly Dictionary<PartScript, float> DensityOf = new Dictionary<PartScript, float>();

        // Parts and their colliders do not change during one shot: results are remembered until the
        // next one starts.
        public static void BeginShot()
        {
            CollidersOf.Clear();
            DensityOf.Clear();
        }

        public static Collider[] Colliders(PartScript part)
        {
            Collider[] colliders;
            if (CollidersOf.TryGetValue(part, out colliders)) return colliders;

            List<Collider> list = new List<Collider>();
            foreach (Collider collider in part.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger) continue;
                if (collider.GetComponentInParent<PartScript>() != part) continue;
                if (collider.GetComponentInParent<CannonProjectileScript>() != null) continue;
                list.Add(collider);
            }
            colliders = list.ToArray();
            CollidersOf[part] = colliders;
            return colliders;
        }

        // Distance (along 'direction' from 'origin') at which the ray enters the part and at which it
        // leaves it, and the total length inside (0 if it does not go through).
        public static float Chord(PartScript part, Vector3 origin, Vector3 direction, float maxDistance,
                                  out float entryDistance, out float exitDistance)
        {
            entryDistance = 0f;
            exitDistance = 0f;

            List<Vector2> stretches = new List<Vector2>();   // (enter, leave)
            Ray forward = new Ray(origin, direction);

            foreach (Collider collider in Colliders(part))
            {
                // Leaving: cast backward from beyond the collider, toward the origin.
                float length = collider.bounds.size.magnitude + 0.05f;
                Ray back = new Ray(origin + direction * (maxDistance + length), -direction);
                RaycastHit backHit;
                if (!collider.Raycast(back, out backHit, maxDistance + length + 0.05f)) continue;
                float leave = maxDistance + length - backHit.distance;
                if (leave <= 0f) continue;                   // the collider is behind the origin

                // Entering: forward. No forward hit while there is a way out means we start inside it.
                RaycastHit frontHit;
                float enter = collider.Raycast(forward, out frontHit, maxDistance + length) ? frontHit.distance : 0f;
                if (leave > enter) stretches.Add(new Vector2(enter, leave));
            }

            if (stretches.Count == 0) return 0f;

            stretches.Sort((a, b) => a.x.CompareTo(b.x));
            entryDistance = stretches[0].x;
            float total = 0f;
            float reached = float.NegativeInfinity;
            foreach (Vector2 stretch in stretches)
            {
                float start = Mathf.Max(stretch.x, reached);
                if (stretch.y > start) total += stretch.y - start;
                reached = Mathf.Max(reached, stretch.y);
                exitDistance = Mathf.Max(exitDistance, stretch.y);
            }
            return total;
        }

        // kg per cubic meter, from the part's mass and the volume of its colliders.
        public static float Density(PartScript part)
        {
            float density;
            if (DensityOf.TryGetValue(part, out density)) return density;

            float mass = part.Part.LoadedMass / GameMassScale;
            float volume = 0f;
            foreach (Collider collider in Colliders(part)) volume += ColliderVolume(collider);

            density = mass / Mathf.Max(volume, 0.001f) * ArmorFragmentsPlugin.DensityModifier.Value;
            DensityOf[part] = density;
            return density;
        }

        // Energy a flying piece of metal has left after 'chord' meters inside a material of this density.
        // The piece slows down as it digs: drag proportional to its speed, or (option) a constant force.
        // Both depend on k = density x area x distance / mass:
        //   proportional drag:  E_out = E_in x e^(-k)
        //   constant force:     E_out = E_in x (1 - k)
        public static float EnergyAfter(float energy, float mass, float areaM2, float density, float chord)
        {
            float k = density * areaM2 * chord / Mathf.Max(mass, 0.000001f);
            return ArmorFragmentsPlugin.ConstantForceLoss.Value
                ? energy * Mathf.Max(0f, 1f - k)
                : energy * Mathf.Exp(-Mathf.Min(k, 80f));
        }

        // How far into the chord the piece gets before its energy drops to 'floor' (it stops there).
        public static float StopDistance(float energy, float floor, float mass, float areaM2, float density, float chord)
        {
            float perMeter = density * areaM2 / Mathf.Max(mass, 0.000001f);   // k per meter
            if (perMeter <= 0f) return chord;

            float distance = ArmorFragmentsPlugin.ConstantForceLoss.Value
                ? (1f - floor / Mathf.Max(energy, 0.000001f)) / perMeter
                : Mathf.Log(Mathf.Max(energy, 0.000001f) / floor) / perMeter;
            return Mathf.Clamp(distance, 0f, chord);
        }

        // Rough volume in cubic meters. Boxes and spheres are exact; a convex mesh collider is taken as
        // 60% of its bounding box, and anything else as half of the world bounding box.
        private static float ColliderVolume(Collider collider)
        {
            Vector3 scale = collider.transform.lossyScale;
            scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));

            BoxCollider box = collider as BoxCollider;
            if (box != null)
                return box.size.x * scale.x * box.size.y * scale.y * box.size.z * scale.z;

            SphereCollider sphere = collider as SphereCollider;
            if (sphere != null)
            {
                float radius = sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                return 4f / 3f * Mathf.PI * radius * radius * radius;
            }

            MeshCollider mesh = collider as MeshCollider;
            if (mesh != null && mesh.sharedMesh != null)
            {
                Vector3 size = mesh.sharedMesh.bounds.size;
                return 0.6f * size.x * scale.x * size.y * scale.y * size.z * scale.z;
            }

            Vector3 world = collider.bounds.size;
            return 0.5f * world.x * world.y * world.z;
        }
    }
}
