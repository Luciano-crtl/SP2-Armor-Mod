using Assets.Scripts.Craft.Parts;
using HarmonyLib;

namespace ArmorFragments
{
    // The game's own damage to an armor part is ignored (for now): applied as normal damage it would
    // simply make the plate vanish, and what a plate does to a shell is already worked out by the energy
    // model. Everything that reaches a part goes through PartScript.OnDamaged (the shell's own impact, an
    // explosion, a crash, machine-gun bullets), so skipping it for parts with an [ARM] tag is enough.
    // This mod never calls OnDamaged on an armor part itself (see FragmentSpawner.Deposit).
    [HarmonyPatch(typeof(PartScript), "OnDamaged")]
    internal static class ArmorDamagePatch
    {
        // Returning false skips the original method.
        private static bool Prefix(PartScript __instance)
        {
            if (!ArmorFragmentsPlugin.IgnoreGameDamageOnArmor.Value) return true;

            PartData data = __instance.Part;
            if (data == null) return true;

            return TagCodec.Parse(data.Name).Armor == null;
        }
    }
}
