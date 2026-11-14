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

        // These show up in BepInEx/config/com.yourname.armorfragments.cfg
        // after the first run, so you can tune them without rebuilding.
        internal static ConfigEntry<bool> ApplyDamage;
        internal static ConfigEntry<float> FragmentMass;
        internal static ConfigEntry<int> MaxVisualFragments;
        internal static ConfigEntry<float> BallisticRayRange;
        internal static ConfigEntry<bool> DrawRays;
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
        internal static ConfigEntry<bool> LogChordDiagnostics;

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

        // Shown in the log and the panel, to tell which build the game is really running.
        public const string Version = "0.7.0";

        private void Update()
        {
            UI.DesignerSection.Tick();
            XRayView.Tick();
            VehicleXRay.Tick();
            NetSync.Tick();
            HostResolver.Tick();
            ModuleOutputs.Tick();
            AmmoRacks.Tick();
            RackFireSync.Tick();
            RackVisualizer.Tick();
            AmmoRackXRay.Tick();
            ImpactCamera.Tick();
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            Log.LogInfo("Armor Fragments " + Version + " loading");

            // Keeps this plugin's latest log lines for the in-game panel.
            BepInEx.Logging.Logger.Listeners.Add(new LogMirror());

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
                "If false (Jakniphe's choice), the game never blows parts or vehicles up because of damage, on any vehicle: " +
                "no 'part explodes' roll on damaged parts, no vehicle explosion at the damage limit, no delayed fuel tank or " +
                "propeller explosions, no explosion chains. Vehicles are knocked out through their modules and ammo racks, " +
                "and keep their shape; destroyed parts stay attached (wings no longer fall off). Weapons' own explosions are " +
                "not affected. True = the game's usual behavior.");
            ArmoredStructureDamage = Config.Bind("Fragments", "ArmoredStructureDamage", false,
                "If false (Jakniphe's choice), on a vehicle with armor ([ARM] parts) only damage modules ([MOD]) and ammo " +
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

            LogChordDiagnostics = Config.Bind("Simulation", "LogChordDiagnostics", true,
                "Each simulated shot also writes to the log how far the line travels inside every part " +
                "(lines starting with [CHORD]). Diagnostic for the energy model; changes nothing.");

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
                    "about 60% for a 20 mm APHE, 85-90% for a 37 mm, near certain for 75 mm and up.",
                    new AcceptableValueRange<float>(0f, 100f)));
            CookOffBurnSeconds = Config.Bind("AmmoRacks", "CookOffBurnSeconds", 10f,
                new ConfigDescription("How long (s) a burning rack burns before it explodes, every time (Jakniphe: a " +
                    "consistent 10 s; replaces the random CookOffDelayMin/Max).",
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

            NetSync.RegisterGlobal(
                ApplyDamage, FragmentMass, MaxVisualFragments, BallisticRayRange, IgnoreGameDamageOnArmor,
                GameDamageExplosions, ArmoredStructureDamage,
                DefaultShellHardness, ArmorResistance, RicochetFriction, RicochetRandomness,
                DamagePerKiloJoule, DensityModifier, ConstantForceLoss, SimpleFragments,
                FillerHE, FillerAPHE, GurneySpeed, DetonationSidewaysBias, DefaultFuse,
                FragmentArmorFactor, DefaultShellBrittleness,
                SimpleFragmentArea, MaxPartsPerFragment, SpreadBaseAngle, AxisTilt, SpreadAnisotropy,
                MaxDeflection, MaxPlates, MaxRicochets, ArmorRicochetAngle, ArmorRicochetMultiplier,
                FragmentRicochetAngle, FragmentRicochetMultiplier,
                AmmoRacksEnabled, CaseDiameterRatio, CaseLitresPerMJ, RoundSpacingPercent,
                CookOffEnergyKJ, CookOffDirectHit, CookOffBurnSeconds, CookOffTNTEfficiency, CookOffWreckKPa, CookOffDamageKPa, CookOffArmorHalvingMm,
                BlastStrength, BlastType, MaxBlastLb, BlastVisualPercent);

            // One patch failing (for example after a game update) must not stop the others.
            SafePatcher.PatchAll(new Harmony("com.yourname.armorfragments"), typeof(ArmorFragmentsPlugin).Assembly);
            CompatibilityCheck.Run();   // says in the log what a game update has changed
            Log.LogInfo("Armor Fragments loaded!");
        }
    }
}
