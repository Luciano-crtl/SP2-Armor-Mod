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
                BuildSimulation();
                BuildXRayPerformance();
                BuildEnergyModel();
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
            Live("Shell energy", () => LastImpact.HasData ? EnergyModel.Kilojoules(LastImpact.ShellEnergy) : "-");
            Live("Absorbed by armor", () => LastImpact.HasData ? EnergyModel.Kilojoules(LastImpact.Absorbed) : "-");
            Live("Left for fragments", () => LastImpact.HasData ? EnergyModel.Kilojoules(LastImpact.Residual) : "-");
            Live("Rays that hit a part", () => LastImpact.HasData ? LastImpact.RaysHit.ToString() : "-");
            Live("Ricochets", () => LastImpact.HasData ? LastImpact.Ricochets.ToString() : "-");
            Live("Total damage", () => LastImpact.HasData ? LastImpact.Damage.ToString("F1") : "-");
            Live("Time since impact", () => LastImpact.HasData
                ? (Time.time - LastImpact.Time).ToString("F0") + " s" : "-");
        }

        private void BuildSimulation()
        {
            builder.Header("Simulate shot");
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Simulate shot", ShotSimulator.Fire),
                new KeyValuePair<string, Action>("Hide X-ray", XRayView.Hide),
            });

            // Lock: the shot keeps the camera's position and direction from the moment it is switched on.
            ToggleControl lockToggle = null;
            lockToggle = builder.Toggle("Lock shot to camera",
                "Saves the orbit camera's position and direction now. Simulated shots start from there, so you can move the camera and watch them from elsewhere.",
                ShotSimulator.IsLocked,
                on =>
                {
                    // If it could not be locked (wrong camera), put the switch back.
                    if (!ShotSimulator.SetLocked(on)) lockToggle.IsOn = false;
                });
            Live("Locked shot", () => ShotSimulator.LockText);

            Live("Result", () => ShotSimulator.Message);
            for (int i = 0; i < 5; i++)
            {
                int index = i;
                Live("#" + (i + 1), () =>
                {
                    if (index >= ShotSimulator.Top.Count) return "-";
                    ShotSimulator.PartResult r = ShotSimulator.Top[index];
                    return r.Name + ": " + r.Damage.ToString("F1") + " (" +
                           (r.Fraction * 100f).ToString("F0") + "% of its health)";
                });
            }

            FloatSetting(ArmorFragmentsPlugin.SimShellMass, "Sim shell mass",
                "Mass of the imaginary shell. With its speed it gives the shell's energy.", 0.1f, "");
            FloatSetting(ArmorFragmentsPlugin.SimShellVelocity, "Sim shell speed",
                "Speed of the imaginary shell.", 10f, " m/s");
            FloatSetting(ArmorFragmentsPlugin.SimCaliber, "Sim caliber",
                "Caliber of the imaginary shell: sets the area the armor has to open.", 1f, " mm");
            IntSetting(ArmorFragmentsPlugin.SimFragmentCount, "Simulated fragments",
                "Fragment rays made by the simulated shell when it penetrates.");
            ToggleSetting(ArmorFragmentsPlugin.XRayAfterSimulation, "X-ray after shot",
                "Switch on the X-ray view after each simulated shot.");
            FloatSetting(ArmorFragmentsPlugin.XRayRadius, "X-ray radius",
                "Parts within this distance of the impact are shown.", 1f, " m");
            FloatSetting(ArmorFragmentsPlugin.XRayDuration, "X-ray duration",
                "How long the X-ray view stays on.", 1f, " s");
            FloatSetting(ArmorFragmentsPlugin.XRayDarkPercent, "Fully dark at",
                "A part losing this share of its health is shown fully dark.", 1f, " %");
        }

        // What the X-ray view costs, and the switches to find out which part is expensive.
        private void BuildXRayPerformance()
        {
            builder.Header("X-ray performance", collapsed: true);

            Live("Frame time", () => XRayStats.FrameMs <= 0f
                ? "-"
                : XRayStats.FrameMs.ToString("F1") + " ms (" + (1000f / XRayStats.FrameMs).ToString("F0") + " FPS)");
            Live("Ghost meshes", () => XRayStats.Ghosts + " (" + XRayStats.RimGhosts + " rim-lit, " +
                                       XRayStats.FlatGhosts + " flat)");
            Live("Vertices", () => XRayStats.TotalVertices + " (" + XRayStats.RimVertices + " rim-lit)");
            Live("Rim color update", () => XRayStats.RimMs.ToString("F2") + " ms/frame, " +
                                           XRayStats.Passes + " passes done");
            Live("Built in", () => XRayStats.BuildMs.ToString("F0") + " ms");

            ToggleSetting(ArmorFragmentsPlugin.XRayRimLook, "Rim-lit look",
                "Edge glow. Costs CPU time. Turn it off to see how much of the cost it is.");
            ToggleSetting(ArmorFragmentsPlugin.XRayShowUndamaged, "Show undamaged parts",
                "Off = only armor and damaged parts get a see-through copy. Fewer meshes to draw.");
            IntSetting(ArmorFragmentsPlugin.XRayMaxRimKiloVertices, "Rim vertex limit (k)",
                "Vertices (thousands) that may use the rim look. The rest use the flat look.");
            IntSetting(ArmorFragmentsPlugin.XRayBudgetKiloVertices, "Rim work per frame (k)",
                "Vertices (thousands) recalculated per frame at most. Lower = smoother, slower glow.");
        }

        // Settings of the energy model (phase 2: they can be edited, the calculation does not use them yet).
        private void BuildEnergyModel()
        {
            builder.Header("Energy model (partly active)", collapsed: true);
            builder.Text("Status", "Armor resistance is active; the rest is not used yet");

            ToggleSetting(ArmorFragmentsPlugin.LogChordDiagnostics, "Log chord diagnostics",
                "Each simulated shot writes how far the line travels inside every part to the log ([CHORD] lines).");

            FloatSetting(ArmorFragmentsPlugin.DefaultShellHardness, "Default shell hardness",
                "Shell hardness for cannons without a [CAN k=] tag.", 5f, " %");
            FloatSetting(ArmorFragmentsPlugin.SimShellHardness, "Sim shell hardness",
                "Shell hardness of the simulated shot.", 5f, " %");
            FloatSetting(ArmorFragmentsPlugin.ArmorResistance, "Armor resistance",
                "Energy armor takes: joules per mm of thickness crossed per cm2 of shell area (about 40 for steel).", 1f, " J/mm/cm2");
            FloatSetting(ArmorFragmentsPlugin.RicochetFriction, "Ricochet friction",
                "Share of the sliding energy lost in a ricochet.", 0.05f, "");
            FloatSetting(ArmorFragmentsPlugin.RicochetRandomness, "Ricochet randomness",
                "Small random shift of the ricochet angle. 0% = none.", 5f, " %");
            FloatSetting(ArmorFragmentsPlugin.DamagePerKiloJoule, "Damage per kJ",
                "Part damage per kilojoule left in a part.", 0.1f, "");
            FloatSetting(ArmorFragmentsPlugin.DensityModifier, "Density modifier",
                "Multiplies the density of parts without tags.", 0.1f, "x");
            ToggleSetting(ArmorFragmentsPlugin.ConstantForceLoss, "Constant force loss",
                "Off = proportional drag (exponential). On = constant force (linear).");
            ToggleSetting(ArmorFragmentsPlugin.SimpleFragments, "Simple fragments",
                "Off = each fragment has its own mass, energy and speed. On = equal and fixed.");
            IntSetting(ArmorFragmentsPlugin.MaxPartsPerFragment, "Parts per fragment",
                "Most parts one fragment may cross.");
            FloatSetting(ArmorFragmentsPlugin.SpreadBaseAngle, "Spread",
                "Base spread of the fragment directions.", 1f, " deg");
            FloatSetting(ArmorFragmentsPlugin.AxisTilt, "Axis tilt",
                "How much the cone leans toward the plate's normal on slanted hits.", 0.05f, "");
            FloatSetting(ArmorFragmentsPlugin.SpreadAnisotropy, "Spread anisotropy",
                "How much wider the cone is along the plane of incidence (1 = round).", 0.1f, "x");
            FloatSetting(ArmorFragmentsPlugin.VisualSpeedMultiplier, "Visual speed multiplier",
                "Drawn rays move at real speed times this.", 0.005f, "x");
        }

        private void BuildFragments()
        {
            builder.Header("Fragments");
            ToggleSetting(ArmorFragmentsPlugin.ApplyDamage, "Apply damage",
                "If on, fragments damage the parts they hit.");
            FloatSetting(ArmorFragmentsPlugin.DamagePerFragment, "Damage per fragment",
                "Damage of one fragment at point-blank range. Falls off with distance.", 0.1f, "");
            IntSetting(ArmorFragmentsPlugin.DefaultFragmentCount, "Default fragments",
                "Fragment rays made when a shell penetrates, for cannons without a [CAN f=] tag.");
            FloatSetting(ArmorFragmentsPlugin.BallisticRayRange, "Ballistic ray range",
                "When a real shell stops in a non-armor part, a ray carries on this far looking for armor.", 1f, " m");
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
            FloatSetting(ArmorFragmentsPlugin.InitialRayWidth, "Initial ray width",
                "Thickness of the initial ray (the shell, or the simulated shot).", 0.005f, " m");
            FloatSetting(ArmorFragmentsPlugin.InitialRaySpeed, "Initial ray speed",
                "How fast the initial ray travels. Visual only; fragments leave once it arrives.", 5f, " m/s");
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
