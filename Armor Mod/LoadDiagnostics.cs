using System;
using System.Linq;
using System.Xml.Linq;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using HarmonyLib;

namespace ArmorFragments
{
    // [LOAD] For each craft read for a flight: its parts, the parts the game left disabled, and the bodies it read, in the log.
    [HarmonyPatch(typeof(Assembly), MethodType.Constructor, new Type[] { typeof(XElement), typeof(int), typeof(CraftLoadContext) })]
    internal static class LoadDiagnosticsPatch
    {
        private static void Postfix(Assembly __instance, XElement assemblyElement, CraftLoadContext loadContext)
        {
            try
            {
                if (loadContext != CraftLoadContext.Flight) return;
                int bodiesInXml = assemblyElement.Elements("Bodies").Elements("Body").Count();
                string disabled = string.Join(", ", __instance.DisabledParts.Select(p => p.Id + " " + (p.PartType != null ? p.PartType.Name : "?")).ToArray());
                ArmorFragmentsPlugin.Log.LogInfo("[LOAD] Craft read for a flight: " + __instance.Parts.Count + " part(s), " + __instance.DisabledParts.Count + " disabled" +
                    (disabled.Length > 0 ? " (" + disabled + ")" : "") + ", " + bodiesInXml + " body element(s) in its XML, " + __instance.RigidBodyGroups.Count + " body group(s) made" +
                    (__instance.MissingParts != null && __instance.MissingParts.Count > 0 ? ", missing part types: " + string.Join(", ", __instance.MissingParts.ToArray()) : ""));
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[LOAD] could not report the craft: " + e.Message);
            }
        }
    }
}
