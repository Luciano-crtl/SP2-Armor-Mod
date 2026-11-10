using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Multiplayer;
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

    // The same rule for a shell that hits a part of ANOTHER player's aircraft. The game does not call
    // OnDamaged there: it sends the damage to the aircraft's owner through DamagePart. Without this, the
    // game's own damage to a remote armor plate would still go out (and land if the owner has no mod).
    [HarmonyPatch(typeof(NetworkAircraftScript), "DamagePart")]
    internal static class RemoteArmorDamagePatch
    {
        private static bool Prefix(PartScript part)
        {
            if (!ArmorFragmentsPlugin.IgnoreGameDamageOnArmor.Value) return true;
            if (part == null || part.Part == null) return true;

            return TagCodec.Parse(part.Part.Name).Armor == null;
        }
    }
}
