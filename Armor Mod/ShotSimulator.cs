using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Flight.Cameras;
using UnityEngine;

namespace ArmorFragments
{
    // Fires an imaginary shell from the camera, straight ahead, through the same armor
    // logic as a real shell. It never damages anything and no cannon is needed.
    internal static class ShotSimulator
    {
        private const float MaxDistance = 1000f;
        private const int TopCount = 5;

        public struct PartResult
        {
            public string Name;
            public float Damage;
            public float Fraction;   // of the part's health
        }

        // What the panel shows.
        public static string Message = "No shot simulated yet";
        public static readonly List<PartResult> Top = new List<PartResult>();

        public static void Fire()
        {
            Top.Clear();

            Camera camera = FindCamera();
            if (camera == null)
            {
                Message = "No camera found";
                return;
            }

            Vector3 origin = camera.transform.position;
            Vector3 direction = camera.transform.forward;

            // Everything along the line, nearest first (real shells in flight are ignored).
            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            RaycastHit[] hits = Physics.RaycastAll(
                origin, direction, MaxDistance, mask, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));

            // The shell flies through every part that is not armor and stops at the first
            // armor part. It also stops at anything that is not a part (the ground, for example).
            HashSet<PartScript> passedThrough = new HashSet<PartScript>();
            RaycastHit hit = default(RaycastHit);
            PartScript part = null;
            ArmorTag armor = null;
            string stoppedBy = null;

            foreach (RaycastHit candidate in hits)
            {
                if (candidate.collider.GetComponentInParent<CannonProjectileScript>() != null) continue;

                PartScript candidatePart = candidate.collider.GetComponentInParent<PartScript>();
                if (candidatePart == null)
                {
                    stoppedBy = "'" + candidate.collider.name + "' at " +
                                candidate.distance.ToString("F0") + " m (not a part)";
                    break;
                }

                ArmorTag candidateArmor = TagCodec.Parse(candidatePart.Part.Name).Armor;
                if (candidateArmor == null)
                {
                    passedThrough.Add(candidatePart);   // no [ARM] tag: the shell goes through
                    continue;
                }

                hit = candidate;
                part = candidatePart;
                armor = candidateArmor;
                break;
            }

            string passed = passedThrough.Count == 0
                ? ""
                : "Passed through " + passedThrough.Count + " non-armor parts. ";

            if (armor == null)
            {
                Message = passed + (stoppedBy != null ? "Stopped by " + stoppedBy : "No armor on the way");
                XRayView.Hide();
                return;
            }

            float angle = Vector3.Angle(direction, hit.normal);
            angle = Mathf.Min(angle, 180f - angle);

            // The same code a real shell goes through, with damage switched off. Every
            // fragment's would-be damage is collected per part.
            Dictionary<PartScript, float> sink = new Dictionary<PartScript, float>();
            FragmentSpawner.DamageSink = sink;
            try
            {
                CannonTag shell = new CannonTag { Penetration = ArmorFragmentsPlugin.SimPenetration.Value };
                FragmentSpawner.Spawn(part, armor, shell, hit.point, direction, hit.normal, angle,
                                      false, true);
            }
            finally
            {
                FragmentSpawner.DamageSink = null;
            }

            foreach (KeyValuePair<PartScript, float> pair in sink.OrderByDescending(p => p.Value).Take(TopCount))
            {
                Top.Add(new PartResult
                {
                    Name = Label(pair.Key),
                    Damage = pair.Value,
                    Fraction = pair.Value / Mathf.Max(pair.Key.MaxHealth, 1f),
                });
            }

            Message = passed + "Hit '" + Label(part) + "' at " + hit.distance.ToString("F0") + " m, angle " +
                      angle.ToString("F0") + " deg: " + sink.Count + " parts would be damaged";

            if (ArmorFragmentsPlugin.XRayAfterSimulation.Value)
                XRayView.Show(hit.point, sink);
        }

        private static Camera FindCamera()
        {
            CameraManagerScript manager = CameraManagerScript.Instance;
            if (manager != null && manager.MainCamera != null) return manager.MainCamera;
            return Camera.main;
        }

        // The player's own label, without our [..] blocks.
        private static string Label(PartScript part)
        {
            string label = TagCodec.ParseCopy(part.Part.Name).Label;
            return label.Length > 0 ? label : part.Part.Name;
        }
    }
}
