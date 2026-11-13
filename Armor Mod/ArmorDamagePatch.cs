using System;
using System.Collections.Generic;
using System.Reflection;
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
    // Patched on PartScript AND on every class that overrides it: a patch on the base method does not
    // reach an override, so a game update that gives some part type its own OnDamaged would let damage
    // through to armor.
    [HarmonyPatch]
    internal static class ArmorDamagePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(PartScript).Assembly))
            {
                if (type == null || !typeof(PartScript).IsAssignableFrom(type)) continue;

                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "OnDamaged" || method.IsAbstract) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length < 2 || parameters[0].ParameterType != typeof(int?)) continue;   // the attacker, then the damage
                    yield return method;
                }
            }
        }

        // Returning false skips the original method.
        private static bool Prefix(PartScript __instance)
        {
            return !ArmorTags.IsProtected(__instance);
        }
    }

    // The part's damage levels (visual damage, and the part breaking off) are the other way armor could
    // be harmed: they are never reached for armor either.
    [HarmonyPatch(typeof(PartScript), "OnDamageLevelIncreased")]
    internal static class ArmorDamageLevelPatch
    {
        private static bool Prefix(PartScript __instance)
        {
            return !ArmorTags.IsProtected(__instance);
        }
    }

    internal static class ArmorTags
    {
        // An armor part whose damage from the game is to be ignored.
        public static bool IsProtected(PartScript part)
        {
            if (!ArmorFragmentsPlugin.IgnoreGameDamageOnArmor.Value) return false;

            PartData data = part != null ? part.Part : null;
            if (data == null) return false;

            return TagCodec.Parse(data.Name).Armor != null;
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
