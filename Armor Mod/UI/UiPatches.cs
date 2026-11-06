using System;
using System.Xml.Linq;
using Assets.Scripts.Flight.UI.Panels;
using HarmonyLib;
using Jundroo.Juicy.Widgets;

namespace ArmorFragments.UI
{
    // The game builds its UI from XML files and finds widget scripts by name inside its
    // own assembly. These three patches add our button, our layout and our script type.

    // 1) Add our button to the in-flight menu.
    [HarmonyPatch(typeof(MenuPanelScript), "OnWidgetInitialized")]
    internal static class MenuButtonPatch
    {
        private static void Postfix(Widget widget)
        {
            try
            {
                ArmorPanel.AddMenuButton(widget);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Couldn't add the Armor Fragments menu button: " + e);
            }
        }
    }

    // 2) When the game asks for our panel's XML, give it ours instead of looking in its files.
    [HarmonyPatch(typeof(Jundroo.Juicy.ResourceLoader), "LoadXml")]
    internal static class PanelLayoutPatch
    {
        private static bool Prefix(string path, ref XElement __result)
        {
            if (path != ArmorPanel.PanelXmlPath) return true;   // not ours: run the original

            __result = ArmorPanel.BuildLayout();
            return false;                                        // skip the original
        }
    }

    // 3) The layout names our script class. The game only searches its own assembly,
    //    so when that fails for one of our names, we answer.
    [HarmonyPatch(typeof(Jundroo.Juicy.ResourceLoader), "GetScriptType")]
    internal static class PanelScriptTypePatch
    {
        private static void Postfix(string scriptTypeName, ref Type __result)
        {
            if (__result == null && scriptTypeName != null &&
                scriptTypeName.StartsWith("ArmorFragments."))
            {
                __result = typeof(ArmorFragmentsPlugin).Assembly.GetType(scriptTypeName);
            }
        }
    }
}
