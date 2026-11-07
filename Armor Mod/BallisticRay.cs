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
        public struct Result
        {
            public PartScript Part;          // the armor part that stopped the ray (null if none)
            public ArmorTag Armor;           // its tag
            public RaycastHit Hit;           // where the ray met it
            public int PassedThrough;        // non-armor parts it flew through
            public string StoppedBy;         // what ended the ray if it was not armor
            public bool HasFirstPoint;
            public Vector3 FirstPoint;       // where it first touched anything
        }

        // ignore: a part to skip completely (the part a real shell has just stopped in).
        public static Result Trace(Vector3 origin, Vector3 direction, float maxDistance, PartScript ignore)
        {
            Result result = new Result();

            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            RaycastHit[] hits = Physics.RaycastAll(
                origin, direction, maxDistance, mask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));

            HashSet<PartScript> passed = new HashSet<PartScript>();

            foreach (RaycastHit candidate in hits)
            {
                Collider collider = candidate.collider;
                if (collider.GetComponentInParent<CannonProjectileScript>() != null) continue;

                PartScript part = collider.GetComponentInParent<PartScript>();
                if (part != null && part == ignore) continue;

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
                if (armor == null)
                {
                    passed.Add(part);   // no [ARM] tag: the ray goes through
                    continue;
                }

                result.Part = part;
                result.Armor = armor;
                result.Hit = candidate;
                break;
            }

            result.PassedThrough = passed.Count;
            return result;
        }

        // Draws the ray's flight from 'from' to the armor it met (only if it passed through something,
        // otherwise the armor impact's own shell track already shows it).
        public static void DrawTrack(Vector3 from, Result result)
        {
            if (result.Armor == null || result.PassedThrough == 0) return;
            if (!ArmorFragmentsPlugin.DrawRays.Value) return;

            RayPath path = new RayPath { Final = RayKind.ArmorImpact };
            path.Add(from, RayKind.None);
            path.Add(result.Hit.point, RayKind.None);
            RayDrawer.Animate(path, 0f);
        }
    }
}
