using UnityEngine;
using Assets.Scripts.Craft.Parts;

namespace ArmorFragments
{
    // A shell flying on as a "ballistic ray": through ordinary parts (paying energy in each, which is
    // their damage) until it meets armor, anything that is not a part, or runs out of energy.
    //
    // Used in two places:
    //  - a real shell that hit a part without an [ARM] tag (the game stops the shell there; the ray goes on);
    //  - an armor-piercing shell that has gone through a plate (it leaves the plate bent a little).
    // If the ray meets armor, that armor is resolved like any impact (ricochet, block or penetration),
    // which may send the shell on again, up to MaxPlates plates.
    internal static class ShellFlight
    {
        // from:         where the ray starts
        // enteredPart:  a part the ray starts inside (a real shell's first contact): it pays energy there but
        //               takes no damage from the ray (the game's own impact damage was applied)
        // ignorePart:   a part to leave out altogether (the plate just gone through)
        // startDelay:   seconds after the impact at which this ray starts moving
        // leadIn:       meters of flight drawn before 'from' (the shell arriving); 0 for none
        // nextPlate:    the number the next armor met would have in the chain of plates
        // continuation: the ray of a shell that left a plate (drawn thick, but it is not the impact ray)
        public static void Continue(Vector3 from, Vector3 direction, ShellInfo shell, CannonTag cannon,
                                    PartScript enteredPart, PartScript ignorePart, float startDelay, float leadIn,
                                    RayKind startMarker, bool applyDamage, bool simulated, int nextPlate,
                                    bool continuation)
        {
            string tag = simulated ? "[SIM] " : "";
            float range = ArmorFragmentsPlugin.BallisticRayRange.Value;

            BallisticRay.Result ray = BallisticRay.Trace(from, direction, range, enteredPart, shell, ignorePart);

            // Where the ray ends: at the armor, or where it stopped.
            Vector3 end;
            RayKind endMarker;
            if (ray.Armor != null) { end = ray.Hit.point; endMarker = RayKind.ArmorImpact; }
            else if (ray.RanOutOfEnergy) { end = ray.StopPoint; endMarker = simulated ? RayKind.Landed : RayKind.Hit; }
            else if (ray.HasFirstPoint) { end = ray.FirstPoint; endMarker = simulated ? RayKind.Landed : RayKind.Miss; }
            else if (continuation) { end = from + direction * Mathf.Min(range, 10f); endMarker = RayKind.None; }   // out into the air
            else { end = from; endMarker = RayKind.None; }

            // The ray as drawn: slowing down in every part it crosses, marked where it enters one.
            float duration = BallisticRay.DrawShellRay(
                from, direction, ref ray, shell, end, leadIn, startMarker, endMarker, startDelay, continuation);

            // What the shell leaves in the ordinary parts it crosses is their damage.
            float damage = FragmentSpawner.DepositCrossings(ray, from, direction, enteredPart, applyDamage, startDelay);

            string origin = continuation ? "Shell through plate " + (nextPlate - 1) : "Shell stopped in a non-armor part";
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

            // The fragments of that armor leave when this ray reaches it.
            FragmentSpawner.Spawn(ray.Part, ray.Armor, cannon, atArmor, ray.Hit.point, direction,
                ray.Hit.normal, angle, applyDamage, simulated, startDelay + duration, nextPlate);
        }
    }
}
