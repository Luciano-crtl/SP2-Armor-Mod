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
        private bool builtLocked;   // were the host-controlled settings hidden when the rows were made?
        private bool builtHost;     // ... and was this player the host?
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
            builtLocked = NetSync.GlobalLocked;
            builtHost = NetSync.IsHost;

            try
            {
                BuildStatus();
                BuildSimulation();
                BuildMultiplayer();
                BuildImpactCamera();
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
            Live("Energy left", () => LastImpact.HasData ? EnergyModel.Kilojoules(LastImpact.Residual) : "-");
            Live("Fragment budget", () => LastImpact.FragmentEnergy <= 0f
                ? "-"
                : EnergyModel.Kilojoules(LastImpact.FragmentEnergy) + ", " + (LastImpact.FragmentMass * 1000f).ToString("F0") +
                  " g, ~" + LastImpact.FragmentSpeed.ToString("F0") + " m/s");
            Live("Fragment cloud", () => LastImpact.FragmentEnergy <= 0f
                ? "-"
                : "spread " + LastImpact.SigmaX.ToString("F1") + " x " + LastImpact.SigmaY.ToString("F1") +
                  " deg, axis leaning " + LastImpact.AxisTilt.ToString("F1") + " deg");
            Live("Rays that hit a part", () => LastImpact.HasData ? LastImpact.RaysHit.ToString() : "-");
            Live("Ricochets", () => LastImpact.HasData ? LastImpact.Ricochets.ToString() : "-");
            Live("Total damage", () => LastImpact.HasData ? LastImpact.Damage.ToString("F1") : "-");
            Live("Time since impact", () => LastImpact.HasData
                ? (Time.time - LastImpact.Time).ToString("F0") + " s" : "-");
        }

        private void BuildSimulation()
        {
            builder.Header("Simulate shot");
            builder.Text("Mod version", ArmorFragmentsPlugin.Version);
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Simulate shot", ShotSimulator.Fire),
                new KeyValuePair<string, Action>("Vehicle X-ray", VehicleXRay.Toggle),
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

            // The simulated cannon: the same two properties a cannon with a [CAN] tag has in the designer.
            IntSetting(ArmorFragmentsPlugin.SimFragmentCount, "Simulated shell fragments",
                "How many fragment rays a shell makes when it penetrates armor.");
            FloatSetting(ArmorFragmentsPlugin.SimShellHardness, "Simulated shell hardness",
                "How hard the shell is, compared with armor. 100 % is the reference. Harder shells ricochet less.", 5f, " %");
            // What a real cannon gives its shell. The mass follows from the caliber, as in the game.
            FloatSetting(ArmorFragmentsPlugin.SimShellVelocity, "Simulated shell velocity",
                "Speed of the imaginary shell.", 10f, " m/s");
            FloatSetting(ArmorFragmentsPlugin.SimCaliber, "Simulated shell caliber",
                "Caliber of the imaginary shell. It sets the area the armor has to open and, with it, the shell's mass.", 1f, " mm");
            Live("Calculated shell mass", () =>
                EnergyModel.ShellMassForCaliber(ArmorFragmentsPlugin.SimCaliber.Value).ToString("F2") + " kg");
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
            builder.Header("Energy model", collapsed: true);
            builder.Text("Status", NetSync.GlobalLocked ? "Balance set by the host" : "All active");

            ToggleSetting(ArmorFragmentsPlugin.LogChordDiagnostics, "Log chord diagnostics",
                "Each simulated shot writes how far the line travels inside every part to the log ([CHORD] lines).");
            FloatSetting(ArmorFragmentsPlugin.VisualSpeedMultiplier, "Visual speed multiplier",
                "Drawn rays move at their real speed (from energy) times this, and slow down as they lose energy.", 0.005f, "x");

            // The rest decides what happens to other players: on someone else's server it is the host's.
            if (NetSync.GlobalLocked) return;

            FloatSetting(ArmorFragmentsPlugin.DefaultShellHardness, "Default shell hardness",
                "Shell hardness for cannons without a [CAN k=] tag. Used for the ricochet angle.", 5f, " %");
            FloatSetting(ArmorFragmentsPlugin.ArmorResistance, "Armor resistance",
                "Energy armor takes, per mm of thickness crossed per cm2 of shell area. About 600 matches real steel.", 5f, " J/mm/cm2");
            FloatSetting(ArmorFragmentsPlugin.RicochetFriction, "Ricochet friction",
                "Share of the sliding energy a shell loses in a ricochet.", 0.05f, "");
            FloatSetting(ArmorFragmentsPlugin.RicochetRandomness, "Ricochet randomness",
                "Small random shift of the critical ricochet angle. 0% = none, 100% = up to 10 degrees.", 5f, " %");
            FloatSetting(ArmorFragmentsPlugin.DensityModifier, "Density modifier",
                "Multiplies the density of parts without tags.", 0.1f, "x");
            ToggleSetting(ArmorFragmentsPlugin.ConstantForceLoss, "Constant force loss",
                "Off = proportional drag (exponential). On = constant force (linear).");
            ToggleSetting(ArmorFragmentsPlugin.SimpleFragments, "Simple fragments",
                "Off = each fragment has its own mass, energy and speed. On = equal and fixed.");
            FloatSetting(ArmorFragmentsPlugin.ShellFragmentShare, "Shell fragment share",
                "Share of the leftover energy that becomes fragments even with an unbrittle plate (the shell breaking up).", 0.05f, "");
            FloatSetting(ArmorFragmentsPlugin.SimpleFragmentArea, "Simple fragment area",
                "Cross-section of every fragment in simple mode.", 0.1f, " cm2");
            IntSetting(ArmorFragmentsPlugin.MaxPartsPerFragment, "Parts per fragment",
                "Most parts one fragment may cross.");
            FloatSetting(ArmorFragmentsPlugin.SpreadBaseAngle, "Spread",
                "Base spread of the fragment directions.", 1f, " deg");
            FloatSetting(ArmorFragmentsPlugin.AxisTilt, "Axis tilt",
                "How much the cone leans toward the plate's normal on slanted hits.", 0.05f, "");
            FloatSetting(ArmorFragmentsPlugin.SpreadAnisotropy, "Spread anisotropy",
                "How much wider the cone is along the plane of incidence (1 = round).", 0.1f, "x");
        }

        private void BuildImpactCamera()
        {
            builder.Header("Impact camera", collapsed: true);
            ToggleSetting(ArmorFragmentsPlugin.ImpactCameraEnabled, "Impact camera",
                "Shows a small camera when a shell you fired hits another vehicle. The toggle key does the same.");
            ToggleSetting(ArmorFragmentsPlugin.ImpactCameraSwitchTargets, "Switch targets",
                "Hitting another vehicle while the camera is open moves it there.");
            FloatSetting(ArmorFragmentsPlugin.ShellLeadIn, "Lead-in distance",
                "Meters of the shot shown before the impact. The camera starts this far behind it.", 1f, " m");
            FloatSetting(ArmorFragmentsPlugin.ImpactCameraLinger, "Stay open after",
                "Seconds the camera stays open after the last ray has ended.", 0.5f, " s");
            FloatSetting(ArmorFragmentsPlugin.ImpactCameraWidthPercent, "Window width",
                "Width of the window, as a percentage of the screen.", 1f, " %");
            FloatSetting(ArmorFragmentsPlugin.ImpactCameraMargin, "Window margin",
                "Distance from the top right corner, in pixels.", 2f, " px");
            IntSetting(ArmorFragmentsPlugin.ImpactCameraResolution, "Picture height",
                "Pixels. Lower = cheaper to render.");
            FloatSetting(ArmorFragmentsPlugin.ImpactCameraFov, "Field of view",
                "Degrees.", 1f, " deg");
            ToggleSetting(ArmorFragmentsPlugin.ImpactCameraShadows, "Shadows",
                "Off = the vehicle is lit but casts no shadows. Cheaper.");
        }

        // Role in a multiplayer session, and the host's switch.
        private void BuildMultiplayer()
        {
            builder.Header("Multiplayer", collapsed: true);
            Live("Session", () => NetSync.Role);
            if (NetSync.IsHost)
            {
                Live("Players with the mod", () => NetSync.ModdedCount.ToString());
                ToggleSetting(ArmorFragmentsPlugin.HostResolvesShots, "Work out others' shells",
                    "Shells from players who do not run the mod are worked out on your computer (needs their cannon to have a [CAN] tag).");
            }
        }

        private void BuildFragments()
        {
            if (NetSync.GlobalLocked) return;   // all of it is the host's call
            builder.Header("Fragments");
            ToggleSetting(ArmorFragmentsPlugin.ApplyDamage, "Apply damage",
                "If on, fragments damage the parts they hit.");
            ToggleSetting(ArmorFragmentsPlugin.IgnoreGameDamageOnArmor, "Ignore game damage on armor",
                "The game's own damage to [ARM] parts (impact, explosions, crashes) is ignored: armor is judged by the energy model only.");
            FloatSetting(ArmorFragmentsPlugin.DamagePerKiloJoule, "Damage per kJ",
                "Part damage per kilojoule a fragment leaves in a part. This is the damage scale.", 0.1f, "");
            IntSetting(ArmorFragmentsPlugin.DefaultFragmentCount, "Default fragments",
                "Fragment rays made when a shell penetrates, for cannons without a [CAN f=] tag.");
            FloatSetting(ArmorFragmentsPlugin.BallisticRayRange, "Ballistic ray range",
                "When a real shell stops in a non-armor part, a ray carries on this far looking for armor.", 1f, " m");
        }

        private void BuildRicochet()
        {
            if (NetSync.GlobalLocked) return;
            builder.Header("Ricochet");
            IntSetting(ArmorFragmentsPlugin.MaxRicochets, "Max ricochets",
                "How many times one ray may bounce. The bounce off the armor counts as the first. 0 turns ricochets off.");
            FloatSetting(ArmorFragmentsPlugin.ArmorRicochetAngle, "Armor angle",
                "Reference critical angle (equal hardness, 800 m/s). Hardness, speed and overmatch move it.", 1f, " deg");
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
                new KeyValuePair<string, Action>("Camera diagnostic", CameraDiagnostic.Run),
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
            {
                // On someone else's server the balance settings are the host's, not ours to reset.
                if (NetSync.GlobalLocked && NetSync.IsGlobal(pair.Value)) continue;
                pair.Value.BoxedValue = pair.Value.DefaultValue;
            }
            NetSync.SaveConfig();

            // Rebuilding destroys the button being clicked, so wait for Update.
            rebuildRequested = true;
        }

        private void ClearRays()
        {
            RayDrawer.ClearPersistent();
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
                x => NetSync.SaveConfig());
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
                x => NetSync.SaveConfig());
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

            // Joined or left a server while the panel was open: show the rows that apply now.
            if (NetSync.GlobalLocked != builtLocked || NetSync.IsHost != builtHost) rebuildRequested = true;

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
                NetSync.SaveConfig();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Couldn't save the settings: " + e.Message);
            }
        }
    }
}
