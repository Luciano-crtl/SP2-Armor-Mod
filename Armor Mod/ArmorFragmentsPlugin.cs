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

            new Harmony("com.yourname.armorfragments").PatchAll();
            Log.LogInfo("Armor Fragments loaded!");
        }
    }
}
