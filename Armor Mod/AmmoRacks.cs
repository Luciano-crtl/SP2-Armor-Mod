using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Explosions;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // Ammo racks ([AMM] fuselages) hold the rounds of the cannons in their group ([CAN g=]).
    //
    //  - A cannon whose group has at least one rack fires only while its racks have rounds (the game's own
    //    "Ammo Count" is overwritten every frame, and Fire() is blocked when the group is empty). Racks empty
    //    in order (o=, then part id), each from its top layer down. A cannon with no rack is left alone.
    //  - A rack's capacity is the whole rounds that fit its shape (RackGrid, AmmoRound).
    //  - Damage reaching a rack (PartScript.OnDamaged: the game's shell impact, fragments, explosions,
    //    crashes) may set it on fire if its line crosses a cell that still holds a round. Chance =
    //    1 - e^(-CookOffSensitivity x rise of the damage level), the level being the damage modules' 0..1.
    //    A rack destroyed with rounds in it always catches fire.
    //  - A burning rack can't feed the gun and explodes CookOffDelayMin..Max seconds later, with the
    //    propellant of the rounds still in it (only propellant: explosive shells aren't modeled yet).
    //  - Funky Trees: <group>_rounds (rounds the group can still fire) and <group>_fire (1 while one of its
    //    racks burns).
    //
    // All of it runs only in the game of the aircraft's owner, in flight (a remote copy takes no damage here).
    internal static class AmmoRacks
    {
        public const string DefaultGroup = "Ammo";

        private sealed class Rack
        {
            public PartScript Part;
            public string Label;
            public int Order;          // o=, or int.MaxValue
            public int PartId;
            public Group Group;
            public RackGrid Grid;
            public bool Burning;
            public bool Exploded;
            public float ExplodeAt;
            public PartDamageEffect Fire;
            public int? Attacker;
            public Vector3 LastPosition;
            public float LastLevel;   // damage level last seen (for a part that vanishes)
            public AircraftScript Aircraft;

            // Broken off the vehicle (no longer connected to the main cockpit, though it was at the start):
            // its rounds stay in it (it can still be hit and cook off) but no longer feed the gun.
            public bool StartedConnected;
            public bool BrokenOff;

            // The rounds' weight is added to the part through its mass scale (PartData.MassScale).
            public float BaseMassScale;
            public float BaseLoadedMass;   // game units (kg x 0.01)
            public int MassRounds = -1;    // rounds the mass was last set for

            public bool CanFeed
            {
                get { return !Burning && !Exploded && !BrokenOff && Grid != null && Part != null; }
            }
        }

        private sealed class Group
        {
            public string Name;
            public string Prefix;      // variable prefix
            public readonly List<Rack> Racks = new List<Rack>();
            public readonly List<CannonScript> Cannons = new List<CannonScript>();
            public AmmoRound Round;
            public bool HasRound;
            public bool Mixed;         // its cannons fire rounds of different sizes
            public AircraftVariable RoundsVariable;
            public AircraftVariable FireVariable;
            public AircraftVariable BoomVariable;   // 1 from the moment one of its racks explodes
            public bool Boomed;

            public int Available
            {
                get
                {
                    int total = 0;
                    foreach (Rack rack in Racks)
                        if (rack.CanFeed) total += rack.Grid.Remaining;
                    return total;
                }
            }

            public bool AnyBurning
            {
                get
                {
                    foreach (Rack rack in Racks)
                        if (rack.Burning) return true;
                    return false;
                }
            }
        }

        private sealed class Craft
        {
            public AircraftScript Aircraft;
            public readonly List<Group> Groups = new List<Group>();
            public bool Measured;
            public float GiveUpMeasuringAt;
        }

        private static readonly Dictionary<AircraftScript, Craft> Crafts = new Dictionary<AircraftScript, Craft>();
        private static readonly Dictionary<PartScript, Rack> RackOf = new Dictionary<PartScript, Rack>();
        private static readonly Dictionary<CannonScript, Group> GroupOf = new Dictionary<CannonScript, Group>();

        private static readonly AccessTools.FieldRef<CannonScript, int> CurrentAmmo =
            AccessTools.FieldRefAccess<CannonScript, int>("_currentAmmo");

        // ---- Names ---------------------------------------------------------------------

        public static string GroupName(string tagGroup)
        {
            return string.IsNullOrEmpty(tagGroup) ? DefaultGroup : tagGroup;
        }

        public static string VariablePrefix(string groupName)
        {
            string prefix = ModuleOutputs.Sanitize(groupName);
            return prefix.Length > 0 ? prefix : DefaultGroup;
        }

        // The damage modules' level: 0 = intact, 1 = all health lost.
        public static float DamageLevel(PartScript part)
        {
            return part == null ? 1f : Mathf.Clamp01(part.PartDamage / Mathf.Max(part.MaxHealth, 1f));
        }

        // ---- Registering (VariableSystemScript.RefreshVariables) -----------------------

        public static void Register(VariableSystemScript system)
        {
            if (!ArmorFragmentsPlugin.AmmoRacksEnabled.Value) return;

            AircraftScript aircraft = system.AircraftScript;
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return;

            // Designer (or a remote copy): only make the variable names exist, so they show in the lists.
            if (aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft)
            {
                foreach (string name in RackGroupNames(aircraft))
                {
                    ModuleOutputs.CreateVariable(system, VariablePrefix(name) + "_rounds");
                    ModuleOutputs.CreateVariable(system, VariablePrefix(name) + "_fire");
                    ModuleOutputs.CreateVariable(system, VariablePrefix(name) + "_boom");
                }
                return;
            }

            // In flight the list may be rebuilt more than once: the racks keep their rounds.
            Craft craft;
            if (!Crafts.TryGetValue(aircraft, out craft))
            {
                craft = Build(aircraft);
                if (craft.Groups.Count == 0) return;
                Crafts[aircraft] = craft;
            }

            foreach (Group group in craft.Groups)
            {
                group.RoundsVariable = ModuleOutputs.CreateVariable(system, group.Prefix + "_rounds");
                group.FireVariable = ModuleOutputs.CreateVariable(system, group.Prefix + "_fire");
                group.BoomVariable = ModuleOutputs.CreateVariable(system, group.Prefix + "_boom");
            }
        }

        private static List<string> RackGroupNames(AircraftScript aircraft)
        {
            List<string> names = new List<string>();
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
            {
                if (data == null) continue;
                PartTags tags = TagCodec.Parse(data.Name);
                if (tags.AmmoRack == null || tags.Armor != null) continue;
                string name = GroupName(tags.AmmoRack.Group);
                if (!names.Contains(name)) names.Add(name);
            }
            return names;
        }

        private static Craft Build(AircraftScript aircraft)
        {
            Craft craft = new Craft { Aircraft = aircraft, GiveUpMeasuringAt = Time.time + 5f };
            Dictionary<string, Group> byName = new Dictionary<string, Group>();

            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;
                PartTags tags = TagCodec.Parse(data.Name);

                if (tags.AmmoRack != null)
                {
                    if (tags.Armor != null)
                    {
                        ArmorFragmentsPlugin.Log.LogWarning("'" + data.Name + "' is both armor and an ammo rack: armor " +
                            "ignores damage, so it could never cook off. Its ammo rack tag is ignored.");
                    }
                    else
                    {
                        Group group = GetGroup(byName, craft, tags.AmmoRack.Group);
                        group.Racks.Add(new Rack
                        {
                            Part = part,
                            Label = string.IsNullOrEmpty(tags.Label) ? data.PartType.Name : tags.Label,
                            Order = tags.AmmoRack.Order ?? int.MaxValue,
                            PartId = data.Id,
                            Group = group,
                            Aircraft = aircraft,
                        });
                    }
                }

                if (tags.Cannon != null)
                {
                    CannonScript cannon = part.GetModifier<CannonScript>();
                    AmmoRound round;
                    if (cannon != null && AmmoRound.TryFromPart(part, out round))
                    {
                        Group group = GetGroup(byName, craft, tags.Cannon.Group);
                        group.Cannons.Add(cannon);
                        if (!group.HasRound)
                        {
                            group.Round = round;
                            group.HasRound = true;
                        }
                        else
                        {
                            if (!round.SameSize(group.Round)) group.Mixed = true;
                            if (round.IsBiggerThan(group.Round)) group.Round = round;
                        }
                    }
                }
            }

            // A group without racks leaves its cannons to the game.
            craft.Groups.RemoveAll(g => g.Racks.Count == 0);

            foreach (Group group in craft.Groups)
            {
                group.Racks.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.PartId.CompareTo(b.PartId));
                foreach (Rack rack in group.Racks) RackOf[rack.Part] = rack;
                foreach (CannonScript cannon in group.Cannons) GroupOf[cannon] = group;
            }
            return craft;
        }

        private static Group GetGroup(Dictionary<string, Group> byName, Craft craft, string tagGroup)
        {
            string name = GroupName(tagGroup);
            Group group;
            if (!byName.TryGetValue(name, out group))
            {
                group = new Group { Name = name, Prefix = VariablePrefix(name) };
                byName[name] = group;
                craft.Groups.Add(group);
            }
            return group;
        }

        // ---- Every frame -----------------------------------------------------------------

        public static void Tick()
        {
            if (Jets.Count > 0 || Recent.Count > 0 || PendingFires.Count > 0)
            {
                try
                {
                    UpdateBlasts();
                }
                catch (Exception e)
                {
                    Jets.Clear();
                    ArmorFragmentsPlugin.Log.LogError("Ammo rack fire jet failed: " + e);
                }
            }
            if (Crafts.Count == 0) return;

            List<AircraftScript> gone = null;
            foreach (KeyValuePair<AircraftScript, Craft> pair in Crafts)
            {
                AircraftScript aircraft = pair.Key;
                if (aircraft == null)
                {
                    if (gone == null) gone = new List<AircraftScript>();
                    gone.Add(aircraft);
                    continue;
                }
                if (aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft) continue;

                try
                {
                    Craft craft = pair.Value;
                    if (!craft.Measured && !TryMeasure(craft)) continue;
                    foreach (Group group in craft.Groups) Update(group);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("Ammo racks: " + e);
                }
            }

            if (gone != null)
                foreach (AircraftScript aircraft in gone) Forget(aircraft);
        }

        private static void Update(Group group)
        {
            foreach (Rack rack in group.Racks)
            {
                if (rack.Grid == null) continue;
                UpdateMass(rack);
                if (rack.Exploded) continue;

                if (rack.Part != null)
                {
                    rack.LastPosition = rack.Grid.WorldCenter(rack.Part.transform);
                    if (rack.StartedConnected && !rack.BrokenOff && !rack.Part.ConnectedToMainCockpit)
                    {
                        rack.BrokenOff = true;
                        ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' broke off the vehicle: its " +
                            rack.Grid.Remaining + " rounds no longer feed the gun (they can still cook off)");
                    }
                }

                if (rack.Burning && Time.time >= rack.ExplodeAt)
                {
                    Explode(rack);
                }
                else if (rack.Part == null && rack.Grid.Remaining > 0)
                {
                    if (rack.Burning)
                    {
                        // Already cooking off and the part is gone: nothing left to burn.
                        ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' was destroyed while burning");
                        Explode(rack);
                    }
                    else
                    {
                        // The game removed the part without our damage check seeing it reach full damage.
                        Destroyed(rack, rack.Attacker, 1f - rack.LastLevel, true);
                    }
                }
                else if (rack.Part != null)
                {
                    rack.LastLevel = DamageLevel(rack.Part);
                }
            }

            SyncCannons(group);
            if (group.RoundsVariable != null) group.RoundsVariable.SetValue(group.Available, 0);
            if (group.FireVariable != null) group.FireVariable.SetValue(group.AnyBurning ? 1f : 0f, 0);
            if (group.BoomVariable != null) group.BoomVariable.SetValue(group.Boomed ? 1f : 0f, 0);
        }

        // The game's weapon system only fires while the cannons' own counters are above 0.
        private static void SyncCannons(Group group)
        {
            int available = group.Available;
            foreach (CannonScript cannon in group.Cannons)
                if (cannon != null) CurrentAmmo(cannon) = available;
        }

        private static bool TryMeasure(Craft craft)
        {
            bool waitForColliders = false;
            foreach (Group group in craft.Groups)
                foreach (Rack rack in group.Racks)
                {
                    rack.Grid = RackGrid.Measure(rack.Part, group.HasRound ? group.Round : default(AmmoRound));
                    if (rack.Grid.Problem == RackGrid.NoColliders) waitForColliders = true;
                }

            // Colliders may come a few frames after the aircraft.
            if (waitForColliders && Time.time < craft.GiveUpMeasuringAt) return false;

            craft.Measured = true;
            foreach (Group group in craft.Groups)
            {
                string round = group.HasRound ? group.Round.Describe() : "no cannon with a [CAN] tag in this group";
                foreach (Rack rack in group.Racks)
                {
                    if (rack.Part != null)
                    {
                        rack.StartedConnected = rack.Part.ConnectedToMainCockpit;
                        rack.BaseMassScale = rack.Part.Part.MassScale;
                        rack.BaseLoadedMass = rack.Part.Part.LoadedMass;
                        UpdateMass(rack);
                    }
                    ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' (group " + group.Name +
                        (rack.Order != int.MaxValue ? ", order " + rack.Order : "") + "): " +
                        (rack.Grid.Problem == null ? rack.Grid.Capacity + " rounds (" + rack.Grid.Describe() +
                                                     ", shape from " + rack.Grid.Method + "), weighing " +
                                                     Fmt(rack.Grid.Capacity * group.Round.RoundMass, "F0") + " kg"
                                                   : "holds nothing: " + rack.Grid.Problem));
                }
                ArmorFragmentsPlugin.Log.LogInfo("Ammo group " + group.Name + ": " + group.Available + " rounds in " +
                    group.Racks.Count + " rack(s) for " + group.Cannons.Count + " cannon(s); " + round);
                if (group.Mixed)
                    ArmorFragmentsPlugin.Log.LogWarning("Ammo group " + group.Name + ": its cannons fire rounds of " +
                        "different sizes; the racks are sized for the biggest one.");
                SyncCannons(group);
            }
            return true;
        }

        // The rack weighs its part plus the rounds still in it: the part's mass scale is set so that its
        // loaded mass grows by (rounds x round mass). The game's body picks up mass changes of more than
        // 1 game unit (100 kg) on its own (BodyScript.OnUpdate), so a rack gets lighter in ~100 kg steps.
        private static void UpdateMass(Rack rack)
        {
            if (rack.Part == null || rack.Part.Part == null || rack.BaseLoadedMass <= 1e-5f) return;
            int rounds = rack.Grid.Remaining;
            if (rounds == rack.MassRounds) return;
            rack.MassRounds = rounds;

            float extra = rounds * rack.Group.Round.RoundMass * 0.01f;   // game units
            rack.Part.Part.MassScale = rack.BaseMassScale * (rack.BaseLoadedMass + extra) / rack.BaseLoadedMass;
            rack.Part.Part.RecalculateLoadedMass(false);
        }

        private static void Forget(AircraftScript aircraft)
        {
            KnockedOut.Remove(aircraft);
            Craft craft;
            if (!Crafts.TryGetValue(aircraft, out craft)) return;
            foreach (Group group in craft.Groups)
            {
                foreach (Rack rack in group.Racks) RackOf.Remove(rack.Part);
                foreach (CannonScript cannon in group.Cannons) GroupOf.Remove(cannon);
            }
            Crafts.Remove(aircraft);
        }

        // ---- Firing (CannonScript.Fire) ---------------------------------------------------

        public static bool CanFire(CannonScript cannon)
        {
            Group group;
            if (cannon == null || !GroupOf.TryGetValue(cannon, out group)) return true;
            if (!IsMeasured(group)) return true;
            return group.Available > 0;
        }

        public static void OnFired(CannonScript cannon)
        {
            Group group;
            if (cannon == null || !GroupOf.TryGetValue(cannon, out group) || !IsMeasured(group)) return;

            foreach (Rack rack in group.Racks)
            {
                if (!rack.CanFeed || rack.Grid.Remaining <= 0) continue;
                rack.Grid.TakeRound();
                if (rack.Grid.Remaining == 0)
                    ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' is empty (group " + group.Name +
                                                     ": " + group.Available + " rounds left)");
                break;
            }
            SyncCannons(group);
        }

        private static bool IsMeasured(Group group)
        {
            return group.Racks.Count > 0 && group.Racks[0].Grid != null;
        }

        // ---- Damage (PartScript.OnDamaged) ------------------------------------------------

        public static bool IsRack(PartScript part)
        {
            return part != null && RackOf.ContainsKey(part);
        }

        // hitShare: the hit's damage as a share of the rack's health, before AmmoRackDamagePatch cut it (a rack
        // already held at 99.9% gains no damage level, but a hit there still counts for its rounds).
        public static void OnDamaged(PartScript part, float levelBefore, float hitShare, int? attacker,
                                     Vector3 position, Vector3 direction)
        {
            Rack rack;
            if (!RackOf.TryGetValue(part, out rack)) return;
            if (rack.Burning || rack.Exploded || rack.Grid == null || rack.Grid.Remaining <= 0) return;

            AircraftScript aircraft = part.Aircraft;
            if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft) return;

            float level = DamageLevel(part);
            float rise = Mathf.Max(level - levelBefore, hitShare);
            if (rise <= 0f) return;
            // "Destroyed" = the rack's box has lost all its health (held at 0.999 by AmmoRackDamagePatch; room for
            // rounding). That is the box, not the rounds: only a hit whose path crosses rounds can set them off.
            bool destroyedNow = level >= 0.998f && levelBefore < 0.998f;
            bool wreckedBefore = levelBefore >= 0.998f;

            // Every hit must pass through rounds, the destroying one too (Jakniphe: with only a bottom layer
            // left, a shot over it destroyed the rack and still cooked the rounds off). A rack wrecked by a hit
            // that missed its rounds keeps them; later hits through them roll like any other hit.
            string levels = "damage level " + Fmt(levelBefore, "F3") + " -> " + Fmt(level, "F3") +
                            (destroyedNow ? " (destroyed by this hit)" : wreckedBefore ? " (already wrecked)" : "");
            if (!rack.Grid.HitsRounds(part.transform, position, direction))
            {
                ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' hit where it holds no rounds (" + levels + ")");
                return;
            }

            if (destroyedNow)
            {
                Destroyed(rack, attacker, rise, false);   // the hit that wrecks it, through its rounds: always cooks off
                return;
            }

            float chance = 1f - Mathf.Exp(-ArmorFragmentsPlugin.CookOffSensitivity.Value * rise);
            float roll = UnityEngine.Random.value;
            bool fire = roll < chance;
            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' hit its rounds: " + levels +
                ", cook-off chance " + Fmt(chance * 100f, "F0") + "%, rolled " + Fmt(roll * 100f, "F0") +
                (fire ? "%: it catches fire" : "%: no fire"));
            if (fire) Ignite(rack, attacker, "hit");
        }

        // A rack destroyed (damage level 1, or the part gone) with rounds still in it always catches fire
        // (Jakniphe, after two test hits in a row rolled the 5% "no fire" of the earlier roll): it burns, then
        // explodes. A part already gone has nothing to burn, so it explodes at once.
        private static void Destroyed(Rack rack, int? attacker, float rise, bool partGone)
        {
            if (attacker.HasValue) rack.Attacker = attacker;
            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' was destroyed" + (partGone ? " (part gone)" : "") +
                " with " + rack.Grid.Remaining + " rounds in it (damage level rose by " + Fmt(rise, "F3") + "): it cooks off");

            if (partGone) Explode(rack);
            else Ignite(rack, rack.Attacker, "destroyed");
        }

        // The X-ray view of your own vehicle draws what is left in each rack.
        public static bool LiveRack(PartScript part, out RackGrid grid, out bool burning)
        {
            grid = null;
            burning = false;
            Rack rack;
            if (part == null || !RackOf.TryGetValue(part, out rack) || rack.Grid == null) return false;
            grid = rack.Grid;
            burning = rack.Burning;
            return true;
        }

        private const float FireScale = 0.5f;

        // The game's fire effect goes out by itself: after its Duration, and when the vehicle (or its fuel tank)
        // has been out of fuel for a while (PartDamageEffect.Update). Our fires never go out: an endless
        // duration (which also freezes its time-based look at the start) and no need for fuel. Only water
        // still puts them out.
        private static readonly AccessTools.FieldRef<PartDamageEffect, float> EffectDuration =
            AccessTools.FieldRefAccess<PartDamageEffect, float>("_duration");
        private static readonly AccessTools.FieldRef<PartDamageEffect, bool> EffectRequiresFuel =
            AccessTools.FieldRefAccess<PartDamageEffect, bool>("_requiresFuel");

        private static void KeepBurning(PartDamageEffect effect)
        {
            try
            {
                EffectDuration(effect) = float.MaxValue;
                EffectRequiresFuel(effect) = false;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack fire could not be made endless: " + e.Message);
            }
        }

        private static void ScaleEffect(Transform effect, float scale)
        {
            effect.localScale *= scale;
            foreach (ParticleSystem particles in effect.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = particles.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            foreach (Light light in effect.GetComponentsInChildren<Light>(true))
                light.range *= scale;
        }

        private static void Ignite(Rack rack, int? attacker, string reason)
        {
            rack.Burning = true;
            rack.Attacker = attacker;
            // A burning rack dooms the vehicle: from now on no game explosion may blow it apart (not even the
            // craft's total-damage limit or another fuselage's disconnect roll during the burn).
            KnockOut(rack.Aircraft);
            float min = ArmorFragmentsPlugin.CookOffDelayMin.Value;
            float max = Mathf.Max(min, ArmorFragmentsPlugin.CookOffDelayMax.Value);
            float delay = UnityEngine.Random.Range(min, max);
            rack.ExplodeAt = Time.time + delay;

            try
            {
                // The game's own burning-part effect (a fuel tank that is about to blow uses the same), at half
                // size (Jakniphe). The effect is a fresh copy for this part (PartDamageEffects.CreateEffect
                // instantiates it), so shrinking it changes no other fire. Its particle systems are set to follow
                // their parents' scale. (Other players see the game's full-size fire, which it syncs itself.)
                if (rack.Part != null && rack.Part.Aircraft != null && rack.Part.Aircraft.DamageEffects != null)
                {
                    rack.Fire = rack.Part.Aircraft.DamageEffects.CreateFire(rack.Part, null);
                    if (rack.Fire != null)
                    {
                        ScaleEffect(rack.Fire.transform, FireScale);
                        KeepBurning(rack.Fire);
                    }
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack fire effect failed: " + e.Message);
            }

            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' is burning (" + reason + "): " +
                rack.Grid.Remaining + " rounds, explodes in " + Fmt(delay, "F1") + " s");
            SyncCannons(rack.Group);
        }

        private static void Explode(Rack rack)
        {
            int rounds = rack.Grid.Remaining;
            rack.Exploded = true;
            rack.Burning = false;
            rack.Grid.EmptyAll();

            try
            {
                if (rack.Fire != null && !rack.Fire.Destroyed) rack.Fire.DestroyEffect();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack fire effect could not be removed: " + e.Message);
            }

            if (rounds <= 0)
            {
                ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' burned out with no rounds left");
                return;
            }

            // Sized in pounds of explosive, like the game's own bombs (Jakniphe: "bomb effect, not bomb vibes"):
            // each round adds its propellant's explosive equivalent (AmmoRound.ExplosiveLb: ~0.53 lb for a 50 mm
            // round at 835 m/s, so a Pz III's ~90 rounds make ~50 lb), capped at MaxBlastLb. The game's bombs
            // scale their explosion with weight: "Boom 25" (250 lb) = 0.8, "Boom 50" (500 lb) = 1.6, so
            // 0.0032 per lb. (The first version gave 67 rounds of 100 mm a size of 57.6: 8 puffs of 16.5.)
            float pounds = Mathf.Min(rounds * rack.Group.Round.ExplosiveLb * ArmorFragmentsPlugin.BlastStrength.Value,
                                     ArmorFragmentsPlugin.MaxBlastLb.Value);
            float size = pounds * BombSizePerLb;
            Vector3 center = rack.Part != null ? rack.Grid.WorldCenter(rack.Part.transform) : rack.LastPosition;

            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' exploded: " + rounds + " rounds of " +
                Fmt(rack.Group.Round.CaliberMm, "F0") + " mm = " + Fmt(pounds, "F1") + " lb of explosive (blast size " +
                Fmt(size, "F2") + "; a 250 lb bomb is 0.80)");

            rack.Group.Boomed = true;   // <group>_boom: detachers wired to it fire now (turret toss)
            KnockOut(rack.Aircraft);    // before the cook: no game explosion may blow the shell apart
            Blast(rack, center, pounds);   // before the wreck: it may destroy the vent part
            WreckInsides(rack, center, pounds);
            SyncCannons(rack.Group);
        }

        // ---- Knocked out -----------------------------------------------------------------
        //
        // A vehicle whose rack has exploded is a knocked-out shell (Jakniphe's choice): from then on the game's
        // own explosions for it are off (AircraftScript.QueueExplosion, the "vehicle destroyed" blast, and
        // BodyScript.ExplodePart, which blows single parts off and, for a part of the cockpit's group, queues
        // that blast). So neither the cook-off nor a fuel tank going up later tears it apart; parts can still
        // reach full damage. The game no longer counts the vehicle as destroyed: Funky Trees on <group>_boom
        // or the modules' damage can cut its controls.

        private static readonly HashSet<AircraftScript> KnockedOut = new HashSet<AircraftScript>();

        private static void KnockOut(AircraftScript aircraft)
        {
            if (aircraft == null || !KnockedOut.Add(aircraft)) return;
            ArmorFragmentsPlugin.Log.LogInfo("Vehicle knocked out by a burning ammo rack: the game's own vehicle and part " +
                                             "explosions are off for it, so it stays an intact shell");
        }

        public static bool IsKnockedOut(AircraftScript aircraft)
        {
            return aircraft != null && KnockedOut.Contains(aircraft);
        }

        // ---- The blast: one bang, then a jet of fire -------------------------------------------
        //
        // (Third version, after Jakniphe's tests: a pillar of chained bomb explosions, even slowed down, flashed
        // unnaturally and was over too quickly.) A cook-off is now:
        //  1. One bang: a single explosion (AmmoRacks.BlastType, BombExplosion by default) at the vehicle's
        //     cook-off vent ([VNT] part, the nearest one: a turret ring or hatch), or just above the rack with
        //     no vent, sized by the pounds of explosive like the game's own bombs.
        //  2. A jet of fire out of the vent for 3 to 10 s (more pounds, longer): the game's own fire effect,
        //     scaled up and stretched upward, with denser flames.
        //  3. It then settles smoothly into the half-size burn-out fire, which never goes out (KeepBurning).
        // The game's blast is kept off the rack's own vehicle (no push, no parts knocked off) and off every
        // armor plate, so the hull keeps its shape; what is inside is wrecked by WreckInsides instead.

        private sealed class RecentBlast
        {
            public Vector3 Position;
            public float Time;
            public AircraftScript Owner;
        }

        private sealed class FireJet
        {
            public PartDamageEffect Effect;
            public Vector3 BaseScale;        // the effect's local scale as the game made it
            public Vector3 JetScale;         // multiplier at the start of the jet
            public float EmissionBoost;      // emission multiplier at the start of the jet
            public float[] BaseEmission;     // each particle system's own emission rate multiplier
            public ParticleSystem[] Particles;
            public float Start;
            public float Duration;
            public string Label;
        }

        private static readonly List<RecentBlast> Recent = new List<RecentBlast>();
        private static readonly List<FireJet> Jets = new List<FireJet>();
        private const float RecentWindow = 3f;         // s a rack blast is recognized by HandleExplosiveBlast
        private const float RecentTolerance = 3f;      // m

        // The game's bombs: explosion size per pound ("Boom 25", 250 lb = 0.8).
        private const float BombSizePerLb = 0.8f / 250f;

        // The fire jet's flare-up (size and extra flames), as a share of the first version's.
        private const float JetSize = 1f / 3f;

        private static void Blast(Rack rack, Vector3 center, float pounds)
        {
            PartScript vent = NearestVent(rack.Aircraft, center);
            PartScript firePart = vent != null ? vent : rack.Part;
            Vector3 at = vent != null ? vent.transform.position : center + Vector3.up * 0.5f;

            // 1. The bang.
            float size = pounds * BombSizePerLb;
            if (size > 0.0001f && FlightSceneScript.Instance != null)
            {
                Recent.Add(new RecentBlast { Position = at, Time = Time.time, Owner = rack.Aircraft });
                FlightSceneScript.Instance.CreateExplosion(ArmorFragmentsPlugin.BlastType.Value, at, size, null,
                                                           rack.Attacker, null, ExplosiveWeaponImpactType.Structure);
            }

            // 2-3. The jet: it flares up at once and settles into the burn-out. At first it was 1.5 to 5 times
            // the fire's size (x 2.5 upward) with up to 7x the flames: a 120 lb cook-off bloomed to 10x the
            // burn-out's width. Jakniphe liked the flare-up but at 1/3 of that: JetSize.
            float duration = Mathf.Clamp(2f + pounds / 15f, 3f, 10f);
            float width = Mathf.Max(FireScale * 1.5f, Mathf.Clamp(1f + pounds / 25f, 1.5f, 5f) * JetSize);
            StartJet(firePart, rack.Label, duration, new Vector3(width, width * 2.5f, width), 1f + pounds / 20f * JetSize);
            if (firePart != null) BurningVents.Add(firePart);

            // 4. Every other vent of the vehicle catches the small burn-out fire too (Jakniphe), a little later
            //    each (0.5 to 2 s: the fire spreading through the hull). A vent already burning gets no second fire.
            int others = 0;
            foreach (PartScript other in Vents(rack.Aircraft))
            {
                if (other == vent || BurningVents.Contains(other)) continue;
                BurningVents.Add(other);
                PendingFires.Add(new PendingFire { At = Time.time + UnityEngine.Random.Range(0.5f, 2f), Part = other });
                others++;
            }

            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' cooks off " +
                (vent != null ? "through '" + vent.Part.Name + "'" : "(no [VNT] part: above the rack)") + ": one " +
                ArmorFragmentsPlugin.BlastType.Value + " of size " + Fmt(size, "F2") + ", then a fire jet " +
                Fmt(width, "F1") + " x " + Fmt(width * 2.5f, "F1") + " for " + Fmt(duration, "F1") +
                " s, settling into a burn-out that never goes out" +
                (others > 0 ? "; " + others + " other vent(s) catch fire too" : ""));
        }

        // Vents ([VNT] parts) burning since a cook-off, and the other vents' fires still to light.
        private sealed class PendingFire
        {
            public float At;
            public PartScript Part;
        }

        private static readonly HashSet<PartScript> BurningVents = new HashSet<PartScript>();
        private static readonly List<PendingFire> PendingFires = new List<PendingFire>();

        private static List<PartScript> Vents(AircraftScript aircraft)
        {
            List<PartScript> vents = new List<PartScript>();
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return vents;
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part != null && TagCodec.Parse(data.Name).Vent != null) vents.Add(part);
            }
            return vents;
        }

        private static void LightPendingFires()
        {
            for (int i = 0; i < PendingFires.Count; i++)
            {
                PendingFire fire = PendingFires[i];
                if (Time.time < fire.At) continue;
                PendingFires.RemoveAt(i--);

                PartScript part = fire.Part;
                if (part == null || part.Aircraft == null || part.Aircraft.DamageEffects == null) continue;
                PartDamageEffect effect = part.Aircraft.DamageEffects.CreateFire(part, null);
                if (effect == null) continue;
                ScaleEffect(effect.transform, FireScale);
                KeepBurning(effect);
            }
            BurningVents.RemoveWhere(p => p == null);
        }

        private static void StartJet(PartScript part, string label, float duration, Vector3 jetScale, float emissionBoost)
        {
            if (part == null || part.Aircraft == null || part.Aircraft.DamageEffects == null) return;
            PartDamageEffect effect = part.Aircraft.DamageEffects.CreateFire(part, null);
            if (effect == null) return;
            KeepBurning(effect);

            FireJet jet = new FireJet
            {
                Effect = effect,
                BaseScale = effect.transform.localScale,
                JetScale = jetScale,
                EmissionBoost = emissionBoost,
                Particles = effect.GetComponentsInChildren<ParticleSystem>(true),
                Start = Time.time,
                Duration = duration,
                Label = label,
            };
            jet.BaseEmission = new float[jet.Particles.Length];
            for (int i = 0; i < jet.Particles.Length; i++)
            {
                ParticleSystem.MainModule main = jet.Particles[i].main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;   // the flames follow the effect's scale
                jet.BaseEmission[i] = jet.Particles[i].emission.rateOverTimeMultiplier;
            }
            Jets.Add(jet);
            ApplyJet(jet, 0f);
        }

        // t: 0 = full jet, 1 = settled burn-out (FireScale). Eases out, so the jet falls off quickly at first.
        private static void ApplyJet(FireJet jet, float t)
        {
            float k = 1f - (1f - t) * (1f - t);
            Vector3 burnOut = Vector3.one * FireScale;
            Vector3 scale = Vector3.Lerp(jet.JetScale, burnOut, k);
            jet.Effect.transform.localScale = Vector3.Scale(jet.BaseScale, scale);

            float boost = Mathf.Lerp(jet.EmissionBoost, 1f, k);
            for (int i = 0; i < jet.Particles.Length; i++)
            {
                if (jet.Particles[i] == null) continue;
                ParticleSystem.EmissionModule emission = jet.Particles[i].emission;
                emission.rateOverTimeMultiplier = jet.BaseEmission[i] * boost;
            }
        }

        private static PartScript NearestVent(AircraftScript aircraft, Vector3 from)
        {
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return null;
            PartScript best = null;
            float bestDistance = float.MaxValue;
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null || TagCodec.Parse(data.Name).Vent == null) continue;
                float distance = (part.transform.position - from).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = part;
                }
            }
            return best;
        }

        // Every frame: grow the jets down to their burn-out, and forget old blasts.
        private static void UpdateBlasts()
        {
            for (int i = 0; i < Jets.Count; i++)
            {
                FireJet jet = Jets[i];
                if (jet.Effect == null || jet.Effect.Destroyed)
                {
                    Jets.RemoveAt(i--);
                    continue;
                }
                float t = Mathf.Clamp01((Time.time - jet.Start) / jet.Duration);
                ApplyJet(jet, t);
                if (t >= 1f)
                {
                    Jets.RemoveAt(i--);
                    ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + jet.Label + "': the jet has settled; the wreck keeps burning");
                }
            }
            Recent.RemoveAll(r => Time.time - r.Time > RecentWindow);
            if (PendingFires.Count > 0) LightPendingFires();
        }

        // HandleExplosiveBlast: is this blast one of ours? Then its own vehicle (and every armor plate) is
        // left out: no push, nothing knocked off.
        public static bool IsRackBlast(Vector3 origin, out AircraftScript owner)
        {
            owner = null;
            foreach (RecentBlast blast in Recent)
            {
                if ((blast.Position - origin).sqrMagnitude > RecentTolerance * RecentTolerance) continue;
                owner = blast.Owner;
                return true;
            }
            return false;
        }

        // The cook-off cooks what is inside: the vehicle's damage modules ([MOD]) and other ammo racks ([AMM])
        // within a radius that grows with the cook-off (1 m + 1 m per 25 lb, up to 8 m: 50 lb -> 3 m) take up
        // to twice their health (destroyed in the inner half, less farther out). Nothing else of the vehicle is
        // touched: the game's own blast is kept off it (AmmoRackBlastPatch, which also skips the game's blast
        // damage), so armor and structure keep the shape. Racks hit this way roll their own chance (chain).
        private static void WreckInsides(Rack rack, Vector3 center, float pounds)
        {
            AircraftScript aircraft = rack.Aircraft;
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return;
            float radius = Mathf.Clamp(1f + pounds / 25f, 1f, 8f);

            List<PartScript> targets = new List<PartScript>();
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null || part == rack.Part) continue;
                PartTags tags = TagCodec.Parse(data.Name);
                if (tags.Armor != null || (tags.Module == null && tags.AmmoRack == null)) continue;
                if ((part.transform.position - center).sqrMagnitude < radius * radius) targets.Add(part);
            }

            int hit = 0;
            foreach (PartScript part in targets)
            {
                if (part == null) continue;
                Vector3 away = part.transform.position - center;
                float damage = part.MaxHealth * 2f * (1f - away.magnitude / radius);
                if (damage <= 0f) continue;
                part.OnDamaged(rack.Attacker, damage, part.transform.position,
                               away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.up);
                hit++;
            }
            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' cooked " + hit + " damage modules / racks inside the vehicle (within " +
                                             Fmt(radius, "F1") + " m; armor and structure untouched)");
        }

        private static string Fmt(float value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        // ---- Designer read-outs -------------------------------------------------------------

        // The round the cannons of this group fire, from the aircraft being designed.
        public static bool DesignRound(PartScript anyPart, string groupName, out AmmoRound round, out int cannons, out bool mixed)
        {
            round = default(AmmoRound);
            cannons = 0;
            mixed = false;
            if (anyPart == null || anyPart.Aircraft == null || anyPart.Aircraft.Aircraft == null ||
                anyPart.Aircraft.Aircraft.Assembly == null) return false;

            foreach (PartData data in anyPart.Aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;
                PartTags tags = TagCodec.Parse(data.Name);
                if (tags.Cannon == null || GroupName(tags.Cannon.Group) != groupName) continue;

                AmmoRound cannonRound;
                if (!AmmoRound.TryFromPart(part, out cannonRound)) continue;
                if (cannons == 0) round = cannonRound;
                else
                {
                    if (!cannonRound.SameSize(round)) mixed = true;
                    if (cannonRound.IsBiggerThan(round)) round = cannonRound;
                }
                cannons++;
            }
            return cannons > 0;
        }

        // Does this cannon draw from ammo racks? (Designer: then its own Ammo Count slider means nothing.)
        public static bool DesignFedByRacks(CannonData cannon)
        {
            PartData data = cannon != null ? cannon.Part : null;
            if (data == null || data.PartScript == null) return false;
            PartTags tags = TagCodec.Parse(data.Name);
            if (tags.Cannon == null) return false;

            AircraftScript aircraft = data.PartScript.Aircraft;
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return false;

            string group = GroupName(tags.Cannon.Group);
            foreach (PartData other in aircraft.Aircraft.Assembly.Parts)
            {
                if (other == null) continue;
                PartTags otherTags = TagCodec.Parse(other.Name);
                if (otherTags.AmmoRack != null && otherTags.Armor == null && GroupName(otherTags.AmmoRack.Group) == group)
                    return true;
            }
            return false;
        }

        // Cheap strings that change whenever a rack's read-outs or drawing would (designer).
        public static string DesignRackSignature(PartScript rack, string group)
        {
            AmmoRound round;
            int cannons;
            bool mixed;
            string roundText = DesignRound(rack, group, out round, out cannons, out mixed)
                ? round.Describe() + mixed : "none";
            return roundText + "|" + ShapeSignature(rack) + "|" +
                   ArmorFragmentsPlugin.CaseDiameterRatio.Value + "/" + ArmorFragmentsPlugin.CaseLitresPerMJ.Value + "/" +
                   ArmorFragmentsPlugin.RoundSpacingPercent.Value;
        }

        // The part's collider boxes in its own rotation, to the millimeter, and its fuselage offset.
        public static string ShapeSignature(PartScript shaped)
        {
            System.Text.StringBuilder signature = new System.Text.StringBuilder();
            Quaternion inverse = Quaternion.Inverse(shaped.transform.rotation);
            foreach (Collider collider in shaped.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger) continue;
                Bounds bounds = collider.bounds;
                Vector3 center = inverse * (bounds.center - shaped.transform.position);
                signature.Append(Vector3Int.RoundToInt(center * 1000f)).Append(Vector3Int.RoundToInt(bounds.size * 1000f));
            }
            foreach (PartModifierData modifier in shaped.Part.Modifiers)
            {
                JFuselageData fuselage = modifier as JFuselageData;
                if (fuselage != null) signature.Append(Vector3Int.RoundToInt(fuselage.Offset * 1000f)).Append(fuselage.IsHollow);
            }

            // The drawn mesh (what RackGrid measures): vertex count, size and a sample of its vertices, so
            // reshaping that keeps the same box (corner rounding, taper...) is noticed too.
            foreach (MeshRenderer renderer in XRayView.GhostSources(shaped))
            {
                if (renderer == null || renderer.name == "AmmoRackGhost" || renderer.name == "AmmoRackRounds") continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;
                signature.Append('m').Append(mesh.vertexCount).Append(Vector3Int.RoundToInt(mesh.bounds.size * 1000f));
                if (!mesh.isReadable) continue;
                Vector3[] vertices = mesh.vertices;
                Vector3 sum = Vector3.zero;
                int step = Mathf.Max(1, vertices.Length / 64);
                for (int i = 0; i < vertices.Length; i += step) sum += vertices[i];
                signature.Append(Vector3Int.RoundToInt(sum * 1000f));
            }
            return signature.ToString();
        }

        // Total rounds the racks of this group hold (designer).
        public static int DesignCapacity(PartScript anyPart, string groupName, AmmoRound round, out int racks)
        {
            racks = 0;
            int total = 0;
            if (anyPart == null || anyPart.Aircraft == null || anyPart.Aircraft.Aircraft == null ||
                anyPart.Aircraft.Aircraft.Assembly == null) return 0;

            foreach (PartData data in anyPart.Aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;
                PartTags tags = TagCodec.Parse(data.Name);
                if (tags.AmmoRack == null || tags.Armor != null || GroupName(tags.AmmoRack.Group) != groupName) continue;
                racks++;
                total += RackGrid.Measure(part, round).Capacity;
            }
            return total;
        }
    }

    // Recreate the ammo variables whenever the game rebuilds the variable list (it drops unknown ones).
    [HarmonyPatch(typeof(VariableSystemScript), "RefreshVariables")]
    internal static class AmmoRackVariablesPatch
    {
        private static void Postfix(VariableSystemScript __instance)
        {
            try
            {
                AmmoRacks.Register(__instance);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Could not register the ammo racks: " + e);
            }
        }
    }

    // Every kind of damage reaches a part through OnDamaged (on the owner's game; another player's shells
    // and fragments arrive here through the game's DamagePart message).
    //
    // A rack is never destroyed by the game: a part destroyed in the cockpit's part group makes the game blow
    // the whole vehicle apart at once (BodyScript.OnPartDamageLevelIncreased -> ExplodePart ->
    // AircraftScript.QueueExplosion), which skipped the burn in Jakniphe's first cook-off test. The damage is
    // cut so the rack stops at 99.9% of its health, which counts as "destroyed" for the cook-off roll.
    [HarmonyPatch(typeof(PartScript), "OnDamaged")]
    internal static class AmmoRackDamagePatch
    {
        private const float MaxRackDamage = 0.999f;

        // __state: (damage level before the hit, the hit's damage as a share of the rack's health, uncut);
        // x < 0 = not a rack.
        private static void Prefix(PartScript __instance, ref float damage, out Vector2 __state)
        {
            __state = new Vector2(-1f, 0f);
            if (!AmmoRacks.IsRack(__instance)) return;
            __state = new Vector2(AmmoRacks.DamageLevel(__instance), damage / Mathf.Max(__instance.MaxHealth, 1f));

            float room = __instance.MaxHealth * MaxRackDamage - __instance.PartDamage;
            if (damage > room) damage = Mathf.Max(0f, room);
        }

        private static void Postfix(PartScript __instance, int? attackerPlayerId, Vector3 position, Vector3 direction,
                                    Vector2 __state)
        {
            if (__state.x < 0f) return;
            try
            {
                AmmoRacks.OnDamaged(__instance, __state.x, __state.y, attackerPlayerId, position, direction);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack damage check failed: " + e);
            }
        }
    }

    // A rack's blast keeps off its own vehicle and off every armor plate: the game's blast pushes the parts
    // it reaches and knocks off those where its force beats a random threshold (30..95), armor included,
    // without going through OnDamaged (so ArmorDamagePatch can't stop it). For our blasts, those parts are
    // taken out of the list the game works on. Other explosions are untouched.
    [HarmonyPatch(typeof(BodyScript), "HandleExplosiveBlast")]
    internal static class AmmoRackBlastPatch
    {
        private static void Prefix(ref List<PartScript> parts, Vector3 blastOrigin)
        {
            try
            {
                AircraftScript owner;
                if (parts == null || !AmmoRacks.IsRackBlast(blastOrigin, out owner)) return;
                List<PartScript> kept = new List<PartScript>(parts.Count);
                foreach (PartScript part in parts)
                {
                    if (part == null || part.Part == null) continue;
                    if (owner != null && part.Aircraft == owner) continue;
                    if (TagCodec.Parse(part.Part.Name).Armor != null) continue;
                    kept.Add(part);
                }
                parts = kept;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack blast filter failed: " + e.Message);
            }
        }
    }

    // A knocked-out vehicle (its rack has exploded) is never blown apart by the game: see AmmoRacks.KnockOut.
    [HarmonyPatch(typeof(AircraftScript), "QueueExplosion")]
    internal static class KnockedOutVehicleExplosionPatch
    {
        private static bool Prefix(AircraftScript __instance)
        {
            return !AmmoRacks.IsKnockedOut(__instance);
        }
    }

    // ExplodePart is also never allowed on a rack itself: every fuselage type has damageDisconnect="true", so
    // each new damage stage rolls a chance to ExplodePart it (BodyScript.OnPartDamageLevelIncreased), without
    // reaching 0 health; for a rack in the cockpit's group that blew the whole vehicle up at once (Jakniphe's
    // second cook-off test, with the rack held at 99.9%: the top stage has the highest chance).
    [HarmonyPatch(typeof(BodyScript), "ExplodePart", new Type[] { typeof(PartScript) })]
    internal static class KnockedOutPartExplosionPatch
    {
        private static bool Prefix(PartScript part)
        {
            return part == null || (!AmmoRacks.IsRack(part) && !AmmoRacks.IsKnockedOut(part.Aircraft));
        }
    }

    [HarmonyPatch(typeof(BodyScript), "ExplodePart", new Type[] { typeof(PartScript), typeof(float), typeof(int) })]
    internal static class KnockedOutPartExplosionCascadePatch
    {
        private static bool Prefix(PartScript part)
        {
            return part == null || (!AmmoRacks.IsRack(part) && !AmmoRacks.IsKnockedOut(part.Aircraft));
        }
    }

    // A cannon fed by racks has no use for the game's "Ammo Count" slider: hide it in the designer. The game
    // asks each part modifier for a visibility callback per property (JFuselageData uses it for its own);
    // CannonData does not override it, so this patch on the base answers for cannons.
    [HarmonyPatch(typeof(PartModifierData), "GetGenericDesignerPropertyVisibilityCallback")]
    internal static class AmmoCountVisibilityPatch
    {
        private static void Postfix(PartModifierData __instance,
                                    Assets.Scripts.Design.UI.PartProperties.IConfigurableProperty property,
                                    ref Func<bool> __result)
        {
            CannonData cannon = __instance as CannonData;
            if (cannon == null || property == null || property.Member == null || property.Member.Name != "_ammoCount") return;

            Func<bool> original = __result;
            __result = () =>
            {
                if (original != null && !original()) return false;
                try
                {
                    return !ArmorFragmentsPlugin.AmmoRacksEnabled.Value || !AmmoRacks.DesignFedByRacks(cannon);
                }
                catch (Exception)
                {
                    return true;
                }
            };
        }
    }

    // A cannon fed by racks can't fire with its group empty, and every shot takes a round from the racks.
    [HarmonyPatch(typeof(CannonScript), "Fire", new Type[0])]
    internal static class AmmoRackFirePatch
    {
        private static bool Prefix(CannonScript __instance, out bool __state)
        {
            __state = AmmoRacks.CanFire(__instance);
            return __state;
        }

        private static void Postfix(CannonScript __instance, bool __state)
        {
            if (!__state) return;
            try
            {
                AmmoRacks.OnFired(__instance);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack shot accounting failed: " + e);
            }
        }
    }
}
