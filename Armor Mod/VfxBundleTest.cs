// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace ArmorFragments
{
    // Experiment: loads the AssetBundle "armorvfx" (built in the Unity project ArmorModVFX, Unity 6000.3.20f1) and hands out its prefabs.
    // The "Test: effect bundle" button in the panel's Tools section spawns the smoke column a few metres in front of the main camera.
    // Everything it learns goes to the log with the tag [VFXBUNDLE].
    internal static class VfxBundleTest
    {
        private const string BundleFile = "armorvfx";
        private static AssetBundle bundle;
        private static readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
        private static readonly List<GameObject> Spawned = new List<GameObject>();
        private static bool loadFailed;

        // A prefab of the bundle by its file name without the extension, lower case ("smoke", "muzzlesmoke"); null if it cannot be had.
        public static GameObject Prefab(string name)
        {
            if (bundle == null && !loadFailed && !Load()) loadFailed = true;
            GameObject prefab;
            return Prefabs.TryGetValue(name, out prefab) ? prefab : null;
        }

        public static void Spawn()
        {
            try
            {
                GameObject prefab = Prefab("smoke");
                if (prefab == null) { ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] the smoke prefab is not available"); return; }

                Camera cam = Camera.main;
                if (cam == null) { ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] no main camera"); return; }

                Vector3 at = cam.transform.position + cam.transform.forward * 6f;
                GameObject go = Object.Instantiate(prefab, at, Quaternion.identity);
                go.name = "ArmorFragments effect test";
                SetLayer(go, ImpactCamera.EffectsLayer);
                Spawned.Add(go);
                Object.Destroy(go, 15f);

                ParticleSystem[] systems = go.GetComponentsInChildren<ParticleSystem>(true);
                foreach (ParticleSystem ps in systems)
                {
                    SmokeSim sim = ps.gameObject.AddComponent<SmokeSim>();
                    sim.StartKelvin = 650f;   // warm, not glowing: it rises and swells a little
                    ps.Play();
                }

                // Report what the game does with the effect's material: a shader the game stripped shows up as a pink or invisible effect.
                foreach (ParticleSystemRenderer r in go.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    Material m = r.sharedMaterial;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] renderer '" + r.name + "': material "
                        + (m != null ? "'" + m.name + "' shader '" + (m.shader != null ? m.shader.name : "none")
                          + "' supported " + (m.shader != null && m.shader.isSupported) : "none"));
                }
                ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] spawned '" + prefab.name + "' (" + systems.Length + " particle system(s)) at "
                    + at + " on layer " + ImpactCamera.EffectsLayer + "; it disappears in 15 s");
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] failed: " + e);
            }
        }

        public static void Shutdown()
        {
            MuzzleSmoke.Shutdown();
            foreach (GameObject go in Spawned) if (go != null) Object.Destroy(go);
            Spawned.Clear();
            Prefabs.Clear();
            if (bundle != null) bundle.Unload(true);
            bundle = null;
            loadFailed = false;
        }

        public static void SetLayer(GameObject go, int layer)
        {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        private static bool Load()
        {
            string path = FindBundle();
            if (path == null) return false;

            bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] AssetBundle.LoadFromFile returned null for " + path
                    + ". The usual cause is a bundle built with another Unity version than the game's (6000.3.20f1 was used).");
                return false;
            }

            string[] names = bundle.GetAllAssetNames();
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] loaded " + path + ", " + names.Length + " asset(s): " + string.Join(", ", names));
            foreach (string asset in names)
            {
                GameObject go = bundle.LoadAsset<GameObject>(asset);
                if (go != null) Prefabs[Path.GetFileNameWithoutExtension(asset).ToLowerInvariant()] = go;
            }
            return true;
        }

        private static string FindBundle()
        {
            string[] folders =
            {
                Path.Combine(Paths.BepInExRootPath, "scripts"),
                Path.Combine(Paths.PluginPath, "ArmorFragments"),
                Paths.PluginPath,
            };
            foreach (string folder in folders)
            {
                string candidate = Path.Combine(folder, BundleFile);
                if (File.Exists(candidate)) return candidate;
            }
            ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] the file '" + BundleFile + "' was not found in: " + string.Join(" | ", folders));
            return null;
        }
    }
}
// RELEASE-STRIP-END
