using UnityEngine;
using Assets.Scripts.Craft.Parts;

namespace ArmorFragments
{
    // A shell flying on as a "ballistic ray": through ordinary parts (paying energy in each, which is
    // their damage) until it meets armor, anything that is not a part, or runs out of energy.
    //
    // Used in three places:
    //  - a real shell that hit a part without an [ARM] tag (the game stops the shell there; the ray goes on);
    //  - a shell that has gone through a plate (it leaves the plate bent a little);
    //  - a shell that has ricocheted off a plate (it flies off with the energy the bounce left it).
    // If the ray meets armor, that armor is resolved like any impact (ricochet, block or penetration),
    // which may send the shell on again, up to MaxPlates plates.
    //
    // An explosive shell with a fuse may burst on the way (BallisticRay.FindBurst): where its fuse runs out,
    // or where it is stopped. The ray then ends there and the casing flies off as fragments.
    internal static class ShellFlight
    {
        // from:         where the ray starts
        // enteredPart:  a part the ray starts inside (a real shell's first contact): it pays energy there. If the
        //               cannon has no [CAN] tag the game's own impact damage was applied to it, so the ray adds
        //               none; a tagged cannon has the game's damage switched off, so the ray's is all it gets
        // ignorePart:   a part to leave out altogether (the plate just gone through)
        // startDelay:   seconds after the impact at which this ray starts moving
        // leadIn:       meters of flight drawn before 'from' (the shell arriving); 0 for none
        // nextPlate:    the number the next armor met would have in the chain of plates
        // continuation: the ray of a shell that left a plate or bounced off one (drawn thick, but it is not the
        //               impact ray)
        // bounces:      ricochets the shell has had already (it may bounce off armor up to MaxRicochets times)
        public static void Continue(Vector3 from, Vector3 direction, ShellInfo shell, CannonTag cannon,
                                    PartScript enteredPart, PartScript ignorePart, float startDelay, float leadIn,
                                    RayKind startMarker, bool applyDamage, bool simulated, int nextPlate,
                                    bool continuation, int bounces = 0)
        {
            string tag = simulated ? "[SIM] " : "";
            float range = ArmorFragmentsPlugin.BallisticRayRange.Value;

            BallisticRay.Result ray = BallisticRay.Trace(from, direction, range, enteredPart, shell, ignorePart);

            // An explosive shell may burst on the way (the fuse counts from the first touch, or is already
            // running if the shell has just come out of a plate).
            BallisticRay.Burst burst = BallisticRay.FindBurst(ref ray, shell, from, direction, continuation);

            // Where the ray ends: where it bursts, at the armor, or where it stopped.
            Vector3 end;
            RayKind endMarker;
            if (burst.Happens) { end = burst.Point; endMarker = RayKind.None; }
            else if (ray.Armor != null) { end = ray.Hit.point; endMarker = RayKind.ArmorImpact; }
            else if (ray.RanOutOfEnergy) { end = ray.StopPoint; endMarker = simulated ? RayKind.Landed : RayKind.Hit; }
            else if (ray.HitObstacle) { end = ray.ObstaclePoint; endMarker = simulated ? RayKind.Landed : RayKind.Miss; }
            else if (ray.HasFirstPoint) { end = ray.FirstPoint; endMarker = simulated ? RayKind.Landed : RayKind.Miss; }
            else if (continuation) { end = from + direction * Mathf.Min(range, 10f); endMarker = RayKind.None; }   // out into the air
            else { end = from; endMarker = RayKind.None; }

            // The ray as drawn: slowing down in every part it crosses, marked where it enters one.
            float duration = BallisticRay.DrawShellRay(
                from, direction, ref ray, shell, end, leadIn, startMarker, endMarker, startDelay, continuation);

            // What the shell leaves in the ordinary parts it crosses is their damage. (The part a real shell
            // stopped in took the game's own damage already, unless the cannon's tag switched that off.)
            float damage = FragmentSpawner.DepositCrossings(ray, from, direction, cannon == null ? enteredPart : null,
                                                            applyDamage, startDelay);

            string origin = startMarker == RayKind.Ricochet ? "Shell ricocheted off plate " + nextPlate
                          : continuation ? "Shell through plate " + (nextPlate - 1)
                          : "Shell stopped in a non-armor part";

            if (burst.Happens)
            {
                ShellInfo burstShell = shell;
                burstShell.Speed = Mathf.Sqrt(2f * burst.Energy / Mathf.Max(shell.Mass, 0.000001f));
                burstShell.FuseLeft = 0f;

                ArmorFragmentsPlugin.Log.LogInfo(
                    tag + origin + "; its ballistic ray went through " + ray.PassedThrough + " parts (damage " +
                    damage.ToString("F1") + ") and the shell burst " + burst.Distance.ToString("F2") + " m on: " + burst.Why +
                    (burst.Inside != null ? " (inside '" + burst.Inside.Part.Name + "')" : ""));
                FragmentSpawner.Detonate(burstShell, burst.Point, direction, burst.Inside, applyDamage, simulated,
                                         startDelay + duration, burst.Why);
                return;
            }

            if (ray.Armor == null)
            {
                ArmorFragmentsPlugin.Log.LogInfo(
                    tag + origin + "; its ballistic ray found no armor" +
                    (ray.StoppedBy != null ? " (" + ray.StoppedBy + ")" : "") +
                    ", " + ray.PassedThrough + " parts crossed, damage " + damage.ToString("F1") +
                    (applyDamage ? "" : " (not applied)"));
                return;
            }

            float angle = Vector3.Angle(direction, ray.Hit.normal);
            angle = Mathf.Min(angle, 180f - angle);

            ArmorFragmentsPlugin.Log.LogInfo(
                tag + origin + "; its ballistic ray passed through " + ray.PassedThrough + " parts (damage " +
                damage.ToString("F1") + ") and met armor '" + ray.Part.Part.Name + "' at " +
                ray.Hit.distance.ToString("F1") + " m with " + EnergyModel.Kilojoules(ray.Energy) +
                " of " + EnergyModel.Kilojoules(shell.Energy));

            // It reaches the armor slower than it started.
            ShellInfo atArmor = shell;
            atArmor.Speed = Mathf.Min(shell.Speed, Mathf.Sqrt(2f * ray.Energy / Mathf.Max(shell.Mass, 0.000001f)));
            atArmor.FuseLeft = burst.FuseLeft;   // the way flown to the armor has used some of the fuse

            // The fragments of that armor leave when this ray reaches it.
            FragmentSpawner.Spawn(ray.Part, ray.Armor, cannon, atArmor, ray.Hit.point, direction,
                ray.Hit.normal, angle, applyDamage, simulated, startDelay + duration, nextPlate, bounces);
        }
    }
}
