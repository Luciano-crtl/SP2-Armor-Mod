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

        // "Lock shot to camera": the shot starts from the camera pose saved when the lock was
        // switched on, so the player can move the camera and watch it from somewhere else.
        public static bool IsLocked { get; private set; }
        private static Vector3 lockedOrigin;
        private static Vector3 lockedDirection;

        public static string LockText
        {
            get
            {
                if (!IsLocked) return "Off";
                return "At " + lockedOrigin.x.ToString("F0") + ", " + lockedOrigin.y.ToString("F0") + ", " +
                       lockedOrigin.z.ToString("F0") + " (world position)";
            }
        }

        // Switches the lock on or off. Switching on needs the orbit camera, because that is the
        // pose being saved. Returns false if the lock could not be switched on.
        public static bool SetLocked(bool locked)
        {
            if (!locked)
            {
                IsLocked = false;
                return true;
            }

            CameraManagerScript manager = CameraManagerScript.Instance;
            Camera camera = manager != null && manager.MainCamera != null ? manager.MainCamera : Camera.main;
            if (camera == null)
            {
                Message = "No camera found";
                return false;
            }
            if (manager == null || !(manager.Controller is OrbitCameraController))
            {
                Message = "Switch to the orbit camera to lock the shot";
                return false;
            }

            lockedOrigin = camera.transform.position;
            lockedDirection = camera.transform.forward;
            IsLocked = true;
            Message = "Shot locked: it will start from where the camera is now";
            return true;
        }

        public static void Fire()
        {
            Top.Clear();
            RayDrawer.ClearPersistent();   // the previous test's stop points go away

            Vector3 origin;
            Vector3 direction;

            if (IsLocked)
            {
                // The saved pose: the camera is free to be anywhere.
                origin = lockedOrigin;
                direction = lockedDirection;
            }
            else
            {
                // The shot starts exactly at the camera, so it only makes sense with the orbit
                // camera (the one that can be moved around freely, away from the aircraft).
                CameraManagerScript manager = CameraManagerScript.Instance;
                Camera camera = manager != null && manager.MainCamera != null ? manager.MainCamera : Camera.main;
                if (camera == null)
                {
                    Message = "No camera found";
                    return;
                }
                if (manager == null || !(manager.Controller is OrbitCameraController))
                {
                    Message = "Switch to the orbit camera to simulate a shot";
                    XRayView.Hide();
                    return;
                }

                origin = camera.transform.position;
                direction = camera.transform.forward;
            }

            CannonTag cannon = null;   // the imaginary cannon has no properties of its own (the shell does)
            ShellInfo shell = new ShellInfo
            {
                Mass = EnergyModel.ShellMassForCaliber(ArmorFragmentsPlugin.SimCaliber.Value),
                Speed = ArmorFragmentsPlugin.SimShellVelocity.Value,
                CaliberMm = ArmorFragmentsPlugin.SimCaliber.Value,
                Hardness = ArmorFragmentsPlugin.SimShellHardness.Value,
                Explosive = ArmorFragmentsPlugin.SimShellExplosive.Value,
            };

            // The shell flies through every part that is not armor (leaving energy in each) and stops
            // at the first armor part, at anything that is not a part (like the ground), or when it
            // runs out of energy.
            BallisticRay.Result ray = BallisticRay.Trace(origin, direction, MaxDistance, null, shell);

            string passed = ray.PassedThrough == 0
                ? ""
                : "Passed through " + ray.PassedThrough + " non-armor parts. ";

            // Where the shell ray ends: at the armor, or where it stopped.
            Vector3 end;
            RayKind endMarker;
            if (ray.Armor != null) { end = ray.Hit.point; endMarker = RayKind.ArmorImpact; }
            else if (ray.RanOutOfEnergy) { end = ray.StopPoint; endMarker = RayKind.Landed; }
            else if (ray.HasFirstPoint) { end = ray.FirstPoint; endMarker = RayKind.Landed; }
            else { end = origin + direction * 5f; endMarker = RayKind.None; }

            // The initial ray: from the exact camera position, thicker than the rest, slowing down in
            // every ordinary part it crosses (and marked where it enters one). The fragments leave once
            // it has arrived. Landed markers stay on screen until the next test.
            float leadTime = BallisticRay.DrawShellRay(origin, direction, ref ray, shell, end, 0f, RayKind.None, endMarker);

            // What the ray leaves in the ordinary parts it crosses is their would-be damage (a simulated
            // shot never applies damage). It shows up in the X-ray view as the initial ray passes.
            Dictionary<PartScript, DamageRecord> sink = new Dictionary<PartScript, DamageRecord>();
            foreach (BallisticRay.Crossed crossing in ray.Crossings)
            {
                float rayDamage = crossing.Dissipated / 1000f * ArmorFragmentsPlugin.DamagePerKiloJoule.Value;
                if (rayDamage <= 0f) continue;

                sink[crossing.Part] = new DamageRecord { Total = rayDamage, FirstTime = crossing.ArrivalTime };
            }

            if (ray.Armor == null)
            {
                FillTop(sink);
                Message = passed + (ray.StoppedBy != null ? "Stopped: " + ray.StoppedBy : "No armor on the way");

                if (sink.Count > 0 && ArmorFragmentsPlugin.XRayAfterSimulation.Value) XRayView.Show(end, sink);
                else XRayView.Hide();
                return;
            }

            PartScript part = ray.Part;
            ArmorTag armor = ray.Armor;
            RaycastHit hit = ray.Hit;

            // Diagnostic: how far the line travels inside each part (method A), in the log.
            if (ArmorFragmentsPlugin.LogChordDiagnostics.Value)
            {
                try
                {
                    ChordProbe.Report(origin, direction, Mathf.Min(MaxDistance, hit.distance + 15f));
                }
                catch (System.Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Chord probe failed: " + e.Message);
                }
            }

            float angle = Vector3.Angle(direction, hit.normal);
            angle = Mathf.Min(angle, 180f - angle);

            // It reaches the armor with what is left of its energy (slower than it left the cannon).
            ShellInfo atArmor = shell;
            atArmor.Speed = Mathf.Min(shell.Speed, Mathf.Sqrt(2f * ray.Energy / Mathf.Max(shell.Mass, 0.000001f)));

            // The same code a real shell goes through, with damage switched off. Every
            // fragment's would-be damage is added to the same per-part list.
            FragmentSpawner.DamageSink = sink;
            try
            {
                FragmentSpawner.Spawn(part, armor, cannon, atArmor, hit.point, direction, hit.normal, angle,
                                      false, true, leadTime);
            }
            finally
            {
                FragmentSpawner.DamageSink = null;
            }

            FillTop(sink);

            Message = passed + "Hit '" + Label(part) + "' at " + hit.distance.ToString("F0") + " m, angle " +
                      angle.ToString("F0") + " deg: " + sink.Count + " parts would be damaged";

            if (ArmorFragmentsPlugin.XRayAfterSimulation.Value)
                XRayView.Show(hit.point, sink);
        }

        // The parts that would take the most damage, for the panel.
        private static void FillTop(Dictionary<PartScript, DamageRecord> sink)
        {
            Top.Clear();
            foreach (KeyValuePair<PartScript, DamageRecord> pair in sink.OrderByDescending(p => p.Value.Total).Take(TopCount))
            {
                Top.Add(new PartResult
                {
                    Name = Label(pair.Key),
                    Damage = pair.Value.Total,
                    Fraction = pair.Value.Total / Mathf.Max(pair.Key.MaxHealth, 1f),
                });
            }
        }

        // The player's own label, without our [..] blocks.
        private static string Label(PartScript part)
        {
            string label = TagCodec.ParseCopy(part.Part.Name).Label;
            return label.Length > 0 ? label : part.Part.Name;
        }
    }
}
