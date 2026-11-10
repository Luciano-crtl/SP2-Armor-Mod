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
        internal static ConfigEntry<int> DefaultFragmentCount;
        internal static ConfigEntry<float> BallisticRayRange;
        internal static ConfigEntry<bool> DrawRays;
        internal static ConfigEntry<float> RayWidth;
        internal static ConfigEntry<float> InitialRayWidth;
        internal static ConfigEntry<bool> IgnoreGameDamageOnArmor;
        internal static ConfigEntry<float> RayTrailLength;
        internal static ConfigEntry<float> BubbleSize;
        internal static ConfigEntry<float> BubbleLifetime;
        internal static ConfigEntry<bool> MirrorLogToGameConsole;

        // Shot simulation and X-ray view.
        internal static ConfigEntry<int> SimFragmentCount;
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
        internal static ConfigEntry<float> ShellFragmentShare;
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

        // Shown in the log and the panel, to tell which build the game is really running.
        public const string Version = "0.2.0";

        private void Update()
        {
            UI.DesignerSection.Tick();
            XRayView.Tick();
            VehicleXRay.Tick();
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
            DefaultFragmentCount = Config.Bind("Fragments", "DefaultFragmentCount", 20,
                new ConfigDescription(
                    "Fragment rays made when a shell penetrates armor, for cannons without a [CAN f=] tag.",
                    new AcceptableValueRange<int>(1, 200)));
            BallisticRayRange = Config.Bind("Fragments", "BallisticRayRange", 100f,
                new ConfigDescription(
                    "When a real shell stops in a part that is not armor, a ballistic ray carries on this far (m) looking for armor.",
                    new AcceptableValueRange<float>(1f, 1000f)));
            DrawRays = Config.Bind("Debug", "DrawRays", true,
                "If true, draws each fragment ray in the world (red = hit a part, yellow = hit nothing).");
            RayWidth = Config.Bind("Debug", "RayWidth", 0.02f,
                new ConfigDescription("Thickness of the drawn rays, in meters.",
                    new AcceptableValueRange<float>(0.001f, 1f)));
            InitialRayWidth = Config.Bind("Debug", "InitialRayWidth", 0.06f,
                new ConfigDescription(
                    "Thickness of the initial ray (the shell, or the simulated shot from the camera), in meters.",
                    new AcceptableValueRange<float>(0.001f, 1f)));
            IgnoreGameDamageOnArmor = Config.Bind("Fragments", "IgnoreGameDamageOnArmor", true,
                "If true, the game's own damage to parts with an [ARM] tag (shell impact, explosions, crashes) is ignored: " +
                "armor is judged only by the energy model and cannot be destroyed.");
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

            SimFragmentCount = Config.Bind("Simulation", "SimulatedFragmentCount", 20,
                new ConfigDescription(
                    "Fragment rays made by the simulated shell when it penetrates armor.",
                    new AcceptableValueRange<int>(1, 200)));
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

            const string NotUsed = " (Energy model: not used by the calculation yet.)";
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
            ShellFragmentShare = Config.Bind("Energy", "ShellFragmentShare", 0.2f,
                new ConfigDescription(
                    "Share of the energy left after the armor that becomes fragments even if the plate does not " +
                    "shatter at all (the shell breaking up by itself). The plate's brittleness adds to it. " +
                    "0 = only the plate makes fragments, 1 = all the leftover energy becomes fragments.",
                    new AcceptableValueRange<float>(0f, 1f)));
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

            // One patch failing (for example after a game update) must not stop the others.
            SafePatcher.PatchAll(new Harmony("com.yourname.armorfragments"), typeof(ArmorFragmentsPlugin).Assembly);
            Log.LogInfo("Armor Fragments loaded!");
        }
    }
}
