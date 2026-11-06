using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ArmorFragments
{
    [BepInPlugin("com.yourname.armorfragments", "Armor Fragments", "0.1.0")]
    public class ArmorFragmentsPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ArmorFragmentsPlugin Instance;

        // These show up in BepInEx/config/com.yourname.armorfragments.cfg
        // after the first run, so you can tune them without rebuilding.
        internal static ConfigEntry<bool> ApplyDamage;
        internal static ConfigEntry<float> DamagePerFragment;
        internal static ConfigEntry<bool> DrawRays;
        internal static ConfigEntry<float> RayWidth;
        internal static ConfigEntry<float> RaySpeed;
        internal static ConfigEntry<float> RayTrailLength;
        internal static ConfigEntry<float> BubbleSize;
        internal static ConfigEntry<float> BubbleLifetime;
        internal static ConfigEntry<bool> MirrorLogToGameConsole;

        // Ricochet. Angles: 0 = head-on, 90 = grazing.
        // A hit ricochets when its angle >= (base angle * multiplier).
        internal static ConfigEntry<int> MaxRicochets;
        internal static ConfigEntry<float> ArmorRicochetAngle;
        internal static ConfigEntry<float> ArmorRicochetMultiplier;
        internal static ConfigEntry<float> FragmentRicochetAngle;
        internal static ConfigEntry<float> FragmentRicochetMultiplier;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Keeps this plugin's latest log lines for the in-game panel.
            BepInEx.Logging.Logger.Listeners.Add(new LogMirror());

            ApplyDamage = Config.Bind("Fragments", "ApplyDamage", true,
                "If true, fragments damage the parts they hit.");
            DamagePerFragment = Config.Bind("Fragments", "DamagePerFragment", 5f,
                new ConfigDescription(
                    "Damage of one fragment at point-blank range. Falls off linearly to 0 at max range.",
                    new AcceptableValueRange<float>(0f, 100f)));
            DrawRays = Config.Bind("Debug", "DrawRays", true,
                "If true, draws each fragment ray in the world (red = hit a part, yellow = hit nothing).");
            RayWidth = Config.Bind("Debug", "RayWidth", 0.02f,
                new ConfigDescription("Thickness of the drawn rays, in meters.",
                    new AcceptableValueRange<float>(0.001f, 1f)));
            RaySpeed = Config.Bind("Debug", "RaySpeed", 40f,
                new ConfigDescription(
                    "How fast the drawn ray moves along its path, in meters per second. " +
                    "Visual only: it does not change damage or physics.",
                    new AcceptableValueRange<float>(1f, 500f)));
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

            MaxRicochets = Config.Bind("Ricochet", "MaxRicochets", 2,
                new ConfigDescription(
                    "Max times one ray may bounce (the bounce off the armor counts as the first). " +
                    "0 turns ricochets off. Keep it low to avoid stutters.",
                    new AcceptableValueRange<int>(0, 10)));
            ArmorRicochetAngle = Config.Bind("Ricochet", "ArmorRicochetAngle", 70f,
                new ConfigDescription(
                    "Base impact angle (degrees) at which a shell ricochets off an ARM_ part " +
                    "instead of spawning fragments. 0 = head-on, 90 = grazing.",
                    new AcceptableValueRange<float>(0f, 90f)));
            ArmorRicochetMultiplier = Config.Bind("Ricochet", "ArmorRicochetMultiplier", 1f,
                new ConfigDescription(
                    "Multiplies the armor angle. Above 1 the armor tolerates steeper hits before " +
                    "ricocheting; below 1 it ricochets sooner. The result is capped at 90.",
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
