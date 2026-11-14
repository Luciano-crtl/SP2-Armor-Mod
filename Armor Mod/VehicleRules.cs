using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;

namespace ArmorFragments
{
    // How vehicles die: through their damaged modules, keeping their shape.
    //
    //  - The game's damage explosions are off on every vehicle (GameDamageExplosions = false): it blows parts up
    //    (BodyScript.ExplodePart) from its "part explodes" roll at each damage stage and on destruction
    //    (OnPartDamageLevelIncreased), from damaged fuel tanks and propellers, and in the chains that spread from
    //    part to part (ExplosionScript); and it blows the whole vehicle up (AircraftScript.QueueExplosion) when a
    //    part of the cockpit's group explodes or the vehicle's damage passes its DamageLimit. Weapons' own
    //    explosions (bombs, missiles, rack cook-offs) are separate and not affected.
    //  - On a vehicle with armor (any [ARM] part), only damage modules ([MOD]) and ammo racks ([AMM]) take the
    //    mod's damage (ArmoredStructureDamage = false): the structure only slows shells and fragments down
    //    (FragmentSpawner.Deposit). Vehicles without armor (planes) take it on every part.
    internal static class VehicleRules
    {
        private static readonly Dictionary<AircraftScript, bool> Armored = new Dictionary<AircraftScript, bool>();

        // May the game blow this vehicle (or one of its parts) up because of damage?
        public static bool GameMayExplode(AircraftScript aircraft)
        {
            if (AmmoRacks.IsKnockedOut(aircraft)) return false;
            return ArmorFragmentsPlugin.GameDamageExplosions.Value;
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
