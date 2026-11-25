using System;
using Assets.Scripts.Craft.Parts.Modifiers;
using HarmonyLib;

namespace ArmorFragments
{
    // A rotator or hinge with an [SPD s=] tag turns at that many degrees per second. The game sets a rotator's top speed when its joint is
    // set up (JointRotatorScript.SetupJoint: _speed = Speed x Speed x the part's MaxSpeed); this puts the tag's speed there instead. Everything
    // that slows a rotator afterwards (damage, the crew's aim efficiency) still applies on top of it.
    [HarmonyPatch(typeof(JointRotatorScript), "SetupJoint")]
    internal static class RotatorSpeedPatch
    {
        private static readonly AccessTools.FieldRef<JointRotatorScript, float> Speed =
            AccessTools.FieldRefAccess<JointRotatorScript, float>("_speed");

        private static void Postfix(JointRotatorScript __instance)
        {
            try
            {
                if (__instance == null || __instance.PartScript == null || __instance.PartScript.Part == null) return;
                SpeedTag tag = TagCodec.Parse(__instance.PartScript.Part.Name).Speed;
                if (tag == null) return;
                Speed(__instance) = Math.Max(0f, tag.DegreesPerSecond);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Rotator speed: couldn't set the speed of a rotator: " + e.Message);
            }
        }
    }

    internal static class RotatorSpeed
    {
        // The speed (degrees per second) the game gives a rotator from its own Speed slider.
        public static float StockDegreesPerSecond(JointRotatorData data)
        {
            return data == null ? 0f : data.Speed * data.Speed * data.MaxSpeed;
        }

        // A hinge can't turn all the way round, so it gets no Unlimited button: its part type says hinge, or its Range slider stops short of 180.
        public static bool IsHinge(Assets.Scripts.Craft.Parts.PartData part, JointRotatorData data)
        {
            string type = part != null && part.PartType != null ? part.PartType.Name ?? "" : "";
            if (type.IndexOf("hinge", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return data != null && data.MaxRange > 0 && data.MaxRange < 180;
        }
    }
}
