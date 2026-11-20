using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Combat;
using Assets.Scripts.Flight.Combat.Teams;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // Something a turret group on automatic aim can shoot at: a vehicle or a target of the game's own registry.
    internal sealed class AutoTarget
    {
        public Target Source;
        public AircraftScript Aircraft;   // a player's vehicle (null for the game's NPCs)
        public string Name;
        public bool Air;                  // flying (as far as it can be told, see AutoAim.IsFlying)
        public Vector3 Position;
        public Vector3 Velocity;
        public float Distance;            // from where it was scanned
    }

    // What a shell does once it leaves the barrel, in the numbers the aim needs.
    internal struct ShellModel
    {
        public Vector3 Muzzle;     // where it starts
        public Vector3 Carrier;    // the vehicle's own velocity, which the shell has too
        public float Speed;        // muzzle velocity, m/s
        public float Drag;         // the shell's linear damping (the game's rigidbody)
        public float Lifetime;     // seconds before it is gone
    }

    internal struct AimSolution
    {
        public bool Ok;
        public Vector3 Direction;    // the barrel's world direction
        public float Range;          // from the muzzle to the point the barrel line is aimed at (the virtual aim point)
        public float FlightTime;     // seconds the shell takes to get to the target
        public Vector3 Intercept;    // where the target will be when the shell arrives
        public float DropDegrees;    // how far above the line of sight the barrel points
    }

    // Automatic aim for a turret group: which target to shoot at, and where to point the barrel to hit it.
    //
    // The targets come from the game's own registry (FlightSceneScript.TargetRegistry): the players' vehicles, the game's NPC aircraft, ground
    // vehicles and ships. The game says what an NPC is (air or ground) but a player's vehicle is "air and ground", so for those the mod decides by
    // the height above the ground and the speed (there are no teams to go by in a free flight).
    //
    // The aim solves the shell's flight like the game's own physics (gravity, then the drag of the shell, every step, with the vehicle's velocity
    // added to the muzzle velocity as CannonScript.Fire does): a point is aimed at, the shell is flown to where the line of sight to the target's
    // future position is crossed, the miss is added to the aim point, and that is repeated until it hits (the target's future position
    // depends on the flight time, which depends on the aim, so that is iterated as well).
    internal static class AutoAim
    {
        private const float StepSeconds = 0.04f;       // the flight is flown in steps this long (the result does not change by more than a fraction of a meter)
        private const float MaxFlightSeconds = 15f;
        private const int OcclusionMask = 1048576;        // layer 20, the one the game's own NPC targeting uses to tell whether something is hidden
        private const float NearestMeters = 15f;          // nothing closer than this is a target (it would be the vehicle's own surroundings)

        // ---- Targets -----------------------------------------------------------------------------------------------------------

        // Reads where a registry target is and how it moves; false when it is gone.
        internal static bool Read(Target target, AircraftScript own, out Vector3 position, out Vector3 velocity, out AircraftScript aircraft)
        {
            position = Vector3.zero;
            velocity = Vector3.zero;
            aircraft = null;
            if (target == null || target.IsDead) return false;
            FlightScenePlayer player = target.Player;
            aircraft = player != null ? player.Aircraft : null;
            if (aircraft != null)
            {
                Transform center = aircraft.OrientedCenterOfMassRigidBodies;
                position = center != null ? center.position : target.Position;
                velocity = aircraft.Velocity;
            }
            else
            {
                position = target.Position;
                velocity = target.Velocity;
            }
            return !float.IsNaN(position.x) && !float.IsNaN(position.y) && !float.IsNaN(position.z);
        }

        // Is a player's vehicle flying? The game does not say. Higher than AirMinimumAgl above the ground, or fast and clear of it.
        internal static bool IsFlying(AircraftScript aircraft, Vector3 velocity)
        {
            float agl = aircraft.AltitudeAgl;
            float speed = velocity.magnitude;
            return agl > ArmorFragmentsPlugin.TurretAutoAirAgl.Value || (speed > ArmorFragmentsPlugin.TurretAutoAirSpeed.Value && agl > 3f);
        }

        // Every target the mode allows (ground, air or both), within the range, from 'from', nearest first. The vehicle itself never counts.
        internal static void Scan(AircraftScript own, Vector3 from, TurretAim.GroupAuto mode, List<AutoTarget> output)
        {
            output.Clear();
            FlightSceneScript scene = FlightSceneScript.Instance;
            if (scene == null || scene.TargetRegistry == null) return;
            float range = ArmorFragmentsPlugin.TurretAutoRange.Value;
            bool skipFriendly = ArmorFragmentsPlugin.TurretAutoSkipFriendly.Value;
            bool clearView = ArmorFragmentsPlugin.TurretAutoClearView.Value;
            FlightScenePlayer local = scene.LocalPlayer;
            ushort myTeam = local != null ? local.TeamId : (ushort)0;
            float sea = 0f;
            try { sea = GameWorld.Instance != null ? GameWorld.Instance.FloatingOriginSeaLevel.GetValueOrDefault() : 0f; } catch (Exception) { }

            IReadOnlyList<Target> targets = scene.TargetRegistry.Targets;
            for (int i = 0; i < targets.Count; i++)
            {
                Target target = targets[i];
                try
                {
                    if (target == null || !target.Visible) continue;
                    if (((int)target.TargetType & (int)TargetType.AirAndGround) == 0) continue;   // information and laser targets are not things to shoot
                    if (target.Player != null && target.Player == local) continue;
                    Vector3 position, velocity;
                    AircraftScript aircraft;
                    if (!Read(target, own, out position, out velocity, out aircraft)) continue;
                    if (aircraft != null && aircraft == own) continue;
                    if (skipFriendly && scene.TeamAggressionManager != null &&
                        scene.TeamAggressionManager.GetAggressionLevel(myTeam, target.TeamId) == AggressionLevel.Friendly) continue;
                    if (position.y - sea < 0f) continue;   // under the water: a wreck

                    float distance = (position - from).magnitude;
                    if (distance > range || distance < NearestMeters) continue;

                    // What it is: the game says for its own targets; a player's vehicle is "air and ground" so it is judged by height and speed.
                    bool air = aircraft != null && target.TargetType == TargetType.AirAndGround ? IsFlying(aircraft, velocity) : target.TargetType == TargetType.Air;
                    if (air ? (mode & TurretAim.GroupAuto.Air) == 0 : (mode & TurretAim.GroupAuto.Ground) == 0) continue;

                    if (clearView && distance > 30f && Physics.Raycast(from, (position - from) / distance, distance - 10f, OcclusionMask, QueryTriggerInteraction.Ignore)) continue;

                    output.Add(new AutoTarget { Source = target, Aircraft = aircraft, Name = string.IsNullOrEmpty(target.Name) ? (air ? "air" : "ground") : target.Name, Air = air, Position = position, Velocity = velocity, Distance = distance });
                }
                catch (Exception)
                {
                    // a target that is half unloaded is not worth a log line every scan
                }
            }
            output.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        // ---- The shell ---------------------------------------------------------------------------------------------------------

        private static readonly System.Reflection.FieldInfo PrefabField = AccessTools.Field(typeof(CannonScript), "_projectilePrefab");
        private static readonly Dictionary<CannonScript, float> dragOf = new Dictionary<CannonScript, float>();
        private static readonly Dictionary<CannonScript, float> lifetimeOf = new Dictionary<CannonScript, float>();

        internal static void Forget()
        {
            dragOf.Clear();
            lifetimeOf.Clear();
        }

        internal static ShellModel ShellOf(CannonScript cannon, Vector3 carrier)
        {
            float drag;
            if (!dragOf.TryGetValue(cannon, out drag))
            {
                GameObject prefab = PrefabField != null ? PrefabField.GetValue(cannon) as GameObject : null;
                Rigidbody body = prefab != null ? prefab.GetComponent<Rigidbody>() : null;
                dragOf[cannon] = drag = body != null ? body.linearDamping : 0f;
            }
            float life;
            if (!lifetimeOf.TryGetValue(cannon, out life))
            {
                life = MaxFlightSeconds;
                PartScript part = cannon.PartScript;
                if (part != null && part.Part != null)
                {
                    foreach (PartModifierData modifier in part.Part.Modifiers)
                    {
                        CannonData data = modifier as CannonData;
                        if (data != null)
                        {
                            if (data.ProjectileLifetime > 0f) life = Mathf.Min(life, data.ProjectileLifetime);
                            break;
                        }
                    }
                }
                lifetimeOf[cannon] = life;
            }
            return new ShellModel { Muzzle = cannon.TipPosition, Carrier = carrier, Speed = cannon.ProjectileVelocity, Drag = drag, Lifetime = life };
        }

        // ---- Where to point --------------------------------------------------------------------------------------------------

        // The barrel direction that puts the shell on a target at 'target' moving at 'targetVelocity' (lead: ahead of where it moves; drop: against
        // gravity and drag). Not Ok when the shell can't get there (out of reach or out of time).
        internal static AimSolution Solve(ShellModel shell, Vector3 target, Vector3 targetVelocity, bool lead, bool drop)
        {
            AimSolution result = default(AimSolution);
            if (shell.Speed < 1f) return result;
            Vector3 gravity = drop ? Physics.gravity : Vector3.zero;
            float drag = drop ? shell.Drag : 0f;
            Vector3 targetMotion = lead ? targetVelocity : Vector3.zero;
            Vector3 carrier = lead ? shell.Carrier : Vector3.zero;

            float time = (target - shell.Muzzle).magnitude / shell.Speed;
            Vector3 aimPoint = target;
            Vector3 point = target;
            bool reached = false;
            for (int outer = 0; outer < 8; outer++)
            {
                point = target + targetMotion * time;
                Vector3 toPoint = point - shell.Muzzle;
                float range = toPoint.magnitude;
                if (range < 1f) return result;
                Vector3 along = toPoint / range;

                // Where must the barrel point so that the shell is on 'point' when it gets there? Fly it, add the miss to the aim point, again.
                aimPoint = point;
                float flight = 0f;
                reached = false;
                for (int inner = 0; inner < 8; inner++)
                {
                    Vector3 direction = (aimPoint - shell.Muzzle).normalized;
                    Vector3 at;
                    reached = Fly(shell, carrier, direction, along, range, gravity, drag, out at, out flight);
                    if (!reached) break;
                    Vector3 miss = point - at;
                    aimPoint += miss;
                    if (miss.sqrMagnitude < 0.04f) break;   // within 20 cm
                }
                if (!reached) return result;
                if (Mathf.Abs(flight - time) < 0.01f)
                {
                    time = flight;
                    break;
                }
                time = flight;
            }
            if (!reached) return result;

            Vector3 final = aimPoint - shell.Muzzle;
            result.Ok = true;
            result.Direction = final.normalized;
            result.Range = final.magnitude;
            result.FlightTime = time;
            result.Intercept = point;
            result.DropDegrees = Vector3.Angle(result.Direction, (point - shell.Muzzle).normalized);
            return result;
        }

        // Flies a shell launched along 'direction' and returns where it crosses the plane 'range' away along 'along', and when.
        private static bool Fly(ShellModel shell, Vector3 carrier, Vector3 direction, Vector3 along, float range, Vector3 gravity, float drag,
                                out Vector3 at, out float time)
        {
            Vector3 position = shell.Muzzle;
            Vector3 velocity = carrier + direction * shell.Speed;
            float step = StepSeconds;
            float gameStep = Mathf.Max(Time.fixedDeltaTime, 0.005f);
            // The game multiplies the velocity by (1 - drag x step) every physics step; over a longer step of ours that is this much.
            float keep = Mathf.Pow(Mathf.Clamp01(1f - drag * gameStep), step / gameStep);
            int steps = Mathf.CeilToInt(Mathf.Min(shell.Lifetime, MaxFlightSeconds) / step);
            float before = 0f;
            for (int i = 0; i < steps; i++)
            {
                Vector3 previous = velocity;
                velocity += gravity * step;
                velocity *= keep;
                Vector3 next = position + (previous + velocity) * (0.5f * step);
                float now = Vector3.Dot(next - shell.Muzzle, along);
                if (now >= range)
                {
                    float fraction = (range - before) / Mathf.Max(now - before, 1e-5f);
                    at = position + (next - position) * Mathf.Clamp01(fraction);
                    time = (i + Mathf.Clamp01(fraction)) * step;
                    return true;
                }
                position = next;
                before = now;
                if (Vector3.Dot(velocity, along) <= 0f) break;   // it has turned back: it will not get there
            }
            at = position;
            time = steps * step;
            return false;
        }
    }
}
