using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace ArmorFragments
{
    [BepInPlugin("com.yourname.armorfragments", "Armor Fragments", "0.1.0")]
    public class ArmorFragmentsPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            new Harmony("com.yourname.armorfragments").PatchAll();
            Log.LogInfo("Armor Fragments loaded!");
        }
    }
}