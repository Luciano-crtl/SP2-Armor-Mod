using System;
using System.Xml.Linq;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.UI;
using Jundroo.Juicy.Widgets;
using UnityEngine;

namespace ArmorFragments
{
    // The mouse aim button on the flight screen: bottom right, above the game's row of buttons (recenter view, enter/exit, change view), made
    // like them (the game's "btn btn-flight": dark, half transparent, rounded) with a mouse drawn on it. Blue while mouse aim is on. A click
    // switches it on or off (hold Alt first: the cursor is locked while the aim is on). Shown only for a vehicle that has Mouse aim guns.
    internal static class MouseAimButton
    {
        private const string Id = "af-mouse-aim-button";
        private const float Size = 50f;           // as the game's own buttons in that row
        private const float RightMargin = 15f;
        private const float Bottom = 95f;          // above the row (see TurretGroupsHud)
        public const float Height = Size + 10f;    // what the group bars stack above

        private static Widget button;
        private static ImageWidget fill;
        private static Sprite icon;
        private static Texture2D iconTexture;
        private static int shownState = -1;
        private static float nextAttempt;
        private static Assets.Scripts.Craft.AircraftScript checkedFor;
        private static bool vehicleHasAim;

        private static readonly Color Blue = new Color(0x28 / 255f, 0x59 / 255f, 0x9E / 255f, 1f);

        public static void Tick()
        {
            try
            {
                FlightSceneScript scene = FlightSceneScript.Instance;
                Assets.Scripts.Craft.AircraftScript aircraft = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
                if (aircraft != checkedFor)
                {
                    checkedFor = aircraft;
                    vehicleHasAim = HasMouseAimGuns(aircraft);
                }
                if (aircraft == null || !vehicleHasAim)
                {
                    if (button != null) Clear();
                    return;
                }
                if (button == null)
                {
                    if (Time.unscaledTime < nextAttempt) return;
                    nextAttempt = Time.unscaledTime + 0.5f;
                    Build(scene);
                    if (button == null) return;
                }
                int state = TurretAim.Active ? 1 : 0;
                if (state != shownState && fill != null)
                {
                    shownState = state;
                    fill.Color.Base = Blue;
                    fill.Image.color = Blue;
                    fill.Image.fillAmount = state;
                }
                Transform t = button.transform;
                if (t != null && t.GetSiblingIndex() != 0) t.SetAsFirstSibling();   // under the game's other menus
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Mouse aim button failed, it is removed: " + e);
                Clear();
                nextAttempt = Time.unscaledTime + 30f;
            }
        }

        private static bool HasMouseAimGuns(Assets.Scripts.Craft.AircraftScript aircraft)
        {
            if (aircraft == null) return false;
            foreach (Assets.Scripts.Craft.Parts.PartData data in aircraft.Aircraft.Assembly.Parts)
                if (data != null && data.PartScript != null && TagCodec.Parse(data.Name).Pointer != null && data.PartScript.GetModifier<CannonScript>() != null)
                    return true;
            return false;
        }

        private static void Build(FlightSceneScript scene)
        {
            FlightUIScript ui = scene != null ? scene.FlightUI : null;
            Widget root = ui != null ? ui.RootWidget : null;
            Widget mainUi = root != null ? root.FindWidget("main-ui") : null;
            if (mainUi == null) return;
            Widget old = mainUi.FindWidget(Id);
            if (old != null) old.Destroy();

            string xml = "<Button id='" + Id + "' class='btn btn-flight' anchor='BottomRight' position='-" + Number(RightMargin) + " " + Number(Bottom) + "' width='" + Number(Size) +
                         "' height='" + Number(Size) + "' mask='EnabledAndShown' tooltipPosition='Left' " +
                         "tooltip='Mouse aim\\nOn: the mouse aims the guns marked Mouse aim and left click fires.\\nHold Alt to free the cursor and click here to switch it off or on.'>" +
                         "<Image id='" + Id + "-fill' sprite='@SpriteWhite' color='#28599E' type='Filled' fillMethod='Horizontal' fillAmount='0' />" +
                         "</Button>";
            button = mainUi.Context.CreateWidget(XElement.Parse(xml), mainUi, mainUi.Stylesheet);
            if (button == null) return;
            button.transform.SetAsFirstSibling();
            button.Clicked += w => TurretAim.ToggleByHand();
            fill = button.FindWidget<ImageWidget>(Id + "-fill");
            // The mouse is a plain Unity image of its own on the button (a widget's image takes its sprite from the stylesheet again).
            GameObject iconObject = new GameObject(Id + "-icon", typeof(RectTransform));
            RectTransform rect = (RectTransform)iconObject.transform;
            rect.SetParent(button.transform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(32f, 32f);
            UnityEngine.UI.Image image = iconObject.AddComponent<UnityEngine.UI.Image>();
            image.sprite = Icon();
            image.preserveAspect = true;
            image.color = Color.white;
            image.raycastTarget = false;
            shownState = -1;
        }

        // A computer mouse, drawn white on clear (the game's icons are white shapes): the body's outline, the split between the two buttons,
        // and the wheel. Anti-aliased by its distance to the strokes.
        private static Sprite Icon()
        {
            if (icon != null) return icon;
            const int N = 64;
            iconTexture = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments mouse icon" };
            Color32[] pixels = new Color32[N * N];
            const float halfW = 15f, halfH = 23f, radius = 14f, stroke = 2.6f;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float px = x + 0.5f - N / 2f, py = y + 0.5f - N / 2f;   // y up
                    // The body: a rounded rectangle; its outline is where the distance to the edge is small.
                    float qx = Mathf.Abs(px) - (halfW - radius), qy = Mathf.Abs(py) - (halfH - radius);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    float d = Mathf.Abs(outside) - stroke * 0.5f;
                    // The split between the buttons (the upper part's middle line) and the line under them.
                    if (outside < 0f)
                    {
                        if (py > 4f) d = Mathf.Min(d, Mathf.Abs(px) - stroke * 0.4f);
                        d = Mathf.Min(d, Mathf.Abs(py - 4f) - stroke * 0.4f);
                    }
                    // The wheel: a short rounded bar across the split.
                    float wy = Mathf.Clamp(py, 10f, 16f);
                    d = Mathf.Min(d, new Vector2(px, py - wy).magnitude - 2.6f);
                    float alpha = Mathf.Clamp01(0.5f - d);
                    pixels[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            iconTexture.SetPixels32(pixels);
            iconTexture.Apply(false, true);
            icon = Sprite.Create(iconTexture, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            icon.name = "ArmorFragments mouse icon";
            return icon;
        }

        private static string Number(float value)
        {
            return value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Clear()
        {
            try
            {
                if (button != null) button.Destroy();
            }
            catch (Exception) { }
            button = null;
            fill = null;
            shownState = -1;
        }

        // RELEASE-STRIP-BEGIN
        public static void Shutdown()
        {
            Clear();
            if (icon != null) UnityEngine.Object.Destroy(icon);
            if (iconTexture != null) UnityEngine.Object.Destroy(iconTexture);
            icon = null;
            iconTexture = null;
        }
        // RELEASE-STRIP-END
    }
}
