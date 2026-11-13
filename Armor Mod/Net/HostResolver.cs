using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Flight;
using Assets.Scripts.Multiplayer;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // The host works out shells fired by players who do not run the mod.
    //
    // A cannon shell only exists in the shooter's game. The other games get a muzzle flash, and the
    // server gets one damage message when the shell hits something. For a hit on an armor part the host:
    //   1. keeps the message instead of passing it on (the game's damage to armor is ignored);
    //   2. looks for the shot that caused it. Every muzzle flash seen on a remote cannon was recorded
    //      with the cannon's pose and velocity; each recorded shot is flown with the game's own physics
    //      (initial velocity + gravity + the shell's drag) and the one that passes closest to the hit wins;
    //   3. runs the ordinary armor / ricochet / fragment model with that shell, as if it had hit locally.
    // Players who run the mod say so (NetSync) and calculate their own shots: the host leaves them alone.
    // A cannon without a [CAN] tag, or a hit no cannon shot explains (a machine gun), does no damage to armor.
    internal static class HostResolver
    {
        private const float MatchWait = 1.0f;       // the muzzle flash can arrive after the hit message
        private const float ShotMemory = 20f;       // seconds a recorded shot stays available
        private const int MaxShots = 300;
        private const float StepSeconds = 0.02f;
        private const float MaxFlightSeconds = 20f;

        private sealed class Shot
        {
            public int PlayerId;
            public PartScript CannonPart;
            public float Time;
            public Vector3 Origin;
            public Vector3 Velocity;
            public float Drag;
            public float FlightSeconds;
            public float ShooterSpeed;
        }

        private sealed class Hit
        {
            public int Attacker;
            public PartScript Target;
            public ArmorTag Armor;
            public Vector3 Point;
            public Vector3 Normal;
            public float Time;
        }

        private static readonly List<Shot> Shots = new List<Shot>();
        private static readonly List<Hit> Hits = new List<Hit>();

        private static readonly System.Reflection.FieldInfo SpawnPointField =
            AccessTools.Field(typeof(CannonScript), "_projectileSpawnPoint");
        private static readonly System.Reflection.FieldInfo PrefabField =
            AccessTools.Field(typeof(CannonScript), "_projectilePrefab");

        public static bool Active
        {
            get { return ArmorFragmentsPlugin.HostResolvesShots.Value && NetSync.IsHost; }
        }

        // ---- Recording shots -------------------------------------------------------

        // A remote cannon showed its muzzle flash: it just fired.
        public static void OnMuzzleFlash(CannonScript cannon)
        {
            if (!Active) return;

            PartScript part = cannon.PartScript;
            AircraftScript aircraft = part != null ? part.Aircraft : null;
            if (aircraft == null || !aircraft.RemoteAircraft || aircraft.NetworkAircraft == null) return;

            CannonData data = DataOf(part);
            if (data == null) return;

            Transform spawn = SpawnPointField != null ? SpawnPointField.GetValue(cannon) as Transform : null;
            Vector3 forward = cannon.transform.forward;

            Shot shot = new Shot
            {
                PlayerId = aircraft.NetworkAircraft.PlayerId,
                CannonPart = part,
                Time = UnityEngine.Time.time,
                Origin = spawn != null ? spawn.position : cannon.transform.position,
                // As in the game's CannonScript.Fire: the cannon's own velocity plus the muzzle velocity.
                Velocity = aircraft.Velocity + forward * data.ProjectileVelocity,
                Drag = DragOf(cannon),
                FlightSeconds = Mathf.Min(data.ProjectileLifetime > 0f ? data.ProjectileLifetime : MaxFlightSeconds, MaxFlightSeconds),
                ShooterSpeed = aircraft.Velocity.magnitude,
            };
            Shots.Add(shot);
            if (Shots.Count > MaxShots) Shots.RemoveRange(0, Shots.Count - MaxShots);
        }

        private static float DragOf(CannonScript cannon)
        {
            GameObject prefab = PrefabField != null ? PrefabField.GetValue(cannon) as GameObject : null;
            Rigidbody body = prefab != null ? prefab.GetComponent<Rigidbody>() : null;
            return body != null ? body.linearDamping : 0f;
        }

        private static CannonData DataOf(PartScript part)
        {
            if (part == null || part.Part == null) return null;
            foreach (PartModifierData modifier in part.Part.Modifiers)
            {
                CannonData data = modifier as CannonData;
                if (data != null) return data;
            }
            return null;
        }

        // ---- Hit messages ----------------------------------------------------------

        // The server received "attacker hit this part". Returns true to pass the message on as the game
        // would, false to hold it back.
        public static bool OnDamageMessage(NetworkAircraftScript targetAircraft, int? attacker, int partId,
                                           Vector3 localPosition, Vector3 localNormal)
        {
            if (!Active || FlightSceneScript.IsPeacefulMode) return true;

            try
            {
                PartData data = targetAircraft.AircraftScript != null
                    ? targetAircraft.AircraftScript.GetPartById(partId) : null;
                if (data == null || data.PartScript == null) return true;

                // Phase 1: only a first contact on an armor part is worked out.
                ArmorTag armor = TagCodec.Parse(data.Name).Armor;
                if (armor == null) return true;

                // The game's own damage to armor is dropped (unless the setting says otherwise).
                bool passOn = !ArmorFragmentsPlugin.IgnoreGameDamageOnArmor.Value;

                // Unknown attacker, or a player who runs the mod: nothing to work out here.
                if (!attacker.HasValue || NetSync.IsModded(attacker.Value)) return passOn;

                Transform part = data.PartScript.transform;
                Hits.Add(new Hit
                {
                    Attacker = attacker.Value,
                    Target = data.PartScript,
                    Armor = armor,
                    Point = part.TransformPoint(localPosition),
                    Normal = part.TransformDirection(localNormal).normalized,
                    Time = UnityEngine.Time.time,
                });
                return passOn;
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Host could not read a damage message: " + e.Message);
                return true;
            }
        }

        // ---- Matching and resolving ------------------------------------------------

        // Called every frame by the plugin.
        public static void Tick()
        {
            float now = UnityEngine.Time.time;
            Shots.RemoveAll(s => now - s.Time > ShotMemory || s.CannonPart == null);

            for (int i = Hits.Count - 1; i >= 0; i--)
            {
                Hit hit = Hits[i];
                if (hit.Target == null)
                {
                    Hits.RemoveAt(i);
                    continue;
                }

                Shot shot;
                Vector3 velocity;
                if (Match(hit, out shot, out velocity))
                {
                    Hits.RemoveAt(i);
                    Shots.Remove(shot);   // one shell, one hit
                    Resolve(hit, shot, velocity);
                }
                else if (now - hit.Time > MatchWait)
                {
                    Hits.RemoveAt(i);
                    ArmorFragmentsPlugin.Log.LogInfo(
                        "[HOST] A hit on '" + hit.Target.Part.Name + "' by player " + hit.Attacker +
                        " matches no cannon shot (machine gun or unknown): the armor takes no damage");
                }
            }
        }

        // The recorded shot of this player that passes closest to the hit.
        private static bool Match(Hit hit, out Shot best, out Vector3 velocityThere)
        {
            best = null;
            velocityThere = Vector3.zero;
            float bestDistance = float.MaxValue;

            foreach (Shot shot in Shots)
            {
                if (shot.PlayerId != hit.Attacker) continue;

                Vector3 velocity;
                float distance = Fly(shot, hit.Point, out velocity);
                // The cannon's pose reaches the host a little late (and the shooter keeps moving), so the
                // allowed miss grows with the shooter's speed.
                float tolerance = 30f + 0.4f * shot.ShooterSpeed;
                if (distance < tolerance && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = shot;
                    velocityThere = velocity;
                }
            }
            return best != null;
        }

        // Flies the shell like the game's physics would (gravity, then drag, every step) and returns how
        // close it comes to 'target', with its velocity at that moment.
        private static float Fly(Shot shot, Vector3 target, out Vector3 velocityAtClosest)
        {
            Vector3 position = shot.Origin;
            Vector3 velocity = shot.Velocity;
            Vector3 gravity = Physics.gravity;
            float damping = shot.Drag;

            float closest = (position - target).sqrMagnitude;
            velocityAtClosest = velocity;

            int steps = Mathf.CeilToInt(shot.FlightSeconds / StepSeconds);
            for (int i = 0; i < steps; i++)
            {
                velocity += gravity * StepSeconds;
                velocity *= Mathf.Clamp01(1f - damping * StepSeconds);
                position += velocity * StepSeconds;

                float d = (position - target).sqrMagnitude;
                if (d < closest)
                {
                    closest = d;
                    velocityAtClosest = velocity;
                }
                else if (Vector3.Dot(velocity, target - position) < 0f)
                {
                    break;   // past the hit and moving away
                }
            }
            return Mathf.Sqrt(closest);
        }

        private static void Resolve(Hit hit, Shot shot, Vector3 velocity)
        {
            CannonData data = DataOf(shot.CannonPart);
            CannonTag cannon = TagCodec.Parse(shot.CannonPart.Part.Name).Cannon;

            if (data == null || cannon == null)
            {
                ArmorFragmentsPlugin.Log.LogInfo(
                    "[HOST] Player " + hit.Attacker + "'s cannon '" + shot.CannonPart.Part.Name +
                    "' has no [CAN] tag: it does no damage to armor");
                return;
            }
            if (velocity.sqrMagnitude < 1f) return;

            ShellInfo shell = new ShellInfo
            {
                Mass = data.ProjectileVolume * 11342f,   // kg: the game's volume x lead density (it then stores it at 1/100)
                Speed = velocity.magnitude,
                CaliberMm = data.CaliberInMilimeters,
                Hardness = cannon.ShellHardness,
                Explosive = data.AmmoType == CannonData.ProjectileType.Explosive,
            };
            Vector3 direction = velocity.normalized;
            float angle = Vector3.Angle(direction, hit.Normal);
            angle = Mathf.Min(angle, 180f - angle);

            ArmorFragmentsPlugin.Log.LogInfo(
                "[HOST] Player " + hit.Attacker + "'s shell hit '" + hit.Target.Part.Name + "' at " +
                shell.Speed.ToString("F0") + " m/s, angle " + angle.ToString("F0") + " deg, energy " +
                EnergyModel.Kilojoules(shell.Energy) + " (caliber " + shell.CaliberMm.ToString("F0") + " mm)");

            try
            {
                PartPhysics.BeginShot();
                FragmentSpawner.Attacker = hit.Attacker;
                FragmentSpawner.Spawn(hit.Target, hit.Armor, cannon, shell, hit.Point, direction,
                                      hit.Normal, angle, ArmorFragmentsPlugin.ApplyDamage.Value);
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("[HOST] Could not work out the shell: " + e);
            }
            finally
            {
                FragmentSpawner.Attacker = null;
            }
        }
    }

    // A remote cannon shows its muzzle flash when it fires: that is how the host learns of a shot.
    [HarmonyPatch(typeof(CannonScript), "PlayMuzzleFlash")]
    internal static class MuzzleFlashPatch
    {
        private static void Postfix(CannonScript __instance)
        {
            try
            {
                HostResolver.OnMuzzleFlash(__instance);
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Could not record a shot: " + e.Message);
            }
        }
    }

    // The server's handling of "a player hit a part of this aircraft". The arguments are used by
    // position because the game does not keep their names.
    [HarmonyPatch(typeof(NetworkAircraftScript), "RpcLogic___RpcDamageRemotePartServer___2803290691")]
    internal static class HostDamageMessagePatch
    {
        // Returning false holds the message back (the owner of the aircraft never gets it).
        private static bool Prefix(NetworkAircraftScript __instance, int? __0, int __1, float __2,
                                   Vector3 __3, Vector3 __4)
        {
            return HostResolver.OnDamageMessage(__instance, __0, __1, __3, __4);
        }
    }
}
