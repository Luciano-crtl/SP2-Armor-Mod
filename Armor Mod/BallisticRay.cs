using System;
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    // A straight "ballistic ray": it flies through every part that is NOT armor and stops at the
    // first armor part, or at anything that is not a part (the ground, for example).
    // The simulated shot uses it from the camera; a real shell that hit a non-armor part
    // uses it to carry on from the point where the shell stopped.
    internal static class BallisticRay
    {
        // Energy a ray keeps when it crosses a part, or loses and stops.
        public struct Crossed
        {
            public PartScript Part;
            public float Dissipated;     // joules left in the part
            public float Distance;       // how far along the ray it was entered
        }

        public struct Result
        {
            public PartScript Part;          // the armor part that stopped the ray (null if none)
            public ArmorTag Armor;           // its tag
            public RaycastHit Hit;           // where the ray met it
            public int PassedThrough;        // non-armor parts it flew through
            public string StoppedBy;         // what ended the ray if it was not armor
            public bool HasFirstPoint;
            public Vector3 FirstPoint;       // where it first touched anything
            public float Energy;             // what the shell still carries when it gets there
            public bool RanOutOfEnergy;      // it stopped inside a part
            public Vector3 StopPoint;        // ... here
            public readonly List<Crossed> Crossings;

            public Result(bool unused)
            {
                Part = null;
                Armor = null;
                Hit = default(RaycastHit);
                PassedThrough = 0;
                StoppedBy = null;
                HasFirstPoint = false;
                FirstPoint = Vector3.zero;
                Energy = 0f;
                RanOutOfEnergy = false;
                StopPoint = Vector3.zero;
                Crossings = new List<Crossed>();
            }
        }

        // The shell as a ray: it flies through every part that is NOT armor, paying energy in each (the
        // same law the fragments follow), and stops at the first armor part, at anything that is not a
        // part (the ground, for example), or when its energy runs out.
        //   enteredPart: the part a real shell has just stopped in (its ray starts inside it)
        public static Result Trace(Vector3 origin, Vector3 direction, float maxDistance,
                                   PartScript enteredPart, ShellInfo shell)
        {
            Result result = new Result(true);
            PartPhysics.BeginShot();

            float energy = shell.Energy;
            float floor = Mathf.Max(1f, energy * EnergyModel.StopFraction);
            float areaM2 = shell.AreaCm2 * 0.0001f;
            float mass = Mathf.Max(shell.Mass, 0.000001f);

            // A real shell has already hit the first part: its ray starts inside it and pays for it.
            if (enteredPart != null)
            {
                if (Cross(ref result, ref energy, floor, mass, areaM2, enteredPart, origin, direction, maxDistance, 0f))
                    return Finish(result, energy);
            }

            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            RaycastHit[] hits = Physics.RaycastAll(
                origin, direction, maxDistance, mask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));

            HashSet<PartScript> done = new HashSet<PartScript>();

            foreach (RaycastHit candidate in hits)
            {
                Collider collider = candidate.collider;
                if (collider.GetComponentInParent<CannonProjectileScript>() != null) continue;

                PartScript part = collider.GetComponentInParent<PartScript>();
                if (part != null && part == enteredPart) continue;
                if (part != null && done.Contains(part)) continue;   // another collider of a part already crossed

                if (!result.HasFirstPoint)
                {
                    result.HasFirstPoint = true;
                    result.FirstPoint = candidate.point;
                }

                if (part == null)
                {
                    result.StoppedBy = "'" + collider.name + "' at " +
                                       candidate.distance.ToString("F0") + " m (not a part)";
                    break;
                }

                ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;
                if (armor != null)
                {
                    result.Part = part;
                    result.Armor = armor;
                    result.Hit = candidate;
                    break;
                }

                // No [ARM] tag: the ray goes through, leaving energy behind.
                done.Add(part);
                if (Cross(ref result, ref energy, floor, mass, areaM2, part, origin, direction,
                          maxDistance, candidate.distance))
                    break;
            }

            return Finish(result, energy);
        }

        // Crosses a part. Returns true if the ray ended inside it.
        private static bool Cross(ref Result result, ref float energy, float floor, float mass, float areaM2,
                                  PartScript part, Vector3 origin, Vector3 direction, float maxDistance,
                                  float enteredAt)
        {
            float entry;
            float exit;
            float chord = PartPhysics.Chord(part, origin, direction, maxDistance, out entry, out exit);
            if (chord <= 0f) return false;   // the line only grazed it

            float density = PartPhysics.Density(part);
            float after = PartPhysics.EnergyAfter(energy, mass, areaM2, density, chord);

            Crossed crossing = new Crossed { Part = part, Distance = entry };
            result.PassedThrough++;

            if (after <= floor)
            {
                crossing.Dissipated = energy;
                result.Crossings.Add(crossing);

                float into = PartPhysics.StopDistance(energy, floor, mass, areaM2, density, chord);
                result.RanOutOfEnergy = true;
                result.StopPoint = origin + direction * (entry + into);
                result.StoppedBy = "ran out of energy inside '" + part.Part.Name + "'";
                energy = 0f;
                return true;
            }

            crossing.Dissipated = energy - after;
            result.Crossings.Add(crossing);
            energy = after;
            return false;
        }

        private static Result Finish(Result result, float energy)
        {
            result.Energy = energy;
            return result;
        }

        // Draws the initial ray (the shell) along 'points', thicker and faster than the fragment
        // rays, with an impact bubble at the last point. Returns how many seconds it takes to
        // arrive: the fragments are drawn to leave after that.
        public static float DrawInitialRay(Vector3[] points)
        {
            float length = 0f;
            for (int i = 1; i < points.Length; i++)
                length += Vector3.Distance(points[i - 1], points[i]);

            float duration = length / Mathf.Max(ArmorFragmentsPlugin.InitialRaySpeed.Value, 0.1f);
            if (!ArmorFragmentsPlugin.DrawRays.Value) return duration;

            RayPath path = new RayPath { Final = RayKind.ArmorImpact };
            for (int i = 0; i < points.Length; i++)
                path.Add(points[i], i == points.Length - 1 ? RayKind.ArmorImpact : RayKind.None);
            RayDrawer.Animate(path, 0f, true);
            return duration;
        }
    }
}
