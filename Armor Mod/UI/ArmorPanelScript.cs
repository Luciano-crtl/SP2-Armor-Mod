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
                BuildMouseAim();
                BuildAmmoDisplay();
                BuildImpactCamera();
                BuildXRayPerformance();
                BuildEnergyModel();
                BuildFragments();
                BuildRicochet();
                BuildRays();
                BuildTools();
                // RELEASE-STRIP-BEGIN
                BuildFluidSmoke();
                // RELEASE-STRIP-END
                BuildDamageMaps();
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
            Live("Impact angle", () => !LastImpact.HasData
                ? "-"
                : LastImpact.Angle.ToString("F0") + " deg" + (LastImpact.Tolerated > 0f
                    ? " (armor ricochets from " + LastImpact.Tolerated.ToString("F0") + " deg)" : ""));
            Live("Shell energy", () => LastImpact.HasData ? EnergyModel.Kilojoules(LastImpact.ShellEnergy) : "-");
            Live("Absorbed by armor", () => LastImpact.HasData ? EnergyModel.Kilojoules(LastImpact.Absorbed) : "-");
            Live("Energy left", () => LastImpact.HasData ? EnergyModel.Kilojoules(LastImpact.Residual) : "-");
            Live("Plate", () => LastImpact.HasData && LastImpact.Plate > 0 ? LastImpact.Plate.ToString() : "-");
            Live("Shell after the plate", () => LastImpact.ShellAfter <= 0f
                ? "-"
                : EnergyModel.Kilojoules(LastImpact.ShellAfter) + ", turned " + LastImpact.Deflection.ToString("F1") + " deg");
            Live("Fragments", () => LastImpact.FragmentCount <= 0
                ? "-"
                : LastImpact.FragmentCount + " in " + LastImpact.FragmentRays + " rays" +
                  (LastImpact.FragmentBundle > 1.001f ? " (" + LastImpact.FragmentBundle.ToString("F1") + " per ray)" : ""));
            Live("Fragment budget", () => LastImpact.FragmentEnergy <= 0f
                ? "-"
                : EnergyModel.Kilojoules(LastImpact.FragmentEnergy) + ", " + (LastImpact.FragmentMass * 1000f).ToString("F0") +
                  " g, ~" + LastImpact.FragmentSpeed.ToString("F0") + " m/s");
            Live("Fragment cloud", () => LastImpact.FragmentEnergy <= 0f
                ? "-"
                : LastImpact.SigmaX <= 0f
                    ? "all around (a burst)"
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

            // The simulated shell: what a cannon with a [CAN] tag has in the designer (the hardness) and what
            // a real cannon gives its shell.
            FloatSetting(ArmorFragmentsPlugin.SimShellHardness, "Simulated shell hardness",
                "How hard the shell is, compared with armor. 100 % is the reference. Harder shells ricochet less.", 5f, " %");
            // What a real cannon gives its shell. The mass follows from the caliber, as in the game.
            FloatSetting(ArmorFragmentsPlugin.SimShellVelocity, "Simulated shell velocity",
                "Speed of the imaginary shell.", 10f, " m/s");
            ToggleSetting(ArmorFragmentsPlugin.SimShellExplosive, "Simulated shell explosive",
                "Off = armor-piercing (AP): it goes through plates and flies on. On = explosive: HE, or APHE if it has a fuse. A real cannon uses its own Projectile Type (Basic = armor-piercing).");
            ToggleSetting(ArmorFragmentsPlugin.SimShellFuse, "Simulated shell fuse",
                "For an explosive shell: on = APHE (it goes through armor and bursts inside, after the fuse distance). Off = HE (it bursts on the first thing it touches). A real cannon's fuse is in the designer.");
            FloatSetting(ArmorFragmentsPlugin.SimShellFuseDistance, "Simulated fuse distance",
                "Meters of flight after the first impact at which the simulated APHE shell bursts.", 0.1f, " m");
            FloatSetting(ArmorFragmentsPlugin.SimShellBrittleness, "Simulated casing brittleness",
                "For an explosive shell: how easily its casing breaks into small fragments (more, smaller ones).", 5f, " %");
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

            // RELEASE-STRIP-BEGIN
            ToggleSetting(ArmorFragmentsPlugin.LogChordDiagnostics, "Log chord diagnostics",
                "Each simulated shot writes how far the line travels inside every part to the log ([CHORD] lines).");
            // RELEASE-STRIP-END
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
            FloatSetting(ArmorFragmentsPlugin.FillerHE, "Filler of HE shells",
                "Explosive filler (percent of the shell's mass) of an explosive shell without a fuse. The designer's filler slider starts here.", 0.5f, " %");
            FloatSetting(ArmorFragmentsPlugin.FillerAPHE, "Filler of APHE shells",
                "Explosive filler (percent of the shell's mass) of an explosive shell with a fuse. The designer's filler slider starts here.", 0.5f, " %");
            FloatSetting(ArmorFragmentsPlugin.DefaultShellBrittleness, "Default shell brittleness",
                "Casing brittleness of a cannon whose [CAN] tag does not say. A brittle casing makes more, smaller fragments.", 5f, " %");
            FloatSetting(ArmorFragmentsPlugin.FragmentArmorFactor, "Fragment armor factor",
                "A fragment that meets armor pays what a shell would (resistance x thickness x its own cross-section) times this. Above 1 armor stops fragments sooner.", 0.05f, "x");
            FloatSetting(ArmorFragmentsPlugin.GurneySpeed, "Explosive speed",
                "Gurney velocity of the explosive (m/s): with the filler it sets how fast the casing fragments fly outward.", 10f, " m/s");
            FloatSetting(ArmorFragmentsPlugin.DetonationSidewaysBias, "Burst sideways bias",
                "0 = a burst throws casing the same in every direction; 1 = mostly sideways, as a real casing does.", 0.05f, "");
            FloatSetting(ArmorFragmentsPlugin.DefaultFuse, "Default fuse",
                "Fuse distance a cannon gets when its Fuse switch is turned on in the designer.", 0.1f, " m");
            FloatSetting(ArmorFragmentsPlugin.MaxDeflection, "Max deflection",
                "Most a plate turns an armor-piercing shell that goes through it (degrees). The real turn grows with the hit angle, the plate's hardness and thickness.", 0.5f, " deg");
            IntSetting(ArmorFragmentsPlugin.MaxPlates, "Max plates",
                "Most plates one armor-piercing shell is resolved against (it flies on after each).");
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

        // The ammo racks' bars on the flight screen (AmmoHud.cs).
        private void BuildAmmoDisplay()
        {
            builder.Header("Ammo display", collapsed: true);
            ToggleSetting(ArmorFragmentsPlugin.AmmoHudEnabled, "Show ammo racks on the flight screen",
                "A bar for each ammo rack above the fire buttons: the rack's round (caliber and kind) on the left and its rounds left on the right.");
            ToggleSetting(ArmorFragmentsPlugin.AmmoHudRelative, "Show percent instead of rounds",
                "The bars show the percent of the rack that is left instead of the rounds left. A click on any bar does the same.");
        }

        // Mouse aim for turrets (TurretAim.cs): rotators marked Turret and cannons marked Pointer in the designer follow the circle on the screen.
        private void BuildMouseAim()
        {
            builder.Header("Mouse aim", collapsed: true);
            Live("Status", () => TurretAim.Status);
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Mouse aim on / off", TurretAim.Toggle),
            });
            // RELEASE-STRIP-BEGIN
            Live("Recording", () => TurretAim.RecordStatus);
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Record turret data (CSV)", TurretAim.ToggleRecording),
            });
            // RELEASE-STRIP-END
            Live("Aim key", () => TurretAim.KeyText(ArmorFragmentsPlugin.TurretAimKey));
            Live("Free look key", () => TurretAim.KeyText(ArmorFragmentsPlugin.TurretLookKey));
            Live("Sight key", () => TurretAim.KeyText(ArmorFragmentsPlugin.TurretSightKey));
            Live("Next group key", () => TurretAim.KeyText(ArmorFragmentsPlugin.TurretGroupKey));
            Live("Fire mouse button", () => ArmorFragmentsPlugin.TurretFireMouseButton.Value.ToString());
            Live("Fire key", () => TurretAim.KeyText(ArmorFragmentsPlugin.TurretFireKey));
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Change aim key", () => TurretAim.CaptureKey(ArmorFragmentsPlugin.TurretAimKey)),
                new KeyValuePair<string, Action>("Change free look key", () => TurretAim.CaptureKey(ArmorFragmentsPlugin.TurretLookKey)),
                new KeyValuePair<string, Action>("Change sight key", () => TurretAim.CaptureKey(ArmorFragmentsPlugin.TurretSightKey)),
            });
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Change fire mouse button", () =>
                    ArmorFragmentsPlugin.TurretFireMouseButton.Value = ArmorFragmentsPlugin.TurretFireMouseButton.Value == TurretFireMouse.Left ? TurretFireMouse.Middle : TurretFireMouse.Left),
                new KeyValuePair<string, Action>("Change fire key", () => TurretAim.CaptureKey(ArmorFragmentsPlugin.TurretFireKey)),
                new KeyValuePair<string, Action>("Change next group key", () => TurretAim.CaptureKey(ArmorFragmentsPlugin.TurretGroupKey)),
            });
            ToggleSetting(ArmorFragmentsPlugin.TurretGroupsHudEnabled, "Show turret groups on the flight screen",
                "A bar for each turret group at the bottom right of the flight screen (above the camera buttons): lit = follows the mouse aim, dark = holds. Click one (Alt frees the cursor) to switch it; several can be controlled together.");
            ToggleSetting(ArmorFragmentsPlugin.TurretTestSingle, "Control only one turret (test)",
                "Only one turret of the active group is controlled and fires; the others hold where they are. Pick it with the buttons below. For finding out whether a swing comes from one turret or from many on one hull.");
            Live("Turret under test", () => TurretAim.TestStatus);
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("<  Previous turret", () => TurretAim.TestStep(-1)),
                new KeyValuePair<string, Action>("Next turret  >", () => TurretAim.TestStep(1)),
            });
            // Five saved sets of the control's gains (direct control, response, braking, damping, shared hull...): pick one to load it, Save to
            // write the numbers in use into the picked one.
            builder.Note("Control profiles", "Pick a profile to load its gains (direct control and its ramp, gain, braking and dead band; response, braking, damping; shared hull; gap smoothing; start stagger). " +
                "Change the sliders below as you like and press Save to keep them in the picked profile.");
            Live("Control profile", () => TurretProfiles.Status);
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>(TurretProfiles.NameOf(1), () => LoadProfile(1)),
                new KeyValuePair<string, Action>(TurretProfiles.NameOf(2), () => LoadProfile(2)),
                new KeyValuePair<string, Action>(TurretProfiles.NameOf(3), () => LoadProfile(3)),
                new KeyValuePair<string, Action>(TurretProfiles.NameOf(4), () => LoadProfile(4)),
                new KeyValuePair<string, Action>(TurretProfiles.NameOf(5), () => LoadProfile(5)),
            });
            builder.TextInput("Profile name", "Rename the picked profile (shown on its button). Leave it empty to get \"Profile N\" back.",
                TurretProfiles.NameOf(TurretProfiles.Selected), TurretProfiles.MaxNameLength, text =>
                {
                    TurretProfiles.Rename(TurretProfiles.Selected, text);
                    // The buttons carry the names; rebuilding destroys the box being edited, so wait for Update.
                    rebuildRequested = true;
                });
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Save gains to the picked profile", TurretProfiles.Save),
            });
            ToggleSetting(ArmorFragmentsPlugin.TurretShareHull, "Slow turrets sharing a hull",
                "Each turret's gain is divided by the number of turrets of the group on the same hull body: many turrets pushing one hull together made it rock. Off for turrets on a heavy hull that does not move.");
            FloatSetting(ArmorFragmentsPlugin.TurretShareStrength, "Turrets at full gain per hull",
                "How many turrets of the group can work at full gain on one hull; with more, each one's gain is this number divided by the number of turrets (14 turrets: a seventh).", 0.1f, "");
            FloatSetting(ArmorFragmentsPlugin.TurretErrorFilter, "Gap smoothing (many turrets)",
                "When several turrets share a hull, the gap each is closing is smoothed over this many seconds, so a rocking hull is not chased (which rocked it more). 0 = off. A single turret is never smoothed.", 0.05f, " s");
            ToggleSetting(ArmorFragmentsPlugin.TurretFreeCursor, "Free cursor",
                "In the orbit view the ring is moved by the mouse over the screen and the camera follows softly once it is some way off the middle (no hard edge). Off = the ring stays at the middle and the mouse turns the camera.");
            FloatSetting(ArmorFragmentsPlugin.TurretFreeCursorZone, "Free cursor: zone",
                "How far from the middle the ring can go before the camera starts to follow, in percent of half the view's height (the camera starts a little inside it and follows fully a little outside it).", 1f, " %");
            FloatSetting(ArmorFragmentsPlugin.TurretFreeCursorFollow, "Free cursor: camera follow time",
                "How quickly the camera closes the distance to the ring once it is outside the zone. Smaller is tighter.", 0.05f, " s");
            FloatSetting(ArmorFragmentsPlugin.TurretFreeCursorRecenter, "Free cursor: recenter when the mouse is still",
                "When the mouse has been still for about half a second the camera slowly turns until the ring is back at the middle of the screen (the aim does not change). The time it takes to cover most of the distance; 0 = the ring stays where it is.", 0.5f, " s");
            FloatSetting(ArmorFragmentsPlugin.TurretFreeCursorSensitivity, "Free cursor: sensitivity",
                "Degrees the ring moves per count of the mouse (less when zoomed in).", 0.005f, " deg");
            ToggleSetting(ArmorFragmentsPlugin.TurretAutoReach, "Automatic aim: prefer targets the turrets can reach",
                "A group on automatic aim picks the target that the most of its turrets can point at within their arcs, not just the nearest one. The group bar shows how many can reach it.");
            ToggleSetting(ArmorFragmentsPlugin.TurretDirect, "Direct control",
                "Aims by moving each rotator along a speed profile from the rotator's own top speed and the angle still to go, instead of closing a fraction of the gap each step. The hull stabilizer, " +
                "the shared-hull gain division and the gap smoothing are not used. Compare it with the recording button. Off = the gain-based control.");
            FloatSetting(ArmorFragmentsPlugin.TurretDirectRamp, "Direct control: ramp",
                "Time a rotator takes to go from rest to its top speed, and back to rest. A longer ramp is gentler on a shared hull.", 0.05f, " s");
            FloatSetting(ArmorFragmentsPlugin.TurretDirectGain, "Direct control: gain near the aim",
                "Near the aim a rotator turns at this many times the angle still to go, per second. Lower is calmer (a springy joint that rings stops being chased) and slower to settle.", 0.5f, " /s");
            FloatSetting(ArmorFragmentsPlugin.TurretDirectBrake, "Direct control: braking (several turrets)",
                "With two or more turrets on one hull, the gap is reduced by the gun's own speed times this many seconds, which keeps the turrets and the hull from pushing each other into a swing. A lone turret does not use it.", 0.01f, " s");
            FloatSetting(ArmorFragmentsPlugin.TurretDirectDeadband, "Direct control: dead band",
                "A rotator whose angle still to go is smaller than this rests.", 0.01f, " deg");
            ToggleSetting(ArmorFragmentsPlugin.TurretFireOnTarget, "Fire only turrets on target",
                "A turret fires only when its gun points within the tolerance of the cursor. Turrets still turning, or stopped at their limit, hold fire while the others shoot.");
            FloatSetting(ArmorFragmentsPlugin.TurretFireTolerance, "On-target tolerance",
                "How far off the cursor a turret's gun may be and still fire.", 0.1f, " deg");
            FloatSetting(ArmorFragmentsPlugin.TurretFireStagger, "Salvo ripple",
                "A salvo fires one cannon at a time with this many seconds between them. 0 = all at once.", 0.01f, " s");
            ToggleSetting(ArmorFragmentsPlugin.TurretLineOfFire, "Hold fire into own vehicle",
                "A cannon does not fire while its barrel points at a part of its own vehicle within 20 meters.");
            FloatSetting(ArmorFragmentsPlugin.TurretMoveStagger, "Turret start stagger",
                "The turrets of a group start following the cursor one after another, each this many seconds after the one before, so they don't all push the hull at once. 0 = together.", 0.005f, " s");
            builder.Note("Automatic aim", "The Ground and Air buttons under a turret group's bar (with both lit the group takes both) make the group aim and fire by itself at the nearest target it can reach. A vehicle of a player counts as air above the height or speed below, otherwise as ground; the game's own NPC aircraft, ground vehicles and ships say what they are.");
            ToggleSetting(ArmorFragmentsPlugin.TurretAutoFire, "Automatic groups fire",
                "A group on automatic aim fires by itself, one cannon at a time, when its gun is on the target (within the on-target tolerance), clear of its own vehicle and the target is in range. Off: it only aims.");
            FloatSetting(ArmorFragmentsPlugin.TurretAutoRange, "Automatic aim range",
                "Targets further than this many meters from the group's lead turret are left alone.", 50f, " m");
            ToggleSetting(ArmorFragmentsPlugin.TurretAutoLead, "Lead moving targets",
                "Aim ahead of a moving target, allowing for the vehicle's own speed too. Off: aim at where the target is now.");
            ToggleSetting(ArmorFragmentsPlugin.TurretAutoDrop, "Allow for shell drop",
                "Aim above the target by as much as the shell falls on the way (gravity and the shell's drag, as the game's physics does it). Off: the barrel points straight at the target.");
            ToggleSetting(ArmorFragmentsPlugin.TurretAutoClearView, "Only targets in clear view",
                "Leave out targets hidden behind the ground, so the turrets don't shoot at a hill.");
            ToggleSetting(ArmorFragmentsPlugin.TurretAutoSkipFriendly, "Skip friendly targets",
                "Leave out targets of your own team and friendly NPCs. Off: every vehicle but your own is a target (also other players').");
            FloatSetting(ArmorFragmentsPlugin.TurretAutoAirAgl, "Player vehicle is air above",
                "A vehicle of a player higher than this above the ground counts as air (the game does not say what it is).", 1f, " m");
            FloatSetting(ArmorFragmentsPlugin.TurretAutoAirSpeed, "Player vehicle is air above speed",
                "A vehicle of a player faster than this and more than 3 meters above the ground counts as air, however low it flies.", 5f, " m/s");
            ToggleSetting(ArmorFragmentsPlugin.TurretObstacleStop, "Stop rotators at obstacles",
                "A rotator blocked by something (a barrel on the deck, a turret on another) stops pushing and may only turn away from it; it tries again after the wait below. Without it the joint keeps pushing harder and harder.");
            Live("Obstacle guard", () => TurretAim.ObstacleStatus);
            FloatSetting(ArmorFragmentsPlugin.TurretObstacleDegrees, "Obstacle: degrees ahead",
                "A rotator counts as blocked when the angle it asks for is this far ahead of the part and the part is not catching up. Lower stops sooner; too low can stop a heavy turret that is only following.", 0.5f, " deg");
            FloatSetting(ArmorFragmentsPlugin.TurretObstacleRetry, "Obstacle: wait before trying again",
                "How long a blocked rotator waits before turning further again (it also tries again at once if it backs off 2 degrees).", 0.5f, " s");
            FloatSetting(ArmorFragmentsPlugin.TurretZoom, "Zoom",
                "How many times the right mouse button zooms in (held: gradually in, let go: gradually out).", 0.5f, " x");
            FloatSetting(ArmorFragmentsPlugin.TurretSightZoom, "Sight zoom",
                "How many times the right mouse button zooms in through the sight camera.", 0.5f, " x");
            FloatSetting(ArmorFragmentsPlugin.TurretSightSensitivity, "Sight sensitivity",
                "Degrees the aim turns for each mouse count through the sight (less when zoomed in).", 0.005f, " deg");
            FloatSetting(ArmorFragmentsPlugin.TurretResponse, "Turret response",
                "Seconds the turret takes to close the gap to the circle. Smaller is snappier; if the gun swings back and forth around the circle, raise it.", 0.01f, " s");
            FloatSetting(ArmorFragmentsPlugin.TurretDerivative, "Turret braking",
                "The turret brakes in proportion to how fast the gun moves in the world (the derivative): this many seconds of its speed are taken off the gap. It keeps a ship's turrets from shaking the hull and swinging together. 0 = off. Raise it if the turrets still swing.", 0.01f, " s");
            FloatSetting(ArmorFragmentsPlugin.TurretDamping, "Turret damping",
                "The turret aims from where the gun will be this many seconds from now, so it does not overshoot and swing around the circle. 0 = off. Raise it if the gun still swings; too much makes it sluggish.", 0.01f, " s");
            FloatSetting(ArmorFragmentsPlugin.TurretCameraHeight, "Camera pivot height",
                "The point the camera turns around (where it sits at full zoom) is centered on the vehicle and this many meters above its highest point.", 0.1f, " m");
            builder.Note("How to", "Designer: Turret on the rotators; on a cannon, Create turret group (add more turrets, fixed cannons, a sight camera, and what each fires with). Alt: cursor. Free look key: orbit the camera. Sight key: a turret's sight camera. Group key: next turret group. Right: zoom. Mouse button / fire key: fire.");
        }

        private void BuildImpactCamera()
        {
            builder.Header("Impact camera", collapsed: true);
            ToggleSetting(ArmorFragmentsPlugin.ImpactCameraEnabled, "Impact camera",
                "Shows a small camera when a shell you fired hits another vehicle. The toggle key does the same.");
            ToggleSetting(ArmorFragmentsPlugin.ImpactCameraSwitchTargets, "Switch targets",
                "Hitting another vehicle while the camera is open moves it there.");
            FloatSetting(ArmorFragmentsPlugin.ShellLeadIn, "Lead-in distance",
                "Meters of the shot shown before the impact. Takes effect with the next impact, after the camera has closed.", 1f, " m");
            FloatSetting(ArmorFragmentsPlugin.ImpactCameraDistance, "Camera distance",
                "How far the camera stays behind the shot, and from the impact.", 0.5f, " m");
            FloatSetting(ArmorFragmentsPlugin.ImpactCameraLinger, "Stay open after impact",
                "Seconds the camera stays open after the shot reaches the impact.", 0.5f, " s");
            FloatSetting(ArmorFragmentsPlugin.ImpactCameraWidthPercent, "Window width",
                "Width of the window, as a percentage of the screen.", 1f, " %");
            FloatSetting(ArmorFragmentsPlugin.ImpactCameraMargin, "Window margin",
                "Distance from the top right corner, in pixels.", 2f, " px");
            IntSetting(ArmorFragmentsPlugin.ImpactCameraResolution, "Picture height",
                "Pixels. Lower = cheaper to render.");
            FloatSetting(ArmorFragmentsPlugin.ImpactCameraFov, "Field of view",
                "Degrees.", 1f, " deg");
            ToggleSetting(ArmorFragmentsPlugin.ImpactCameraXRay, "X-ray on impact",
                "Switches to the X-ray view the moment the shot hits, showing the damage as the fragments arrive.");
            ToggleSetting(ArmorFragmentsPlugin.ImpactCameraDamageView, "Armor damage view",
                "Draws the armor's damage map over the plates in the impact camera: dark red where a plate is badly dented or holed, a lighter " +
                "see-through red where it is slightly marked, nothing where it is untouched.");
            IntSetting(ArmorFragmentsPlugin.ImpactCameraGhostLayer, "X-ray layer",
                "Unity layer of the X-ray ghosts (29 or 30 = the game's mod layers, 3 = unused). Change it if the ghosts do not show; it applies to the next impact.");
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
            FloatSetting(ArmorFragmentsPlugin.FragmentMass, "Fragment mass",
                "Typical mass of one fragment (of a 75 mm shell or a plate). The number of fragments is the cloud's mass over this; there is no limit to it.", 0.5f, " g");
            IntSetting(ArmorFragmentsPlugin.MaxVisualFragments, "Max number of visual fragments",
                "Most rays one cloud is traced and drawn with. A cloud with more fragments puts a bundle of them in each ray: how far a fragment goes is worked out for one real fragment, so this changes the detail and the cost, never the penetration.");
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
            ToggleSetting(ArmorFragmentsPlugin.HideRaysOutsideViews, "Rays only in the camera and simulated shot",
                "On: rays are drawn only in the impact camera and in the simulated shot; the shots of the world around you draw none (what they do does not change). Off: every shot draws its rays.");
            FloatSetting(ArmorFragmentsPlugin.RayWidthScale, "Ray width scale",
                "Rays are as thick as what they show (the shell as wide as its caliber, a fragment as wide as the fragment). This multiplies that.", 0.1f, "x");
            FloatSetting(ArmorFragmentsPlugin.MinRayWidth, "Thinnest ray",
                "No ray is drawn thinner than this, so tiny fragments stay visible.", 0.001f, " m");
            FloatSetting(ArmorFragmentsPlugin.RayTrailLength, "Trail length",
                "Length of the fading trail behind the moving ray.", 0.1f, " m");
            FloatSetting(ArmorFragmentsPlugin.BubbleSize, "Impact bubble size",
                "Diameter of the colored ball at each impact.", 0.01f, " m");
            FloatSetting(ArmorFragmentsPlugin.BubbleLifetime, "Impact bubble time",
                "Seconds an impact bubble takes to shrink away.", 0.05f, " s");
        }

        // Marks on armor plates (see DamageMaps/).
        private void BuildDamageMaps()
        {
            builder.Header("Damage maps", collapsed: true);
            ToggleSetting(ArmorFragmentsPlugin.DamageMapsEnabled, "Marks on armor",
                "Shots leave dents, holes, grooves and pits on armor plates.");
            ToggleSetting(ArmorFragmentsPlugin.DamageMapsPaintDamage, "Scorched and flaked paint",
                "Marks scorch the paint and knock it off, showing steel. Marked plates are drawn with the Lit shader in their paint's color. Applies to the next vehicle that gets marks (Clear starts over).");
            ToggleSetting(ArmorFragmentsPlugin.PartDecalsEnabled, "Marks on parts that are not armor",
                "Holes, scorch and bare metal on wings, plain fuselage and other parts a shell goes through or bursts on, laid over the surface as decals. Only where seen from outside.");
            IntSetting(ArmorFragmentsPlugin.PartDecalsMax, "Decals kept per vehicle",
                "The oldest are taken off beyond this (each is a small mesh and two 128 x 128 textures).");
            ToggleSetting(ArmorFragmentsPlugin.DamageMapsFlex, "Heavy hits bend plates",
                "A heavy blow bends the whole plate it hits, and a little the armor plates joined to it. Only a look.");
            ToggleSetting(ArmorFragmentsPlugin.DamageMapsAvoidOverlap, "Marks stay inside overlapping plates",
                "Where armor plates overlap or are clipped into each other, a dent cannot run through the plate behind it and metal pushed out cannot come through the plate in front.");
            ToggleSetting(ArmorFragmentsPlugin.DamageMapsSteelGloss, "Shiny steel, dull soot",
                "The steel that shows through the paint is metallic and shiny (brightest where a shell skidded or cut through) and scorched paint is dull. Needs 'Scorched and flaked paint'. Applies to the next vehicle that gets marks.");
            ToggleSetting(ArmorFragmentsPlugin.ImpactEffectsEnabled, "Impact effects",
                "A flash, sparks, paint dust and smoke where a shell hits armor.");
            ToggleSetting(ArmorFragmentsPlugin.ImpactEffectsLitSmoke, "Smoke and dust lit by the scene",
                "Smoke and paint dust use the URP lit particle shader (sun, sky and shadows light them). Takes effect after F6 or a restart.");
            FloatSetting(ArmorFragmentsPlugin.ImpactEffectsAmount, "Impact effects amount",
                "How many sparks and how much smoke (1 = normal).", 0.1f, "x");
            ToggleSetting(ArmorFragmentsPlugin.DamageMapsShareMarks, "Share marks with other players",
                "In multiplayer, send the marks of your shots to the other players who have the mod, and draw theirs. Players without the mod see nothing.");
            ToggleSetting(ArmorFragmentsPlugin.DamageMapsSimulatedShots, "Marks on simulated shots",
                "The simulated shot leaves its marks (and impact simulation) on the plates too. Only a look: they never weaken the armor, and Clear removes them.");
            ToggleSetting(ArmorFragmentsPlugin.KeepPlatesApart, "Keep armor plates out of the mesh merge",
                "Needed for the marks. Takes effect when a flight starts: the game merges the meshes of a group then, and a merged plate cannot be changed.");
            if (!NetSync.GlobalLocked)
                ToggleSetting(ArmorFragmentsPlugin.DamageMapsWeakenArmor, "Marks weaken the armor",
                    "A hole lets shells and fragments through; a dent makes the plate thinner there. Not synchronized between players yet.");
            ToggleSetting(ArmorFragmentsPlugin.DamageMapsGeometry, "Real geometry (experimental)",
                "Marked flat armor plates are rewritten as real geometry: a fine grid pushed in or out by the damage, and holes that are " +
                "really holes. The plate keeps its own paint, texture and decals. Thick, curved or non-rectangular plates keep the maps.");
            FloatSetting(ArmorFragmentsPlugin.DamageMapsGridMm, "Geometry grid",
                "Cell size of the real geometry, in millimeters. Smaller = finer, more vertices.", 1f, " mm");
            FloatSetting(ArmorFragmentsPlugin.DamageMapsFineGridMm, "Fine grid at holes",
                "Cell size of the real geometry around holes and sharp features (cracks, rims), in millimeters. 0 = none. Smaller = sharper edges.", 0.5f, " mm");
            IntSetting(ArmorFragmentsPlugin.DamageMapsMaxKiloVertices, "Geometry vertex limit",
                "Most vertices, in thousands, one plate's real geometry may have. A bigger plate gets a coarser grid.");
            IntSetting(ArmorFragmentsPlugin.DamageMapsBudgetKiloVertices, "Geometry budget per vehicle",
                "Most vertices, in thousands, all of a vehicle's damaged plates may have together. Too many makes every frame slow; plates marked after the budget is used get a coarser grid.");
            FloatSetting(ArmorFragmentsPlugin.GeometryStartPixels, "Geometry from (pixels)",
                "Below this many pixels of error the maps are used. The error is the biggest push x tan(view angle) x pixels per meter.", 0.1f, " px");
            FloatSetting(ArmorFragmentsPlugin.GeometryFullPixels, "Full geometry at (pixels)",
                "From this many pixels the plate shows its full geometry; in between the maps and the geometry are mixed.", 0.1f, " px");
            FloatSetting(ArmorFragmentsPlugin.GeometryAngleStart, "Geometry from angle",
                "Degrees from the plate's normal (0 = head-on, 90 = grazing). Seen at less than this, the maps are always enough.", 1f, " deg");
            FloatSetting(ArmorFragmentsPlugin.GeometryAngleFull, "Full geometry at angle",
                "From this angle the geometry may be shown in full (if the pixel error says so too).", 1f, " deg");
            FloatSetting(ArmorFragmentsPlugin.GeometryBlendSeconds, "Transition time",
                "Seconds the change between the maps and the geometry takes.", 0.05f, " s");
            ToggleSetting(ArmorFragmentsPlugin.DamageMapsHoleBothSides, "Holes: draw both sides",
                "Shows the inside of the plate through a hole.");
            FloatSetting(ArmorFragmentsPlugin.DamageMapsDensity, "Detail",
                "Pixels per meter of plate. Applies to vehicles that get their maps from now on.", 16f, " px/m");
            FloatSetting(ArmorFragmentsPlugin.DamageMapsNormalStrength, "Dent depth",
                "How deep the dents look.", 0.1f, "x");
            ChoiceSetting(ArmorFragmentsPlugin.DamageMapsRelief, "Relief",
                "How the marks are shaded. Normal map: lighting only (as before). Bump map: the shader's parallax moves the surface itself " +
                "by the marks' height. Both: the two together.",
                new[] { "Normal map", "Bump map", "Normal + bump" });
            IntSetting(ArmorFragmentsPlugin.DamageMapsMaxVehicles, "Vehicles at once",
                "Vehicles with maps at the same time. The one hit longest ago gives its maps back.");
            ToggleSetting(ArmorFragmentsPlugin.UseGpu, "Use the graphics card (OpenCL)",
                "The impact simulation runs on the graphics card when the computer has OpenCL, and on the CPU if not. Takes effect the next time the game or the plugin loads.");
            Live("Graphics card", () => GpuStatus.Text);
            IntSetting(ArmorFragmentsPlugin.SimQuality, "Simulation quality",
                "0 = off (the quick marks), 1 = low (40x40), 2 = medium (64x64), 3 = high (96x96), 4 = ultra (128x128), never finer than the map can show. A shell's impact is worked out by a small physics simulation of the plate, in the background; the mark appears when it is ready.");
            ToggleSetting(ArmorFragmentsPlugin.SimCracks, "Cracks in the marks",
                "The torn links of the simulated plate make thin grooves around a hole and across a dent.");
            ToggleSetting(ArmorFragmentsPlugin.SimSmoothEdges, "Smooth, ragged hole edges",
                "The edge of a hole follows where the simulated metal runs out, a little ragged, instead of the simulation grid's square steps.");
            IntSetting(ArmorFragmentsPlugin.SimThreads, "Simulation threads",
                "Threads the simulation may use. 0 = automatic (all the cores but two). They run below the game's own threads and take work only when free.");
            FloatSetting(ArmorFragmentsPlugin.SimTimeLimit, "Simulation time limit",
                "Seconds a simulation may take; a slower one is given up and the quick marks are drawn instead.", 0.5f, " s");
            ToggleSetting(ArmorFragmentsPlugin.SimAutoQuality, "Lower the quality if it is slow",
                "If a simulation takes most of its time limit the next ones use a lower quality, and go back up when they are quick.");
            IntSetting(ArmorFragmentsPlugin.SimMaxQueue, "Simulations that may wait (at least)",
                "When that many are waiting, new impacts get the quick marks. While simulations are quick (a graphics card) more may wait, up to about 0.8 s of work or 12.");
            ToggleSetting(ArmorFragmentsPlugin.SimShedLoad, "Give threads back when the FPS drops",
                "While a simulation works and the frame rate drops, it uses fewer threads for a while.");
            Live("Simulation", () => PlateDamageMaps.SimStatus);
            Live("Maps", () => PlateDamageMaps.Status);
            // RELEASE-STRIP-BEGIN
            Live("Test", () => PlateDamageMaps.Message);
            // RELEASE-STRIP-END
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                // RELEASE-STRIP-BEGIN
                new KeyValuePair<string, Action>("Test: dent", () => PlateDamageMaps.RequestTest(DamageKind.Blocked)),
                new KeyValuePair<string, Action>("Test: hole", () => PlateDamageMaps.RequestTest(DamageKind.Penetrated)),
                new KeyValuePair<string, Action>("Test: groove", () => PlateDamageMaps.RequestTest(DamageKind.Ricochet)),
                // RELEASE-STRIP-END
                new KeyValuePair<string, Action>("Clear", PlateDamageMaps.Clear),
            });
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
                // RELEASE-STRIP-BEGIN
                new KeyValuePair<string, Action>("Camera diagnostic", CameraDiagnostic.Run),
                // RELEASE-STRIP-END
            });
        }

        // RELEASE-STRIP-BEGIN
        // The smoke of the cannons: a real fluid simulation on the GPU.
        private void BuildFluidSmoke()
        {
            builder.Header("Cannon smoke (fluid)", collapsed: true);
            TextControl status = builder.Text("Now", "");
            refreshers.Add(() => status.ValueText.Text = (FluidSmoke.Enabled ? "on" : "off") + ", quality " + FluidSmoke.QualityName);
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("On / off", FluidSmoke.Toggle),
                new KeyValuePair<string, Action>("Quality", FluidSmoke.CycleQuality),
                new KeyValuePair<string, Action>("Show solids (red)", FluidSmoke.ToggleShowSolid),
            });
            FloatSetting(ArmorFragmentsPlugin.SmokeBlastBrake, "Blast brake",
                "The brake on the air during the blast, per second. High stops the blast at once; low lets the fireball and the smoke fly farther. Applies to the next shot.", 0.5f, " /s");
            FloatSetting(ArmorFragmentsPlugin.SmokeBlastSeconds, "Blast brake duration",
                "How long the hard brake of the blast lasts, in seconds. Applies to the next shot.", 0.05f, " s");
            FloatSetting(ArmorFragmentsPlugin.SmokeLateBrake, "Late brake",
                "The brake on the air after the blast, per second. 1.5 stops the smoke after about 3 s; 0.4 lets it drift for many seconds; 0 never brakes it. Applies to the clouds already in the air too.", 0.05f, " /s");
            FloatSetting(ArmorFragmentsPlugin.SmokeBuoyancy, "Smoke buoyancy",
                "How strongly heat lifts the smoke. More makes a rising column; 0 only drifts.", 1f, " m/s2");
            FloatSetting(ArmorFragmentsPlugin.SmokeHeatCooling, "Heat loss",
                "How fast the smoke loses its residual heat, per second. Lower: it rises for longer.", 0.05f, " /s");
            FloatSetting(ArmorFragmentsPlugin.SmokeResidualHeat, "Smoke heat",
                "How hot the propellant smoke is after the flame (0 to 0.35). Applies to the next shot.", 0.025f, "");
            FloatSetting(ArmorFragmentsPlugin.SmokeTurbulence, "Background turbulence",
                "A weak endless swirling of the air so the smoke keeps billowing in calm weather. 0 = still air.", 0.025f, " m/s2");
            FloatSetting(ArmorFragmentsPlugin.SmokeSizeThinning, "Small guns: thinner smoke",
                "The amount of smoke, heat and dust of a shot is (calibre / 75 mm) to this power. 0 = every gun as dense as a 75 mm, 1 = proportional to the calibre. Applies to the next shot.", 0.05f, "");
            FloatSetting(ArmorFragmentsPlugin.SmokeSizeCooling, "Small guns: smoke fades faster",
                "How much faster the smoke of a small gun loses its heat and thins out (rates divided by (calibre / 75 mm) to this power).", 0.1f, "");
            FloatSetting(ArmorFragmentsPlugin.SmokeShockWave, "Shock wave",
                "The pressure wave of the shot: a ring of dust over the ground and a push of the air around the barrel. Applies to the next shot.", 0.1f, "");
            FloatSetting(ArmorFragmentsPlugin.SmokeMultiScatter, "Multiple scattering",
                "How much light scatters several times inside dense smoke, so its middle is lighter and softer.", 0.05f, "");
            FloatSetting(ArmorFragmentsPlugin.SmokeDustWeight, "Dust settling",
                "How fast dust settles out of the air.", 0.25f, " m/s2");
            FloatSetting(ArmorFragmentsPlugin.SmokeDetail, "Small eddies",
                "Small eddies inside the smoke, as strong as the smoke is dense.", 0.1f, " m/s2");
            ToggleSetting(ArmorFragmentsPlugin.SmokeTerrain, "Terrain and walls stop the smoke",
                "Hills, slopes, roads (a height map from rays) and still walls, buildings and rocks are solid for the smoke.");
            ToggleSetting(ArmorFragmentsPlugin.SmokeOverflow, "Smoke goes on past the box",
                "Smoke that reaches the edge of its box goes on into a second, bigger box with coarser cells (at most two at a time), instead of being cut off.");
            ToggleSetting(ArmorFragmentsPlugin.SmokeFastDrawing, "Faster drawing of the smoke",
                "Skips blocks with no smoke and looks up the sun and the shadows on every second sample. About a third faster to draw, the picture hardly changes.");
            ToggleSetting(ArmorFragmentsPlugin.SmokeFastSolids, "Measure solids less often when nothing moves",
                "After the blast, when nothing near the cloud moves, vehicles and ground are measured every second frame.");
            ToggleSetting(ArmorFragmentsPlugin.SmokeHalfPrecision, "16-bit smoke fields (ultra, high and medium)",
                "The ultra, high and medium levels use half the memory and about 40% of the time per step with the same look. Applies to the next cloud.");
            ToggleSetting(ArmorFragmentsPlugin.SmokePlayerBubble, "Box that follows my vehicle",
                "A small box follows your vehicle while smoke reaches it and takes the smoke inside it, so smoke you drive through or drag along travels with you.");
            ToggleSetting(ArmorFragmentsPlugin.SmokePlayerBubbleTail, "Leave a trail of smoke behind",
                "The smoke the following box leaves behind as you drive on becomes a new cloud that stays on the road and thins out.");
            FloatSetting(ArmorFragmentsPlugin.SmokePlayerBubbleSize, "Following box width",
                "How wide the box around your vehicle is, in metres. Applies the next time it is made.", 1f, " m");
            FloatSetting(ArmorFragmentsPlugin.SmokeBoxSize, "Simulation box size",
                "How big the box the smoke is simulated in is (1 = standard). Smoke that leaves it vanishes; a bigger box has coarser cells because the cell count is fixed by the quality level. Applies to the next shot.", 0.1f, " x");
            FloatSetting(ArmorFragmentsPlugin.SmokeJetFocus, "Jet focus",
                "The shape of the blast: 0 is a round fireball, 1 a narrow jet along the barrel. Applies to the next shot.", 0.05f, "");
            FloatSetting(ArmorFragmentsPlugin.SmokeMuzzleBrake, "Muzzle brake",
                "How much of the gas leaves sideways, as through a muzzle brake. 0 for a bare barrel. Applies to the next shot.", 0.05f, "");
            ToggleSetting(ArmorFragmentsPlugin.SmokeFlash, "Flash of light",
                "The blast lights up the smoke and, if the game's render pipeline draws extra lights, the ground and the vehicle.");
            FloatSetting(ArmorFragmentsPlugin.SmokeFlashIntensity, "Flash brightness",
                "How bright the flash of light is.", 0.1f, "");
            ToggleSetting(ArmorFragmentsPlugin.SmokeGroundDust, "Dust has the ground's colour",
                "The dust the blast lifts takes the colour of the ground under it. Applies to the next shot.");
            FloatSetting(ArmorFragmentsPlugin.SmokeBlastDust, "Blast dust",
                "How much dust the blast lifts from the ground (spots along the line of fire and a fan from under the muzzle). 0 for none. Applies to the next shot.", 0.05f, "");
            ToggleSetting(ArmorFragmentsPlugin.SmokeShadows, "Smoke casts shadows",
                "The smoke casts the sun's shadow on the ground and on vehicles (inside the game's shadow distance).");
            FloatSetting(ArmorFragmentsPlugin.SmokeShadowStrength, "Shadow darkness",
                "How dark the shadow of dense smoke is.", 0.05f, "");
            ToggleSetting(ArmorFragmentsPlugin.SmokeWash, "Propellers, rotors and jets blow the smoke",
                "Slipstream of propellers, downwash of helicopter rotors and exhaust of jet engines move the smoke (vehicles with a part in the smoke's box).");
            FloatSetting(ArmorFragmentsPlugin.SmokeWashStrength, "Prop wash strength",
                "How hard propellers and rotors blow. 1 is what their thrust gives.", 0.1f, "");
            ToggleSetting(ArmorFragmentsPlugin.SmokeWake, "Moving vehicles drag the air",
                "A moving vehicle drags the air near it and behind it along, so the smoke is pulled after it.");
            FloatSetting(ArmorFragmentsPlugin.SmokeWakeStrength, "Vehicle wake strength",
                "The share of the vehicle's speed that the air next to it takes.", 0.05f, "");
        }
        // RELEASE-STRIP-END

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

        private void LoadProfile(int number)
        {
            TurretProfiles.Select(number);
            // The sliders show the old numbers; rebuilding destroys the button being clicked, so wait for Update.
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

        // A setting that is one of a few named choices (a slider with that many positions that shows the name).
        private void ChoiceSetting(ConfigEntry<int> entry, string label, string tooltip, string[] names)
        {
            int last = names.Length - 1;
            builder.Slider(label, tooltip, 0f, last, names.Length,
                Mathf.Clamp(entry.Value, 0, last), "",
                x => names[Mathf.Clamp(Mathf.RoundToInt(x), 0, last)],
                x => SetValue(entry, Mathf.Clamp(Mathf.RoundToInt(x), 0, last), false),
                x => NetSync.SaveConfig());
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
                entry.Value, "",
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
                entry.Value, (unit ?? "").Trim(),
                x => x.ToString(format, CultureInfo.InvariantCulture),
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
