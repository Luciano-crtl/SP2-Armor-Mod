// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ArmorFragments.Fluid
{
    // The light of the blast: for a moment the muzzle is brighter than the sun. A warm point light that lights the ground, the vehicle and
    // the surroundings (when the game's render pipeline draws extra lights), and, separately, the smoke itself (the smoke shader gets the
    // flash's position and strength, so that works whatever the pipeline does).
    internal static class MuzzleFlash
    {
        private sealed class Flash
        {
            public Light light;
            public float age, duration, peak;
        }

        private static readonly List<Flash> Flashes = new List<Flash>();
        private static int extraLights = -2;        // -2 not asked yet, -1 unknown, 0 off, 1 per vertex, 2 per pixel
        private static bool warned;

        public static bool Enabled { get { return ArmorFragmentsPlugin.SmokeFlash != null ? ArmorFragmentsPlugin.SmokeFlash.Value : true; } }
        public static float Intensity { get { return ArmorFragmentsPlugin.SmokeFlashIntensity != null ? ArmorFragmentsPlugin.SmokeFlashIntensity.Value : 1f; } }

        // How bright the flash still is `age` seconds after the shot (1 at the shot, a quick fall).
        public static float Level(float age, float timeScale)
        {
            if (age < 0f) return 0f;
            return Mathf.Exp(-age / (0.045f * timeScale));
        }

        // Does the game's render pipeline draw extra lights on the scene (a point light shows only if so)?
        private static int ExtraLights()
        {
            if (extraLights != -2) return extraLights;
            extraLights = -1;
            try
            {
                UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
                if (asset != null)
                {
                    extraLights = (int)asset.additionalLightsRenderingMode;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] muzzle flash: the render pipeline draws extra lights " + (extraLights == 0 ? "never (the flash will light only the smoke)"
                        : extraLights == 1 ? "per vertex" : "per pixel") + ", at most " + asset.maxAdditionalLightsCount + " per object");
                }
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] muzzle flash: could not ask the render pipeline about extra lights: " + e.Message);
            }
            return extraLights;
        }

        // A flash at `position` (the world), for a blast of this size.
        public static void Spawn(Vector3 position, float scale, float timeScale)
        {
            if (!Enabled || Intensity <= 0.01f) return;
            if (ExtraLights() == 0) { WarnOnce(); return; }
            try
            {
                GameObject go = new GameObject("ArmorFragments muzzle flash");
                go.transform.position = position;
                Light light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.58f, 0.25f);
                light.shadows = LightShadows.None;
                light.range = Mathf.Clamp(20f * Mathf.Sqrt(scale), 8f, 50f);
                float peak = 10f * Mathf.Pow(Mathf.Max(scale, 0.2f), 1.2f) * Intensity;
                light.intensity = peak;
                Flashes.Add(new Flash { light = light, age = 0f, duration = 0.35f * timeScale, peak = peak });
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] muzzle flash: could not make the light: " + e.Message);
            }
        }

        private static void WarnOnce()
        {
            if (warned) return;
            warned = true;
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] muzzle flash: no point light made, because the render pipeline has extra lights off");
        }

        public static void Tick(float dt, float timeScale)
        {
            for (int i = Flashes.Count - 1; i >= 0; i--)
            {
                Flash f = Flashes[i];
                f.age += dt;
                if (f.light == null || f.age >= f.duration)
                {
                    if (f.light != null) Object.Destroy(f.light.gameObject);
                    Flashes.RemoveAt(i);
                    continue;
                }
                f.light.intensity = f.peak * Level(f.age, timeScale);
            }
        }

        public static void Shutdown()
        {
            foreach (Flash f in Flashes) if (f.light != null) Object.Destroy(f.light.gameObject);
            Flashes.Clear();
            extraLights = -2;
            warned = false;
        }
    }
}
// RELEASE-STRIP-END
