using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Design;
using Assets.Scripts.Design.UI;
using HarmonyLib;
using Jundroo.Juicy.Widgets;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ArmorFragments
{
    // Designer: the armor view. A shield button at the bottom right (beside the XML overload button) turns it on and
    // off; it starts off each time the designer opens. While it is on, every armored face of the craft is colored by
    // its thickness: the armored faces of blocks armored per face (open faces are not), the armored walls of holes
    // carved into them, and the whole surface of parts armored as a whole. The thicknesses on the craft are ranked,
    // thinnest the lightest green to thickest the darkest red, and listed with their colors at the top left. Parts
    // without armor get no color. Drawn after the game's see-through parts, so it stays on in its X-ray view. While it
    // is on it replaces the edit tint (FaceHighlight) of the part being edited.
    internal static class ArmorView
    {
        public const string ButtonId = "button-armor-view";
        private const string IconId = "button-armor-view-icon";
        private const string LayoutPath = "Xml/Design/Designer";
        private const string XrayButtonId = "button-ghost-view";
        private const string OverloadButtonId = "btn-overload-xml";
        private const float RefreshInterval = 0.5f;

        private sealed class Entry
        {
            public Mesh Mesh;               // null: the part has no armored face
            public Transform Frame;
            public Transform LayerSource;   // the object whose layer the part is drawn on
            public string Signature;
            public float[] Mm;              // the armor's thickness under each vertex of Mesh
            public Vector3[] Vertices;      // Mesh's vertices (Frame's space) and its outer surface's triangles, for the hover readout
            public int[] Outer;
        }

        // The thicknesses on the craft, thinnest first (whole millimeters), and their colors: ranked from the lightest
        // green (thinnest) through yellow and orange to the darkest red (thickest), whatever the values are.
        private static readonly List<float> Legend = new List<float>();
        private static readonly List<Color> LegendColors = new List<Color>();
        private const float Alpha = 1f;   // solid: the armor view shows the armor, not the paint under it
        private static readonly Color[] Ramp =
        {
            new Color(0.62f, 0.96f, 0.45f),   // light green
            new Color(0.80f, 0.90f, 0.12f),   // yellow green
            new Color(0.98f, 0.78f, 0.08f),   // amber
            new Color(0.96f, 0.48f, 0.05f),   // orange
            new Color(0.85f, 0.12f, 0.05f),   // red
            new Color(0.45f, 0.00f, 0.02f),   // dark red
        };

        public static bool On { get; private set; }
        private static ButtonWidget button;
        private static Sprite shield;
        private static Material material;
        private static readonly Dictionary<PartScript, Entry> Parts = new Dictionary<PartScript, Entry>();
        private static float nextRefresh;
        private static bool warned;

        // ---- The button ----------------------------------------------------------

        // The game read its designer layout: the shield button goes in the row at the bottom right, just left of the
        // XML overload button and styled like it (or, without that button, after the X-ray button in the view panel).
        internal static void AddToLayout(string path, XElement layout)
        {
            if (path != LayoutPath || layout == null) return;
            if (layout.Descendants().Any(e => e.Name.LocalName == "Button" && (string)e.Attribute("id") == ButtonId)) return;
            const string tooltip = "Toggle Armor View\nColor every armored face of the craft by its thickness: thinnest light green, thickest dark red.";

            XElement overload = FindButton(layout, OverloadButtonId);
            if (overload != null)
            {
                XNamespace ns = overload.Name.Namespace;
                overload.AddBeforeSelf(new XElement(ns + "Button",
                    new XAttribute("class", "btn btn-default btn-icon right-button"),
                    new XAttribute("id", ButtonId),
                    new XAttribute("tooltipPosition", "Above"),
                    new XAttribute("tooltip", tooltip),
                    new XElement(ns + "Image", new XAttribute("id", IconId), new XAttribute("margin", "10"))));
                return;
            }

            XElement xray = FindButton(layout, XrayButtonId);
            if (xray == null)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Armor view: neither the designer's XML overload nor X-ray button was found; no armor view button");
                return;
            }
            XNamespace xns = xray.Name.Namespace;
            xray.AddAfterSelf(new XElement(xns + "Button",
                new XAttribute("class", "btn btn-default btn-icon right-button"),
                new XAttribute("id", ButtonId),
                new XAttribute("tooltip", tooltip),
                new XElement(xns + "Image", new XAttribute("id", IconId))));
        }

        private static XElement FindButton(XElement layout, string id)
        {
            return layout.Descendants().FirstOrDefault(e => e.Name.LocalName == "Button" && (string)e.Attribute("id") == id);
        }

        // The designer's UI was built: the button gets its shield and its click, and the view starts off.
        internal static void Attach(Widget root)
        {
            Clear();
            On = false;
            button = root != null ? root.FindWidget<ButtonWidget>(ButtonId) : null;
            if (button == null) return;
            ImageWidget icon = root.FindWidget<ImageWidget>(IconId);
            if (icon != null && icon.Image != null)
            {
                icon.Image.sprite = Shield();
                icon.Image.preserveAspect = true;
            }
            button.Clicked += (Widget w) => Toggle();
            UpdateButton();
        }

        private static void Toggle()
        {
            On = !On;
            if (!On) Clear();
            nextRefresh = 0f;
            UpdateButton();
        }

        // Off: the game's plain (grey) button. On: its blue, as its own view buttons show a view in use.
        private static void UpdateButton()
        {
            if (button == null) return;
            try
            {
                button.EnableClass("btn-primary", On);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Armor view: couldn't restyle the button: " + e.Message);
            }
        }

        // A plain white shield (the game tints its icons): an outline, its left half filled.
        private static Sprite Shield()
        {
            if (shield != null) return shield;
            const int size = 128;
            const int samples = 4;   // per side, for smooth edges
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ArmorViewShield",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            float u = ((x + (sx + 0.5f) / samples) / size) * 2f - 1f;    // -1 left .. 1 right
                            float v = 1f - (y + (sy + 0.5f) / samples) / size;            // 0 top .. 1 bottom
                            bool outer = InShield(u, v, 0.78f, 0.08f, 0.95f);
                            bool inner = InShield(u, v, 0.62f, 0.19f, 0.80f);
                            bool fill = InShield(u, v, 0.50f, 0.27f, 0.69f) && u < -0.04f;
                            if ((outer && !inner) || fill) covered++;
                        }
                    }
                    pixels[y * size + x] = new Color(1f, 1f, 1f, covered / (float)(samples * samples));
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            shield = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            shield.name = "ArmorViewShield";
            return shield;
        }

        // A shield: straight sides from the top down to just under halfway, then curving in to a point.
        private static bool InShield(float u, float v, float halfWidth, float top, float tip)
        {
            if (v < top || v > tip) return false;
            float shoulder = top + (tip - top) * 0.45f;
            float width = v <= shoulder
                ? halfWidth
                : halfWidth * Mathf.Cos((v - shoulder) / (tip - shoulder) * Mathf.PI * 0.5f);
            return Mathf.Abs(u) <= width;
        }

        // ---- The tint ------------------------------------------------------------

        public static void Tick()
        {
            try
            {
                Designer designer = Designer.Instance;
                if (!On || designer == null || FaceHighlight.FlightShowing())
                {
                    if (On && designer != null) Report("a flight is showing");
                    hoverShown = false;
                    if (Parts.Count > 0) Clear();
                    return;
                }
                if (Time.unscaledTime >= nextRefresh)
                {
                    nextRefresh = Time.unscaledTime + RefreshInterval;
                    Refresh(designer);
                }
                Report(Parts.Count == 0 ? "no armored part found" : null);
                Draw();
                UpdateHover(designer);
            }
            catch (Exception e)
            {
                if (!warned) ArmorFragmentsPlugin.Log.LogWarning("Armor view failed: " + e);
                warned = true;
                Clear();
            }
        }

        // Why the armor view (switched on) draws nothing, said once in the log each time the reason changes.
        private static string reported;

        private static void Report(string reason)
        {
            if (reason == reported) return;
            if (reason != null) ArmorFragmentsPlugin.Log.LogInfo("[ARMORVIEW] Armor view not drawn: " + reason);
            else if (reported != null) ArmorFragmentsPlugin.Log.LogInfo("[ARMORVIEW] Armor view drawn again");
            reported = reason;
        }

        // Each armored part's tint is made again only when its armor or shape changed; parts gone are dropped.
        private static void Refresh(Designer designer)
        {
            HashSet<PartScript> seen = new HashSet<PartScript>();
            bool changed = false;
            IEnumerable<PartData> parts = designer.Aircraft != null && designer.Aircraft.Aircraft != null && designer.Aircraft.Aircraft.Assembly != null
                ? designer.Aircraft.Aircraft.Assembly.Parts
                : null;
            if (parts != null)
            {
                foreach (PartData data in parts)
                {
                    PartScript part = data != null ? data.PartScript : null;
                    if (part == null) continue;
                    if (data.Name == null || !data.Name.Contains("[ARM")) continue;
                    seen.Add(part);

                    List<KeyValuePair<Mesh, Transform>> sources = FaceHighlight.Sources(part);
                    string signature = FaceHighlight.Signature(part, sources, UI.DesignerSection.AllFaces);
                    Entry entry;
                    if (Parts.TryGetValue(part, out entry) && entry.Signature == signature &&
                        (entry.Mesh == null || entry.Frame != null)) continue;

                    if (entry != null && entry.Mesh != null) Object.Destroy(entry.Mesh);
                    entry = new Entry { Signature = signature };
                    Transform frame;
                    List<float> mm = new List<float>();
                    entry.Mesh = FaceHighlight.BuildMesh(part, sources, UI.DesignerSection.AllFaces, out frame, mm);
                    entry.Mm = mm.ToArray();
                    if (entry.Mesh != null)
                    {
                        entry.Vertices = entry.Mesh.vertices;
                        entry.Outer = entry.Mesh.GetTriangles(0);
                    }
                    entry.Frame = frame;
                    entry.LayerSource = sources.Count > 0 && sources[0].Value != null ? sources[0].Value : part.transform;
                    Parts[part] = entry;
                    changed = true;
                }
            }

            List<PartScript> gone = null;
            foreach (PartScript part in Parts.Keys)
                if (part == null || !seen.Contains(part)) (gone ?? (gone = new List<PartScript>())).Add(part);
            if (gone != null)
            {
                foreach (PartScript part in gone)
                {
                    Entry entry = Parts[part];
                    if (entry.Mesh != null) Object.Destroy(entry.Mesh);
                    Parts.Remove(part);
                }
                changed = true;
            }
            if (changed) Recolor();
        }

        private static Material nearestMaterial;          // the tints' own depth, drawn before their colors

        // The legend from every thickness on the craft, then each part's colors from it.
        private static void Recolor()
        {
            SortedSet<float> values = new SortedSet<float>();
            foreach (Entry entry in Parts.Values)
            {
                if (entry.Mesh == null || entry.Mm == null) continue;
                foreach (float mm in entry.Mm)
                    if (mm > 0f) values.Add(Mathf.Round(mm));
            }
            Legend.Clear();
            LegendColors.Clear();
            Legend.AddRange(values);
            for (int i = 0; i < Legend.Count; i++)
                LegendColors.Add(RampAt(Legend.Count == 1 ? 0.5f : i / (float)(Legend.Count - 1)));

            foreach (Entry entry in Parts.Values)
            {
                if (entry.Mesh == null || entry.Mm == null || entry.Mm.Length != entry.Mesh.vertexCount) continue;
                Color[] colors = new Color[entry.Mm.Length];
                for (int v = 0; v < colors.Length; v++) colors[v] = ColorOf(entry.Mm[v]);
                entry.Mesh.colors = colors;
            }
        }

        private static Color ColorOf(float mm)
        {
            int index = Legend.BinarySearch(Mathf.Round(mm));
            Color color = index >= 0 ? LegendColors[index] : RampAt(0f);
            // Vertex colors reach the shader as they are, while the legend's (GUI) colors are converted: in the game's
            // linear color space the face gets the linear value, so both show the same color on screen.
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.linear;
            color.a = Alpha;
            return color;
        }

        // 0: thinnest (light green) .. 1: thickest (dark red).
        private static Color RampAt(float at)
        {
            float scaled = Mathf.Clamp01(at) * (Ramp.Length - 1);
            int low = Mathf.Min(Mathf.FloorToInt(scaled), Ramp.Length - 2);
            return Color.Lerp(Ramp[low], Ramp[low + 1], scaled - low);
        }

        // The legend at the top left: one row per thickness on the craft, thinnest at the top. With very many
        // thicknesses, an even pick of them (always the thinnest and the thickest).
        private const int MaxRows = 16;
        private static GUIStyle legendText;

        // The designer's UI (its menus, the flyouts that open from the left).
        internal static DesignerUIScript DesignerUi;

        // The right edge (screen pixels) of the designer menu open on the left side of the screen, sliding in or out
        // included; 0 with none.
        private static float OpenMenuRight()
        {
            DesignerUIScript ui = DesignerUi;
            if (ui == null || ui.Flyouts == null) return 0f;
            Assets.Scripts.UI.IFlyout flyout = ui.Flyouts.Selected;
            if (flyout == null || (!flyout.IsOpen && !flyout.IsClosing) || flyout.Widget == null) return 0f;
            RectTransform rect = flyout.Widget.transform as RectTransform;
            if (rect == null || !rect.gameObject.activeInHierarchy) return 0f;

            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            float left = float.MaxValue, right = float.MinValue;
            foreach (Vector3 corner in corners)
            {
                float screenX = RectTransformUtility.WorldToScreenPoint(camera, corner).x;
                left = Mathf.Min(left, screenX);
                right = Mathf.Max(right, screenX);
            }
            return left < Screen.width * 0.5f ? right : 0f;   // a menu on the right side leaves the legend be
        }

        internal static void OnGui()
        {
            if (!On || Designer.Instance == null) return;
            if (FaceHighlight.FlightShowing()) return;
            if (Event.current.type != EventType.Repaint) return;
            DrawHover();
            if (Legend.Count == 0) return;

            float scale = Mathf.Max(1f, Screen.height / 1080f);
            float row = 22f * scale, swatch = 22f * scale, width = 78f * scale;
            float x = 12f * scale, y = 90f * scale;
            float menuRight = OpenMenuRight();   // a menu open on the left: the legend moves out beside it
            if (menuRight > 0f) x = Mathf.Max(x, menuRight + 12f * scale);
            if (legendText == null)
                legendText = new GUIStyle(GUI.skin.label);
            legendText.fontSize = Mathf.RoundToInt(14f * scale);
            legendText.normal.textColor = Color.white;

            int rows = Mathf.Min(Legend.Count, MaxRows);
            GUI.color = new Color(0.12f, 0.14f, 0.17f, 0.85f);
            GUI.DrawTexture(new Rect(x, y, width, rows * row + 4f * scale), Texture2D.whiteTexture);
            for (int r = 0; r < rows; r++)
            {
                int i = rows == Legend.Count ? r : Mathf.RoundToInt(r * (Legend.Count - 1) / (float)(rows - 1));
                float top = y + 2f * scale + r * row;
                GUI.color = LegendColors[i];
                GUI.DrawTexture(new Rect(x + 2f * scale, top, swatch, row - 1f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(x + swatch + 8f * scale, top, width - swatch - 8f * scale, row),
                    Legend[i].ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " mm", legendText);
            }
            GUI.color = Color.white;
        }


        // ---- The hover readout -------------------------------------------------------------------------------------
        // With the armor view on, the armored surface under the mouse, found by a ray from the camera through the mouse: the armor there, the
        // angle that ray meets the surface at (from its normal), and the thickness a shot along it would go through (the armor over the cosine of
        // that angle). Hidden over the game's menus, while a mouse button is held and after a click until the mouse moves on (so it does not
        // cover the part being picked).

        private static bool hoverShown;
        private static float hoverMm, hoverAngle, hoverEffective;
        private static Vector2 hoverAt;              // the mouse (GUI pixels, y down)
        private static Vector2 hiddenAt;             // where a click hid it (screen pixels)
        private static bool hiddenByClick;
        private const float HideMoveDistance = 12f;  // pixels the mouse moves after a click before it shows again

        private static void UpdateHover(Designer designer)
        {
            hoverShown = false;
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || Parts.Count == 0) return;
            Vector2 screen = mouse.position.ReadValue();
            bool held = mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed;
            if (held || mouse.leftButton.wasReleasedThisFrame)
            {
                hiddenByClick = true;
                hiddenAt = screen;
                return;
            }
            if (hiddenByClick)
            {
                if ((screen - hiddenAt).magnitude < HideMoveDistance) return;
                hiddenByClick = false;
            }
            if (!OverDesignerView(designer, screen)) return;   // over the game's menus and panels

            Camera cam = null;
            try { cam = designer.CameraController != null ? designer.CameraController.Camera : null; } catch (Exception) { }
            if (cam == null || !cam.isActiveAndEnabled) return;
            Ray ray = cam.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));

            float best = float.PositiveInfinity;
            float bestMm = 0f;
            Vector3 bestNormal = Vector3.zero;
            foreach (KeyValuePair<PartScript, Entry> pair in Parts)
            {
                Entry entry = pair.Value;
                if (!Shown(pair.Key, entry) || entry.Vertices == null || entry.Outer == null || entry.Mm == null) continue;
                // The ray in the frame's space (the mesh's own), so the vertices need no transforming.
                Matrix4x4 toLocal = entry.Frame.worldToLocalMatrix;
                Vector3 origin = toLocal.MultiplyPoint3x4(ray.origin);
                Vector3 dir = toLocal.MultiplyVector(ray.direction);
                Vector3[] v = entry.Vertices;
                int[] t = entry.Outer;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    float along;
                    if (!RayTriangle(origin, dir, v[t[i]], v[t[i + 1]], v[t[i + 2]], out along)) continue;
                    Vector3 world = entry.Frame.TransformPoint(origin + dir * along);
                    float distance = Vector3.Dot(world - ray.origin, ray.direction);
                    if (distance <= 0f || distance >= best) continue;
                    best = distance;
                    int vi = t[i];
                    bestMm = vi < entry.Mm.Length ? entry.Mm[vi] : 0f;
                    bestNormal = entry.Frame.TransformDirection(Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]])).normalized;
                }
            }
            if (float.IsInfinity(best) || bestMm <= 0f) return;

            float cos = Mathf.Abs(Vector3.Dot(bestNormal, ray.direction));
            hoverMm = bestMm;
            hoverAngle = Mathf.Acos(Mathf.Clamp01(cos)) * Mathf.Rad2Deg;
            hoverEffective = cos > 0.01f ? bestMm / cos : float.PositiveInfinity;
            hoverAt = new Vector2(screen.x, Screen.height - screen.y);
            hoverShown = true;
        }

        // The designer's 3D view is itself a full-screen UI element (its screen input): the mouse is over the view when that is the topmost
        // UI element under it, not a panel or menu on top.
        private static readonly List<UnityEngine.EventSystems.RaycastResult> uiHits = new List<UnityEngine.EventSystems.RaycastResult>();

        private static bool OverDesignerView(Designer designer, Vector2 screen)
        {
            UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
            if (events == null) return true;
            GameObject view = null;
            try
            {
                Assets.Scripts.Design.UI.Input.DesignerScreenInputScript input = designer.DesignerScript != null && designer.DesignerScript.DesignerUI != null
                    ? designer.DesignerScript.DesignerUI.ScreenInput : null;
                view = input != null ? input.gameObject : null;
            }
            catch (Exception) { }
            uiHits.Clear();
            events.RaycastAll(new UnityEngine.EventSystems.PointerEventData(events) { position = screen }, uiHits);
            if (uiHits.Count == 0) return true;
            GameObject top = uiHits[0].gameObject;
            return view != null && top != null && (top == view || top.transform.IsChildOf(view.transform));
        }

        // Moller-Trumbore: does the ray meet the triangle (either side), and how far along it.
        private static bool RayTriangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float along)
        {
            along = 0f;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 s = origin - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float w = Vector3.Dot(dir, q) * inv;
            if (w < 0f || u + w > 1f) return false;
            along = Vector3.Dot(e2, q) * inv;
            return along > 0f;
        }

        private static GUIStyle hoverTitle, hoverBig, hoverRow, hoverValue;

        private static void DrawHover()
        {
            if (!hoverShown) return;
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            if (hoverTitle == null)
            {
                hoverTitle = new GUIStyle(GUI.skin.label);
                hoverBig = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                hoverRow = new GUIStyle(GUI.skin.label);
                hoverValue = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight };
            }
            hoverTitle.fontSize = Mathf.RoundToInt(13f * scale);
            hoverBig.fontSize = Mathf.RoundToInt(24f * scale);
            hoverRow.fontSize = hoverValue.fontSize = Mathf.RoundToInt(13f * scale);
            hoverTitle.normal.textColor = hoverRow.normal.textColor = new Color(0.78f, 0.80f, 0.83f, 1f);
            hoverBig.normal.textColor = hoverValue.normal.textColor = Color.white;

            float width = 170f * scale, line = 20f * scale, pad = 10f * scale, big = 34f * scale;
            float height = pad * 2f + line + big + line * 2f;
            float x = hoverAt.x + 18f * scale, y = hoverAt.y + 18f * scale;
            if (x + width > Screen.width) x = hoverAt.x - 18f * scale - width;    // kept on the screen
            if (y + height > Screen.height) y = hoverAt.y - 18f * scale - height;

            GUI.color = new Color(0.12f, 0.14f, 0.17f, 0.92f);
            GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            float top = y + pad, inner = width - 2f * pad;
            GUI.Label(new Rect(x + pad, top, inner, line), "Effective armor", hoverTitle);
            top += line;
            string effective = float.IsInfinity(hoverEffective) || hoverEffective > 9999f ? "grazing" : hoverEffective.ToString("0.0", inv) + " mm";
            GUI.Label(new Rect(x + pad, top, inner, big), effective, hoverBig);
            top += big;
            GUI.Label(new Rect(x + pad, top, inner, line), "Angle", hoverRow);
            GUI.Label(new Rect(x + pad, top, inner, line), hoverAngle.ToString("0.0", inv) + " deg", hoverValue);
            top += line;
            GUI.Label(new Rect(x + pad, top, inner, line), "Armor", hoverRow);
            GUI.Label(new Rect(x + pad, top, inner, line), hoverMm.ToString("0", inv) + " mm", hoverValue);
        }

        private static void Draw()
        {
            if (Parts.Count == 0) return;
            if (material == null)
            {
                material = FaceHighlight.MakeTintMaterial(Color.white);   // the colors are the mesh's own
                if (material == null) return;
            }
            if (nearestMaterial == null) nearestMaterial = FaceHighlight.MakeDepthMaterial(FaceHighlight.RenderQueue - 50);
            foreach (KeyValuePair<PartScript, Entry> pair in Parts)
            {
                PartScript part = pair.Key;
                Entry entry = pair.Value;
                if (!Shown(part, entry)) continue;
                int layer = entry.LayerSource != null ? entry.LayerSource.gameObject.layer : part.gameObject.layer;
                Matrix4x4 matrix = entry.Frame.localToWorldMatrix;
                // The outside and the inside of the armored faces. Their depth first (all of them, before any color):
                // only the tinted surface nearest the camera is then colored at each point, so what shows does not
                // depend on the order the game draws the parts in (it changes with the camera).
                for (int sub = 0; sub <= FaceHighlight.InsideSubmesh; sub += FaceHighlight.InsideSubmesh)
                {
                    if (sub >= entry.Mesh.subMeshCount || entry.Mesh.GetIndexCount(sub) == 0) continue;
                    if (nearestMaterial != null)
                        Graphics.DrawMesh(entry.Mesh, matrix, nearestMaterial, layer, null, sub, null, ShadowCastingMode.Off, false);
                    Graphics.DrawMesh(entry.Mesh, matrix, material, layer, null, sub, null, ShadowCastingMode.Off, false);
                }
            }
        }

        private static bool Shown(PartScript part, Entry entry)
        {
            if (part == null || entry.Mesh == null || entry.Frame == null) return false;
            if (!part.gameObject.activeInHierarchy) return false;
            return part.PartMaterialScript == null || part.PartMaterialScript.Visible;   // not hidden in the designer
        }

        private static void Clear()
        {
            foreach (Entry entry in Parts.Values)
                if (entry.Mesh != null) Object.Destroy(entry.Mesh);
            Parts.Clear();
            Legend.Clear();
            LegendColors.Clear();
        }

        // The plugin is being unloaded: the tints, material and shield are freed.
        internal static void Shutdown()
        {
            Clear();
            On = false;
            button = null;
            if (material != null) Object.Destroy(material);
            material = null;
            if (nearestMaterial != null) Object.Destroy(nearestMaterial);
            nearestMaterial = null;
            if (shield != null)
            {
                Object.Destroy(shield.texture);
                Object.Destroy(shield);
            }
            shield = null;
        }
    }

    // The designer's UI was built from its layout (with the shield button in it).
    [HarmonyPatch(typeof(DesignerUIScript), "Initialize")]
    internal static class ArmorViewButtonPatch
    {
        private static void Postfix(DesignerUIScript __instance)
        {
            try
            {
                ArmorView.DesignerUi = __instance;
                ArmorView.Attach(__instance.RootWidget);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Armor view: couldn't set up the button: " + e.Message);
            }
        }
    }
}
