using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ArmorFragments
{
    // Impact camera, in the style of War Thunder: when a shell you fired hits another vehicle, a small
    // window in the top right corner shows the shot arriving and what it does. It stays on the vehicle
    // that was hit (the camera is attached to it), and renders ONLY that vehicle and the mod's rays on a
    // plain grey background, which keeps it cheap. Nothing exists while it is not in use.
    //
    // What it shows is the ray the mod already draws (at the user's visual speed), so it is a slow replay
    // of the impact, started when the impact has been worked out.
    //
    // Phase 1: camera, window, lifetime. (Later: rays attached to the hit part, the camera's own X-ray.)
    internal static class ImpactCamera
    {
        // The mod's rays and bubbles live here ('Mod Layer 1': empty, and drawn by the main camera too).
        public const int RayLayer = 28;

        // Layers that are never drawn for the vehicle: default (world props, our own leftovers), water,
        // UI, gizmos, explosion effects, terrain.
        private const int ExcludedLayers = (1 << 0) | (1 << 4) | (1 << 5) | (1 << 10) | (1 << 19) | (1 << 20);

        private static readonly Color Background = new Color(0.32f, 0.33f, 0.36f, 1f);
        private const float MinShowSeconds = 2f;   // even if no ray was drawn

        private static GameObject cameraObject;
        private static Camera camera;
        private static RenderTexture texture;
        private static GameObject panel;
        private static Canvas canvas;
        private static RectTransform borderRect;
        private static RectTransform imageRect;

        private static AircraftScript targetAircraft;
        private static float startTime;
        private static bool active;

        public static bool IsActive
        {
            get { return active; }
        }

        // ---- Starting --------------------------------------------------------------

        // A shell hit 'hit' at 'point', flying along 'direction'. 'shooter' is the aircraft that fired it.
        public static void OnShellImpact(PartScript hit, Vector3 point, Vector3 direction, AircraftScript shooter)
        {
            if (!ArmorFragmentsPlugin.ImpactCameraEnabled.Value) return;
            if (hit == null || shooter == null || direction.sqrMagnitude < 0.0001f) return;

            // Only your own shots.
            FlightScenePlayer local = FlightSceneScript.Instance != null ? FlightSceneScript.Instance.LocalPlayer : null;
            if (local == null || local.Aircraft != shooter) return;

            // Any vehicle but your own.
            AircraftScript aircraft = hit.Aircraft;
            if (aircraft == null || aircraft == shooter) return;

            if (active)
            {
                // More hits on the same vehicle: the camera stays where it is (the timer follows the rays).
                if (aircraft == targetAircraft) return;
                if (!ArmorFragmentsPlugin.ImpactCameraSwitchTargets.Value) return;
                Stop();
            }

            try
            {
                Start(hit, aircraft, point, direction.normalized);
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Impact camera failed to start: " + e);
                Stop();
            }
        }

        private static void Start(PartScript hit, AircraftScript aircraft, Vector3 point, Vector3 direction)
        {
            targetAircraft = aircraft;
            startTime = Time.time;

            int mask = LayersOf(aircraft) | (1 << RayLayer);
            CreateTexture();
            CreateCamera(mask);
            CreatePanel();

            // Behind the shell, a bit to the side and above, so the ray is seen from an angle and not end-on.
            float lead = FragmentSpawner.ShellLeadIn;
            Vector3 right = Vector3.Cross(Vector3.up, direction);
            if (right.sqrMagnitude < 0.01f) right = Vector3.right;
            right.Normalize();
            Vector3 position = point - direction * lead + right * (0.35f * lead) + Vector3.up * (0.2f * lead);

            camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(point - position, Vector3.up));
            camera.transform.SetParent(hit.transform, true);   // it goes where the vehicle goes
            camera.farClipPlane = Mathf.Max(60f, lead * 6f);
            camera.enabled = true;

            active = true;
            ArmorFragmentsPlugin.Log.LogInfo(
                "Impact camera on: '" + hit.Part.Name + "', " + lead.ToString("F0") + " m behind the impact");
        }

        // The layers the vehicle's visible meshes are on, so the camera can draw it and nothing else.
        // Meshes on a layer shared with the world (the default one) are not drawn; they are listed in the log.
        private static int LayersOf(AircraftScript aircraft)
        {
            int mask = 0;
            Dictionary<string, int> skipped = new Dictionary<string, int>();

            foreach (PartData data in aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;

                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>())
                {
                    if (!renderer.enabled) continue;

                    int layer = renderer.gameObject.layer;
                    if ((ExcludedLayers & (1 << layer)) != 0)
                    {
                        if (layer == 0)
                        {
                            string key = renderer.GetType().Name + " '" + renderer.gameObject.name + "'";
                            int count;
                            skipped.TryGetValue(key, out count);
                            skipped[key] = count + 1;
                        }
                        continue;
                    }
                    mask |= 1 << layer;
                }
            }

            if (skipped.Count > 0)
            {
                List<string> names = new List<string>();
                foreach (KeyValuePair<string, int> pair in skipped)
                {
                    if (names.Count >= 8) break;
                    names.Add(pair.Key + " x" + pair.Value);
                }
                ArmorFragmentsPlugin.Log.LogInfo(
                    "Impact camera: enabled meshes on the default layer are not drawn: " + string.Join("; ", names.ToArray()));
            }
            return mask;
        }

        // ---- Camera, texture and window --------------------------------------------

        private static void CreateTexture()
        {
            int height = Mathf.Clamp(ArmorFragmentsPlugin.ImpactCameraResolution.Value, 64, 2048);
            int width = Mathf.RoundToInt(height * 4f / 3f);
            texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            texture.name = "ArmorImpactCamera";
            texture.Create();
        }

        private static void CreateCamera(int mask)
        {
            cameraObject = new GameObject("ArmorImpactCamera");
            camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.cullingMask = mask;
            camera.nearClipPlane = 0.1f;
            camera.fieldOfView = Mathf.Clamp(ArmorFragmentsPlugin.ImpactCameraFov.Value, 10f, 100f);
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.allowDynamicResolution = false;
            camera.useOcclusionCulling = false;
            camera.targetTexture = texture;
            camera.depth = -10f;
            ConfigureUniversalCamera(camera);
        }

        // The game renders with URP. Its per-camera settings are set by reflection (no reference to the
        // URP assembly is needed): no shadows (the main camera already paid for them), no post-processing,
        // no anti-aliasing, no extra depth or color copies.
        private static void ConfigureUniversalCamera(Camera target)
        {
            System.Type type = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            if (type == null)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Impact camera: URP camera settings not found; using the defaults");
                return;
            }

            Component data = target.gameObject.GetComponent(type) ?? target.gameObject.AddComponent(type);
            SetProperty(data, "renderShadows", ArmorFragmentsPlugin.ImpactCameraShadows.Value);
            SetProperty(data, "renderPostProcessing", false);
            SetProperty(data, "antialiasing", 0);          // none
            SetProperty(data, "requiresDepthOption", 0);   // off
            SetProperty(data, "requiresColorOption", 0);   // off
            SetProperty(data, "stopNaN", false);
            SetProperty(data, "dithering", false);
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name);
            if (property == null || !property.CanWrite) return;
            try
            {
                object converted = property.PropertyType.IsEnum ? System.Enum.ToObject(property.PropertyType, value) : value;
                property.SetValue(target, converted, null);
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Impact camera: could not set " + name + ": " + e.Message);
            }
        }

        private static System.Type FindType(string fullName)
        {
            foreach (Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        // A canvas over the screen with a dark border and the camera's picture, in the top right corner.
        private static void CreatePanel()
        {
            panel = new GameObject("ArmorImpactCameraPanel");
            canvas = panel.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;

            GameObject border = new GameObject("Border");
            border.transform.SetParent(panel.transform, false);
            Image borderImage = border.AddComponent<Image>();
            borderImage.color = new Color(0f, 0f, 0f, 0.85f);
            borderImage.raycastTarget = false;
            borderRect = border.GetComponent<RectTransform>();

            GameObject picture = new GameObject("Picture");
            picture.transform.SetParent(panel.transform, false);
            RawImage image = picture.AddComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            imageRect = picture.GetComponent<RectTransform>();

            foreach (RectTransform rect in new[] { borderRect, imageRect })
            {
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
            }
            Layout();
        }

        // Size and place the window (the settings may change while it is open).
        private static void Layout()
        {
            if (imageRect == null || borderRect == null || texture == null) return;

            float width = Screen.width * Mathf.Clamp(ArmorFragmentsPlugin.ImpactCameraWidthPercent.Value, 5f, 80f) / 100f;
            float height = width * texture.height / texture.width;
            float margin = ArmorFragmentsPlugin.ImpactCameraMargin.Value;
            const float border = 3f;

            borderRect.anchoredPosition = new Vector2(-margin, -margin);
            borderRect.sizeDelta = new Vector2(width + 2f * border, height + 2f * border);
            imageRect.anchoredPosition = new Vector2(-margin - border, -margin - border);
            imageRect.sizeDelta = new Vector2(width, height);
        }

        // ---- Every frame -----------------------------------------------------------

        public static void Tick()
        {
            HandleKey();
            if (!active) return;

            if (!ArmorFragmentsPlugin.ImpactCameraEnabled.Value || camera == null || targetAircraft == null ||
                FlightSceneScript.Instance == null)
            {
                Stop();
                return;
            }

            // Stay until the last ray of the last impact has finished, plus the linger time.
            float end = Mathf.Max(startTime + MinShowSeconds, RayDrawer.LastEndTime) + ArmorFragmentsPlugin.ImpactCameraLinger.Value;
            if (Time.time > end)
            {
                Stop();
                return;
            }

            // Hidden together with the rest of the flight UI; the camera rests while nobody sees it.
            bool show = FlightSceneScript.Instance.FlightUI == null || FlightSceneScript.Instance.FlightUI.Visible;
            canvas.enabled = show;
            camera.enabled = show;
            Layout();
        }

        // The key switches the impact camera on and off for good (it is a setting).
        private static void HandleKey()
        {
            try
            {
                Key key = ArmorFragmentsPlugin.ImpactCameraKey.Value;
                Keyboard keyboard = Keyboard.current;
                if (key == Key.None || keyboard == null || !keyboard[key].wasPressedThisFrame) return;

                bool on = !ArmorFragmentsPlugin.ImpactCameraEnabled.Value;
                ArmorFragmentsPlugin.ImpactCameraEnabled.Value = on;
                ArmorFragmentsPlugin.Log.LogInfo("Impact camera " + (on ? "enabled" : "disabled"));
                if (!on) Stop();
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Impact camera key failed: " + e);
            }
        }

        // Frees everything: with the camera off nothing is rendered and no memory is held.
        public static void Stop()
        {
            if (active) ArmorFragmentsPlugin.Log.LogInfo("Impact camera off");
            active = false;
            targetAircraft = null;

            if (cameraObject != null) Object.Destroy(cameraObject);
            if (panel != null) Object.Destroy(panel);
            if (texture != null)
            {
                texture.Release();
                Object.Destroy(texture);
            }
            cameraObject = null;
            camera = null;
            panel = null;
            canvas = null;
            borderRect = null;
            imageRect = null;
            texture = null;
        }
    }
}
