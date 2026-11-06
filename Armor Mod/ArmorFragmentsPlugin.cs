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

        // These show up in BepInEx/config/com.yourname.armorfragments.cfg
        // after the first run, so you can tune them without rebuilding.
        internal static ConfigEntry<bool> ApplyDamage;
        internal static ConfigEntry<float> DamagePerFragment;
        internal static ConfigEntry<bool> DrawRays;
        internal static ConfigEntry<float> RayLifetime;

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

            ApplyDamage = Config.Bind("Fragments", "ApplyDamage", true,
                "If true, fragments damage the parts they hit.");
            DamagePerFragment = Config.Bind("Fragments", "DamagePerFragment", 5f,
                "Damage of one fragment at point-blank range. Falls off linearly to 0 at max range.");
            DrawRays = Config.Bind("Debug", "DrawRays", true,
                "If true, draws each fragment ray in the world (red = hit a part, yellow = hit nothing).");
            RayLifetime = Config.Bind("Debug", "RayLifetime", 2f,
                "Seconds the drawn rays stay visible.");

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

            new Harmony("com.yourname.armorfragments").PatchAll();
            Log.LogInfo("Armor Fragments loaded!");
        }
    }
}
