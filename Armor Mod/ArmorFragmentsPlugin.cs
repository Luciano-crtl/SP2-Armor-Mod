using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ArmorFragments
{
    [BepInPlugin("com.yourname.armorfragments", "Armor Fragments", Version)]
    public class ArmorFragmentsPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ArmorFragmentsPlugin Instance;

        // Kept so that OnDestroy (Lifecycle.Shutdown) can take back what Awake put in: the Harmony instance and the log listener.
        internal static Harmony HarmonyInstance;
        internal static ILogListener MirrorListener;

        // These show up in BepInEx/config/com.yourname.armorfragments.cfg
        // after the first run, so you can tune them without rebuilding.
        internal static ConfigEntry<bool> ApplyDamage;
        internal static ConfigEntry<float> FragmentMass;
        internal static ConfigEntry<int> MaxVisualFragments;
        internal static ConfigEntry<float> BallisticRayRange;
        internal static ConfigEntry<bool> DrawRays;
        internal static ConfigEntry<bool> HideRaysOutsideViews;   // local: rays only in the impact camera and the simulated shot
        internal static ConfigEntry<float> RayWidthScale;
        internal static ConfigEntry<float> MinRayWidth;
        internal static ConfigEntry<bool> IgnoreGameDamageOnArmor;
        internal static ConfigEntry<bool> GameDamageExplosions;
        internal static ConfigEntry<bool> ArmoredStructureDamage;
        internal static ConfigEntry<float> RayTrailLength;
        internal static ConfigEntry<float> BubbleSize;
        internal static ConfigEntry<float> BubbleLifetime;
        internal static ConfigEntry<bool> MirrorLogToGameConsole;
        internal static ConfigEntry<bool> LogNetworkDamage;
        internal static ConfigEntry<bool> HostResolvesShots;

        // Impact camera (all local: none of it affects other players).
        internal static ConfigEntry<bool> ImpactCameraEnabled;
        internal static ConfigEntry<UnityEngine.InputSystem.Key> ImpactCameraKey;
        internal static ConfigEntry<bool> ImpactCameraSwitchTargets;
        internal static ConfigEntry<float> ShellLeadIn;
        internal static ConfigEntry<float> ImpactCameraDistance;
        internal static ConfigEntry<float> ImpactCameraLinger;
        internal static ConfigEntry<float> ImpactCameraWidthPercent;
        internal static ConfigEntry<float> ImpactCameraMargin;
        internal static ConfigEntry<int> ImpactCameraResolution;
        internal static ConfigEntry<float> ImpactCameraFov;
        internal static ConfigEntry<bool> ImpactCameraShadows;
        internal static ConfigEntry<bool> ImpactCameraXRay;
        internal static ConfigEntry<bool> ImpactCameraDamageView;   // local: the damage map drawn over the plates in the impact camera
        internal static ConfigEntry<int> DamageMapsRelief;           // local: 0 normal map, 1 bump (height) map, 2 both
        internal static ConfigEntry<bool> UseGpu;                    // local: the impact simulation on the graphics card (OpenCL), the CPU if it cannot
        internal static ConfigEntry<bool> DamageMapsGeometry;        // local, experimental: marked flat plates are rewritten as real geometry
        internal static ConfigEntry<float> DamageMapsGridMm;         // local: that geometry's cell size
        internal static ConfigEntry<int> DamageMapsMaxAtlas;         // local: the biggest atlas a vehicle's plates may share (2048 or 4096)
        internal static ConfigEntry<float> DamageMapsFineGridMm;     // local: the finer cell size around holes and sharp features (0 = none)
        internal static ConfigEntry<int> DamageMapsMaxKiloVertices;  // local: the most vertices one plate's geometry may have (thousands)
        internal static ConfigEntry<int> DamageMapsBudgetKiloVertices;  // local: the most vertices all of a vehicle's plates may have together (thousands)
        internal static ConfigEntry<float> GeometryStartPixels, GeometryFullPixels;   // local: the transition from maps to geometry, in pixels of error
        internal static ConfigEntry<float> GeometryAngleStart, GeometryAngleFull;     // local: ... and in degrees of view angle
        internal static ConfigEntry<float> GeometryBlendSeconds;                      // local: ... and how fast it moves
        internal static ConfigEntry<int> ImpactCameraGhostLayer;

        // Shot simulation and X-ray view.
        internal static ConfigEntry<bool> XRayAfterSimulation;
        internal static ConfigEntry<UnityEngine.InputSystem.Key> VehicleXRayKey;
        internal static ConfigEntry<float> XRayRadius;
        internal static ConfigEntry<float> XRayDuration;
        internal static ConfigEntry<float> XRayDarkPercent;
        internal static ConfigEntry<bool> XRayRimLook;
        internal static ConfigEntry<bool> XRayShowUndamaged;
        internal static ConfigEntry<int> XRayMaxRimKiloVertices;
        internal static ConfigEntry<int> XRayBudgetKiloVertices;

        // Energy model (phase 2: defined and editable, not used by the calculation yet).
        internal static ConfigEntry<float> DefaultShellHardness;
        internal static ConfigEntry<float> SimShellHardness;
        internal static ConfigEntry<float> SimCaliber;
        internal static ConfigEntry<float> SimShellVelocity;
        internal static ConfigEntry<float> ArmorResistance;
        internal static ConfigEntry<float> RicochetFriction;
        internal static ConfigEntry<float> RicochetRandomness;
        internal static ConfigEntry<float> DamagePerKiloJoule;
        internal static ConfigEntry<float> DensityModifier;
        internal static ConfigEntry<bool> ConstantForceLoss;
        internal static ConfigEntry<bool> SimpleFragments;
        internal static ConfigEntry<float> FillerHE;
        internal static ConfigEntry<float> FillerAPHE;
        internal static ConfigEntry<float> GurneySpeed;
        internal static ConfigEntry<float> DetonationSidewaysBias;
        internal static ConfigEntry<float> DefaultFuse;
        internal static ConfigEntry<bool> KeepPlatesApart;          // local: armor plates keep their own renderer in flight
        internal static ConfigEntry<bool> DamageMapsEnabled;        // local: marks on the plates
        internal static ConfigEntry<bool> DamageMapsPaintDamage;    // local: marks scorch the paint and flake it off, showing steel (marked plates are drawn with the Lit shader)
        internal static ConfigEntry<bool> PartDecalsEnabled;        // local: marks (holes, scorch) on the parts that are not armor, as decals
        internal static ConfigEntry<int> PartDecalsMax;             // local: how many a vehicle keeps
        internal static ConfigEntry<bool> DamageMapsFlex;           // local: a heavy blow bends the plate it hits, and the armor plates joined to it, a little
        internal static ConfigEntry<bool> DamageMapsAvoidOverlap;   // local: marks do not push a plate's metal through the armor plates it overlaps
        internal static ConfigEntry<bool> DamageMapsSteelGloss;     // local: bare steel is metallic and shiny (a metallic/smoothness map next to the paint), scorched paint is dull
        internal static ConfigEntry<bool> ImpactEffectsEnabled;            // local: sparks, flash, smoke and paint dust where a shell hits armor
        internal static ConfigEntry<bool> ImpactEffectsLitSmoke;    // local: smoke and dust are drawn with the URP lit particle shader (lit by the scene)
        internal static ConfigEntry<float> ImpactEffectsAmount;     // local: how many sparks and how much smoke (1 = normal)
        internal static ConfigEntry<bool> DamageMapsShareMarks;     // local: send the marks of my shots to the other players, and draw theirs
        internal static ConfigEntry<bool> DamageMapsSimulatedShots; // local: the simulated shot leaves (visual only) marks too
        internal static ConfigEntry<bool> DamageMapsWeakenArmor;    // global: holes let shots through, dents thin the plate
        internal static ConfigEntry<float> DamageMapsDensity;       // local: pixels per meter of the maps
        internal static ConfigEntry<float> DamageMapsNormalStrength;// local: how deep the dents look
        internal static ConfigEntry<bool> DamageMapsHoleBothSides;  // local: draw both sides of a holed plate
        internal static ConfigEntry<int> DamageMapsMaxVehicles;     // local: vehicles with maps at once
        internal static ConfigEntry<int> SimQuality;                // local: 0 off, 1 low, 2 medium, 3 high, 4 ultra
        internal static ConfigEntry<bool> SimCracks;                // local: the simulation's torn links make grooves in the marks
        internal static ConfigEntry<bool> SimSmoothEdges;           // local: the edge of a hole is smooth and a little ragged, not the grid's steps
        internal static ConfigEntry<int> SimThreads;                // local: 0 = all cores but two
        internal static ConfigEntry<float> SimTimeLimit;            // local: seconds before a simulation is given up
        internal static ConfigEntry<bool> SimAutoQuality;           // local: lower the quality when it is too slow
        internal static ConfigEntry<int> SimMaxQueue;               // local: simulations waiting before new impacts get the quick marks
        internal static ConfigEntry<bool> SimShedLoad;              // local: lend threads back when the frame rate drops
        internal static ConfigEntry<float> FragmentArmorFactor;
        internal static ConfigEntry<float> DefaultShellBrittleness;
        internal static ConfigEntry<float> SimShellBrittleness;
        internal static ConfigEntry<bool> SimShellFuse;
        internal static ConfigEntry<float> SimShellFuseDistance;
        internal static ConfigEntry<float> MaxDeflection;
        internal static ConfigEntry<int> MaxPlates;
        internal static ConfigEntry<bool> SimShellExplosive;
        internal static ConfigEntry<float> SimpleFragmentArea;
        internal static ConfigEntry<int> MaxPartsPerFragment;
        internal static ConfigEntry<float> SpreadBaseAngle;
        internal static ConfigEntry<float> AxisTilt;
        internal static ConfigEntry<float> SpreadAnisotropy;
        internal static ConfigEntry<float> VisualSpeedMultiplier;

        // Ricochet. Angles: 0 = head-on, 90 = grazing.
        // A hit ricochets when its angle >= (base angle * multiplier).
        internal static ConfigEntry<int> MaxRicochets;
        internal static ConfigEntry<float> ArmorRicochetAngle;
        internal static ConfigEntry<float> ArmorRicochetMultiplier;
        internal static ConfigEntry<float> FragmentRicochetAngle;
        internal static ConfigEntry<float> FragmentRicochetMultiplier;

        // Ammo racks ([AMM] fuselages, see AmmoRacks.cs).
        internal static ConfigEntry<bool> AmmoRacksEnabled;
        internal static ConfigEntry<float> CaseDiameterRatio;
        internal static ConfigEntry<float> CaseLitresPerMJ;
        internal static ConfigEntry<float> RoundSpacingPercent;
        internal static ConfigEntry<float> CookOffEnergyKJ;
        internal static ConfigEntry<float> CookOffDirectHit;
        internal static ConfigEntry<float> CookOffBurnSeconds;
        internal static ConfigEntry<float> CookOffTNTEfficiency;
        internal static ConfigEntry<float> CookOffWreckKPa;
        internal static ConfigEntry<float> CookOffDamageKPa;
        internal static ConfigEntry<float> CookOffArmorHalvingMm;
        internal static ConfigEntry<float> BlastStrength;
        internal static ConfigEntry<string> BlastType;
        internal static ConfigEntry<float> MaxBlastLb;
        internal static ConfigEntry<float> BlastVisualPercent;
        internal static ConfigEntry<bool> ShowRacksInDesigner;   // local: only changes what you see
        internal static ConfigEntry<bool> DebugDumpShape;        // local: diagnostic file

        // Mouse aim for turrets (see TurretAim.cs). Local: only your own controls.
        internal static ConfigEntry<UnityEngine.InputSystem.Key> TurretAimKey;
        internal static ConfigEntry<UnityEngine.InputSystem.Key> TurretLookKey;
        internal static ConfigEntry<float> TurretZoom;
        internal static ConfigEntry<float> TurretResponse;
        internal static ConfigEntry<float> TurretCameraHeight;
        internal static ConfigEntry<float> TurretDamping;
        internal static ConfigEntry<float> TurretDerivative;
        internal static ConfigEntry<bool> TurretFireOnTarget, TurretLineOfFire, TurretShareHull;
        internal static ConfigEntry<float> TurretShareStrength, TurretErrorFilter;
        internal static ConfigEntry<bool> TurretDirect, TurretAutoReach, TurretFreeCursor, TurretAutoStart, TurretSightFollowsMouse;
        internal static ConfigEntry<float> TurretFreeCursorZone, TurretFreeCursorFollow, TurretFreeCursorSensitivity, TurretFreeCursorRecenter;
        internal static ConfigEntry<float> TurretDirectRamp, TurretDirectDeadband, TurretDirectGain, TurretDirectBrake;
        internal static ConfigEntry<bool> TurretGroupsHudEnabled;
        internal static ConfigEntry<bool> TurretTestSingle;
        internal static ConfigEntry<float> TurretFireTolerance, TurretFireStagger, TurretMoveStagger;
        internal static ConfigEntry<bool> TurretAutoFire, TurretAutoLead, TurretAutoDrop, TurretAutoClearView, TurretAutoSkipFriendly;
        internal static ConfigEntry<float> TurretAutoRange, TurretAutoAirAgl, TurretAutoAirSpeed;
        internal static ConfigEntry<bool> TurretObstacleStop;
        internal static ConfigEntry<float> TurretObstacleDegrees, TurretObstacleRetry;
        internal static ConfigEntry<UnityEngine.InputSystem.Key> TurretSightKey, TurretNextSightKey, TurretRangeLockKey;
        internal static ConfigEntry<float> TurretSightZoom, TurretDirectBrakeRamp;
        internal static ConfigEntry<float> TurretSightSensitivity;
        internal static ConfigEntry<bool> TurretCrosshairFlipY;

        // The rounds left in each ammo rack, on the flight screen (see AmmoHud.cs). Local.
        internal static ConfigEntry<bool> AmmoHudEnabled;
        internal static ConfigEntry<bool> AmmoHudRelative;
        internal static ConfigEntry<TurretFireMouse> TurretFireMouseButton;
        internal static ConfigEntry<UnityEngine.InputSystem.Key> TurretGroupKey;
        internal static ConfigEntry<UnityEngine.InputSystem.Key> TurretFireKey;

        // Crew ([CRW] seats, see Crew.cs).
        internal static ConfigEntry<bool> CrewEnabled;
        internal static ConfigEntry<float> CrewSwitchSeconds;

        // Shown in the log and the panel, to tell which build the game is really running.
        public const string Version = "0.12.2";

        private float attachMenuAt = -1f;

        private void Update()
        {
            if (attachMenuAt > 0f && UnityEngine.Time.unscaledTime >= attachMenuAt)
            {
                attachMenuAt = -1f;
                try { GpuStatus.Start(); }
                catch (System.Exception e) { Log.LogWarning("Couldn't start the graphics card check: " + e.Message); }
            }
            GpuStatus.Tick();
            UI.DesignerSection.Tick();
            XRayView.Tick();
            VehicleXRay.Tick();
            NetSync.Tick();
            HostResolver.Tick();
            ModuleOutputs.Tick();
            AmmoRacks.Tick();
            Crew.Tick();
            CrewBodies.Tick();
            RackFireSync.Tick();
            if (DamageMapsShareMarks.Value) MarksSync.Tick();
            RackVisualizer.Tick();
            FaceHighlight.Tick();
            ArmorView.Tick();
            ArmorMass.Tick();
            AmmoRackXRay.Tick();
            ImpactCamera.Tick();
            PlateDamageMaps.Tick();
            ImpactEffects.Tick();
            TurretAim.Tick();
            AmmoHud.Tick();
            TurretGroupsHud.Tick();
            MouseAimButton.Tick();
            GunSightPart.Tick();
        }

        // The turret aim's circle and marker are drawn here (they invert the picture behind them).
        private void OnGUI()
        {
            TurretAim.OnGui();
            ArmorView.OnGui();
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            Log.LogInfo("Armor Fragments " + Version + " loading");

            // Keeps this plugin's latest log lines for the in-game panel.
            MirrorListener = new LogMirror();
            BepInEx.Logging.Logger.Listeners.Add(MirrorListener);

            ApplyDamage = Config.Bind("Fragments", "ApplyDamage", true,
                "If true, fragments damage the parts they hit.");
            FragmentMass = Config.Bind("Fragments", "FragmentMass", 10f,
                new ConfigDescription(
                    "Typical mass (grams) of one fragment of a 75 mm shell or a plate. The number of fragments of a cloud is " +
                    "its mass over this: smaller for a brittle plate or casing and for a stronger explosion, bigger for " +
                    "a bigger shell. There is no limit to that number (see MaxVisualFragments).",
                    new AcceptableValueRange<float>(0.5f, 500f)));
            MaxVisualFragments = Config.Bind("Fragments", "MaxVisualFragments", 400,
                new ConfigDescription(
                    "Most fragment RAYS one cloud is traced and drawn with (detail and cost). A cloud with more fragments than " +
                    "this puts a bundle of them in each ray: how far a fragment goes is worked out for one real fragment, " +
                    "only the damage it leaves is multiplied by the bundle's size. So this never changes the penetration.",
                    new AcceptableValueRange<int>(100, 1000)));
            BallisticRayRange = Config.Bind("Fragments", "BallisticRayRange", 100f,
                new ConfigDescription(
                    "When a real shell stops in a part that is not armor, a ballistic ray carries on this far (m) looking for armor.",
                    new AcceptableValueRange<float>(1f, 1000f)));
            DrawRays = Config.Bind("Debug", "DrawRays", true,
                "If true, draws each fragment ray in the world (red = hit a part, yellow = hit nothing).");
            HideRaysOutsideViews = Config.Bind("Debug", "HideRaysOutsideViews", true,
                "On: the rays are only drawn where they are watched, in the impact camera and in the simulated shot; the shots " +
                "of the world around you draw none (nothing changes in what they do). Off: every shot draws its rays. " +
                "(Needs DrawRays on.)");
            RayWidthScale = Config.Bind("Debug", "RayWidthScale", 1f,
                new ConfigDescription(
                    "A drawn ray is as thick as the thing it shows: the shell as wide as its caliber, a fragment as wide " +
                    "as the fragment. This multiplies that (1 = true size; raise it to see small fragments from afar).",
                    new AcceptableValueRange<float>(0.1f, 50f)));
            MinRayWidth = Config.Bind("Debug", "MinRayWidth", 0.004f,
                new ConfigDescription(
                    "No ray is drawn thinner than this (meters), so tiny fragments stay visible. 0 = true proportions only.",
                    new AcceptableValueRange<float>(0f, 0.1f)));
            IgnoreGameDamageOnArmor = Config.Bind("Fragments", "IgnoreGameDamageOnArmor", true,
                "If true, the game's own damage to parts with an [ARM] tag (shell impact, explosions, crashes) is ignored: " +
                "armor is judged only by the energy model and cannot be destroyed.");
            GameDamageExplosions = Config.Bind("Fragments", "GameDamageExplosions", false,
                "If false, the game never blows an armored vehicle (any [ARM] part) or its parts up because of damage: no " +
                "'part explodes' roll on damaged parts, no vehicle explosion at the damage limit, no delayed fuel tank or " +
                "propeller explosions, no explosion chains. Armored vehicles keep their shape and are killed when an ammo " +
                "rack explodes. Vehicles without armor always behave as in the base game. Weapons' own explosions are not " +
                "affected. True = the game's usual behavior on armored vehicles too.");
            ArmoredStructureDamage = Config.Bind("Fragments", "ArmoredStructureDamage", false,
                "If false, on a vehicle with armor ([ARM] parts) only damage modules ([MOD]) and ammo " +
                "racks ([AMM]) take the mod's damage (shells and fragments); the rest of the structure only slows them down. " +
                "Vehicles without armor (planes) take it on every part either way.");
            RayTrailLength = Config.Bind("Debug", "RayTrailLength", 6f,
                new ConfigDescription("Length of the fading trail behind the moving ray, in meters.",
                    new AcceptableValueRange<float>(0.1f, 50f)));
            BubbleSize = Config.Bind("Debug", "BubbleSize", 0.15f,
                new ConfigDescription("Diameter of the colored ball shown at each impact, in meters.",
                    new AcceptableValueRange<float>(0.01f, 2f)));
            BubbleLifetime = Config.Bind("Debug", "BubbleLifetime", 0.6f,
                new ConfigDescription("Seconds an impact ball takes to shrink away.",
                    new AcceptableValueRange<float>(0.05f, 10f)));
            MirrorLogToGameConsole = Config.Bind("Debug", "MirrorLogToGameConsole", false,
                "If true, also prints this plugin's log lines to the game's developer console. " +
                "They then show up twice in LogOutput.log (once as 'Unity Log').");

            LogNetworkDamage = Config.Bind("Debug", "LogNetworkDamage", false,
                "If true, writes a log line for every piece of damage sent to another player's aircraft (many lines per shot).");

            ImpactCameraEnabled = Config.Bind("ImpactCamera", "Enabled", true,
                "Shows a small camera in the top right corner when a shell you fired hits another vehicle.");
            ImpactCameraKey = Config.Bind("ImpactCamera", "ToggleKey", UnityEngine.InputSystem.Key.F9,
                "Key that switches the impact camera on and off. 'None' disables the key.");
            TurretAimKey = Config.Bind("TurretAim", "AimKey", UnityEngine.InputSystem.Key.Backspace,
                "Key that switches the mouse aim on and off (also the panel's Tools button). 'None' disables the key.");
            TurretLookKey = Config.Bind("TurretAim", "FreeLookKey", UnityEngine.InputSystem.Key.C,
                "Hold it to free the camera from the turret: the turret keeps pointing where it was while you look around the vehicle; " +
                "let go and the camera comes back. 'None' disables it. While the aim is on, the game's view-switching keys do nothing.");
            TurretZoom = Config.Bind("TurretAim", "Zoom", 4f,
                new ConfigDescription("How many times the right mouse button zooms in (held: gradually in, let go: gradually out).",
                    new AcceptableValueRange<float>(1.5f, 12f)));
            TurretCameraHeight = Config.Bind("TurretAim", "PivotHeight", 0.5f,
                new ConfigDescription("The point the camera turns around (where it sits at full zoom) is centered on the vehicle and this many meters above its highest point.",
                    new AcceptableValueRange<float>(0f, 10f)));
            AmmoHudEnabled = Config.Bind("AmmoDisplay", "Enabled", true,
                "Shows a bar for each ammo rack above the fire buttons (bottom left of the flight screen): the rack's round (caliber and kind) and its rounds left. Clicking a bar switches them all between rounds and percent.");
            AmmoHudRelative = Config.Bind("AmmoDisplay", "ShowPercent", false,
                "The bars show the percent of the rack that is left instead of the rounds left (a click on a bar switches it).");
            TurretGroupKey = Config.Bind("TurretAim", "GroupKey", UnityEngine.InputSystem.Key.Tab,
                "In mouse aim, switches to the next turret group when the vehicle has more than one (the turrets of the active group are aimed and fire; the others hold). 'None' disables it.");
            TurretFireMouseButton = Config.Bind("TurretAim", "FireMouseButton", TurretFireMouse.Left,
                "In mouse aim, the mouse button that fires the cannons of the active turret group that are not set to fire by key (Left or Middle; the right button is the zoom).");
            TurretShareHull = Config.Bind("TurretAim", "SlowSharedHull", true,
                "Turrets on the same hull body push it together when they turn (a recording of 14 turrets on one ship: the hull rocked 5 degrees at 2 Hz, with the ship on land). " +
                "With this on, each turret's gain is divided by the number of turrets of the active group that share its hull, so many turrets are gentler on the hull (and slower to arrive). " +
                "Turn it off for turrets on a heavy hull that does not move.");
            TurretTestSingle = Config.Bind("TurretAim", "TestOneTurret", false,
                "For testing: in mouse aim only ONE turret of the active group is controlled and fires (the others hold where they are), so its behavior can be told apart from " +
                "the effect of many turrets on one hull. The panel has < and > buttons to pick which one.");
            TurretShareStrength = Config.Bind("TurretAim", "SharedHullFullGain", 1.9f,
                new ConfigDescription("With SlowSharedHull: how many turrets of the group can work at full gain on one hull. With more, each turret's gain is this number divided by the number " +
                    "of turrets (14 turrets: 1.9 / 14 = a seventh of the gain). A model of 14 turrets on a ship (and a recording of one) put the limit of a steady aim at about " +
                    "1 turn a second of gain for 14 turrets; fewer turrets can have more.",
                    new AcceptableValueRange<float>(1f, 20f)));
            TurretGroupsHudEnabled = Config.Bind("TurretAim", "GroupsDisplay", true,
                "In mouse aim, with more than one turret group, shows a bar for each group at the bottom right of the flight screen, above the camera buttons: lit = the group follows the mouse aim, dark = it holds. " +
                "A click switches the group (several can be controlled together); hold Alt to free the cursor for it.");
            TurretErrorFilter = Config.Bind("TurretAim", "ErrorFilterSeconds", 0.4f,
                new ConfigDescription("When several turrets share a hull, the gap each one is closing is smoothed over this many seconds, so a hull that rocks (2 times a second) is not chased " +
                    "by the turrets, which would rock it more. In the model it lets the gain be 4 times higher for the same steadiness. 0 = off. A single turret is never smoothed.",
                    new AcceptableValueRange<float>(0f, 1.5f)));
            TurretAutoStart = Config.Bind("TurretAim", "AutoStart", true,
                "Mouse aim switches on by itself when you spawn a vehicle that has cannons marked Mouse aim. The mouse button at the bottom right of the flight screen (or the aim key) switches it off and on.");
            TurretSightFollowsMouse = Config.Bind("TurretAim", "SightFollowsMouse", true,
                "Through the sight camera the view turns with the mouse (the fixed crosshair is where you aim) and the gun circle shows where the gun really points, catching up as the turret turns. " +
                "Off = the view stays locked to the gun and the aim point moves over it.");
            TurretFreeCursor = Config.Bind("TurretAim", "FreeCursorWarThunder", true,
                "In the orbit view the ring is a cursor the mouse moves over the screen, instead of being fixed at the middle: the turrets aim where the ring is. The camera does not follow " +
                "at once: it starts turning once the ring is some way off the middle (FreeCursorZonePercent) and follows more and more as the ring goes further, so there is no hard edge, and the ring " +
                "drifts back toward the middle as the camera catches up. The sight view, the free look (C) and Alt work as before. Off = the ring stays at the middle and the mouse turns the camera.");
            TurretFreeCursorZone = Config.Bind("TurretAim", "FreeCursorZonePercent", 30f,
                new ConfigDescription("With FreeCursor: how far from the middle of the screen the ring can be before the camera starts to follow, as a percent of half the view's height. " +
                    "The camera begins to follow a little inside this (at 60% of it) and follows fully a little outside it (at 140%).", new AcceptableValueRange<float>(5f, 90f)));
            TurretFreeCursorFollow = Config.Bind("TurretAim", "FreeCursorFollowSeconds", 0.25f,
                new ConfigDescription("With FreeCursor: how quickly the camera closes the distance to the ring once it is outside the zone (the time it takes to cover most of it). Smaller is tighter.",
                    new AcceptableValueRange<float>(0.05f, 1.5f)));
            TurretFreeCursorRecenter = Config.Bind("TurretAim", "FreeCursorRecenterSeconds", 3f,
                new ConfigDescription("With FreeCursor: when the mouse has been still for about half a second, the camera slowly turns until the ring is back at the middle of the screen " +
                    "(the aim does not change, only the view). This is the time it takes to cover most of the distance; 0 = the ring stays where it is.", new AcceptableValueRange<float>(0f, 20f)));
            TurretFreeCursorSensitivity = Config.Bind("TurretAim", "FreeCursorSensitivity", 0.06f,
                new ConfigDescription("With FreeCursor: degrees the ring moves per count of the mouse (less when zoomed in). The sight's own setting is SightSensitivity.", new AcceptableValueRange<float>(0.01f, 0.5f)));
            TurretAutoReach = Config.Bind("TurretAim", "AutoPreferReachable", true,
                "Automatic aim: a group prefers the target that the most of its turrets can point at with their rotators (their arcs, as read from each rotator's input), over simply the nearest one. " +
                "A recording of a ship with 14 turrets on one target: 7 could not turn far enough, because the target was behind them. Turrets that cannot reach still turn as far as they can; they just " +
                "do not decide what the group shoots at. Off = the nearest target the shell can reach, as before.");
            TurretDirect = Config.Bind("TurretAim", "DirectControl", true,
                "Aim without the gain: each rotator is moved along a speed profile worked out from what is read from it (its own top speed, the angle it still has to turn to " +
                "bring the gun onto the aim, measured from where the barrel will settle), instead of closing a fraction of the gap every physics step. It speeds up over DirectRampSeconds, " +
                "runs at the rotator's own top speed and slows down so as to arrive exactly. The hull stabilizer, the shared-hull gain division and the gap smoothing are not used. Off = the " +
                "gain-based control, as before.");
            TurretDirectRamp = Config.Bind("TurretAim", "DirectRampSeconds", 0.5f,
                new ConfigDescription("With DirectControl: how long a rotator takes to go from rest to its top speed (DirectBrakeRampSeconds is the way back down). Shorter is faster to arrive and harder on the hull; with many " +
                    "turrets on one hull a longer ramp is gentler.", new AcceptableValueRange<float>(0.05f, 2f)));
            TurretDirectBrakeRamp = Config.Bind("TurretAim", "DirectBrakeRampSeconds", 0.15f,
                new ConfigDescription("With DirectControl: how long a rotator takes to stop from its top speed. Shorter brakes later and harder, so the turret keeps its speed closer to the aim before it settles.", new AcceptableValueRange<float>(0.05f, 2f)));
            TurretDirectGain = Config.Bind("TurretAim", "DirectGain", 4.5f,
                new ConfigDescription("With DirectControl: near the aim a rotator's speed is this many times the angle it still has to turn (per second): 3 means 3 degrees a second for a degree to go. " +
                    "Farther away it follows the speed profile (speeds up, runs at top speed, slows to arrive). Without this zone the speed went to the top for a gap of a degree and a springy joint " +
                    "(its barrel ringing at 3 to 5 Hz) made the rotator swing back and forth: a recording showed the elevation swinging at 4.5 Hz. Lower is calmer and slower to settle.",
                    new AcceptableValueRange<float>(0.5f, 12f)));
            TurretDirectBrake = Config.Bind("TurretAim", "DirectBrakingSeconds", 0.15f,
                new ConfigDescription("With DirectControl and two or more turrets of the groups being aimed on one hull: the gap each rotator closes is reduced by the gun's own speed times this many " +
                    "seconds (the same braking BrakingSeconds gives the gain-based control), and the gap is smoothed over ErrorFilterSeconds, and the gain near the aim is divided by the number of " +
                    "turrets as SharedHullFullGain says. A model of turrets on one hull (the hull rocks at 2 Hz and the rotators push it) was unstable for any gain without the braking, and a " +
                    "recording of 6 turrets under the direct control swung at 2 Hz with a growing amplitude. A lone turret uses none of the three. 0 = no braking.",
                    new AcceptableValueRange<float>(0f, 0.5f)));
            TurretDirectDeadband = Config.Bind("TurretAim", "DirectDeadbandDegrees", 0.05f,
                new ConfigDescription("With DirectControl: a rotator whose angle still to go is smaller than this rests (the settled gap is noisy by about a tenth of a degree).",
                    new AcceptableValueRange<float>(0f, 1f)));
            TurretFireOnTarget = Config.Bind("TurretAim", "FireOnlyOnTarget", false,
                "In mouse aim, a turret fires only when its gun points within FireToleranceDegrees of where it should (the cursor). Turrets still turning, or stopped at their limit, hold fire while the others shoot.");
            TurretFireTolerance = Config.Bind("TurretAim", "FireToleranceDegrees", 2f,
                new ConfigDescription("How far off the cursor a turret's gun may be and still fire (with FireOnlyOnTarget).", new AcceptableValueRange<float>(0.2f, 20f)));
            TurretFireStagger = Config.Bind("TurretAim", "FireStaggerSeconds", 0.1f,
                new ConfigDescription("In mouse aim, a salvo fires one cannon at a time with this many seconds between them (a ripple), which also spreads the recoil on the hull. 0 = all at once.",
                    new AcceptableValueRange<float>(0f, 1f)));
            TurretLineOfFire = Config.Bind("TurretAim", "CheckLineOfFire", true,
                "In mouse aim, a cannon does not fire while its barrel points at a part of its own vehicle within 20 meters (a turret's own roof, another turret).");
            TurretMoveStagger = Config.Bind("TurretAim", "MoveStaggerSeconds", 0.02f,
                new ConfigDescription("The turrets of a group start following the cursor one after another, each this many seconds after the one before, so they don't all push the hull at the same instant. 0 = together.",
                    new AcceptableValueRange<float>(0f, 0.1f)));
            TurretAutoFire = Config.Bind("TurretAim", "AutoFire", true,
                "A turret group on automatic aim (the Ground / Air buttons under its bar) also fires by itself at its target, one cannon at a time, when its gun points at it (FireToleranceDegrees), " +
                "the line of fire is clear of its own vehicle and the target is within AutoRange. Off: the group only aims.");
            TurretAutoRange = Config.Bind("TurretAim", "AutoRange", 2000f,
                new ConfigDescription("Automatic aim only picks targets within this many meters of the group's lead turret.", new AcceptableValueRange<float>(100f, 6000f)));
            TurretAutoLead = Config.Bind("TurretAim", "AutoLead", true,
                "Automatic aim aims ahead of a moving target (and allows for the vehicle's own speed, which the shell has too). Off: it aims at where the target is now.");
            TurretAutoDrop = Config.Bind("TurretAim", "AutoDrop", true,
                "Automatic aim aims above the target by as much as the shell falls on the way (gravity and the shell's drag, flown like the game's physics does). Off: the barrel points straight at the target.");
            TurretAutoClearView = Config.Bind("TurretAim", "AutoClearView", true,
                "Automatic aim leaves out targets that are hidden behind the ground (the game's own occlusion layer), so the turrets don't shoot at a hill.");
            TurretAutoSkipFriendly = Config.Bind("TurretAim", "AutoSkipFriendly", false,
                "Automatic aim leaves out targets of your own team and friendly NPCs (the game's team rules). Off: only your own vehicle is left alone (every other player's vehicle is a target).");
            TurretAutoAirAgl = Config.Bind("TurretAim", "AutoAirHeight", 12f,
                new ConfigDescription("The game does not say whether a player's vehicle is a plane or a tank. One higher than this above the ground counts as air (for the Air button), otherwise as ground.",
                    new AcceptableValueRange<float>(2f, 200f)));
            TurretAutoAirSpeed = Config.Bind("TurretAim", "AutoAirSpeed", 70f,
                new ConfigDescription("A player's vehicle faster than this (m/s) and more than 3 m above the ground counts as air, however low it flies.", new AcceptableValueRange<float>(10f, 400f)));
            TurretObstacleStop = Config.Bind("TurretAim", "StopAtObstacles", true,
                "A rotator that is blocked by something (a barrel on the deck, a turret on another) stops pushing: the joint is asked to stay where the part is, the rotator may turn away from the " +
                "obstacle but not further into it, and it tries again after ObstacleRetrySeconds. Without it the angle the rotator asks for keeps running ahead and the joint pushes harder and harder.");
            TurretObstacleDegrees = Config.Bind("TurretAim", "ObstacleDegrees", 2.5f,
                new ConfigDescription("A rotator counts as blocked when the angle it asks for is this many degrees ahead of where the part really is and the part is not catching up. " +
                    "Lower stops it sooner; too low can stop a heavy, slow turret that is only following.", new AcceptableValueRange<float>(0.5f, 15f)));
            TurretObstacleRetry = Config.Bind("TurretAim", "ObstacleRetrySeconds", 4f,
                new ConfigDescription("How long a blocked rotator waits before it tries to turn further again (it also tries again at once if it backs off 2 degrees).", new AcceptableValueRange<float>(0.5f, 30f)));
            TurretFireKey = Config.Bind("TurretAim", "FireKey",UnityEngine.InputSystem.Key.Space,
                "In mouse aim, the key that fires the cannons of the turret group that are set to fire by key in the designer (Pointer > Fire by key). 'None' disables it.");
            TurretRangeLockKey = Config.Bind("TurretAim", "RangeLockKey", UnityEngine.InputSystem.Key.X,
                "World of Tanks style: locks the range in use (the gun stays raised for that range) until it is pressed again, for leading a fast target.");
            TurretNextSightKey = Config.Bind("TurretAim", "NextSightKey", UnityEngine.InputSystem.Key.V,
                "In mouse aim: switches to the next gun sight of the guns being aimed, when they have more than one.");
            TurretSightKey = Config.Bind("TurretAim", "SightKey", UnityEngine.InputSystem.Key.LeftShift,
                "Press it to switch to the pointer cannon's sight camera (picked in the designer: Pointer > Pick sight camera) and back. 'None' disables it.");
            TurretSightZoom = Config.Bind("TurretAim", "SightZoom", 6f,
                new ConfigDescription("How many times the right mouse button zooms in through the sight, on top of the zoom set with the mouse wheel.",
                    new AcceptableValueRange<float>(1.5f, 30f)));
            TurretSightSensitivity = Config.Bind("TurretAim", "SightSensitivity", 0.06f,
                new ConfigDescription("Degrees the aim turns for each mouse count through the sight (less when zoomed in).",
                    new AcceptableValueRange<float>(0.005f, 0.5f)));
            TurretCrosshairFlipY = Config.Bind("TurretAim", "CrosshairFlipY", false,
                "Only if the circle and the marker show mirrored up and down (they move the wrong way when the aim moves up or down): turn it on.");
            TurretDamping = Config.Bind("TurretAim", "DampingSeconds", 0.12f,
                new ConfigDescription("The turret aims from where the gun will be this many seconds from now (its own speed times this), so it does not overshoot " +
                    "and swing around the circle. 0 = off. Raise it if the gun still swings; too much makes it sluggish.",
                    new AcceptableValueRange<float>(0f, 0.4f)));
            TurretDerivative = Config.Bind("TurretAim", "BrakingSeconds", 0.15f,
                new ConfigDescription("The turret brakes in proportion to how fast the gun is moving in the world (the derivative of a PID): this many seconds of its " +
                    "own speed are taken off the gap it is closing. It stops a ship's turrets from shaking the hull and swinging together, and from overshooting a " +
                    "big move. 0 = off. Raise it if the turrets still swing; too much makes them slow to arrive.",
                    new AcceptableValueRange<float>(0f, 0.6f)));
            TurretResponse = Config.Bind("TurretAim", "ResponseSeconds", 0.12f,
                new ConfigDescription("How quickly the turret closes on the cursor, in seconds (the time the gap takes to shrink to a third). " +
                    "Smaller is snappier; if the gun swings back and forth around the cursor, raise it.",
                    new AcceptableValueRange<float>(0.03f, 1f)));
            ImpactCameraSwitchTargets = Config.Bind("ImpactCamera", "SwitchTargets", true,
                "If true, hitting another vehicle while the camera is open moves it to that vehicle. If false it stays on the first one until it closes.");
            ShellLeadIn = Config.Bind("ImpactCamera", "LeadInDistance", 10f,
                new ConfigDescription("Meters of the shell's flight shown before an impact: the drawn shell ray starts this far from the impact. Takes effect with the next impact (after the camera has closed).",
                    new AcceptableValueRange<float>(1f, 100f)));
            ImpactCameraDistance = Config.Bind("ImpactCamera", "CameraDistance", 3f,
                new ConfigDescription("How far (meters) the camera stays behind the shell: it flies this far behind the shell's ray and stops this far from the impact.",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            ImpactCameraLinger = Config.Bind("ImpactCamera", "LingerSeconds", 5f,
                new ConfigDescription("Seconds the camera stays open after the shell's ray reaches the impact (a new impact on the same vehicle restarts the count).",
                    new AcceptableValueRange<float>(0.5f, 60f)));
            ImpactCameraWidthPercent = Config.Bind("ImpactCamera", "WidthPercent", 22f,
                new ConfigDescription("Width of the camera window, as a percentage of the screen width.",
                    new AcceptableValueRange<float>(5f, 60f)));
            ImpactCameraMargin = Config.Bind("ImpactCamera", "MarginPixels", 16f,
                new ConfigDescription("Distance (pixels) of the window from the top right corner of the screen.",
                    new AcceptableValueRange<float>(0f, 300f)));
            ImpactCameraResolution = Config.Bind("ImpactCamera", "ResolutionHeight", 384,
                new ConfigDescription("Height (pixels) of the camera's picture; the width is 4/3 of it. Lower = cheaper.",
                    new AcceptableValueRange<int>(128, 1080)));
            ImpactCameraFov = Config.Bind("ImpactCamera", "FieldOfView", 45f,
                new ConfigDescription("Field of view (degrees) of the impact camera.",
                    new AcceptableValueRange<float>(15f, 90f)));
            ImpactCameraXRay = Config.Bind("ImpactCamera", "XRay", true,
                "If true, the impact camera switches to an X-ray view of the vehicle the moment the shell hits, " +
                "showing the damage (yellow, orange, red) as the fragments reach each part.");
            ImpactCameraDamageView = Config.Bind("ImpactCamera", "DamageView", false,
                "If true, the impact camera also draws the armor's damage map over the plates: dark red where a plate is badly dented " +
                "or holed, a lighter, more see-through red where it is only slightly marked, nothing where it is untouched. " +
                "(Only in the impact camera: the main view does not change.)");
            ImpactCameraGhostLayer = Config.Bind("ImpactCamera", "XRayLayer", 29,
                new ConfigDescription("Unity layer the impact camera's X-ray ghosts live on. Only that camera draws it (the main camera is told to skip it while the camera is open). " +
                    "29 and 30 are the game's 'Mod Layer 2/3'; 3 is unused. If the ghosts do not show, try another of these.",
                    new AcceptableValueRange<int>(0, 31)));
            ImpactCameraShadows = Config.Bind("ImpactCamera", "Shadows", false,
                "If true, the impact camera draws shadows. Costs GPU time; off = the vehicle is lit but casts no shadows.");

            HostResolvesShots = Config.Bind("Multiplayer", "HostResolvesShots", true,
                "Only matters when you host. If true, shells from players who do not run the mod are worked out " +
                "here (armor, ricochet, fragments), using the cannon's [CAN] tag. Players who run the mod " +
                "calculate their own shots and are left alone.");

            VehicleXRayKey = Config.Bind("Simulation", "VehicleXRayKey", UnityEngine.InputSystem.Key.F8,
                "Key that shows your own vehicle in the X-ray view, coloured by the damage its parts have taken " +
                "(yellow = a little, orange = damaged, red = a lot). Press again to switch it off. 'None' disables it.");
            Log.LogInfo("Vehicle X-ray key: " + VehicleXRayKey.Value);
            XRayAfterSimulation = Config.Bind("Simulation", "XRayAfterSimulation", true,
                "If true, a simulated shot switches on the X-ray view.");
            XRayRadius = Config.Bind("Simulation", "XRayRadius", 40f,
                new ConfigDescription("Parts within this distance (m) of the impact are shown in the X-ray view.",
                    new AcceptableValueRange<float>(5f, 200f)));
            XRayDuration = Config.Bind("Simulation", "XRayDuration", 8f,
                new ConfigDescription("Seconds the X-ray view stays on.",
                    new AcceptableValueRange<float>(1f, 120f)));
            XRayDarkPercent = Config.Bind("Simulation", "XRayDarkPercent", 30f,
                new ConfigDescription(
                    "A part that would lose this percentage of its health is shown fully dark.",
                    new AcceptableValueRange<float>(1f, 100f)));


            DefaultShellHardness = Config.Bind("Energy", "DefaultShellHardness", 100f,
                new ConfigDescription("Shell hardness (%) for cannons without a [CAN k=] tag. Compared with the armor's hardness for the ricochet angle.",
                    new AcceptableValueRange<float>(10f, 300f)));
            SimShellHardness = Config.Bind("Energy", "SimulatedShellHardness", 100f,
                new ConfigDescription("Shell hardness (%) of the simulated shot. Compared with the armor's hardness for the ricochet angle.",
                    new AcceptableValueRange<float>(10f, 300f)));
            SimCaliber = Config.Bind("Energy", "SimulatedCaliber", 100f,
                new ConfigDescription("Caliber (mm) of the simulated shell. Its mass is worked out from it, as the game does for a real cannon.",
                    new AcceptableValueRange<float>(5f, 500f)));
            SimShellVelocity = Config.Bind("Energy", "SimulatedShellVelocity", 800f,
                new ConfigDescription("Speed (m/s) of the simulated shell.",
                    new AcceptableValueRange<float>(10f, 3000f)));
            ArmorResistance = Config.Bind("Energy", "ArmorResistance", 600f,
                new ConfigDescription(
                    "How much energy armor takes, in joules per mm of thickness crossed per cm2 of shell area. " +
                    "Opening a plug of steel only needs about 40, but real shells lose most of their energy to " +
                    "deformation and heat, so about 600 matches real penetration (a 40 mm shell at 1000 m/s " +
                    "goes through roughly 50-60 mm of steel). The shell is blocked if its energy is lower than " +
                    "resistance x thickness (divided by cos of the angle) x shell area.",
                    new AcceptableValueRange<float>(0.1f, 2000f)));
            RicochetFriction = Config.Bind("Energy", "RicochetFriction", 0.2f,
                new ConfigDescription("Share of the sliding energy a shell loses in a ricochet (it keeps sin^2 of the angle, minus this).",
                    new AcceptableValueRange<float>(0f, 1f)));
            RicochetRandomness = Config.Bind("Energy", "RicochetRandomness", 50f,
                new ConfigDescription("Small random shift of the critical ricochet angle at each impact. 0% = none (same result every time), 100% = up to 10 degrees either way.",
                    new AcceptableValueRange<float>(0f, 100f)));
            DamagePerKiloJoule = Config.Bind("Energy", "DamagePerKiloJoule", 1f,
                new ConfigDescription("Part damage per kilojoule of energy a fragment (or the shell's ray) leaves in a part. This is the damage scale.",
                    new AcceptableValueRange<float>(0.001f, 1000f)));
            DensityModifier = Config.Bind("Energy", "DensityModifier", 1f,
                new ConfigDescription("Multiplies the density (mass over collider volume) of every part. Denser parts stop fragments sooner.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            ConstantForceLoss = Config.Bind("Energy", "ConstantForceLoss", false,
                "Energy a fragment loses inside a part: off = drag proportional to its speed (exponential), on = constant force (linear).");
            SimpleFragments = Config.Bind("Energy", "SimpleFragments", false,
                "Off = fragments with their own mass, energy and speed (central ones carry more). On = equal energy and mass, fixed size (simpler).");
            FillerHE = Config.Bind("Energy", "FillerHE", 10f,
                new ConfigDescription(
                    "Explosive filler (percent of the shell's mass) of an HE shell (explosive, no fuse). " +
                    "The designer's filler slider starts here for that kind of ammunition.",
                    new AcceptableValueRange<float>(0f, 50f)));
            FillerAPHE = Config.Bind("Energy", "FillerAPHE", 3f,
                new ConfigDescription(
                    "Explosive filler (percent of the shell's mass) of an APHE shell (explosive, with a fuse). " +
                    "The designer's filler slider starts here for that kind of ammunition.",
                    new AcceptableValueRange<float>(0f, 50f)));
            GurneySpeed = Config.Bind("Energy", "GurneySpeed", 2440f,
                new ConfigDescription(
                    "Gurney velocity (m/s) of the explosive: with the filler and the casing's mass it gives the speed the " +
                    "casing fragments fly outward at, v = G x sqrt(c/m / (1 + c/(2m))). About 2440 for TNT.",
                    new AcceptableValueRange<float>(500f, 4000f)));
            DetonationSidewaysBias = Config.Bind("Energy", "DetonationSidewaysBias", 0.5f,
                new ConfigDescription(
                    "How much more of the casing flies sideways than along the shell's axis when it bursts. " +
                    "0 = the same in every direction, 1 = mostly sideways (as the casing of a real shell does).",
                    new AcceptableValueRange<float>(0f, 1f)));
            KeepPlatesApart = Config.Bind("DamageMaps", "KeepPlatesApart", true,
                "Armor plates keep their own renderer in flight instead of being merged into their group's mesh (a few more draw calls). " +
                "Needed for the damage maps: a merged plate cannot be changed. Takes effect when a flight starts.");
            DamageMapsEnabled = Config.Bind("DamageMaps", "Enabled", true,
                "Shots leave marks on armor plates: dents, holes, grooves and pits, drawn into the plate's normal map and hole mask.");
            DamageMapsPaintDamage = Config.Bind("DamageMaps", "PaintDamage", true,
                "Marks also change the plate's COLOR: the paint is scorched dark around a hit and flakes off where the metal was struck, showing bright " +
                "steel (and a bright rim round holes). Plates with marks are then drawn with the Lit shader in their paint's color (a plate with a pattern " +
                "or a texture loses it). Takes effect for the next vehicle that gets marks (the panel's Clear button starts over).")
            ;
            PartDecalsEnabled = Config.Bind("Decals", "Enabled", true,
                "Shells that go through or burst on parts that are NOT armor (wings, plain fuselage, engines...) leave marks: a hole where they went in and, " +
                "if they came out, a bigger torn one, with scorch and bare metal round it. Laid over the surface as small decals (those parts are merged into " +
                "their group's mesh in flight, so they cannot be marked like armor plates), only where seen from outside. Local: other players' are not sent.");
            PartDecalsMax = Config.Bind("Decals", "MaxPerVehicle", 48,
                new ConfigDescription("How many of these decals a vehicle keeps (each is a small mesh and two 128 x 128 textures); the oldest are taken off.",
                    new AcceptableValueRange<int>(4, 200)));
            DamageMapsFlex = Config.Bind("DamageMaps", "Flex", true,
                "A heavy blow bends the whole plate it hits (a broad dish on the struck face, a bulge on the other) and, a little, the armor plates joined " +
                "to it, the less the further they are. Only a look, and only for heavy hits (a big shell against a plate that stops it).");
            DamageMapsAvoidOverlap = Config.Bind("DamageMaps", "AvoidOverlap", true,
                "Where armor plates overlap or are clipped into each other, the marks of a hit stay inside the room there is: a dent cannot run through " +
                "the plate behind it and metal pushed out cannot come through the plate in front. Costs a few physics queries the first time a plate with " +
                "neighbors is hit.");
            DamageMapsSteelGloss = Config.Bind("DamageMaps", "SteelGloss", true,
                "With paint damage on, the steel that shows through is metallic and shiny (rubbed bright where a shell skidded or cut through, duller where " +
                "the metal was torn) and scorched paint is dull. Uses the plate's bump atlas as a metallic/smoothness map (one more atlas per vehicle, " +
                "about 21 MB). Takes effect for the next vehicle that gets marks.");
            ImpactEffectsEnabled = Config.Bind("Effects", "Impacts", true,
                "Where a shell hits armor: a flash, sparks, paint dust and smoke (a skid of sparks for a ricochet, a spray out the back for a penetration, a " +
                "fireball for an HE shell). Only for the shells the mod resolves.");
            ImpactEffectsLitSmoke = Config.Bind("Effects", "LitSmoke", true,
                "Smoke and paint dust are drawn with the URP lit particle shader, so the sun, the sky and the shadows light them (the flash, sparks and " +
                "embers glow on their own). If the build has no such shader they fall back to the plain one. Takes effect after F6 or a restart.");
            ImpactEffectsAmount = Config.Bind("Effects", "Amount", 1f,
                "How many sparks and how much smoke the impact effects make (1 = normal, 0.3 to 2).");
            DamageMapsShareMarks = Config.Bind("DamageMaps", "ShareMarks", true,
                "In multiplayer, the marks of the shells you shoot at another player's vehicle are sent to the other players who have the mod " +
                "(and to the player who was shot), and theirs are drawn on your screen and your vehicle. Players without the mod see nothing of it.");
            DamageMapsSimulatedShots = Config.Bind("DamageMaps", "SimulatedShots", false,
                "The panel's simulated shot leaves its marks (dents, holes, grooves, pits, worked out like a real shot's, with the " +
                "impact simulation) on the plates it hits. Only the look: they never weaken the armor or let anything through, " +
                "and the panel's Clear button takes them off.");
            DamageMapsWeakenArmor = Config.Bind("DamageMaps", "WeakenArmor", true,
                "The marks count: a hole lets shells and fragments through, a dent makes the plate thinner there. " +
                "(Not synchronized between players yet: each computer keeps the marks of the shots it worked out.)");
            DamageMapsDensity = Config.Bind("DamageMaps", "PixelsPerMeter", 512f,
                new ConfigDescription("How detailed the maps are: pixels per meter of plate. More = finer marks, more memory.",
                    new AcceptableValueRange<float>(64f, 1024f)));
            DamageMapsMaxAtlas = Config.Bind("DamageMaps", "MaxAtlasSize", 2048,
                new ConfigDescription("The biggest atlas (texture) the maps of one vehicle's plates may share, in pixels per side: 2048 (16 MB each, up to 4 " +
                    "of them a vehicle) or 4096 (64 MB each, up to 256 MB a vehicle, about twice the detail on a vehicle with many plates, " +
                    "and each atlas takes about 4 times as long to make). Experimental: the log says the density and the memory it got.",
                    new AcceptableValueRange<int>(1024, 4096)));
            DamageMapsNormalStrength = Config.Bind("DamageMaps", "NormalStrength", 1.5f,
                new ConfigDescription("How deep the dents look (the normal map's strength).",
                    new AcceptableValueRange<float>(0.2f, 5f)));
            DamageMapsRelief = Config.Bind("DamageMaps", "Relief", 0,
                new ConfigDescription(
                    "How a mark's relief is shown on the plate: 0 = the normal map (as before), 1 = the bump (height) map, which the " +
                    "shader's parallax reads and which moves the surface itself, 2 = both together.",
                    new AcceptableValueRange<int>(0, 2)));
            UseGpu = Config.Bind("DamageMaps", "UseGpu", true,
                "Run the impact simulation on the graphics card through OpenCL when the computer has it (it comes with the graphics driver). " +
                "If it is missing or fails, the CPU does the work, as before. (This is the player's wish: the game itself never depends on it.)");
            DamageMapsGeometry = Config.Bind("DamageMaps", "RealGeometry", false,
                "EXPERIMENTAL. A flat rectangular armor plate that has been marked is rewritten as real geometry: a fine grid pushed in or " +
                "out by the damage, and a hole is cells that are not there. Keeps the plate's own paint, texture and decals (a hole " +
                "needs no other material). Plates that are thick, curved or not rectangular keep the maps. The normal and bump maps " +
                "are switched off on a plate that has it.");
            DamageMapsGridMm = Config.Bind("DamageMaps", "GridMillimeters", 8f,
                new ConfigDescription("Cell size of the real geometry's grid, in millimeters. Smaller = finer, more vertices.",
                    new AcceptableValueRange<float>(2f, 40f)));
            DamageMapsFineGridMm = Config.Bind("DamageMaps", "FineGridMillimeters", 3f,
                new ConfigDescription("Cell size of the real geometry around holes and sharp features (cracks, rims), in millimeters; the rest of " +
                    "the damage uses the grid above. 0 = no finer zone. Smaller = sharper hole edges, more vertices there.",
                    new AcceptableValueRange<float>(0f, 8f)));
            DamageMapsMaxKiloVertices = Config.Bind("DamageMaps", "MaxKiloVertices", 30,
                new ConfigDescription("Most vertices (in thousands) one plate's real geometry may have; a bigger plate gets a coarser grid.",
                    new AcceptableValueRange<int>(10, 500)));
            DamageMapsBudgetKiloVertices = Config.Bind("DamageMaps", "BudgetKiloVertices", 80,
                new ConfigDescription("Most vertices (in thousands) ALL of one vehicle's damaged plates may have together. Past it the plates marked later get a coarser " +
                    "grid. (In a test, 230 thousand vertices made frames take over a second; 180 thousand was still playable.)",
                    new AcceptableValueRange<int>(10, 1000)));
            GeometryStartPixels = Config.Bind("DamageMaps", "GeometryStartPixels", 1f,
                new ConfigDescription(
                    "Real geometry fades in with how much the maps would get wrong on screen: the biggest push x tan(view angle) x pixels " +
                    "per meter. Below this many pixels only the maps (normal / bump) are used.",
                    new AcceptableValueRange<float>(0.1f, 20f)));
            GeometryFullPixels = Config.Bind("DamageMaps", "GeometryFullPixels", 3f,
                new ConfigDescription("From this many pixels of error the plate shows its full geometry (and the maps are off); in between they are mixed.",
                    new AcceptableValueRange<float>(0.2f, 40f)));
            GeometryAngleStart = Config.Bind("DamageMaps", "GeometryAngleStart", 65f,
                new ConfigDescription("Degrees between the view and the plate's normal (0 = head-on, 90 = grazing) below which the maps are always enough.",
                    new AcceptableValueRange<float>(0f, 89f)));
            GeometryAngleFull = Config.Bind("DamageMaps", "GeometryAngleFull", 78f,
                new ConfigDescription("From this angle the geometry may be shown in full (if the pixel error says so too).",
                    new AcceptableValueRange<float>(1f, 90f)));
            GeometryBlendSeconds = Config.Bind("DamageMaps", "GeometryBlendSeconds", 0.35f,
                new ConfigDescription("Seconds the transition between the maps and the geometry takes, from one end to the other.",
                    new AcceptableValueRange<float>(0.05f, 3f)));
            DamageMapsHoleBothSides = Config.Bind("DamageMaps", "HoleBothSides", true,
                "A plate with a hole is drawn on both sides (shows the inside of the plate through the hole). Off = back faces are not drawn.");
            SimQuality = Config.Bind("DamageMaps", "SimQuality", 2,
                new ConfigDescription(
                    "How a shell's impact on a plate is worked out: 0 = off (the quick analytic marks), 1 = low (a 40 x 40 patch), " +
                    "2 = medium (64 x 64), 3 = high (96 x 96), 4 = ultra (128 x 128). It is a small physics simulation of the plate, run in the " +
                    "background on spare threads; the mark appears when it is ready. Higher = finer, slower, and never finer than the plate's map " +
                    "can show (a vehicle with many plates has a coarse map, and then 'ultra' is lowered by itself). " +
                    "(Only the look: the energy model decides the game.)",
                    new AcceptableValueRange<int>(0, 4)));
            SimCracks = Config.Bind("DamageMaps", "SimCracks", true,
                "The torn links of the simulated plate make thin cracks (grooves) around a hole and across a dent.");
            SimSmoothEdges = Config.Bind("DamageMaps", "SimSmoothEdges", true,
                "The edge of a hole follows where the simulated metal runs out, a little ragged, instead of the simulation grid's square steps.");
            SimThreads = Config.Bind("DamageMaps", "SimThreads", 0,
                new ConfigDescription(
                    "Threads the simulation may use, the simulation's own included. 0 = automatic (all the cores but two). " +
                    "They run below the game's own threads and hand the work to whichever is free.",
                    new AcceptableValueRange<int>(0, 64)));
            SimTimeLimit = Config.Bind("DamageMaps", "SimTimeLimit", 8f,
                new ConfigDescription(
                    "Seconds a simulation may take. One that takes longer is given up and the quick marks are drawn instead.",
                    new AcceptableValueRange<float>(1f, 60f)));
            SimAutoQuality = Config.Bind("DamageMaps", "SimAutoQuality", true,
                "If a simulation takes most of its time limit, the next ones use a lower quality, and go back up when they are quick again.");
            SimMaxQueue = Config.Bind("DamageMaps", "SimMaxQueue", 3,
                new ConfigDescription(
                    "The fewest simulations that may wait their turn. When that many are waiting, new impacts get the quick marks (a burst of shots does not pile up). While simulations are quick (a graphics card) more may wait, up to about 0.8 s of work or 12.",
                    new AcceptableValueRange<int>(1, 10)));
            SimShedLoad = Config.Bind("DamageMaps", "SimShedLoad", true,
                "When the frame rate drops, the simulation uses fewer threads for a while (and takes them back when it recovers).");
            DamageMapsMaxVehicles = Config.Bind("DamageMaps", "MaxVehicles", 6,
                new ConfigDescription("Most vehicles with damage maps at once (each has atlases of up to 32 MB). The one hit longest ago gives it back.",
                    new AcceptableValueRange<int>(1, 20)));
            FragmentArmorFactor = Config.Bind("Energy", "FragmentArmorFactor", 1f,
                new ConfigDescription(
                    "A fragment that meets armor pays what a shell would (ArmorResistance x thickness x its own cross-section) " +
                    "times this. Above 1 armor stops fragments sooner; below 1 they get through more easily.",
                    new AcceptableValueRange<float>(0.05f, 20f)));
            DefaultShellBrittleness = Config.Bind("Energy", "DefaultShellBrittleness", 50f,
                new ConfigDescription(
                    "How easily the casing of an explosive shell breaks into small fragments (percent), for a cannon whose " +
                    "[CAN] tag does not say. More brittle = more, smaller fragments.",
                    new AcceptableValueRange<float>(0f, 100f)));
            SimShellBrittleness = Config.Bind("Simulation", "SimulatedShellBrittleness", 50f,
                new ConfigDescription("Casing brittleness (percent) of the simulated explosive shell.",
                    new AcceptableValueRange<float>(0f, 100f)));
            DefaultFuse = Config.Bind("Energy", "DefaultFuse", 0.5f,
                new ConfigDescription(
                    "Fuse distance (m after the impact) a cannon gets when its Fuse switch is turned on in the designer.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            MaxDeflection = Config.Bind("Energy", "MaxDeflection", 6f,
                new ConfigDescription(
                    "Most (degrees) a plate turns an armor-piercing shell that goes through it, toward the plate's normal. " +
                    "The real turn is this x sin(hit angle) x sqrt(plate hardness / shell hardness) x min(1, thickness / caliber): " +
                    "none head-on, more on slanted, hard, thick plates. 0 = shells fly straight on.",
                    new AcceptableValueRange<float>(0f, 30f)));
            MaxPlates = Config.Bind("Energy", "MaxPlates", 4,
                new ConfigDescription(
                    "Most plates one armor-piercing shell may be resolved against (it flies on after each plate it " +
                    "goes through, so spaced armor counts plate by plate).",
                    new AcceptableValueRange<int>(1, 10)));
            SimShellFuse = Config.Bind("Simulation", "SimulatedShellFuse", false,
                "Simulated explosive shell: on = it has a fuse (APHE: goes through armor and bursts inside), off = no fuse (HE: bursts on the first thing it touches).");
            SimShellFuseDistance = Config.Bind("Simulation", "SimulatedShellFuseDistance", 0.5f,
                new ConfigDescription("Fuse of the simulated explosive shell: meters after the first impact at which it bursts.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            SimShellExplosive = Config.Bind("Simulation", "SimulatedShellExplosive", false,
                "Simulated shell: off = armor-piercing (goes through plates and flies on), on = explosive (breaks up on the plate). " +
                "A real cannon uses its own 'Projectile Type' (Basic = armor-piercing, Explosive).");
            SimpleFragmentArea = Config.Bind("Energy", "SimpleFragmentAreaCm2", 0.5f,
                new ConfigDescription("Cross-section (cm2) of every fragment in simple mode.",
                    new AcceptableValueRange<float>(0.01f, 20f)));
            MaxPartsPerFragment = Config.Bind("Energy", "MaxPartsPerFragment", 3,
                new ConfigDescription("Most parts one fragment may cross before it stops (it also stops when its energy runs out).",
                    new AcceptableValueRange<int>(1, 10)));
            SpreadBaseAngle = Config.Bind("Energy", "SpreadBaseAngle", 15f,
                new ConfigDescription("Base spread of the fragment directions (degrees, one standard deviation). Brittle and thick plates widen it; leftover energy narrows it.",
                    new AcceptableValueRange<float>(1f, 60f)));
            AxisTilt = Config.Bind("Energy", "AxisTilt", 0.5f,
                new ConfigDescription("How much the fragment cone leans from the shell's path toward the plate's normal on slanted hits (0 = none, 1 = strong). Harder plates lean it more.",
                    new AcceptableValueRange<float>(0f, 1f)));
            SpreadAnisotropy = Config.Bind("Energy", "SpreadAnisotropy", 1.5f,
                new ConfigDescription("How much wider the cone is along the plane of incidence on a fully grazing hit (1 = round).",
                    new AcceptableValueRange<float>(1f, 4f)));
            VisualSpeedMultiplier = Config.Bind("Energy", "VisualSpeedMultiplier", 0.05f,
                new ConfigDescription("Drawn rays move at their real speed (worked out from their energy and mass) times this, so they can be followed. They slow down as they lose energy.",
                    new AcceptableValueRange<float>(0.001f, 1f)));

            XRayRimLook = Config.Bind("Simulation", "XRayRimLook", true,
                "Rim-lit X-ray look (edges glow). Costs CPU time. Off = flat colors, much cheaper.");
            XRayShowUndamaged = Config.Bind("Simulation", "XRayShowUndamaged", true,
                "If false, the X-ray view only shows armor and parts that would take damage; " +
                "the rest stays as it is. Fewer extra meshes to draw.");
            XRayMaxRimKiloVertices = Config.Bind("Simulation", "XRayMaxRimKiloVertices", 60,
                new ConfigDescription(
                    "Most vertices (in thousands) that get the rim-lit look. Beyond this, the least " +
                    "important parts (least damage, farthest) use the cheap flat look.",
                    new AcceptableValueRange<int>(0, 1000)));
            XRayBudgetKiloVertices = Config.Bind("Simulation", "XRayBudgetKiloVertices", 20,
                new ConfigDescription(
                    "Most vertices (in thousands) the rim look may recalculate per frame. " +
                    "Lower = smoother frame rate, but the glow follows the camera more slowly.",
                    new AcceptableValueRange<int>(1, 500)));

            MaxRicochets = Config.Bind("Ricochet", "MaxRicochets", 2,
                new ConfigDescription(
                    "Max times one ray may bounce (the bounce off the armor counts as the first). " +
                    "0 turns ricochets off. Keep it low to avoid stutters.",
                    new AcceptableValueRange<int>(0, 10)));
            ArmorRicochetAngle = Config.Bind("Ricochet", "ArmorRicochetAngle", 70f,
                new ConfigDescription(
                    "Impact angle (degrees) from which a shell ricochets off armor when armor and shell have " +
                    "the same hardness and the shell flies at 800 m/s. Harder armor, a slower shell lower it; " +
                    "softer armor, a faster shell raise it; a shell much bigger than the plate is thick never " +
                    "ricochets. 0 = head-on, 90 = grazing.",
                    new AcceptableValueRange<float>(0f, 90f)));
            ArmorRicochetMultiplier = Config.Bind("Ricochet", "ArmorRicochetMultiplier", 1f,
                new ConfigDescription(
                    "Multiplies the armor's critical ricochet angle (for testing). Above 1 the armor tolerates " +
                    "steeper hits before ricocheting; below 1 it ricochets sooner. The result is capped at 90.",
                    new AcceptableValueRange<float>(0f, 10f)));
            FragmentRicochetAngle = Config.Bind("Ricochet", "FragmentRicochetAngle", 60f,
                new ConfigDescription(
                    "Base impact angle (degrees) at which a fragment ray ricochets off any " +
                    "object it hits. Lower than the armor angle by default.",
                    new AcceptableValueRange<float>(0f, 90f)));
            FragmentRicochetMultiplier = Config.Bind("Ricochet", "FragmentRicochetMultiplier", 1f,
                new ConfigDescription(
                    "Multiplies the fragment angle, same idea as ArmorRicochetMultiplier.",
                    new AcceptableValueRange<float>(0f, 10f)));

            // The settings that decide what happens to other players. On someone else's server they come
            // from the host (see NetSync); everything else (rays, X-ray, logs, simulated shot) stays yours.
            AmmoRacksEnabled = Config.Bind("AmmoRacks", "Enabled", true,
                "If true, fuselages with an [AMM] tag are ammo racks: they hold the rounds of the cannons in their " +
                "group ([CAN g=]), and can catch fire and explode when hit.");
            CaseDiameterRatio = Config.Bind("AmmoRacks", "CaseDiameterRatio", 1.3f,
                new ConfigDescription("A round's case is this many times wider than the caliber (its widest part).",
                    new AcceptableValueRange<float>(1f, 2f)));
            CaseLitresPerMJ = Config.Bind("AmmoRacks", "CaseLitresPerMJ", 2f,
                new ConfigDescription(
                    "Case volume per MJ of muzzle energy: faster guns need more propellant, so longer rounds. " +
                    "2 matches the Pz III's 50 mm guns (KwK 38: 29 cm case, KwK 39: 43 cm).",
                    new AcceptableValueRange<float>(0.5f, 10f)));
            RoundSpacingPercent = Config.Bind("AmmoRacks", "RoundSpacingPercent", 10f,
                new ConfigDescription("Gap between rounds across the rack (the rack's own structure), as a percentage of the round's width.",
                    new AcceptableValueRange<float>(0f, 50f)));
            CookOffEnergyKJ = Config.Bind("AmmoRacks", "CookOffEnergyKJ", 100f,
                new ConfigDescription(
                    "Energy part of the cook-off chance: e^(-kJ that went into a rack's rounds / this) is the chance they " +
                    "survive it. At 100: 100 kJ into the rounds gives 63%, 300 kJ 95%; fragments add up. Lower = racks " +
                    "cook off more easily.",
                    new AcceptableValueRange<float>(1f, 10000f)));
            CookOffDirectHit = Config.Bind("AmmoRacks", "CookOffDirectHit", 50f,
                new ConfigDescription(
                    "Chance (%) that a shell punching through a rack's rounds sets them off, whatever its size, on top " +
                    "of the energy part. Chance = 1 - (1 - this)^(shells through the rounds) x e^(-kJ / CookOffEnergyKJ): " +
                    "with the shell's own energy counted, about 64% for a 20 mm APHE, 95% for a 37 mm, certain for 75 mm and up.",
                    new AcceptableValueRange<float>(0f, 100f)));
            CookOffBurnSeconds = Config.Bind("AmmoRacks", "CookOffBurnSeconds", 10f,
                new ConfigDescription("How long (s) a burning rack burns before it explodes.",
                    new AcceptableValueRange<float>(0f, 120f)));
            CookOffTNTEfficiency = Config.Bind("AmmoRacks", "CookOffTNTEfficiency", 25f,
                new ConfigDescription(
                    "How much of a cook-off's propellant counts as a real explosion, in % of its TNT equivalent (a " +
                    "cook-off is an inefficient detonation). It sets the charge of the blast that wrecks the damage " +
                    "modules and racks inside: at 25%, a 96 lb rack is 10.9 kg of TNT, wrecking within 2.7 m and " +
                    "damaging out to 4.8 m. The reach grows with the cube root of the charge.",
                    new AcceptableValueRange<float>(0.1f, 100f)));
            CookOffWreckKPa = Config.Bind("AmmoRacks", "CookOffWreckKPa", 1000f,
                new ConfigDescription(
                    "Blast overpressure (kPa) that wrecks a damage module or rack (its whole health; twice that at most, " +
                    "closer in). 1000 kPa = 145 psi.",
                    new AcceptableValueRange<float>(10f, 100000f)));
            CookOffDamageKPa = Config.Bind("AmmoRacks", "CookOffDamageKPa", 200f,
                new ConfigDescription(
                    "Blast overpressure (kPa) below which the cook-off does no damage; between this and CookOffWreckKPa the " +
                    "damage grows with the log of the pressure. 200 kPa = 29 psi.",
                    new AcceptableValueRange<float>(1f, 100000f)));
            CookOffArmorHalvingMm = Config.Bind("AmmoRacks", "CookOffArmorHalvingMm", 10f,
                new ConfigDescription(
                    "Armor between a cook-off and a part shields it: every this many mm of effective armor on the line " +
                    "(thickness / cos of the angle) halves the blast's pressure. At 10: a 20 mm plate leaves a quarter, " +
                    "64 mm about 1%.",
                    new AcceptableValueRange<float>(0.1f, 1000f)));
            BlastStrength = Config.Bind("AmmoRacks", "BlastStrength", 1f,
                new ConfigDescription(
                    "Multiplies each round's explosive equivalent (at 1: a 50 mm round at 835 m/s is about 0.58 lb, so " +
                    "~90 of them make about 55 lb). The cook-off is sized like the game's own bombs: Boom 25 (250 lb) = 0.8.",
                    new AcceptableValueRange<float>(0f, 10f)));
            MaxBlastLb = Config.Bind("AmmoRacks", "MaxBlastLb", 120f,
                new ConfigDescription("Most pounds of explosive one rack's cook-off can be, however many rounds it holds.",
                    new AcceptableValueRange<float>(1f, 1000f)));
            BlastVisualPercent = Config.Bind("AmmoRacks", "BlastVisualPercent", 75f,
                new ConfigDescription(
                    "Size of the cook-off's bang, in % of a game bomb of the same pounds (Boom 25 = 250 lb = 0.8): at 75%, " +
                    "96 lb gives 0.23. Only for show: the cook-off's damage, inside the vehicle and around it, comes from " +
                    "the blast physics (CookOffTNTEfficiency), not from this.",
                    new AcceptableValueRange<float>(0f, 200f)));
            BlastType = Config.Bind("AmmoRacks", "BlastType", "BombExplosion",
                new ConfigDescription(
                    "The game explosion of a cook-off's bang, at the nearest [VNT] part (or just above the rack), before " +
                    "the fire jet. Sizes are worked out in the bombs' scale (Boom 25 = 250 lb = 0.8), so other types " +
                    "will look bigger or smaller.",
                    new AcceptableValueList<string>("BombExplosion", "GeneralExplosion", "MissileExplosion", "RocketExplosion")));
            ShowRacksInDesigner = Config.Bind("AmmoRacks", "ShowInDesigner", true,
                "If true, ammo racks are drawn see-through in the designer with their rounds inside (they can still be " +
                "clicked). Only changes what you see.");
            DebugDumpShape = Config.Bind("AmmoRacks", "DebugDumpShape", false,
                "Diagnostic: each time the designer re-measures an ammo rack, write its shape, round positions and " +
                "measurement to BepInEx/ammo-rack-shape.obj (overwritten each time).");

            CrewEnabled = Config.Bind("Crew", "Enabled", true,
                "If true, seats with a [CRW] tag are crew members: each dies when its seat has lost all its " +
                "health, and the vehicle is killed when its last crew member dies.");
            CrewSwitchSeconds = Config.Bind("Crew", "SwitchSeconds", 5f,
                new ConfigDescription("Seconds a crew member takes to reach the station of a job he takes over from a dead " +
                    "one. Until then he does that job at 0%: the only driver gone means no engine power, the only aimer gone " +
                    "frozen aimed rotators, the only loader gone no shots.", new AcceptableValueRange<float>(0f, 30f)));

            NetSync.RegisterGlobal(
                ApplyDamage, FragmentMass, MaxVisualFragments, BallisticRayRange, IgnoreGameDamageOnArmor,
                GameDamageExplosions, ArmoredStructureDamage,
                DefaultShellHardness, ArmorResistance, RicochetFriction, RicochetRandomness,
                DamagePerKiloJoule, DensityModifier, ConstantForceLoss, SimpleFragments,
                FillerHE, FillerAPHE, GurneySpeed, DetonationSidewaysBias, DefaultFuse,
                FragmentArmorFactor, DefaultShellBrittleness, DamageMapsWeakenArmor,
                SimpleFragmentArea, MaxPartsPerFragment, SpreadBaseAngle, AxisTilt, SpreadAnisotropy,
                MaxDeflection, MaxPlates, MaxRicochets, ArmorRicochetAngle, ArmorRicochetMultiplier,
                FragmentRicochetAngle, FragmentRicochetMultiplier,
                AmmoRacksEnabled, CaseDiameterRatio, CaseLitresPerMJ, RoundSpacingPercent,
                CookOffEnergyKJ, CookOffDirectHit, CookOffBurnSeconds, CookOffTNTEfficiency, CookOffWreckKPa, CookOffDamageKPa, CookOffArmorHalvingMm,
                BlastStrength, BlastType, MaxBlastLb, BlastVisualPercent,
                CrewEnabled, CrewSwitchSeconds);

            // One patch failing (for example after a game update) must not stop the others.
            HarmonyInstance = new Harmony("com.yourname.armorfragments");
            SafePatcher.PatchAll(HarmonyInstance, typeof(ArmorFragmentsPlugin).Assembly);
            CompatibilityCheck.Run();   // says in the log what a game update has changed
            Log.LogInfo("Armor Fragments loaded!");
            attachMenuAt = UnityEngine.Time.unscaledTime + 0.5f;   // loaded in the middle of a flight (a reload): the flight menu is already built
        }

    }
}
