using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Cameras;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ArmorFragments
{
    // Impact camera, in the style of War Thunder: when a shell you fired hits another vehicle, a small
    // window in the top right corner shows the shot arriving and what it does. The camera flies behind
    // the shell's ray as it approaches, stops at the impact, and stays with the vehicle that was hit. It
    // renders ONLY that vehicle and the mod's rays on a plain grey background, which keeps it cheap.
    // Nothing exists while it is not in use.
    //
    // What it shows is the ray the mod draws (at the user's visual speed, attached to the hit part), so it
    // is a slow replay of the impact, started when the impact has been worked out.
    internal static class ImpactCamera
    {
        // The mod's rays and bubbles live here ('Mod Layer 1': empty, and drawn by the main camera too).
        public const int RayLayer = 28;

        // Layers that are never drawn for the vehicle: default (world props, our own leftovers), water,
        // UI, gizmos, explosion effects, terrain.
        private const int ExcludedLayers = (1 << 0) | (1 << 4) | (1 << 5) | (1 << 10) | (1 << 19) | (1 << 20);

        private static readonly Color Background = new Color(0.32f, 0.33f, 0.36f, 1f);
        private const float MinShowSeconds = 1f;   // the 'impact' time when no ray was drawn (rays off)

        private static GameObject cameraObject;
        private static Camera camera;
        private static RenderTexture texture;
        private static GameObject panel;
        private static Canvas canvas;
        private static RawImage image;
        private static RectTransform borderRect;
        private static RectTransform backingRect;
        private static RectTransform imageRect;

        private static AircraftScript targetAircraft;
        private static PartScript anchorPart;      // the part that was hit; the camera moves with it
        private static Vector3 impactLocal;        // the impact point and the shot's direction, in that part's space
        private static Vector3 directionLocal;
        private static float startTime;
        private static bool active;
        private static bool shadowsApplied;

        // The camera's own X-ray: built while the camera flies in, switched to when the shell arrives
        // (the picture dips to dark and back while the real vehicle gives way to the ghosts).
        private enum XState { Off, Waiting, FadeOut, FadeIn, Shown }
        private static ImpactXRay xray;
        private static XState xstate;
        private static float xstateStart;
        private static float waitStart = -1f;   // when the shell reached the impact and the X-ray started waiting to switch
        private const float MaxWaitSeconds = 0.3f;   // then it switches anyway; parts still being built appear as they are done
        private static bool mainHadGhostLayer;   // did the main camera draw the ghost layer before we turned it off?
        private static int mainHiddenLayer = -1;
        private const float FadeOutSeconds = 0.12f;
        private const float FadeInSeconds = 0.2f;

        // Following the shell's ray
        private static TracerAnimation tracer;
        private static int serialAtStart;
        private static bool hadTracer;
        private static bool followDone;
        private static float firstSegment;         // meters of the shell's flight before the impact

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
                // More hits on the same vehicle: the camera stays where it is (the timer follows the impacts).
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
            anchorPart = hit;
            impactLocal = hit.transform.InverseTransformPoint(point);
            directionLocal = hit.transform.InverseTransformDirection(direction);
            startTime = Time.time;

            // The shell's ray is drawn right after this call: the camera follows the first one.
            serialAtStart = RayDrawer.InitialSerial;
            tracer = null;
            hadTracer = false;
            firstSegment = FragmentSpawner.ShellLeadIn;
            followDone = !ArmorFragmentsPlugin.DrawRays.Value;   // no ray drawn, nothing to follow

            int mask = LayersOf(aircraft) | (1 << RayLayer);
            CreateTexture(ArmorFragmentsPlugin.ImpactCameraResolution.Value);
            CreateCamera(mask);
            CreatePanel();

            // The X-ray ghosts are made a few at a time over the next frames; the damage the parts have
            // right now is noted here, before this impact's own damage is applied.
            xray = null;
            xstate = XState.Off;
            waitStart = -1f;
            if (ArmorFragmentsPlugin.ImpactCameraXRay.Value)
            {
                ImpactXRay candidate = new ImpactXRay(aircraft, point);
                if (candidate.Available)
                {
                    xray = candidate;
                    xstate = XState.Waiting;
                    HideFromMainCamera(candidate.GhostLayer);
                }
            }

            active = true;
            UpdatePose();
            camera.enabled = true;

            ArmorFragmentsPlugin.Log.LogInfo(
                "Impact camera on: '" + hit.Part.Name + "', " +
                ArmorFragmentsPlugin.ImpactCameraDistance.Value.ToString("F1") + " m from the impact, shot shown from " +
                FragmentSpawner.ShellLeadIn.ToString("F0") + " m before it");
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

        private static RenderTexture MakeTexture(int height)
        {
            height = Mathf.Clamp(height, 64, 2048);
            int width = Mathf.RoundToInt(height * 4f / 3f);
            RenderTexture result = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            result.name = "ArmorImpactCamera";
            result.Create();
            return result;
        }

        private static void CreateTexture(int height)
        {
            texture = MakeTexture(height);
        }

        // The picture height setting changed while the camera is open: a new texture of that size.
        private static void ResizeTexture(int height)
        {
            RenderTexture old = texture;
            texture = MakeTexture(height);
            camera.targetTexture = texture;
            if (image != null) image.texture = texture;

            if (old != null)
            {
                old.Release();
                Object.Destroy(old);
            }
        }

        private static void CreateCamera(int mask)
        {
            cameraObject = new GameObject("ArmorImpactCamera");
            camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.cullingMask = mask;
            camera.nearClipPlane = 0.05f;
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
        // URP assembly is needed): shadows as the setting says, no post-processing, no anti-aliasing,
        // no extra depth or color copies.
        private static void ConfigureUniversalCamera(Camera target)
        {
            System.Type type = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            if (type == null)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Impact camera: URP camera settings not found; using the defaults");
                return;
            }

            shadowsApplied = ArmorFragmentsPlugin.ImpactCameraShadows.Value;
            Component data = target.gameObject.GetComponent(type) ?? target.gameObject.AddComponent(type);
            SetProperty(data, "renderShadows", shadowsApplied);
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
            foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
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

            // Dark behind the picture, so it can dip to dark when it changes look.
            GameObject back = new GameObject("Backing");
            back.transform.SetParent(panel.transform, false);
            Image backImage = back.AddComponent<Image>();
            backImage.color = new Color(0.07f, 0.07f, 0.08f, 1f);
            backImage.raycastTarget = false;
            backingRect = back.GetComponent<RectTransform>();

            GameObject picture = new GameObject("Picture");
            picture.transform.SetParent(panel.transform, false);
            image = picture.AddComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            imageRect = picture.GetComponent<RectTransform>();

            foreach (RectTransform rect in new[] { borderRect, backingRect, imageRect })
            {
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
            }
            Layout();
        }

        // Size and place the window (the settings may change while it is open).
        private static void Layout()
        {
            if (imageRect == null || borderRect == null || backingRect == null || texture == null) return;

            float width = Screen.width * Mathf.Clamp(ArmorFragmentsPlugin.ImpactCameraWidthPercent.Value, 5f, 80f) / 100f;
            float height = width * texture.height / texture.width;
            float margin = ArmorFragmentsPlugin.ImpactCameraMargin.Value;
            const float border = 3f;

            borderRect.anchoredPosition = new Vector2(-margin, -margin);
            borderRect.sizeDelta = new Vector2(width + 2f * border, height + 2f * border);
            imageRect.anchoredPosition = new Vector2(-margin - border, -margin - border);
            imageRect.sizeDelta = new Vector2(width, height);
            backingRect.anchoredPosition = imageRect.anchoredPosition;
            backingRect.sizeDelta = imageRect.sizeDelta;
        }

        // ---- The camera's movement -------------------------------------------------

        // Behind the shell by 'Distance' meters, a little to the side and above (so the ray is seen from an
        // angle, not end-on), looking at the impact. While the shell flies, the camera flies with it; once
        // the shell has arrived it stays, and keeps moving with the hit part.
        private static void UpdatePose()
        {
            if (anchorPart == null || camera == null) return;

            Transform part = anchorPart.transform;
            Vector3 impact = part.TransformPoint(impactLocal);
            Vector3 direction = part.TransformDirection(directionLocal).normalized;

            float progress = followDone ? firstSegment
                : (tracer != null ? Mathf.Min(tracer.HeadDistance, firstSegment) : 0f);
            float distance = Mathf.Max(ArmorFragmentsPlugin.ImpactCameraDistance.Value, 0.2f);

            Vector3 right = Vector3.Cross(Vector3.up, direction);
            if (right.sqrMagnitude < 0.01f) right = Vector3.right;
            right.Normalize();

            float back = (firstSegment - progress) + distance;
            Vector3 position = impact - direction * back + right * (0.35f * distance) + Vector3.up * (0.2f * distance);
            camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(impact - position, Vector3.up));
            camera.farClipPlane = Mathf.Max(60f, (firstSegment + distance) * 6f);
        }

        // Finds the shell's ray and checks whether it has reached the impact.
        private static void UpdateFollow()
        {
            if (followDone) return;

            if (tracer == null && !hadTracer && RayDrawer.InitialSerial > serialAtStart)
            {
                tracer = RayDrawer.InitialTracer;
                hadTracer = tracer != null;
            }

            if (hadTracer && tracer == null)
            {
                followDone = true;   // the ray is gone: it has long reached the impact
            }
            else if (tracer != null)
            {
                if (tracer.FirstSegment > 0.01f) firstSegment = tracer.FirstSegment;
                if (tracer.HeadDistance >= firstSegment - 0.01f) followDone = true;
            }
            else if (Time.time - startTime > 1f)
            {
                followDone = true;   // no ray appeared
            }
        }

        // ---- Every frame -----------------------------------------------------------

        public static void Tick()
        {
            HandleKey();
            if (!active) return;

            if (!ArmorFragmentsPlugin.ImpactCameraEnabled.Value || camera == null || targetAircraft == null ||
                anchorPart == null || FlightSceneScript.Instance == null)
            {
                Stop();
                return;
            }

            // Closes a while after the impact, counted from the moment the shell's ray reaches it (the
            // last impact on this vehicle, if there were several).
            float impactTime = RayDrawer.LastImpactTime > startTime ? RayDrawer.LastImpactTime : startTime + MinShowSeconds;
            float end = impactTime + ArmorFragmentsPlugin.ImpactCameraLinger.Value;
            if (Time.time > end)
            {
                Stop();
                return;
            }

            ApplySettings();
            UpdateFollow();
            UpdateXRay();
            UpdatePose();

            // Hidden together with the rest of the flight UI; the camera rests while nobody sees it.
            bool show = FlightSceneScript.Instance.FlightUI == null || FlightSceneScript.Instance.FlightUI.Visible;
            canvas.enabled = show;
            camera.enabled = show;
            Layout();
        }

        // Builds the X-ray ghosts, shows damage as it arrives, and switches the picture to them when the
        // shell has reached the impact.
        private static void UpdateXRay()
        {
            if (xray == null) return;

            HideFromMainCamera(xray.GhostLayer);        // in case the game reset the main camera's layers
            // Build as fast as is bearable: gently while the camera flies in, harder once the shell has arrived.
            if (!xray.BuildDone) xray.BuildStep(followDone ? 14f : 6f);
            xray.Tick(camera.transform.position, followDone);

            float seconds = Time.time - xstateStart;
            switch (xstate)
            {
                case XState.Waiting:
                    if (followDone)
                    {
                        if (waitStart < 0f) waitStart = Time.time;
                        // Switch when everything is built, or after a short wait: the parts nearest the impact
                        // are built first, and the rest appear as they are finished.
                        if (xray.BuildDone || Time.time - waitStart > MaxWaitSeconds)
                        {
                            xstate = XState.FadeOut;
                            xstateStart = Time.time;
                        }
                    }
                    break;

                case XState.FadeOut:
                    SetPictureAlpha(Mathf.Lerp(1f, 0.05f, Mathf.Clamp01(seconds / FadeOutSeconds)));
                    if (seconds >= FadeOutSeconds)
                    {
                        // The real vehicle is no longer drawn; the ghosts (on their own layer) are.
                        camera.cullingMask = (1 << xray.GhostLayer) | (1 << RayLayer);
                        ArmorFragmentsPlugin.Log.LogInfo("Impact camera X-ray on: " + xray.Describe());
                        xstate = XState.FadeIn;
                        xstateStart = Time.time;
                    }
                    break;

                case XState.FadeIn:
                    SetPictureAlpha(Mathf.Lerp(0.05f, 1f, Mathf.Clamp01(seconds / FadeInSeconds)));
                    if (seconds >= FadeInSeconds)
                    {
                        SetPictureAlpha(1f);
                        xstate = XState.Shown;
                    }
                    break;
            }
        }

        // The ghosts must be seen only by the impact camera: the main camera stops drawing their layer while
        // the impact camera is open, and gets it back afterwards.
        private static Camera MainCamera()
        {
            CameraManagerScript manager = CameraManagerScript.Instance;
            return manager != null && manager.MainCamera != null ? manager.MainCamera : Camera.main;
        }

        private static void HideFromMainCamera(int layer)
        {
            Camera main = MainCamera();
            if (main == null || layer == RayLayer) return;

            if (mainHiddenLayer != layer)
            {
                mainHadGhostLayer = (main.cullingMask & (1 << layer)) != 0;
                mainHiddenLayer = layer;
            }
            main.cullingMask &= ~(1 << layer);
        }

        private static void RestoreMainCamera()
        {
            if (mainHiddenLayer >= 0 && mainHadGhostLayer)
            {
                Camera main = MainCamera();
                if (main != null) main.cullingMask |= 1 << mainHiddenLayer;
            }
            mainHiddenLayer = -1;
        }

        private static void SetPictureAlpha(float alpha)
        {
            if (image != null) image.color = new Color(1f, 1f, 1f, alpha);
        }

        // What the fragments of an impact did to each part (only parts of the camera's vehicle matter).
        public static void OnImpactResolved(Dictionary<PartScript, DamageRecord> sink, float impactTime)
        {
            if (active && xray != null) xray.AddEvents(sink, impactTime);
        }

        // Settings that can change while the camera is open take effect at once.
        private static void ApplySettings()
        {
            camera.fieldOfView = Mathf.Clamp(ArmorFragmentsPlugin.ImpactCameraFov.Value, 10f, 100f);

            if (ArmorFragmentsPlugin.ImpactCameraShadows.Value != shadowsApplied)
                ConfigureUniversalCamera(camera);

            int height = Mathf.Clamp(ArmorFragmentsPlugin.ImpactCameraResolution.Value, 64, 2048);
            if (texture != null && texture.height != height) ResizeTexture(height);
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
            anchorPart = null;
            tracer = null;
            if (xray != null)
            {
                xray.Dispose();
                xray = null;
            }
            xstate = XState.Off;
            RestoreMainCamera();

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
            image = null;
            borderRect = null;
            backingRect = null;
            imageRect = null;
            texture = null;
        }
    }
}
