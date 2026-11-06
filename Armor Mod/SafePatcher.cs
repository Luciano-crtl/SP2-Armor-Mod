using System;
using System.Reflection;
using HarmonyLib;

namespace ArmorFragments
{
    // Like Harmony.PatchAll(), but each patch class is applied on its own.
    // If one fails, it is logged and the rest still work.
    internal static class SafePatcher
    {
        public static void PatchAll(Harmony harmony, Assembly assembly)
        {
            foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
            {
                if (type == null || !type.IsDefined(typeof(HarmonyPatch), false)) continue;

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ArmorFragmentsPlugin.Log.LogInfo("Patch applied: " + type.Name);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning(
                        "Patch " + type.Name + " skipped (the game may have changed): " +
                        e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message);
                }
            }
        }
    }
}
