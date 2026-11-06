using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.UI;
using Assets.Scripts.UI;
using Assets.Scripts.UI.Controls;
using BepInEx.Configuration;
using Jundroo.Juicy.Widgets;
using UnityEngine;

namespace ArmorFragments.UI
{
    // The panel window. The game creates it from the layout in ArmorPanel.BuildLayout()
    // and calls OnWidgetInitialized once its widgets exist.
    public class ArmorPanelScript : PanelDialogScript
    {
        private const int LogRowCount = 10;
        private const float RefreshInterval = 0.5f;

        private Widget content;
        private PanelBuilder builder;

        // Small functions that refresh the live rows (status and log).
        private readonly List<Action> refreshers = new List<Action>();

        private float nextRefresh;
        private bool rebuildRequested;
        private bool visible = true;
        private int shownLogVersion = -1;

        // Doesn't block the game: you keep flying while it is open.
        public override bool IsModal
        {
            get { return false; }
        }

        public override void OnWidgetInitialized(Widget widget)
        {
            base.OnWidgetInitialized(widget);

            widget.FindWidget("armor-close-button").Clicked += (Widget w) => Close();
            content = widget.FindWidget("armor-content");

            BuildContent();
        }

        // (Re)creates every row. Used on opening and after "Reset to defaults".
        private void BuildContent()
        {
            foreach (Widget child in content.Widgets.ToList())
                child.Destroy();
            refreshers.Clear();
            shownLogVersion = -1;

            builder = new PanelBuilder(content);

            try
            {
                BuildStatus();
                BuildFragments();
                BuildRicochet();
                BuildRays();
                BuildTools();
                BuildLog();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Couldn't build the Armor Fragments panel: " + e);
            }

            RefreshLive();
            nextRefresh = Time.unscaledTime + RefreshInterval;
        }

        // ---- Sections ------------------------------------------------------------

        private void BuildStatus()
        {
            builder.Header("Last impact");
            Live("Result", () => LastImpact.Kind);
            Live("Impact angle", () => LastImpact.HasData
                ? LastImpact.Angle.ToString("F0") + " deg (armor ricochets from " +
                  LastImpact.Tolerated.ToString("F0") + " deg)"
                : "-");
            Live("Rays that hit a part", () => LastImpact.HasData ? LastImpact.RaysHit.ToString() : "-");
            Live("Ricochets", () => LastImpact.HasData ? LastImpact.Ricochets.ToString() : "-");
            Live("Total damage", () => LastImpact.HasData ? LastImpact.Damage.ToString("F1") : "-");
            Live("Time since impact", () => LastImpact.HasData
                ? (Time.time - LastImpact.Time).ToString("F0") + " s" : "-");
        }

        private void BuildFragments()
        {
            builder.Header("Fragments");
            ToggleSetting(ArmorFragmentsPlugin.ApplyDamage, "Apply damage",
                "If on, fragments damage the parts they hit.");
            FloatSetting(ArmorFragmentsPlugin.DamagePerFragment, "Damage per fragment",
                "Damage of one fragment at point-blank range. Falls off with distance.", 0.1f, "");
        }

        private void BuildRicochet()
        {
            builder.Header("Ricochet");
            IntSetting(ArmorFragmentsPlugin.MaxRicochets, "Max ricochets",
                "How many times one ray may bounce. The bounce off the armor counts as the first. 0 turns ricochets off.");
            FloatSetting(ArmorFragmentsPlugin.ArmorRicochetAngle, "Armor angle",
                "Impact angle (0 = head-on, 90 = grazing) from which a shell ricochets off the armor.", 1f, " deg");
            FloatSetting(ArmorFragmentsPlugin.ArmorRicochetMultiplier, "Armor multiplier",
                "Multiplies the armor angle. Above 1 the armor takes steeper hits before ricocheting.", 0.05f, "x");
            FloatSetting(ArmorFragmentsPlugin.FragmentRicochetAngle, "Fragment angle",
                "Impact angle from which a fragment ray ricochets off any object.", 1f, " deg");
            FloatSetting(ArmorFragmentsPlugin.FragmentRicochetMultiplier, "Fragment multiplier",
                "Multiplies the fragment angle.", 0.05f, "x");
        }

        private void BuildRays()
        {
            builder.Header("Ray display");
            ToggleSetting(ArmorFragmentsPlugin.DrawRays, "Draw rays",
                "Shows each fragment ray and impact in the world.");
            FloatSetting(ArmorFragmentsPlugin.RayWidth, "Ray width",
                "Thickness of the drawn rays.", 0.005f, " m");
            FloatSetting(ArmorFragmentsPlugin.RaySpeed, "Ray speed",
                "How fast the drawn ray travels. Visual only.", 1f, " m/s");
            FloatSetting(ArmorFragmentsPlugin.RayTrailLength, "Trail length",
                "Length of the fading trail behind the moving ray.", 0.1f, " m");
            FloatSetting(ArmorFragmentsPlugin.BubbleSize, "Impact bubble size",
                "Diameter of the colored ball at each impact.", 0.01f, " m");
            FloatSetting(ArmorFragmentsPlugin.BubbleLifetime, "Impact bubble time",
                "Seconds an impact bubble takes to shrink away.", 0.05f, " s");
        }

