using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // One-off report for planning the impact camera. It only reads and writes log lines ([CAMDIAG]):
    // which render pipeline the game uses, how its cameras are set up, which layers things live on
    // (and which layers nothing uses), and what the lighting looks like.
    internal static class CameraDiagnostic
    {
        public static void Run()
        {
            try
            {
                Line("---- camera diagnostic (mod " + ArmorFragmentsPlugin.Version + ") ----");
                Pipeline();
                Cameras();
                Layers();
                LocalVehicle();
                Lighting();
                Line("---- end of camera diagnostic ----");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("[CAMDIAG] failed: " + e);
            }
        }

        private static void Line(string text)
        {
            ArmorFragmentsPlugin.Log.LogInfo("[CAMDIAG] " + text);
        }

        // ---- Render pipeline ----------------------------------------------------

        private static void Pipeline()
        {
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            Line("Render pipeline: " + (asset != null ? asset.GetType().FullName + " (" + asset.name + ")" : "built-in"));

            Type urp = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            Line("URP camera data type: " + (urp != null ? "found in " + urp.Assembly.GetName().Name : "NOT found"));

            QualitySettings.GetQualityLevel();
            Line("Quality level: " + QualitySettings.names[QualitySettings.GetQualityLevel()] +
                 ", shadow distance " + QualitySettings.shadowDistance.ToString("F0") + " m");
        }

        // ---- Cameras ------------------------------------------------------------

        private static void Cameras()
        {
            Type urp = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");

            Camera[] cameras = Camera.allCameras;
            Line("Active cameras: " + cameras.Length);
            foreach (Camera camera in cameras)
            {
                StringBuilder text = new StringBuilder();
                text.Append("  '").Append(camera.name).Append("' depth ").Append(camera.depth.ToString("F0"))
                    .Append(", clear ").Append(camera.clearFlags)
                    .Append(", fov ").Append(camera.fieldOfView.ToString("F0"))
                    .Append(", near/far ").Append(camera.nearClipPlane.ToString("F2")).Append("/").Append(camera.farClipPlane.ToString("F0"))
                    .Append(", target ").Append(camera.targetTexture != null ? "texture " + camera.targetTexture.width + "x" + camera.targetTexture.height : "screen")
                    .Append(", mask ").Append(MaskText(camera.cullingMask));

                if (urp != null)
                {
                    Component data = camera.GetComponent(urp);
                    if (data != null)
                    {
                        text.Append(" | URP:");
                        foreach (string property in new[] { "renderType", "renderShadows", "renderPostProcessing", "antialiasing", "requiresDepthOption", "requiresColorOption" })
                            text.Append(' ').Append(property).Append('=').Append(Read(data, property));
                    }
                }
                Line(text.ToString());
            }
        }

        // ---- Layers -------------------------------------------------------------

        private static void Layers()
        {
            int[] objects = new int[32];
            int[] renderers = new int[32];
            int[] colliders = new int[32];

            foreach (GameObject go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                objects[go.layer]++;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                renderers[renderer.gameObject.layer]++;
            foreach (Collider collider in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
                colliders[collider.gameObject.layer]++;

            Line("Layers (objects / renderers / colliders):");
            List<int> free = new List<int>();
            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                if (objects[i] == 0 && string.IsNullOrEmpty(name)) free.Add(i);
                if (objects[i] == 0 && string.IsNullOrEmpty(name)) continue;
                Line("  " + i + " '" + name + "': " + objects[i] + " / " + renderers[i] + " / " + colliders[i]);
            }
            Line("Layers with no name and no objects (candidates for our own): " +
                 (free.Count == 0 ? "none" : string.Join(", ", free.ConvertAll(x => x.ToString()).ToArray())));
        }

        // The player's own vehicle: on which layers are its meshes and colliders?
        private static void LocalVehicle()
        {
            FlightScenePlayer player = FlightSceneScript.Instance != null ? FlightSceneScript.Instance.LocalPlayer : null;
            AircraftScript aircraft = player != null ? player.Aircraft : null;
            if (aircraft == null)
            {
                Line("Local vehicle: none (open this in a flight)");
                return;
            }

            Dictionary<int, int> renderers = new Dictionary<int, int>();
            Dictionary<int, int> colliders = new Dictionary<int, int>();
            Dictionary<int, int> groups = new Dictionary<int, int>();
            int parts = 0;
            foreach (PartData data in aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;
                parts++;
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true)) Count(renderers, renderer.gameObject.layer);
                foreach (Collider collider in part.GetComponentsInChildren<Collider>(true)) Count(colliders, collider.gameObject.layer);
                if (part.PartGroup != null && part.PartGroup.Renderer != null) Count(groups, part.PartGroup.Renderer.gameObject.layer);
            }

            Line("Local vehicle: " + parts + " parts");
            Line("  part renderers by layer: " + Format(renderers));
            Line("  combined group renderers by layer: " + Format(groups));
            Line("  part colliders by layer: " + Format(colliders));
        }

        // ---- Lighting -----------------------------------------------------------

        private static void Lighting()
        {
            Light sun = RenderSettings.sun;
            Line("RenderSettings.sun: " + (sun != null ? sun.name + " (" + sun.type + ", shadows " + sun.shadows + ", intensity " + sun.intensity.ToString("F2") + ")" : "none"));
            Line("Ambient: mode " + RenderSettings.ambientMode + ", intensity " + RenderSettings.ambientIntensity.ToString("F2") +
                 ", skybox " + (RenderSettings.skybox != null ? RenderSettings.skybox.name : "none") +
                 ", reflection mode " + RenderSettings.defaultReflectionMode);

            Light[] lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            int directional = 0, shadowed = 0;
            foreach (Light light in lights)
            {
                if (light.type == LightType.Directional) directional++;
                if (light.shadows != LightShadows.None) shadowed++;
            }
            Line("Lights in the scene: " + lights.Length + " (" + directional + " directional, " + shadowed + " casting shadows)");
        }

        // ---- Helpers ------------------------------------------------------------

        private static void Count(Dictionary<int, int> counts, int layer)
        {
            int n;
            counts.TryGetValue(layer, out n);
            counts[layer] = n + 1;
        }

        private static string Format(Dictionary<int, int> counts)
        {
            if (counts.Count == 0) return "none";
            List<string> items = new List<string>();
            foreach (KeyValuePair<int, int> pair in counts)
                items.Add(pair.Key + " '" + LayerMask.LayerToName(pair.Key) + "' x" + pair.Value);
            return string.Join("; ", items.ToArray());
        }

        // "Default, UI, 14:Parts..." for a culling mask.
        private static string MaskText(int mask)
        {
            if (mask == -1) return "everything";
            List<string> layers = new List<string>();
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) layers.Add(i.ToString());
            return "layers " + string.Join(",", layers.ToArray());
        }

        private static string Read(object target, string member)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(member);
                return property != null ? Convert.ToString(property.GetValue(target, null)) : "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
