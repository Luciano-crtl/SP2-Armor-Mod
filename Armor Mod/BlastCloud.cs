// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    // A baked blast cloud: one big upright sprite that plays a pre-simulated flipbook (tools/vfx/bake_flipbook.py). The wind drifts it
    // (SmokeSim in baked mode). The "Test: blast cloud" button puts one on the ground ahead of the camera.
    internal static class BlastCloud
    {
        public const float FrameMeters = 14f;   // the picture of one frame is this many metres tall (CreateBlastCloud.FrameMeters)
        public static float LifeSeconds = 6f;

        // The ground blast of a cannon shot.
        public static bool OnShots = true;
        public static int MaxLive = 6;                 // heavy clouds alive at once
        public static float MinInterval = 0.6f;        // seconds between the clouds of one cannon
        public static float MaxGroundDistance = 10f;   // metres: the muzzle must be this close to the ground, else there is no dust
        public static float CaliberForFullSize = 100f; // the picture is shown at its full size for this caliber (mm)
        public static float AheadMeters = 2.5f;        // the dust is thrown up this far in front of the muzzle

        private static readonly List<GameObject> Live = new List<GameObject>();
        private static readonly Dictionary<int, float> LastShot = new Dictionary<int, float>();
        private static bool loggedFirst;

        public static void ToggleOnShots()
        {
            OnShots = !OnShots;
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] ground blast on shots " + (OnShots ? "on" : "off"));
        }

        // A cannon fired: if its muzzle is near the ground, the blast throws dust up in front of it.
        public static void OnShot(CannonScript cannon)
        {
            if (!OnShots || cannon == null) return;
            try
            {
                Live.RemoveAll(g => g == null);
                if (Live.Count >= MaxLive) return;

                int id = cannon.GetInstanceID();
                float now = Time.time, last;
                if (LastShot.TryGetValue(id, out last) && now - last < MinInterval) return;

                Vector3 tip = cannon.TipPosition;
                Vector3 forward = cannon.transform.forward;
                if (forward.y > 0.6f) return;     // aimed steeply upward: nothing to kick up

                Vector3 ground;
                if (!FindGround(tip + forward * AheadMeters, cannon, out ground)) return;
                LastShot[id] = now;

                float caliber = MuzzleSmoke.CaliberMillimeters(cannon);
                float scale = Mathf.Clamp(caliber / CaliberForFullSize, 0.45f, 1.4f);
                GameObject go = Spawn(ground, scale, Vector3.zero);
                if (go == null) return;
                Live.Add(go);

                if (!loggedFirst)
                {
                    loggedFirst = true;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] first ground blast: caliber " + caliber.ToString("F0") + " mm, size x"
                        + scale.ToString("F2") + ", ground " + (tip.y - ground.y).ToString("F1") + " m below the muzzle");
                }
            }
            catch (System.Exception e)
            {
                OnShots = false;
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] the ground blast failed and is now off: " + e);
            }
        }

        // The ground below `from`: the first thing hit that is not a part of a vehicle (the shooter's own vehicle is ignored; the
        // roof of another one means "no ground here") and not a shell.
        private static bool FindGround(Vector3 from, CannonScript cannon, out Vector3 point)
        {
            point = from;
            AircraftScript own = cannon.PartScript != null ? cannon.PartScript.Aircraft : null;
            RaycastHit[] hits = Physics.RaycastAll(from + Vector3.up * 0.5f, Vector3.down, MaxGroundDistance + 0.5f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.GetComponentInParent<CannonProjectileScript>() != null) continue;
                PartScript part = hit.collider.GetComponentInParent<PartScript>();
                if (part != null)
                {
                    if (own != null && part.Aircraft == own) continue;
                    return false;
                }
                point = hit.point;
                return true;
            }
            return false;
        }

        // `ground` is the point on the ground under the blast; `scale` stretches it (1 = a 14 m picture).
        public static GameObject Spawn(Vector3 ground, float scale, Vector3 carriedVelocity)
        {
            GameObject prefab = VfxBundleTest.Prefab("blastcloud");
            if (prefab == null) return null;

            // The picture's bottom edge is the ground, so the sprite's centre is half its height up (minus a little, so it sinks into the ground).
            float size = FrameMeters * scale;
            GameObject go = Object.Instantiate(prefab, ground + Vector3.up * (size * 0.5f - size * 0.04f), Quaternion.identity);   // sunk a little: the ground cuts it
            go.name = "ArmorFragments blast cloud";
            VfxBundleTest.SetLayer(go, ImpactCamera.EffectsLayer);

            ParticleSystem ps = go.GetComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.startSizeMultiplier *= scale;

            SmokeSim sim = go.AddComponent<SmokeSim>();
            sim.Baked = true;
            sim.InitialVelocity = carriedVelocity;

            ps.Play();
            Object.Destroy(go, LifeSeconds);
            return go;
        }

        public static void Test()
        {
            try
            {
                Camera cam = Camera.main;
                if (cam == null) { ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] no main camera"); return; }

                // Twelve metres ahead (flat), then down to the ground.
                Vector3 flat = cam.transform.forward;
                flat.y = 0f;
                flat = flat.sqrMagnitude < 0.01f ? Vector3.forward : flat.normalized;
                Vector3 at = cam.transform.position + flat * 18f;
                RaycastHit hit;
                if (Physics.Raycast(at + Vector3.up * 60f, Vector3.down, out hit, 400f)) at = hit.point;
                else at.y = cam.transform.position.y - 2f;

                GameObject go = Spawn(at, 1f, Vector3.zero);
                if (go == null) { ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] the BlastCloud prefab is not in the bundle (bake the textures first)"); return; }
                ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] blast cloud at " + at + " (14 m picture, 4.5 s)");
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] blast cloud failed: " + e);
            }
        }
    }
}
// RELEASE-STRIP-END
