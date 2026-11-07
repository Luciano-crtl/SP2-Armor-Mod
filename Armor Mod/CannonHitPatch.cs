using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
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
        // ____cannonScript is the cannon that fired this shell (also a private field).
        private static void Prefix(CannonProjectileScript __instance, Collision collision,
                                   Vector3 ____previousFrameVelocity, CannonScript ____cannonScript)
        {
            if (collision.contactCount == 0) return;

            ContactPoint contact = collision.GetContact(0);
            PartScript part = contact.otherCollider.GetComponentInParent<PartScript>();
            if (part == null) return;

            // Only parts whose name carries an [ARM ...] tag act as armor.
            ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;

            // The firing cannon's [CAN ...] tag, if it has one (it may be destroyed already).
            CannonTag cannon = null;
            if (____cannonScript != null && ____cannonScript.PartScript != null)
                cannon = TagCodec.Parse(____cannonScript.PartScript.Part.Name).Cannon;

            Vector3 direction = ____previousFrameVelocity.normalized;
            float speed = ____previousFrameVelocity.magnitude;

            // 0 degrees = head-on, 90 degrees = grazing.
            float angle = Vector3.Angle(direction, contact.normal);
            angle = Mathf.Min(angle, 180f - angle);

            // The shell as the energy model sees it: its real mass and speed, and its caliber.
            ShellInfo shell = BuildShell(__instance, ____cannonScript, speed);

            ArmorFragmentsPlugin.Log.LogInfo(
                "Shell hit part '" + part.Part.Name + "' at " +
                speed.ToString("F0") + " m/s, angle " + angle.ToString("F0") + " deg, energy " +
                EnergyModel.Kilojoules(shell.Energy) + " (mass " + shell.Mass.ToString("F2") +
                ", caliber " + shell.CaliberMm.ToString("F0") + " mm)");

            if (!Handled.Add(__instance.GetInstanceID()))
            {
                ArmorFragmentsPlugin.Log.LogInfo("Shell already handled, skipping fragments");
                return; // once per shell
            }

            try
            {
                if (armor == null)
                {
                    // Not armor: the game stops the shell here (damage, explosion...).
                    // A ballistic ray carries on in its place, until it meets armor.
                    ContinueAsBallisticRay(part, cannon, shell, contact.point, direction);
                    return;
                }

                FragmentSpawner.Spawn(part, armor, cannon, shell, contact.point, direction,
                    contact.normal, angle, ArmorFragmentsPlugin.ApplyDamage.Value);
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Fragment spawn failed: " + e);
            }
        }

        // Mass: the shell's own Rigidbody (the game sets it from the cannon's projectile volume).
        // Caliber: from the cannon that fired it; if that cannon is gone, from the shell's own size
        // (the game scales the shell to half the cannon's diameter, and caliber = scale x 1000 mm).
        private static ShellInfo BuildShell(CannonProjectileScript projectile, CannonScript cannon, float speed)
        {
            Rigidbody body = projectile.GetComponent<Rigidbody>();

            float caliber = projectile.transform.localScale.x * 1000f;
            if (cannon != null && cannon.PartScript != null)
            {
                foreach (PartModifierData modifier in cannon.PartScript.Part.Modifiers)
                {
                    CannonData data = modifier as CannonData;
                    if (data != null)
                    {
                        caliber = data.CaliberInMilimeters;
                        break;
                    }
                }
            }

            return new ShellInfo
            {
                Mass = body != null ? body.mass : 1f,
                Speed = speed,
                CaliberMm = caliber,
            };
        }

        // The shell has stopped in 'stoppedIn', which is not armor. The ray goes on in the shell's
        // direction, flying through other non-armor parts, and meets the first armor part with the
        // same rules (and the same damage) as a real shell that had hit it directly.
        private static void ContinueAsBallisticRay(PartScript stoppedIn, CannonTag cannon, ShellInfo shell,
                                                   Vector3 from, Vector3 direction)
        {
            BallisticRay.Result ray = BallisticRay.Trace(
                from, direction, ArmorFragmentsPlugin.BallisticRayRange.Value, stoppedIn);

            if (ray.Armor == null)
            {
                ArmorFragmentsPlugin.Log.LogInfo(
                    "Shell stopped in a non-armor part; its ballistic ray found no armor" +
                    (ray.StoppedBy != null ? " (stopped by " + ray.StoppedBy + ")" : ""));
                return;
            }

            float angle = Vector3.Angle(direction, ray.Hit.normal);
            angle = Mathf.Min(angle, 180f - angle);

            ArmorFragmentsPlugin.Log.LogInfo(
                "Shell stopped in a non-armor part; its ballistic ray passed through " +
                ray.PassedThrough + " parts and met armor '" + ray.Part.Part.Name + "' at " +
                ray.Hit.distance.ToString("F1") + " m");

            // The initial ray: the shell's last meters, the stop point, then on to the armor.
            float leadTime = BallisticRay.DrawInitialRay(new[]
            {
                from - direction * FragmentSpawner.ShellLeadIn, from, ray.Hit.point,
            });
            FragmentSpawner.Spawn(ray.Part, ray.Armor, cannon, shell, ray.Hit.point, direction,
                ray.Hit.normal, angle, ArmorFragmentsPlugin.ApplyDamage.Value,
                simulated: false, leadTime: leadTime);
        }
    }
}