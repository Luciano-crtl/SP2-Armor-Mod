// RELEASE-STRIP-BEGIN
using UnityEngine;

namespace ArmorFragments
{
    // A baked blast cloud: one big upright sprite that plays a pre-simulated flipbook (tools/vfx/bake_flipbook.py). The wind drifts it
    // (SmokeSim in baked mode). The "Test: blast cloud" button puts one on the ground ahead of the camera.
    internal static class BlastCloud
    {
        public const float FrameMeters = 14f;   // the picture of one frame is this many metres tall (CreateBlastCloud.FrameMeters)
        public static float LifeSeconds = 6f;

        // `ground` is the point on the ground under the blast; `scale` stretches it (1 = a 14 m picture).
        public static GameObject Spawn(Vector3 ground, float scale, Vector3 carriedVelocity)
        {
            GameObject prefab = VfxBundleTest.Prefab("blastcloud");
            if (prefab == null) return null;

            // The picture's bottom edge is the ground, so the sprite's centre is half its height up.
            float size = FrameMeters * scale;
            GameObject go = Object.Instantiate(prefab, ground + Vector3.up * (size * 0.5f), Quaternion.identity);
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
