using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using HarmonyLib;
using UnityEngine;


namespace ArmorFragments
{
    [HarmonyPatch(typeof(CannonProjectileScript), "OnCollisionEnter")]
    internal static class CannonHitPatch
    {
        private static readonly HashSet<int> Handled = new HashSet<int>();

        // Harmony fills this argument with the shell's private field
        // _previousFrameVelocity: three underscores + the field's name.
        private static void Prefix(CannonProjectileScript __instance, Collision collision, Vector3 ____previousFrameVelocity)
        {
            if (collision.contactCount == 0) return;

            ContactPoint contact = collision.GetContact(0);
            PartScript part = contact.otherCollider.GetComponentInParent<PartScript>();
            if (part == null) return;
            if (part.Part.Name == null || !part.Part.Name.StartsWith("ARM_")) return;


            Vector3 direction = ____previousFrameVelocity.normalized;
            float speed = ____previousFrameVelocity.magnitude;

            // 0 degrees = head-on, 90 degrees = grazing.
            float angle = Vector3.Angle(direction, contact.normal);
            angle = Mathf.Min(angle, 180f - angle);

            ArmorFragmentsPlugin.Log.LogInfo(
                "Shell hit part '" + part.Part.Name + "' at " +
                speed.ToString("F0") + " m/s, angle " + angle.ToString("F0") + " deg");

            if (!Handled.Add(__instance.GetInstanceID()))
            {
                ArmorFragmentsPlugin.Log.LogInfo("Shell already handled, skipping fragments");
                return; // once per shell
            }

            ArmorFragmentsPlugin.Log.LogInfo("Calling FragmentSpawner.Spawn");
            try
            {
                FragmentSpawner.Spawn(part, contact.point, direction, false);
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Fragment spawn failed: " + e);
            }
        }
    }
}