        private void BuildTools()
        {
            builder.Header("Tools");
            ToggleSetting(ArmorFragmentsPlugin.MirrorLogToGameConsole, "Log to game console",
                "Also print this plugin's log lines to the game's developer console.");
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Reset to defaults", ResetToDefaults),
                new KeyValuePair<string, Action>("Clear rays", ClearRays),
            });
        }

        private void BuildLog()
        {
            builder.Header("Log (newest first)", collapsed: true);

            List<TextControl> rows = new List<TextControl>();
            for (int i = 0; i < LogRowCount; i++)
                rows.Add(builder.Text("", ""));

            refreshers.Add(() =>
            {
                if (LogMirror.Version == shownLogVersion) return;
                shownLogVersion = LogMirror.Version;

                string[] times;
                string[] lines;
                LogMirror.Latest(LogRowCount, out times, out lines);
                for (int i = 0; i < rows.Count; i++)
                {
                    rows[i].LabelText = i < lines.Length ? times[i] : "";
                    rows[i].ValueText.Text = i < lines.Length ? lines[i] : "";
                }
            });
        }

        // ---- Button actions ------------------------------------------------------

        private void ResetToDefaults()
        {
            ConfigFile config = ArmorFragmentsPlugin.Instance.Config;
            foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> pair in
                     (IEnumerable<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)config)
                pair.Value.BoxedValue = pair.Value.DefaultValue;
            config.Save();

            // Rebuilding destroys the button being clicked, so wait for Update.
            rebuildRequested = true;
        }

        private void ClearRays()
        {
            foreach (TracerAnimation tracer in UnityEngine.Object.FindObjectsByType<TracerAnimation>(FindObjectsSortMode.None))
                Destroy(tracer.gameObject);
            foreach (BubbleAnimation bubble in UnityEngine.Object.FindObjectsByType<BubbleAnimation>(FindObjectsSortMode.None))
                Destroy(bubble.gameObject);
        }

        // ---- Setting rows --------------------------------------------------------

        private void Live(string label, Func<string> getText)
        {
            TextControl row = builder.Text(label, getText());
            refreshers.Add(() =>
            {
                string text = getText();
                if (row.ValueText.Text != text) row.ValueText.Text = text;
            });
        }

        private void ToggleSetting(ConfigEntry<bool> entry, string label, string tooltip)
        {
            builder.Toggle(label, tooltip, entry.Value, x => SetValue(entry, x, true));
        }

        private void IntSetting(ConfigEntry<int> entry, string label, string tooltip)
        {
            float min = 0f;
            float max = Mathf.Max(1f, entry.Value * 2f);
            AcceptableValueRange<int> range = entry.Description.AcceptableValues as AcceptableValueRange<int>;
            if (range != null)
            {
                min = range.MinValue;
                max = range.MaxValue;
            }

            builder.Slider(label, tooltip, min, max, Mathf.RoundToInt(max - min) + 1,
                entry.Value,
                x => Mathf.RoundToInt(x).ToString(CultureInfo.InvariantCulture),
                x => SetValue(entry, Mathf.Clamp(Mathf.RoundToInt(x), (int)min, (int)max), false),
                x => entry.ConfigFile.Save());
        }

        private void FloatSetting(ConfigEntry<float> entry, string label, string tooltip,
                                  float step, string unit)
        {
            float min = 0f;
            float max = Mathf.Max(1f, entry.Value * 4f);
            AcceptableValueRange<float> range = entry.Description.AcceptableValues as AcceptableValueRange<float>;
            if (range != null)
            {
                min = range.MinValue;
                max = range.MaxValue;
            }

            int positions = Mathf.RoundToInt((max - min) / step) + 1;
            string format = NumberFormat(step);

            builder.Slider(label, tooltip, min, max, positions <= 1000 ? positions : 0,
                entry.Value,
                x => x.ToString(format, CultureInfo.InvariantCulture) + unit,
                x => SetValue(entry, Mathf.Clamp(Mathf.Round(x / step) * step, min, max), false),
                x => entry.ConfigFile.Save());
        }

        // "0" for whole steps, "0.0" for tenths, "0.00" for hundredths...
        private static string NumberFormat(float step)
        {
            if (step >= 1f) return "0";
            int decimals = Mathf.Clamp(Mathf.CeilToInt(-Mathf.Log10(step) - 0.001f), 1, 4);
            return "0." + new string('0', decimals);
        }

        // Changes a setting. While a slider is dragged we don't write the file on every
        // tiny change (save = false); it is saved when the slider is released.
        private static void SetValue<T>(ConfigEntry<T> entry, T value, bool save)
        {
            ConfigFile file = entry.ConfigFile;
            bool original = file.SaveOnConfigSet;
            file.SaveOnConfigSet = save && original;
            try
            {
                entry.Value = value;
            }
            finally
            {
                file.SaveOnConfigSet = original;
            }
        }

        // ---- Per-frame -----------------------------------------------------------

        protected void Update()
        {
            UpdateVisibility();

            if (rebuildRequested)
            {
                rebuildRequested = false;
                BuildContent();
                return;
            }

            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + RefreshInterval;
                RefreshLive();
            }
        }

        private void RefreshLive()
        {
            foreach (Action refresh in refreshers)
            {
                try
                {
                    refresh();
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Panel refresh failed: " + e.Message);
                }
            }
        }

        // Hide the panel together with the rest of the flight UI (for clean screenshots).
        private void UpdateVisibility()
        {
            FlightUIScript flightUI = FlightSceneScript.Instance != null
                ? FlightSceneScript.Instance.FlightUI : null;
            bool show = flightUI == null || flightUI.Visible;
            if (show == visible) return;
            visible = show;

            CanvasGroup group = Widget.gameObject.GetComponent<CanvasGroup>();
            if (group == null) group = Widget.gameObject.AddComponent<CanvasGroup>();
            group.alpha = show ? 1f : 0f;
            group.interactable = show;
            group.blocksRaycasts = show;
        }

        protected void OnDestroy()
        {
            try
            {
                ArmorFragmentsPlugin.Instance.Config.Save();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Couldn't save the settings: " + e.Message);
            }
        }
    }
}
