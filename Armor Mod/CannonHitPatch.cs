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
        //
        // __state tells the Postfix whether this impact was resolved by our model, in which case the
        // physical shell has done its job and must go (otherwise the game's physics keeps bouncing it
        // around, which looks like a ricochet no matter what the model decided).
        private static void Prefix(CannonProjectileScript __instance, Collision collision,
                                   Vector3 ____previousFrameVelocity, CannonScript ____cannonScript,
                                   out bool __state)
        {
            __state = false;
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
            ShellInfo shell = BuildShell(__instance, ____cannonScript, cannon, speed);

            ArmorFragmentsPlugin.Log.LogInfo(
                "Shell hit part '" + part.Part.Name + "' at " +
                speed.ToString("F0") + " m/s, angle " + angle.ToString("F0") + " deg, energy " +
                EnergyModel.Kilojoules(shell.Energy) + " (mass " + shell.Mass.ToString("F2") +
                ", caliber " + shell.CaliberMm.ToString("F0") + " mm, " + Ammunition.NameOf(shell.Kind) +
                (shell.HasFuse ? ", fuse " + shell.FuseLeft.ToString("F2") + " m" : "") +
                (shell.Explosive ? ", filler " + (shell.FillerFraction * 100f).ToString("F1") + " %" : "") + ")");

            if (!Handled.Add(__instance.GetInstanceID()))
            {
                ArmorFragmentsPlugin.Log.LogInfo("Shell already handled, skipping fragments");
                return; // once per shell
            }

            // A shot of yours that hit another vehicle: the impact camera may show it.
            try
            {
                ImpactCamera.OnShellImpact(part, contact.point, direction, __instance.Owner);
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Impact camera trigger failed: " + e.Message);
            }

            // The rays drawn for this impact travel with the part that was hit.
            RayDrawer.Anchor = part.transform;
            RayDrawer.FallbackAnchor = ImpactCamera.CockpitOf(part.Aircraft);

            // What the fragments do to each part, for the impact camera's X-ray (only if it is open).
            Dictionary<PartScript, DamageRecord> impactSink = ImpactCamera.IsActive ? new Dictionary<PartScript, DamageRecord>() : null;
            FragmentSpawner.ImpactSink = impactSink;
            float impactTime = Time.time;

            try
            {
                PartPhysics.BeginShot();   // part densities and colliders are remembered per shot
                FragmentSpawner.Attacker = __instance.Owner != null && __instance.Owner.NetworkAircraft != null
                    ? __instance.Owner.NetworkAircraft.PlayerId : (int?)null;

                if (armor == null)
                {
                    if (shell.Kind == AmmoKind.HE)
                    {
                        // An explosive shell without a fuse bursts on the first thing it touches.
                        BurstOnPart(shell, contact.point, direction, contact.normal, angle);
                    }
                    else
                    {
                        // Not armor: the game stops the shell here (damage, explosion...).
                        // A ballistic ray carries on in its place, until it meets armor (or, with a fuse, bursts).
                        ContinueAsBallisticRay(part, cannon, shell, contact.point, direction);
                    }
                    __state = true;   // the ray (or the burst) replaces the shell
                    return;
                }

                ImpactOutcome outcome = FragmentSpawner.Spawn(part, armor, cannon, shell, contact.point, direction,
                    contact.normal, angle, ArmorFragmentsPlugin.ApplyDamage.Value);

                // Blocked or penetrated: the energy has been accounted for. Only a ricochet leaves
                // the physical shell flying (it really does bounce away).
                __state = outcome != ImpactOutcome.Ricochet;
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Fragment spawn failed: " + e);
            }
            finally
            {
                FragmentSpawner.Attacker = null;
                RayDrawer.Anchor = null;
                RayDrawer.FallbackAnchor = null;
                FragmentSpawner.ImpactSink = null;
                if (impactSink != null) ImpactCamera.OnImpactResolved(impactSink, impactTime);
            }
        }

        // After the game has handled the collision (damage, explosion if it is an explosive shell):
        // a shell that is still alive and was resolved by our model is stopped dead and retired by the
        // game's own end-of-life path (the self-destruct timer), so it cannot bounce on.
        private static void Postfix(CannonProjectileScript __instance, bool __state)
        {
            if (!__state || __instance == null || __instance.IsDead || __instance.HasExploded) return;

            Rigidbody body = __instance.Rigidbody;
            if (body != null)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            __instance.SelfDestructTimer = 0f;
        }

        // The game keeps every mass at 1/100 of its real value (the designer divides by 0.01 to show
        // kilograms, and the cannon sets a shell's mass as volume x density x 0.01).
        private const float GameMassScale = 0.01f;

        // Mass: the shell's own Rigidbody (the game sets it from the cannon's projectile volume).
        // Caliber: from the cannon that fired it; if that cannon is gone, from the shell's own size
        // (the game scales the shell to half the cannon's diameter, and caliber = scale x 1000 mm).
        private static ShellInfo BuildShell(CannonProjectileScript projectile, CannonScript cannonScript,
                                            CannonTag cannonTag, float speed)
        {
            Rigidbody body = projectile.GetComponent<Rigidbody>();

            float caliber = projectile.transform.localScale.x * 1000f;
            bool explosive = ProjectileIsExplosive(projectile);   // if the cannon is gone, from the shell itself
            if (cannonScript != null && cannonScript.PartScript != null)
            {
                foreach (PartModifierData modifier in cannonScript.PartScript.Part.Modifiers)
                {
                    CannonData data = modifier as CannonData;
                    if (data != null)
                    {
                        caliber = data.CaliberInMilimeters;
                        // The game's own 'Projectile Type' setting of the cannon: Basic or Explosive.
                        explosive = data.AmmoType == CannonData.ProjectileType.Explosive;
                        break;
                    }
                }
            }

            ShellInfo shell = new ShellInfo
            {
                Mass = body != null ? body.mass / GameMassScale : 1f,   // kilograms
                Speed = speed,
                CaliberMm = caliber,
                Hardness = cannonTag != null ? cannonTag.ShellHardness : ArmorFragmentsPlugin.DefaultShellHardness.Value,
            };
            // Explosive or not, its fuse and its filler (from the cannon's [CAN] tag and the settings).
            Ammunition.Configure(ref shell, explosive, cannonTag);
            return shell;
        }

        private static readonly System.Reflection.FieldInfo ExplosiveField =
            HarmonyLib.AccessTools.Field(typeof(CannonProjectileScript), "_isExplosive");

        // The shell's own flag (set on the projectile prefab that matches the cannon's projectile type).
        private static bool ProjectileIsExplosive(CannonProjectileScript projectile)
        {
            try
            {
                return ExplosiveField != null && (bool)ExplosiveField.GetValue(projectile);
            }
            catch (System.Exception)
            {
                return true;   // unknown: as before this was a choice
            }
        }

        // An HE shell (explosive, no fuse) has touched a part that is not armor: it bursts there, on the surface.
        private static void BurstOnPart(ShellInfo shell, Vector3 point, Vector3 direction, Vector3 normal, float angle)
        {
            LastImpact.ShellEnergy = shell.Energy;
            LastImpact.Absorbed = 0f;
            LastImpact.Residual = 0f;
            LastImpact.ShellAfter = 0f;
            LastImpact.Deflection = 0f;
            LastImpact.Plate = 0;
            LastImpact.ClearCloud();

            // The shell's last meters, drawn as the impact ray; the burst happens when it arrives.
            float lead = BallisticRay.DrawSimpleTrack(point - direction * FragmentSpawner.ShellLeadIn, point,
                                                      shell.Energy, shell.Mass, shell.CaliberMm * 0.001f);
            FragmentSpawner.BurstOnSurface(shell, point, direction, normal, angle, ArmorFragmentsPlugin.ApplyDamage.Value,
                                           false, lead, "it burst on touching a part (no fuse)");
        }

        // The shell has stopped in 'stoppedIn', which is not armor. The ray goes on in the shell's
        // direction, flying through other non-armor parts (paying energy in each, the first one
        // included) and meets the first armor part with the energy it has left, under the same
        // rules (and the same damage) as a real shell that had hit it directly.
        // The first part already took the game's own impact damage; the others take what the shell
        // leaves in them (ShellFlight).
        private static void ContinueAsBallisticRay(PartScript stoppedIn, CannonTag cannon, ShellInfo shell,
                                                   Vector3 from, Vector3 direction)
        {
            ShellFlight.Continue(from, direction, shell, cannon, stoppedIn, null, 0f,
                                 FragmentSpawner.ShellLeadIn, RayKind.Hit,
                                 ArmorFragmentsPlugin.ApplyDamage.Value, false, 1, false);
        }
    }
}