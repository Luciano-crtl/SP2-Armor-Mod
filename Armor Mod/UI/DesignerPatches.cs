using System;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Design.UI.PartProperties;
using HarmonyLib;

namespace ArmorFragments.UI
{
    // Hooks into the designer's part properties panel (the one with the name field).

    // The panel was created: add our section to it.
    [HarmonyPatch(typeof(PartPropertiesPanelScript), "InitializeDesignerPanel")]
    internal static class DesignerPanelInitPatch
    {
        private static void Postfix(PartPropertiesPanelScript __instance)
        {
            try
            {
                DesignerSection.Attach(__instance);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Couldn't add the designer section: " + e);
            }
        }
    }

    // A part was selected (or the selection was cleared).
    [HarmonyPatch(typeof(PartPropertiesPanelScript), "SetSelectedPart")]
    internal static class DesignerSelectionPatch
    {
        private static void Postfix(PartPropertiesPanelScript __instance, PartScript selectedPart)
        {
            DesignerSection.OnPartSelected(__instance, selectedPart);
        }
    }

    // The game refreshed the name field: the name may have changed (undo, paste...).
    [HarmonyPatch(typeof(PartPropertiesPanelScript), "UpdateCommonPartProperties")]
    internal static class DesignerNameRefreshPatch
    {
        private static void Postfix(PartPropertiesPanelScript __instance)
        {
            DesignerSection.OnCommonPropertiesUpdated(__instance);
        }
    }
}
