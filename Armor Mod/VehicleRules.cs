using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using HarmonyLib;

namespace ArmorFragments
{
    // How vehicles die. Armored vehicles (any [ARM] part) die through their modules and keep their shape;
    // vehicles without armor (planes, cars) die as in the base game.
    //
    //  - The game's damage explosions are off on armored vehicles (GameDamageExplosions = false): it blows parts up
    //    (BodyScript.ExplodePart) from its "part explodes" roll at each damage stage and on destruction
    //    (OnPartDamageLevelIncreased), from damaged fuel tanks and propellers, and in the chains that spread from
    //    part to part (ExplosionScript); and it blows the whole vehicle up (AircraftScript.QueueExplosion) when a
    //    part of the cockpit's group explodes or the vehicle's damage passes its DamageLimit. Weapons' own
    //    explosions (bombs, missiles, rack cook-offs) are separate and not affected.
    //  - The game kills a vehicle only when it loses parts: AircraftStructureChanged -> CheckForCriticalDamage (the
    //    share of parts or wing area left below CriticalDamageThreshold) -> MarkAsCriticallyDamaged, which raises
    //    AircraftKilled (kill credit, kill cam, respawn; sent to the other players). With its damage explosions off,
    //    an armored vehicle loses no parts, so the mod kills it itself through Kill: an ammo rack exploding is the
    //    kill switch.
    //  - On an armored vehicle only damage modules ([MOD]) and ammo racks ([AMM]) take the mod's damage
    //    (ArmoredStructureDamage = false): the structure only slows shells and fragments down
    //    (FragmentSpawner.Deposit). Vehicles without armor take it on every part.
    internal static class VehicleRules
    {
        private static readonly Dictionary<AircraftScript, bool> Armored = new Dictionary<AircraftScript, bool>();

        private static readonly MethodInfo MarkAsCriticallyDamaged =
            AccessTools.Method(typeof(AircraftScript), "MarkAsCriticallyDamaged");

        // May the game blow this vehicle (or one of its parts) up because of damage?
        public static bool GameMayExplode(AircraftScript aircraft)
        {
            if (AmmoRacks.IsKnockedOut(aircraft)) return false;
            return !IsArmored(aircraft) || ArmorFragmentsPlugin.GameDamageExplosions.Value;
        }

        // The game's own death for this vehicle (only in its owner's game): kill credit, kill cam, respawn, and the
        // other players are told. It doesn't break or explode anything.
        public static void Kill(AircraftScript aircraft, string reason)
        {
            if (aircraft == null || aircraft.RemoteAircraft) return;
            try
            {
                if (MarkAsCriticallyDamaged == null)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Can't kill the vehicle (" + reason + "): AircraftScript.MarkAsCriticallyDamaged not found");
                    return;
                }
                MarkAsCriticallyDamaged.Invoke(aircraft, null);
                ArmorFragmentsPlugin.Log.LogInfo("Vehicle killed: " + reason);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Can't kill the vehicle (" + reason + "): " + e.Message);
            }
        }

        // Does the mod's damage (shells, fragments) go into this part?
        public static bool TakesModDamage(PartScript part, PartTags tags)
        {
            if (tags.Armor != null) return false;
            if (tags.Module != null || tags.AmmoRack != null) return true;
            return ArmorFragmentsPlugin.ArmoredStructureDamage.Value || !IsArmored(part.Aircraft);
        }

        // A vehicle with at least one [ARM] part. Part names don't change in flight, so it is worked out once.
        public static bool IsArmored(AircraftScript aircraft)
        {
            if (aircraft == null) return false;
            bool armored;
            if (Armored.TryGetValue(aircraft, out armored)) return armored;

            armored = false;
            if (aircraft.Aircraft != null && aircraft.Aircraft.Assembly != null)
            {
                foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
                {
                    if (data == null || TagCodec.Parse(data.Name).Armor == null) continue;
                    armored = true;
                    break;
                }
            }

            if (Armored.Count > 64) Forget();
            Armored[aircraft] = armored;
            return armored;
        }

        private static void Forget()
        {
            List<AircraftScript> gone = new List<AircraftScript>();
            foreach (AircraftScript key in Armored.Keys)
                if (key == null) gone.Add(key);
            foreach (AircraftScript key in gone) Armored.Remove(key);
            if (Armored.Count > 64) Armored.Clear();
        }
    }
}
