// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace ArmorFragments
{
    // Loads the AssetBundle "armorvfx" (built in the Unity project ArmorModVFX, Unity 6000.3.20f1) and hands out what the mod needs from
    // it: the fluid solver (a compute shader) and the material of the ray-marched smoke. Everything it learns goes to the log with the
    // tag [VFXBUNDLE].
    internal static class VfxBundleTest
    {
        private const string BundleFile = "armorvfx";
        private static AssetBundle bundle;
        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();
        private static readonly Dictionary<string, ComputeShader> Computes = new Dictionary<string, ComputeShader>();
        private static bool loadFailed;

        // A compute shader of the bundle by name, lower case ("fluid"); null if it cannot be had.
        public static ComputeShader Compute(string name)
        {
            if (bundle == null && !loadFailed && !Load()) loadFailed = true;
            ComputeShader shader;
            return Computes.TryGetValue(name, out shader) ? shader : null;
        }

        // A material of the bundle by name, lower case ("smokevolume"); null if it cannot be had.
        public static Material Material(string name)
        {
            if (bundle == null && !loadFailed && !Load()) loadFailed = true;
            Material material;
            return Materials.TryGetValue(name, out material) ? material : null;
        }

        public static void Shutdown()
        {
            FluidSmoke.Shutdown();
            Materials.Clear();
            Computes.Clear();
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
                string key = Path.GetFileNameWithoutExtension(asset).ToLowerInvariant();
                Material material = bundle.LoadAsset<Material>(asset);
                if (material != null) { Materials[key] = material; continue; }
                ComputeShader compute = bundle.LoadAsset<ComputeShader>(asset);
                if (compute != null) Computes[key] = compute;
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
