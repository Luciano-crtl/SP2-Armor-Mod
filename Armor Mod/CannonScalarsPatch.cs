using System;
using System.Reflection;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using HarmonyLib;

namespace ArmorFragments
{
    // A cannon with a [CAN] tag is worked out by this mod, so the game's own damage must not add to it:
    // its shells get an impact damage scalar and an explosion scalar of 0 when fired. (The game copies both
    // from the cannon's data to each projectile at the moment of firing, so this is the place.)
    // Only the flight's copy of the cannon data is changed; nothing is saved into the craft.
    [HarmonyPatch(typeof(CannonScript), "Fire")]
    internal static class CannonScalarsPatch
    {
        private static readonly PropertyInfo ImpactDamageScalar = typeof(CannonData).GetProperty("ImpactDamageScalar", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo ExplosionScalar = typeof(CannonData).GetProperty("ExplosionScalar", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static void Prefix(CannonScript __instance)
        {
            try
            {
                PartScript part = __instance.PartScript;
                if (part == null || part.Part == null) return;
                if (TagCodec.Parse(part.Part.Name).Cannon == null) return;

                foreach (PartModifierData modifier in part.Part.Modifiers)
                {
                    CannonData data = modifier as CannonData;
                    if (data == null) continue;

                    SetToZero(ImpactDamageScalar, data);
                    SetToZero(ExplosionScalar, data);
                    break;
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Could not switch off the game's damage of a tagged cannon: " + e.Message);
            }
        }

        private static void SetToZero(PropertyInfo property, CannonData data)
        {
            if (property == null) return;
            if ((float)property.GetValue(data, null) == 0f) return;
            property.SetValue(data, 0f, null);
        }
    }
}
