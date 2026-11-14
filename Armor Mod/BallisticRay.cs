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
            public float Exit;           // ... and left (for a ray that stopped inside: where it stopped)
            public float EnergyIn;       // energy on entering
            public float EnergyOut;      // energy on leaving (0 if it stopped inside)
            public float ArrivalTime;    // seconds after the shot starts that the drawn ray reaches it
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
            public bool HitObstacle;         // it ran into something that is not a part (the ground...)
            public Vector3 ObstaclePoint;    // ... here
            public float ObstacleDistance;   // ... this far along the ray
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
                HitObstacle = false;
                ObstaclePoint = Vector3.zero;
                ObstacleDistance = 0f;
                Crossings = new List<Crossed>();
            }
        }

        // The shell as a ray: it flies through every part that is NOT armor, paying energy in each (the
        // same law the fragments follow), and stops at the first armor part, at anything that is not a
        // part (the ground, for example), or when its energy runs out.
        //   enteredPart: the part a real shell has just stopped in (its ray starts inside it)
        //   ignorePart:  a part to leave out altogether (the plate a shell has just gone through)
        public static Result Trace(Vector3 origin, Vector3 direction, float maxDistance,
                                   PartScript enteredPart, ShellInfo shell, PartScript ignorePart = null)
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
                if (part != null && part == ignorePart) continue;
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
                    result.HitObstacle = true;
                    result.ObstaclePoint = candidate.point;
                    result.ObstacleDistance = candidate.distance;
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

        // Where an explosive shell bursts along its ray.
        public struct Burst
        {
            public bool Happens;
            public float Distance;       // along the ray from its origin
            public Vector3 Point;
            public float Energy;         // what the shell still carries there (0 if it was stopped)
            public PartScript Inside;    // the part the burst point is inside of (null: in the open)
            public string Why;
            public float FuseLeft;       // when it does not burst before the armor it meets: the fuse left on arrival
        }

        // Where an explosive shell bursts along the ray 'ray' (traced from 'origin' along 'direction'):
        //  - HE (no fuse): at the first thing it touches;
        //  - APHE: where its fuse runs out, counted from the first touch (or from the start of the ray, if the
        //    fuse is already running: a shell that has just gone through a plate); or earlier, where it is
        //    stopped (it ran out of energy inside a part, or it hit something that is not a part).
        // The armor the ray meets is not a stop: the armor decides what happens to the shell there (if the fuse
        // has not run out first, the fuse that is left goes with the shell: Burst.FuseLeft).
        // If it bursts, the ray is cut at that point: the crossings after it go, the one it is inside ends there,
        // and the armor the ray was going to meet is forgotten.
        public static Burst FindBurst(ref Result ray, ShellInfo shell, Vector3 origin, Vector3 direction, bool fuseRunning)
        {
            Burst burst = new Burst { FuseLeft = shell.FuseLeft };
            if (!shell.Explosive) return burst;

            float infinity = float.PositiveInfinity;
            float armorDistance = ray.Armor != null ? ray.Hit.distance : infinity;
            float stopDistance = infinity;
            if (ray.RanOutOfEnergy) stopDistance = Vector3.Dot(ray.StopPoint - origin, direction);
            else if (ray.HitObstacle) stopDistance = ray.ObstacleDistance;

            // The first thing it touches.
            float firstTouch = Mathf.Min(armorDistance, stopDistance);
            if (ray.Crossings.Count > 0) firstTouch = Mathf.Min(firstTouch, ray.Crossings[0].Distance);
            if (float.IsInfinity(firstTouch) && !fuseRunning) return burst;   // it touches nothing: no fuse, no burst

            float at;
            string why;
            if (!shell.HasFuse)
            {
                at = firstTouch;
                why = "it burst on touching something (no fuse)";
                if (float.IsInfinity(at)) return burst;
            }
            else
            {
                float fuseStart = fuseRunning ? 0f : firstTouch;
                float byFuse = fuseStart + Mathf.Max(shell.FuseLeft, 0f);
                float end = Mathf.Min(armorDistance, stopDistance);

                if (byFuse < end)
                {
                    at = byFuse;
                    why = "its fuse ran out";
                }
                else if (stopDistance <= armorDistance && !float.IsInfinity(stopDistance))
                {
                    at = stopDistance;
                    why = ray.RanOutOfEnergy ? "it stopped inside a part" : "it hit something that is not a part";
                }
                else
                {
                    // It meets armor before the fuse runs out (or flies off): the fuse is shorter by the way gone.
                    if (!float.IsInfinity(armorDistance)) burst.FuseLeft = Mathf.Max(byFuse - armorDistance, 0f);
                    return burst;
                }
            }

            // It bursts at distance 'at'. Where is that, and what has the shell got left?
            burst.Happens = true;
            burst.Distance = at;
            burst.Why = why;
            burst.Point = origin + direction * at;
            burst.Energy = EnergyAt(ray, shell.Energy, at);

            // HE bursts on the surface it touches, just outside it.
            if (!shell.HasFuse) burst.Point -= direction * 0.02f;

            // Cut the ray there. A part the shell entered before the burst stays (cut at the burst, and the burst is
            // inside it); one it only touches at the burst (HE: the burst is just outside it) goes.
            float cutAfter = shell.HasFuse ? at + 0.0001f : at - 0.0001f;
            for (int i = ray.Crossings.Count - 1; i >= 0; i--)
            {
                Crossed crossing = ray.Crossings[i];
                if (crossing.Distance > cutAfter)
                {
                    ray.Crossings.RemoveAt(i);
                    continue;
                }
                if (crossing.Exit >= at - 0.0001f) burst.Inside = crossing.Part;
                if (crossing.Exit > at)
                {
                    crossing.Exit = at;
                    crossing.Dissipated = Mathf.Max(crossing.EnergyIn - burst.Energy, 0f);
                    crossing.EnergyOut = burst.Energy;
                    ray.Crossings[i] = crossing;
                }
            }
            ray.PassedThrough = ray.Crossings.Count;
            ray.Part = null;
            ray.Armor = null;
            ray.RanOutOfEnergy = false;
            ray.HitObstacle = false;
            ray.StoppedBy = why;
            return burst;
        }

        // The shell's energy 'distance' meters along the ray: what it had, less what the parts it has crossed took.
        private static float EnergyAt(Result ray, float start, float distance)
        {
            float energy = start;
            foreach (Crossed crossing in ray.Crossings)
            {
                if (crossing.Distance >= distance) break;
                float after = Mathf.Max(crossing.EnergyOut, 0f);
                if (crossing.Exit <= distance + 0.0001f) { energy = after; continue; }

                // In the middle of this part.
                float f = (distance - crossing.Distance) / Mathf.Max(crossing.Exit - crossing.Distance, 0.0001f);
                return Mathf.Lerp(crossing.EnergyIn, after, Mathf.Clamp01(f));
            }
            return energy;
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

            Crossed crossing = new Crossed { Part = part, Distance = entry, Exit = exit, EnergyIn = energy };
            result.PassedThrough++;

            if (after <= floor)
            {
                crossing.Dissipated = energy;
                crossing.EnergyOut = 0f;
                float into = PartPhysics.StopDistance(energy, floor, mass, areaM2, density, chord);
                crossing.Exit = entry + into;
                result.Crossings.Add(crossing);

                result.RanOutOfEnergy = true;
                result.StopPoint = origin + direction * (entry + into);
                result.StoppedBy = "ran out of energy inside '" + part.Part.Name + "'";
                energy = 0f;
                return true;
            }

            crossing.Dissipated = energy - after;
            crossing.EnergyOut = after;
            result.Crossings.Add(crossing);
            energy = after;
            return false;
        }

        private static Result Finish(Result result, float energy)
        {
            result.Energy = energy;
            return result;
        }

        // Drawn speed of something carrying 'energy' with 'mass': its real speed times the multiplier
        // that makes it slow enough to follow.
        public static float VisualSpeed(float energy, float mass)
        {
            float real = Mathf.Sqrt(2f * Mathf.Max(energy, 0f) / Mathf.Max(mass, 0.000001f));
            return Mathf.Max(real * ArmorFragmentsPlugin.VisualSpeedMultiplier.Value, 1f);
        }

        // The shell's last meters before an impact (a single stretch at the shell's own speed).
        // Returns how many seconds it takes: whatever follows is drawn to start after that.
        public static float DrawSimpleTrack(Vector3 start, Vector3 end, float shellEnergy, float shellMass,
                                            float diameterMeters)
        {
            float speed = VisualSpeed(shellEnergy, shellMass);
            float duration = Vector3.Distance(start, end) / speed;
            if (!ArmorFragmentsPlugin.DrawRays.Value) return duration;

            RayPath path = new RayPath { Final = RayKind.ArmorImpact, Width = diameterMeters };
            path.Add(start, RayKind.None);
            path.Add(end, RayKind.ArmorImpact, speed);
            RayDrawer.Animate(path, 0f, true);
            return duration;
        }

        // The shell as a ray that goes through parts: drawn from 'start' to 'end', slowing down in every
        // part it crosses (marked with a bubble where it enters). Fills in each crossing's ArrivalTime
        // and returns the total duration.
        //   leadIn:      meters drawn before 'start' (the shell arriving), 0 for none
        //   startMarker: bubble at 'start' (a real shell has just hit a part there)
        //   endMarker:   bubble at 'end' (the armor impact, or where the ray stopped)
        //   delay:       seconds to wait before it starts moving (a shell that left a plate starts later)
        //   continuation: the ray of a shell that has gone through a plate: thick like the shell's, but it
        //                 is not "the impact" (the impact camera follows only the first)
        public static float DrawShellRay(Vector3 start, Vector3 direction, ref Result ray, ShellInfo shell,
                                         Vector3 end, float leadIn, RayKind startMarker, RayKind endMarker,
                                         float delay = 0f, bool continuation = false)
        {
            RayPath path = new RayPath { Final = RayKind.ArmorImpact, Width = shell.CaliberMm * 0.001f, Continuation = continuation };
            float time = 0f;
            float energy = shell.Energy;
            float speed = VisualSpeed(energy, shell.Mass);
            float distance = 0f;   // along the ray from 'start'

            if (leadIn > 0f)
            {
                path.Add(start - direction * leadIn, RayKind.None);
                path.Add(start, startMarker, speed);
                time += leadIn / speed;
            }
            else
            {
                path.Add(start, startMarker);
            }

            for (int i = 0; i < ray.Crossings.Count; i++)
            {
                Crossed crossing = ray.Crossings[i];

                // Free flight up to the part (an entered part starts at distance 0: nothing to draw).
                if (crossing.Distance > distance + 0.0001f)
                {
                    time += (crossing.Distance - distance) / speed;
                    path.Add(start + direction * crossing.Distance,
                             crossing.Distance > 0.0001f ? RayKind.Hit : RayKind.None, speed);
                    distance = crossing.Distance;
                }

                crossing.ArrivalTime = time;

                // Inside the part: it slows down from the speed it came in with to the one it leaves with.
                float speedOut = crossing.EnergyOut > 0f ? VisualSpeed(crossing.EnergyOut, shell.Mass) : speed * 0.5f;
                float inside = Mathf.Max(crossing.Exit - distance, 0f);
                float average = 0.5f * (speed + speedOut);
                time += inside / average;
                path.Add(start + direction * crossing.Exit, RayKind.None, average);
                distance = Mathf.Max(distance, crossing.Exit);
                speed = speedOut;

                ray.Crossings[i] = crossing;
            }

            // On to the end point.
            float toEnd = Vector3.Distance(start, end);
            if (toEnd > distance + 0.0001f)
            {
                time += (toEnd - distance) / speed;
                path.Add(end, endMarker, speed);
            }
            else if (path.Points.Count > 0)
            {
                // The last stretch already reaches it: just put the marker there.
                path.Markers[path.Markers.Count - 1] = endMarker;
            }

            if (ArmorFragmentsPlugin.DrawRays.Value)
            {
                if (continuation) RayDrawer.Animate(path, delay, false, true);
                else RayDrawer.Animate(path, delay, true);
            }
            return time;
        }
    }
}
