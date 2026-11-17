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
    // Ammo racks ([AMM] fuselages) hold the rounds of the cannons that draw from them. A cannon lists its racks
    // by part id in [CAN r=], picked in the designer; cannons that fire the same round can share a rack.
    //
    //  - A cannon with racks fires only while they have rounds (the game's own "Ammo Count" is overwritten every
    //    frame, and Fire() is blocked when they are empty). It empties its racks in order (o=, then part id),
    //    each from its top layer down. A cannon with no rack keeps the game's Ammo Count.
    //  - A rack's capacity is the whole rounds that fit its shape (RackGrid), for the round of the cannons that
    //    draw from it (AmmoRound; the biggest, if they differ). A rack no cannon draws from holds nothing.
    //  - Damage reaching a rack (PartScript.OnDamaged: the game's shell impact, fragments, explosions,
    //    crashes) may set it on fire if its line crosses a cell that still holds a round. Chance =
    //    1 - (1 - CookOffDirectHit)^(shells through the rounds) x e^(-kJ into the rounds / CookOffEnergyKJ); the
    //    mod's damage is turned back into kJ with DamagePerKiloJoule. A hit that destroys the rack through its
    //    rounds always sets it on fire.
    //  - A burning rack can't feed the gun and explodes CookOffBurnSeconds (10) later, with the
    //    propellant of the rounds still in it (only propellant; explosive filler isn't counted).
    //  - Funky Trees, per group of racks (their g=, default "Ammo"): <group>_rounds (rounds its racks can still
    //    feed), <group>_fire (1 while one of them burns) and <group>_boom (1 once one has exploded).
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
            public Group Group;        // its Funky Trees variables
            public Craft Craft;
            public AmmoRound Round;    // the round of the cannons that draw from it (the biggest, if they differ)
            public bool HasRound;
            public bool Mixed;         // those cannons fire rounds of different sizes
            public int Cannons;        // how many cannons draw from it
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

        // A cannon that draws from racks.
        private sealed class Feed
        {
            public CannonScript Cannon;
            public string Label;
            public Craft Craft;
            public readonly List<Rack> Racks = new List<Rack>();   // in the order it empties them

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
        }

        // Racks with the same g=: they share their Funky Trees variables.
        private sealed class Group
        {
            public string Name;
            public string Prefix;      // variable prefix
            public readonly List<Rack> Racks = new List<Rack>();
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
            public readonly List<Rack> Racks = new List<Rack>();
            public readonly List<Group> Groups = new List<Group>();
            public readonly List<Feed> Feeds = new List<Feed>();
            public bool Measured;
            public float GiveUpMeasuringAt;   // real time (Time.realtimeSinceStartup): the game may run faster or slower
            public float NextLateMeasure;     // real time: racks whose mesh came late are measured again
        }

        private static readonly Dictionary<AircraftScript, Craft> Crafts = new Dictionary<AircraftScript, Craft>();
        private static readonly Dictionary<PartScript, Rack> RackOf = new Dictionary<PartScript, Rack>();
        private static readonly Dictionary<CannonScript, Feed> FeedOf = new Dictionary<CannonScript, Feed>();

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

        // After a hot reload (F6) the new copy knows nothing of the vehicles already flying: the racks are only found when the game rebuilds a
        // vehicle's variable list. So it is asked to rebuild it for every vehicle in flight, which runs the patch that registers the racks (and
        // the damage module variables) the same way as at the start of a flight. A rack keeps no rounds from before (it is full again).
        public static int RegisterExisting()
        {
            int count = 0;
            foreach (AircraftScript aircraft in UnityEngine.Object.FindObjectsByType<AircraftScript>(FindObjectsSortMode.None))
            {
                if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft) continue;
                VariableSystemScript system = aircraft.VariableSystem;
                if (system == null) continue;
                try { system.RefreshVariables(); count++; }
                catch (Exception e) { ArmorFragmentsPlugin.Log.LogWarning("Could not rebuild the variables of '" + aircraft.name + "': " + e.Message); }
            }
            if (count > 0) ArmorFragmentsPlugin.Log.LogInfo("[LIFECYCLE] looked for ammo racks in " + count + " vehicle(s) already in flight");
            return count;
        }

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
            Craft craft = new Craft { Aircraft = aircraft, GiveUpMeasuringAt = Time.realtimeSinceStartup + 10f };
            Dictionary<string, Group> byName = new Dictionary<string, Group>();
            Dictionary<int, Rack> byId = new Dictionary<int, Rack>();
            List<KeyValuePair<PartData, PartTags>> cannons = new List<KeyValuePair<PartData, PartTags>>();

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
                        Rack rack = new Rack
                        {
                            Part = part,
                            Label = string.IsNullOrEmpty(tags.Label) ? data.PartType.Name : tags.Label,
                            Order = tags.AmmoRack.Order ?? int.MaxValue,
                            PartId = data.Id,
                            Group = group,
                            Craft = craft,
                            Aircraft = aircraft,
                        };
                        group.Racks.Add(rack);
                        craft.Racks.Add(rack);
                        byId[data.Id] = rack;
                    }
                }

                if (tags.Cannon != null && tags.Cannon.Racks.Count > 0)
                    cannons.Add(new KeyValuePair<PartData, PartTags>(data, tags));
            }

            foreach (KeyValuePair<PartData, PartTags> pair in cannons)
            {
                PartScript part = pair.Key.PartScript;
                CannonScript cannon = part.GetModifier<CannonScript>();
                AmmoRound round;
                if (cannon == null || !AmmoRound.TryFromPart(part, out round)) continue;

                Feed feed = new Feed
                {
                    Cannon = cannon,
                    Label = string.IsNullOrEmpty(pair.Value.Label) ? pair.Key.PartType.Name : pair.Value.Label,
                    Craft = craft,
                };
                foreach (int id in pair.Value.Cannon.Racks)
                {
                    Rack rack;
                    if (byId.TryGetValue(id, out rack) && !feed.Racks.Contains(rack)) feed.Racks.Add(rack);
                }
                if (feed.Racks.Count == 0) continue;   // none of its racks is left: it keeps the game's Ammo Count
                // Numbered racks first, lowest up; then the ones with no order, nearest to the gun first (also between
                // racks with the same number). Distances are taken where the parts are at spawn.
                Vector3 gunAt = part.transform.position;
                feed.Racks.Sort((a, b) =>
                {
                    if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
                    float da = a.Part != null ? (a.Part.transform.position - gunAt).sqrMagnitude : float.MaxValue;
                    float db = b.Part != null ? (b.Part.transform.position - gunAt).sqrMagnitude : float.MaxValue;
                    return da != db ? da.CompareTo(db) : a.PartId.CompareTo(b.PartId);
                });

                foreach (Rack rack in feed.Racks)
                {
                    rack.Cannons++;
                    if (!rack.HasRound)
                    {
                        rack.Round = round;
                        rack.HasRound = true;
                    }
                    else
                    {
                        if (!round.SameSize(rack.Round)) rack.Mixed = true;
                        if (round.IsBiggerThan(rack.Round)) rack.Round = round;
                    }
                }
                craft.Feeds.Add(feed);
            }

            foreach (Rack rack in craft.Racks) RackOf[rack.Part] = rack;
            foreach (Feed feed in craft.Feeds) FeedOf[feed.Cannon] = feed;
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
                    MeasureLate(craft);
                    foreach (Rack rack in craft.Racks) Update(rack);
                    SyncCannons(craft);
                    foreach (Group group in craft.Groups) UpdateVariables(group);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("Ammo racks: " + e);
                }
            }

            if (gone != null)
                foreach (AircraftScript aircraft in gone) Forget(aircraft);
        }

        private static void Update(Rack rack)
        {
            if (rack.Grid == null) return;
            UpdateMass(rack);
            if (rack.Exploded) return;

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
                ExplodeWithPartners(rack);
            }
            else if (rack.Part == null && rack.Grid.Remaining > 0)
            {
                if (rack.Burning)
                {
                    // Already cooking off and the part is gone: nothing left to burn.
                    ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' was destroyed while burning");
                    ExplodeWithPartners(rack);
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

        private static void UpdateVariables(Group group)
        {
            if (group.RoundsVariable != null) group.RoundsVariable.SetValue(group.Available, 0);
            if (group.FireVariable != null) group.FireVariable.SetValue(group.AnyBurning ? 1f : 0f, 0);
            if (group.BoomVariable != null) group.BoomVariable.SetValue(group.Boomed ? 1f : 0f, 0);
        }

        // The game's weapon system only fires while the cannons' own counters are above 0.
        private static void SyncCannons(Craft craft)
        {
            foreach (Feed feed in craft.Feeds)
                if (feed.Cannon != null) CurrentAmmo(feed.Cannon) = feed.Available;
        }

        // A rack whose mesh and colliders weren't built yet when the wait ran out (the game builds fuselage meshes after
        // the craft appears, sometimes much later) is measured again once a (real) second until it has a shape; then it
        // fills and the cannons drawing from it get its rounds.
        private static void MeasureLate(Craft craft)
        {
            if (Time.realtimeSinceStartup < craft.NextLateMeasure) return;
            craft.NextLateMeasure = Time.realtimeSinceStartup + 1f;

            bool changed = false;
            foreach (Rack rack in craft.Racks)
            {
                if (rack.Grid == null || rack.Grid.Problem != RackGrid.NoColliders) continue;
                if (rack.Part == null || rack.Burning || rack.Exploded) continue;
                RackGrid grid = RackGrid.Measure(rack.Part, rack.HasRound ? rack.Round : default(AmmoRound));
                if (grid.Problem == RackGrid.NoColliders) continue;

                rack.Grid = grid;
                rack.BaseMassScale = rack.Part.Part.MassScale;
                rack.BaseLoadedMass = rack.Part.Part.LoadedMass;
                UpdateMass(rack);
                changed = true;
                ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' measured late (its shape wasn't built at spawn): " +
                    (grid.Problem == null ? grid.Remaining + " rounds of " + rack.Round.Describe() + " (" + grid.Describe() +
                                            ", shape from " + grid.Method + ")"
                                          : "holds nothing: " + grid.Problem));
            }
            if (changed) SyncCannons(craft);
        }

        private static bool TryMeasure(Craft craft)
        {
            bool waitForColliders = false;
            foreach (Rack rack in craft.Racks)
            {
                rack.Grid = RackGrid.Measure(rack.Part, rack.HasRound ? rack.Round : default(AmmoRound));
                if (rack.Grid.Problem == RackGrid.NoColliders) waitForColliders = true;
            }

            // Colliders may come a few frames after the aircraft.
            if (waitForColliders && Time.realtimeSinceStartup < craft.GiveUpMeasuringAt) return false;

            craft.Measured = true;
            foreach (Rack rack in craft.Racks)
            {
                if (rack.Part != null)
                {
                    rack.StartedConnected = rack.Part.ConnectedToMainCockpit;
                    rack.BaseMassScale = rack.Part.Part.MassScale;
                    rack.BaseLoadedMass = rack.Part.Part.LoadedMass;
                    UpdateMass(rack);
                }
                ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' (group " + rack.Group.Name +
                    (rack.Order != int.MaxValue ? ", order " + rack.Order : "") + "): " +
                    (rack.Grid.Problem == null ? (rack.Grid.Remaining < rack.Grid.Capacity ? rack.Grid.Remaining + " of " : "") + rack.Grid.Capacity + " rounds of " + rack.Round.Describe() + " (" +
                                                 rack.Grid.Describe() + ", shape from " + rack.Grid.Method + "), weighing " +
                                                 Fmt(rack.Grid.Remaining * rack.Round.RoundMass, "F0") + " kg, for " +
                                                 rack.Cannons + " cannon(s)"
                                               : "holds nothing: " + rack.Grid.Problem));
                if (rack.Mixed)
                    ArmorFragmentsPlugin.Log.LogWarning("Ammo rack '" + rack.Label + "': the cannons drawing from it fire " +
                        "rounds of different sizes; it is sized for the biggest one.");
            }
            foreach (Feed feed in craft.Feeds)
            {
                List<string> labels = new List<string>();
                foreach (Rack rack in feed.Racks) labels.Add("'" + rack.Label + "'");
                ArmorFragmentsPlugin.Log.LogInfo("Cannon '" + feed.Label + "': " + feed.Available + " rounds in " +
                    feed.Racks.Count + " rack(s), emptied in this order: " + string.Join(", ", labels.ToArray()));
            }
            SyncCannons(craft);
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

            float extra = rounds * rack.Round.RoundMass * 0.01f;   // game units
            rack.Part.Part.MassScale = rack.BaseMassScale * (rack.BaseLoadedMass + extra) / rack.BaseLoadedMass;
            rack.Part.Part.RecalculateLoadedMass(false);
        }

        private static void Forget(AircraftScript aircraft)
        {
            VehicleRules.Forget(aircraft);
            Craft craft;
            if (!Crafts.TryGetValue(aircraft, out craft)) return;
            foreach (Rack rack in craft.Racks) RackOf.Remove(rack.Part);
            foreach (Feed feed in craft.Feeds) FeedOf.Remove(feed.Cannon);
            Crafts.Remove(aircraft);
        }

        // ---- Firing (CannonScript.Fire) ---------------------------------------------------

        // A rack-fed cannon fires when one of its racks has a round it can feed and the vehicle has crew to load it
        // (Crew.CanLoad).
        public static bool CanFire(CannonScript cannon)
        {
            Feed feed;
            if (cannon == null || !FeedOf.TryGetValue(cannon, out feed)) return true;
            if (!Crew.CanLoad(cannon)) return false;
            if (!feed.Craft.Measured) return true;
            return feed.Available > 0;
        }

        public static bool IsRackFed(CannonScript cannon)
        {
            return cannon != null && FeedOf.ContainsKey(cannon);
        }

        public static void OnFired(CannonScript cannon)
        {
            Feed feed;
            if (cannon == null || !FeedOf.TryGetValue(cannon, out feed) || !feed.Craft.Measured) return;

            foreach (Rack rack in feed.Racks)
            {
                if (!rack.CanFeed || rack.Grid.Remaining <= 0) continue;
                rack.Grid.TakeRound();
                if (rack.Grid.Remaining == 0)
                    ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' is empty (cannon '" + feed.Label +
                                                     "': " + feed.Available + " rounds left)");
                break;
            }
            SyncCannons(feed.Craft);
        }

        // ---- Damage (PartScript.OnDamaged) ------------------------------------------------

        public static bool IsRack(PartScript part)
        {
            return part != null && RackOf.ContainsKey(part);
        }

        // The mod adds up a shot's damage per part and hands the game ONE hit, at the biggest piece's point and
        // direction (FragmentSpawner.Accumulate). A rack needs every piece: a big fragment through its empty top
        // would hide small ones through its rounds. So FragmentSpawner shows each piece here first, and
        // OnDamaged then counts only the pieces whose path crossed rounds. Only racks this game owns: a remote
        // rack's owner gets the summed hit over the network and judges it by the biggest piece. Everything of
        // one shot happens in the same frame.
        private sealed class Pieces
        {
            public float Through;     // damage of the pieces whose path crossed rounds
            public int Shells;        // how many of them were the shell itself (a direct hit), not fragments
            public float ShellKJ;     // what those shells carried into the rack
        }

        private static readonly Dictionary<PartScript, Pieces> ThroughRounds = new Dictionary<PartScript, Pieces>();
        private static int throughFrame = -1;

        // shell: the piece is the shell itself (FragmentSpawner.DepositCrossings), not a fragment. shellEnergyJ: what
        // it carries into the rack; a shell through the cartridges counts with that energy, not with what it loses
        // there. shellRadius (m): the shell is checked as a tube of its caliber, so it can't thread between packed
        // rounds the way a thin line can.
        public static void NotePiece(PartScript part, float damage, Vector3 point, Vector3 direction, bool shell,
                                     float shellEnergyJ, float shellRadius)
        {
            Rack rack;
            if (damage <= 0f || part == null || !RackOf.TryGetValue(part, out rack) || rack.Grid == null) return;
            if (part.Aircraft == null || part.Aircraft.RemoteAircraft) return;
            if (throughFrame != Time.frameCount)
            {
                ThroughRounds.Clear();
                throughFrame = Time.frameCount;
            }

            Pieces pieces;
            if (!ThroughRounds.TryGetValue(part, out pieces)) ThroughRounds[part] = pieces = new Pieces();   // kept empty too: pieces hit it, none through its rounds
            if (rack.Grid.Remaining <= 0) return;
            if (!rack.Grid.HitsRounds(part.transform, point, direction, shell ? shellRadius : 0f))
            {
                if (shell) ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "': the shell's own path (" +
                    Fmt(shellRadius * 2000f, "F0") + " mm wide) missed its rounds");
                return;
            }
            pieces.Through += damage;
            if (!shell) return;
            pieces.Shells++;
            pieces.ShellKJ += Mathf.Max(shellEnergyJ, 0f) / 1000f;
        }

        private static bool TakeNoted(PartScript part, out Pieces pieces)
        {
            pieces = null;
            if (throughFrame != Time.frameCount || !ThroughRounds.TryGetValue(part, out pieces)) return false;
            ThroughRounds.Remove(part);
            return true;
        }

        // hitShare: the hit's damage as a share of the rack's health, before AmmoRackDamagePatch cut it (a rack
        // already held at 99.9% gains no damage level, but a hit there still counts for its rounds).
        public static void OnDamaged(PartScript part, float levelBefore, float hitShare, int? attacker,
                                     Vector3 position, Vector3 direction)
        {
            Rack rack;
            if (!RackOf.TryGetValue(part, out rack)) return;
            Pieces pieces;
            bool perPiece = TakeNoted(part, out pieces);
            if (rack.Burning || rack.Exploded || rack.Grid == null || rack.Grid.Remaining <= 0) return;

            AircraftScript aircraft = part.Aircraft;
            if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft) return;

            float level = DamageLevel(part);
            // "Destroyed" = the rack's box has lost all its health (held at 0.999 by AmmoRackDamagePatch; room for
            // rounding). That is the box, not the rounds: only a hit whose path crosses rounds can set them off.
            bool destroyedNow = level >= 0.998f && levelBefore < 0.998f;
            bool wreckedBefore = levelBefore >= 0.998f;

            // Every hit must pass through rounds, the destroying one too. A rack wrecked by a hit that missed its
            // rounds keeps them; later hits through them roll like any other hit.
            string levels = "damage level " + Fmt(levelBefore, "F3") + " -> " + Fmt(level, "F3") +
                            (destroyedNow ? " (destroyed by this hit)" : wreckedBefore ? " (already wrecked)" : "");
            // The chance comes from what hit the rounds, not from the rack's health (the mod's damage is
            // kJ x DamagePerKiloJoule, small against a fuselage's health):
            //   1 - (1 - CookOffDirectHit)^(shells through the rounds) x e^(-kJ into the rounds / CookOffEnergyKJ)
            // A shell punching through the cases is likely to set them off whatever its size; fragments add up by
            // their energy. Damage is turned back into kJ with DamagePerKiloJoule.
            float perKiloJoule = Mathf.Max(ArmorFragmentsPlugin.DamagePerKiloJoule.Value, 1e-6f);
            float rise;
            float kiloJoules;
            int shells = 0;
            if (perPiece)
            {
                // A shot of the mod's own (shell, fragments, bursts): only what went through rounds counts.
                if (pieces.Through <= 0f)
                {
                    ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' hit, but no piece went through its rounds (" + levels + ")");
                    return;
                }
                rise = pieces.Through / Mathf.Max(part.MaxHealth, 1f);
                // Fragments by what they left in the rounds, shells by what they carried into them.
                kiloJoules = pieces.Through / perKiloJoule + pieces.ShellKJ;
                shells = pieces.Shells;
            }
            else
            {
                // Any other hit (the game's own, or a summed hit from another player): its one line decides.
                rise = Mathf.Max(level - levelBefore, hitShare);
                if (rise <= 0f) return;
                if (!rack.Grid.HitsRounds(part.transform, position, direction))
                {
                    ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' hit where it holds no rounds (" + levels + ")");
                    return;
                }
                kiloJoules = rise * Mathf.Max(part.MaxHealth, 1f) / perKiloJoule;
            }
            levels += ", " + Fmt(kiloJoules, "F0") + " kJ into its rounds" +
                      (shells > 0 ? " (shell through them, a direct hit carrying " + Fmt(pieces.ShellKJ, "F0") + " kJ)" : "");

            if (destroyedNow)
            {
                Destroyed(rack, attacker, rise, false);   // the hit that wrecks it, through its rounds: always cooks off
                return;
            }

            float direct = Mathf.Clamp01(ArmorFragmentsPlugin.CookOffDirectHit.Value / 100f);
            float chance = 1f - Mathf.Pow(1f - direct, shells) *
                                Mathf.Exp(-kiloJoules / Mathf.Max(ArmorFragmentsPlugin.CookOffEnergyKJ.Value, 0.001f));
            float roll = UnityEngine.Random.value;
            bool fire = roll < chance;
            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' hit its rounds: " + levels +
                ", cook-off chance " + Fmt(chance * 100f, "F0") + "%, rolled " + Fmt(roll * 100f, "F0") +
                (fire ? "%: it catches fire" : "%: no fire"));
            if (fire) Ignite(rack, attacker, "hit");
        }

        // A rack destroyed (damage level 1, or the part gone) with rounds still in it always catches fire: it
        // burns, then explodes. A part already gone has nothing to burn, so it explodes at once.
        private static void Destroyed(Rack rack, int? attacker, float rise, bool partGone)
        {
            if (attacker.HasValue) rack.Attacker = attacker;
            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' was destroyed" + (partGone ? " (part gone)" : "") +
                " with " + rack.Grid.Remaining + " rounds in it (damage level rose by " + Fmt(rise, "F3") + "): it cooks off");

            if (partGone) ExplodeWithPartners(rack);
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

        // A fire that burns at half size and never goes out: the rack's fire before it explodes, and every vent's
        // burn-out. Also used by RackFireSync on the fires other players' games make for our cook-off.
        internal static void DressBurnOut(PartDamageEffect effect)
        {
            ScaleEffect(effect.transform, FireScale);
            KeepBurning(effect);
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
            float delay = Mathf.Max(ArmorFragmentsPlugin.CookOffBurnSeconds.Value, 0f);   // always the same burn
            rack.ExplodeAt = Time.time + delay;

            try
            {
                // The game's own burning-part effect (a fuel tank that is about to blow uses the same), at half
                // size. The effect is a fresh copy for this part (PartDamageEffects.CreateEffect
                // instantiates it), so shrinking it changes no other fire. Its particle systems are set to follow
                // their parents' scale. (The game sends the plain fire to the other players itself; RackFireSync
                // tells their mods to give it the same look.)
                if (rack.Part != null && rack.Part.Aircraft != null && rack.Part.Aircraft.DamageEffects != null)
                {
                    rack.Fire = rack.Part.Aircraft.DamageEffects.CreateFire(rack.Part, null);
                    if (rack.Fire != null)
                    {
                        DressBurnOut(rack.Fire);
                        RackFireSync.Send(rack.Part, RackFireSync.RackFire);
                    }
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack fire effect failed: " + e.Message);
            }

            ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' is burning (" + reason + "): " +
                rack.Grid.Remaining + " rounds, explodes in " + Fmt(delay, "F1") + " s");
            SyncCannons(rack.Craft);
        }

        // Racks of one vehicle due to explode within this many seconds of each other go up as one blast.
        private const float TogetherWindow = 0.5f;

        // A rack explodes, with every other burning rack of its vehicle that is due within TogetherWindow.
        private static void ExplodeWithPartners(Rack first)
        {
            List<Rack> racks = new List<Rack> { first };
            foreach (Rack other in first.Craft.Racks)
                if (other != first && other.Burning && !other.Exploded && other.ExplodeAt <= Time.time + TogetherWindow)
                    racks.Add(other);
            Explode(racks);
        }

        // One blast for these racks: their explosive adds up (capped at MaxBlastLb), measured from their center.
        private static void Explode(List<Rack> racks)
        {
            Rack first = racks[0];
            List<Rack> live = new List<Rack>();                                  // racks that still had rounds
            Dictionary<Rack, Vector3> centers = new Dictionary<Rack, Vector3>();
            Dictionary<Rack, float> rackPounds = new Dictionary<Rack, float>();
            float pounds = 0f;
            Vector3 weighted = Vector3.zero;
            int? attacker = null;

            foreach (Rack rack in racks)
            {
                int rounds = rack.Grid.Remaining;
                Vector3 center = rack.Part != null ? rack.Grid.WorldCenter(rack.Part.transform) : rack.LastPosition;
                rack.Exploded = true;
                rack.Burning = false;
                rack.Grid.EmptyAll();

                try
                {
                    if (rack.Fire != null && !rack.Fire.Destroyed) rack.Fire.DestroyEffect();
                    if (rack.Fire != null) RackFireSync.Send(rack.Part, RackFireSync.RackFireOut);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Ammo rack fire effect could not be removed: " + e.Message);
                }

                if (rounds <= 0)
                {
                    ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' burned out with no rounds left");
                    continue;
                }

                // Sized in pounds of explosive: each round adds its propellant's explosive equivalent
                // (AmmoRound.ExplosiveLb: ~0.58 lb for a 50 mm round at 835 m/s, so a Pz III's ~90 rounds make ~55 lb).
                float lb = rounds * rack.Round.ExplosiveLb * ArmorFragmentsPlugin.BlastStrength.Value;
                ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + rack.Label + "' exploded: " + rounds + " rounds of " +
                    Fmt(rack.Round.CaliberMm, "F0") + " mm = " + Fmt(lb, "F1") + " lb of explosive");

                live.Add(rack);
                centers[rack] = center;
                rackPounds[rack] = lb;
                pounds += lb;
                weighted += center * lb;
                if (!attacker.HasValue) attacker = rack.Attacker;
                rack.Group.Boomed = true;   // <group>_boom: detachers wired to it fire now (turret toss)
            }

            if (live.Count == 0)
            {
                SyncCannons(first.Craft);
                return;
            }

            float total = Mathf.Min(pounds, ArmorFragmentsPlugin.MaxBlastLb.Value);
            Vector3 blastCenter = pounds > 0f ? weighted / pounds : centers[live[0]];
            List<string> labels = new List<string>();
            foreach (Rack rack in live) labels.Add("'" + rack.Label + "'");
            string names = string.Join(" + ", labels.ToArray());
            ArmorFragmentsPlugin.Log.LogInfo("Cook-off of " + names + ": " + Fmt(total, "F1") + " lb of explosive" +
                (live.Count > 1 ? " (" + live.Count + " racks together)" : "") +
                (total < pounds ? " (capped from " + Fmt(pounds, "F1") + " lb at MaxBlastLb)" : "") +
                ", blast size " + Fmt(BangSize(total), "F2") + " (120 lb makes " + Fmt(BangSize(BangReferenceLb), "F2") + ")");

            KnockOut(first.Aircraft);    // before the cook: no game explosion may blow the shell apart
            Blast(live, centers, rackPounds, blastCenter, total, attacker);   // before the wreck: it may destroy a vent
            WreckInsides(live, blastCenter, total, attacker);
            SyncCannons(first.Craft);

            // A rack going up is the vehicle's kill switch: the game's own death (kill credit, kill cam, respawn),
            // when it actually explodes, not while it burns. A rack that broke off the vehicle first doesn't kill it.
            foreach (Rack rack in live)
            {
                if (rack.BrokenOff) continue;
                VehicleRules.Kill(first.Aircraft, "ammo rack " + names + " exploded");
                break;
            }
        }

        // ---- Knocked out -----------------------------------------------------------------
        //
        // A vehicle with a burning or exploded rack is a knocked-out shell (VehicleRules.KnockOut): the game's own
        // explosions for it are off (AircraftScript.QueueExplosion, the "vehicle destroyed" blast, and
        // BodyScript.ExplodePart, which blows single parts off and, for a part of the cockpit's group, queues that
        // blast), on any vehicle. When the rack explodes, the vehicle is killed (VehicleRules.Kill: the game's own
        // death, without breaking anything).

        private static void KnockOut(AircraftScript aircraft)
        {
            VehicleRules.KnockOut(aircraft, "a burning ammo rack");
        }

        // ---- The blast: one bang, then a jet of fire -------------------------------------------
        //
        // A cook-off is:
        //  1. One bang: a single explosion (AmmoRacks.BlastType, BombExplosion by default) at the rack (racks going
        //     up together: at their center, weighted by their explosive), sized by the pounds of explosive like the
        //     game's own bombs. Only for show.
        //  2. A jet of fire out of each rack's nearest cook-off vent ([VNT] part: a turret ring or hatch), or out of
        //     the rack with no vent, for 3 to 10 s (more pounds, longer): the game's own fire effect, scaled up and
        //     stretched upward, with denser flames. Racks going up together through the same vent make one jet.
        //  3. It then settles smoothly into the half-size burn-out fire, which never goes out (KeepBurning).
        // The game's blast does nothing (AmmoRackBlastPatch): WreckInsides does all the cook-off's damage.

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

        // The bang: a BangReferenceLb charge is sized like the game's bombs ("Boom 25", 250 lb = 0.8); other charges
        // grow with the cube root of their pounds, as a blast's reach does (Hopkinson-Cranz), so a cook-off of
        // low-velocity rounds (little propellant) still shows a cloud. Times BlastVisualPercent.
        // Only for show: AmmoRackBlastPatch gives the game's blast nothing to act on.
        private const float BangReferenceLb = 120f;

        private static float BangSize(float pounds)
        {
            if (pounds <= 0f) return 0f;
            float size = BangReferenceLb * BombSizePerLb * Mathf.Pow(pounds / BangReferenceLb, 1f / 3f);
            return size * Mathf.Max(ArmorFragmentsPlugin.BlastVisualPercent.Value, 0f) / 100f;
        }

        // Scales the fire jet's flare-up (size and extra flames).
        private const float JetSize = 1f / 3f;

        private static void Blast(List<Rack> racks, Dictionary<Rack, Vector3> centers, Dictionary<Rack, float> rackPounds,
                                  Vector3 center, float pounds, int? attacker)
        {
            AircraftScript aircraft = racks[0].Aircraft;

            // Each rack's fire comes out of its nearest vent (no vent: out of the rack itself); racks sharing a vent
            // make one jet, sized by their explosive together.
            List<PartScript> fireParts = new List<PartScript>();
            Dictionary<PartScript, float> jetPounds = new Dictionary<PartScript, float>();
            Dictionary<PartScript, List<string>> jetLabels = new Dictionary<PartScript, List<string>>();
            foreach (Rack rack in racks)
            {
                PartScript vent = NearestVent(aircraft, centers[rack]);
                PartScript firePart = vent != null ? vent : rack.Part;
                if (firePart == null) continue;
                if (!jetPounds.ContainsKey(firePart))
                {
                    fireParts.Add(firePart);
                    jetPounds[firePart] = 0f;
                    jetLabels[firePart] = new List<string>();
                }
                jetPounds[firePart] += rackPounds[rack];
                jetLabels[firePart].Add(rack.Label);
            }

            // 1. The bang, at the rack. Its message to other players needs a part of the vehicle to place it on.
            float size = BangSize(pounds);
            if (size > 0.0001f && FlightSceneScript.Instance != null)
            {
                Recent.Add(new RecentBlast { Position = center, Time = Time.time, Owner = aircraft });
                PartScript reference = null;
                foreach (Rack rack in racks)
                    if (rack.Part != null) { reference = rack.Part; break; }
                if (reference == null && fireParts.Count > 0) reference = fireParts[0];
                // Sent before the explosion, so other players' mods keep it off their copy of this vehicle too.
                RackFireSync.SendBlast(reference, center);
                FlightSceneScript.Instance.CreateExplosion(ArmorFragmentsPlugin.BlastType.Value, center, size, null,
                                                           attacker, null, ExplosiveWeaponImpactType.Structure);
            }

            // 2-3. The jets: each flares up at once (wider with more pounds, x 2.5 upward, more flames, all scaled
            // by JetSize) and settles into the burn-out.
            List<string> jets = new List<string>();
            foreach (PartScript firePart in fireParts)
            {
                float lb = Mathf.Min(jetPounds[firePart], pounds);
                float duration = Mathf.Clamp(2f + lb / 15f, 3f, 10f);
                float width = Mathf.Max(FireScale * 1.5f, Mathf.Clamp(1f + lb / 25f, 1.5f, 5f) * JetSize);
                string label = string.Join(" + ", jetLabels[firePart].ToArray());
                StartJet(firePart, label, duration, new Vector3(width, width * 2.5f, width), 1f + lb / 20f * JetSize);
                BurningVents.Add(firePart);
                jets.Add("'" + firePart.Part.Name + "' (" + Fmt(width, "F1") + " x " + Fmt(width * 2.5f, "F1") +
                         " for " + Fmt(duration, "F1") + " s)");
            }

            // 4. Every other vent of the vehicle catches the small burn-out fire too, a little later each (0.5 to
            //    2 s: the fire spreading through the hull). A vent already burning gets no second fire.
            int others = 0;
            foreach (PartScript other in Vents(aircraft))
            {
                if (jetPounds.ContainsKey(other) || BurningVents.Contains(other)) continue;
                BurningVents.Add(other);
                PendingFires.Add(new PendingFire { At = Time.time + UnityEngine.Random.Range(0.5f, 2f), Part = other });
                others++;
            }

            ArmorFragmentsPlugin.Log.LogInfo("Cook-off: one " + ArmorFragmentsPlugin.BlastType.Value + " of size " +
                Fmt(size, "F2") + " at the rack" + (racks.Count > 1 ? "s" : "") + ", then fire jet(s) out of " +
                (jets.Count > 0 ? string.Join(", ", jets.ToArray()) : "nothing (the rack is gone, no vent)") +
                ", settling into a burn-out that never goes out" +
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
                DressBurnOut(effect);
                RackFireSync.Send(part, RackFireSync.VentFire);
            }
            BurningVents.RemoveWhere(p => p == null);
        }

        private static void StartJet(PartScript part, string label, float duration, Vector3 jetScale, float emissionBoost)
        {
            if (part == null || part.Aircraft == null || part.Aircraft.DamageEffects == null) return;
            PartDamageEffect effect = part.Aircraft.DamageEffects.CreateFire(part, null);
            if (effect == null) return;
            DressJet(effect, label, duration, jetScale, emissionBoost);
            RackFireSync.Send(part, RackFireSync.Jet(duration, jetScale, emissionBoost));
        }

        // Turns a fire into the cook-off's jet, which then settles into the burn-out (UpdateBlasts). Also used by
        // RackFireSync on the fire other players' games make at our vent.
        internal static void DressJet(PartDamageEffect effect, string label, float duration, Vector3 jetScale, float emissionBoost)
        {
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

        // Another player's cook-off, announced by RackFireSync just before its explosion arrives: kept off their
        // vehicle (our copy of it) and off armor here too, as their own game does.
        internal static void NoteRemoteBlast(Vector3 at, AircraftScript owner)
        {
            Recent.Add(new RecentBlast { Position = at, Time = Time.time, Owner = owner });
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

        // The cook-off's blast, by real blast physics (a cook-off is an inefficient detonation):
        //  - Charge: W = the propellant's TNT equivalent (the rack's pounds, in kg) x CookOffTNTEfficiency (25%).
        //  - Hopkinson-Cranz scaling: what a blast does depends on the scaled distance Z = R / W^(1/3) (m/kg^1/3),
        //    so the reach grows with the cube root of the rounds left, not in proportion.
        //  - Peak overpressure (Mills' fit for a free-air blast): P = 1772/Z^3 - 114/Z^2 + 108/Z kPa.
        //  - Damage: none below CookOffDamageKPa (200 kPa), the part's whole health at CookOffWreckKPa (1000 kPa:
        //    wrecked), up to twice it closer in; in between it grows with ln(P), as damage does with pressure.
        //  - R is the distance to the part's nearest surface (its colliders), not its center.
        // Example at 25%: 96 lb (42 rounds of 100 mm) is 10.9 kg of TNT: wrecks within 2.7 m, damages out to 4.8 m.
        // It is the cook-off's only damage (the bang is just for show: AmmoRackBlastPatch), measured from the rack
        // (racks going up together: their center) for every vehicle, its own and the others around, by the same
        // rules (armor never; armored vehicles in modules and racks; others in every part). Armor in the way
        // shields, the burning vehicle's own hull included. A remote player's vehicle is damaged over the
        // network, as fragments are. Racks hit this way roll their own chance (chain).
        private static void WreckInsides(List<Rack> racks, Vector3 center, float pounds, int? attacker)
        {
            AircraftScript aircraft = racks[0].Aircraft;
            HashSet<PartScript> exploding = new HashSet<PartScript>();
            foreach (Rack rack in racks)
                if (rack.Part != null) exploding.Add(rack.Part);

            float tntKg = pounds * 0.4536f * Mathf.Clamp(ArmorFragmentsPlugin.CookOffTNTEfficiency.Value, 0f, 100f) / 100f;
            if (tntKg <= 0f) return;
            float cubeRoot = Mathf.Pow(tntKg, 1f / 3f);
            float onsetKPa = Mathf.Max(ArmorFragmentsPlugin.CookOffDamageKPa.Value, 1f);
            float wreckKPa = Mathf.Max(ArmorFragmentsPlugin.CookOffWreckKPa.Value, onsetKPa * 1.01f);
            float reach = ScaledDistanceAt(onsetKPa) * cubeRoot;       // m: no damage beyond
            float wreckWithin = ScaledDistanceAt(wreckKPa) * cubeRoot;  // m: for the log

            // Every part around the rack, of any vehicle, at its nearest surface.
            Dictionary<PartScript, Vector3> nearest = new Dictionary<PartScript, Vector3>();
            Collect(nearest, center, reach, exploding);

            int inside = 0, outside = 0, shielded = 0;
            float halvingMm = Mathf.Max(ArmorFragmentsPlugin.CookOffArmorHalvingMm.Value, 0.1f);
            HashSet<AircraftScript> others = new HashSet<AircraftScript>();
            foreach (KeyValuePair<PartScript, Vector3> pair in nearest)
            {
                PartScript part = pair.Key;
                if (part == null || part.Part == null) continue;
                bool own = part.Aircraft == aircraft;
                if (!VehicleRules.TakesModDamage(part, TagCodec.Parse(part.Part.Name))) continue;

                float distance = Vector3.Distance(pair.Value, center);
                if (distance >= reach) continue;

                // Armor in the way shields in proportion to how much of it there is: every
                // CookOffArmorHalvingMm of effective thickness halves the pressure.
                float pressure = Overpressure(Mathf.Max(distance, 0.05f) / cubeRoot);
                float armorMm = ArmorBetween(center, pair.Value, part);
                if (armorMm > 0f) pressure *= Mathf.Pow(0.5f, armorMm / halvingMm);
                if (pressure <= onsetKPa)
                {
                    if (armorMm > 0f) shielded++;
                    continue;
                }
                float share = Mathf.Log(pressure / onsetKPa) / Mathf.Log(wreckKPa / onsetKPa);   // 0 at onset, 1 = wrecked
                float damage = part.MaxHealth * Mathf.Clamp(share, 0f, 2f);
                if (damage <= 0f) continue;

                Vector3 away = pair.Value - center;
                Vector3 direction = away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.up;
                if (ApplyBlastDamage(part, damage, pair.Value, direction, attacker))
                {
                    if (own) inside++;
                    else
                    {
                        outside++;
                        if (part.Aircraft != null) others.Add(part.Aircraft);
                    }
                }
            }
            ArmorFragmentsPlugin.Log.LogInfo("Cook-off blast: " + Fmt(pounds, "F0") + " lb at " +
                Fmt(ArmorFragmentsPlugin.CookOffTNTEfficiency.Value, "F0") + "% = " + Fmt(tntKg, "F1") + " kg of TNT, from the rack" +
                (racks.Count > 1 ? "s" : "") + "; wrecks within " + Fmt(wreckWithin, "F1") + " m, damages out to " +
                Fmt(reach, "F1") + " m; damaged " + inside + " module(s) / rack(s) of this vehicle, " + outside +
                " part(s) of " + others.Count + " other vehicle(s)" +
                (shielded > 0 ? "; " + shielded + " part(s) in range saved by armor in the way" : ""));
        }

        // Effective armor (mm) on the straight line from the blast to a part: each whole-block [ARM] part crossed
        // counts its thickness / cos(angle), as for shells (the angle limited to ~78 deg), and so does each armored
        // face of a per-face block the line gets in or out through.
        private static readonly List<RayEvent> BetweenEvents = new List<RayEvent>();

        private static float ArmorBetween(Vector3 from, Vector3 to, PartScript target)
        {
            Vector3 line = to - from;
            float distance = line.magnitude;
            if (distance < 0.01f) return 0f;
            Vector3 direction = line / distance;

            float total = 0f;
            foreach (RayEvent e in RayEvents.Gather(from, direction, distance, target, null, BetweenEvents))
            {
                if (e.Part == null || e.Armor == null || e.Open) continue;
                float cos = Mathf.Abs(Vector3.Dot(e.Normal, direction));
                total += (e.Face ? e.Mm : e.Armor.Thickness) / Mathf.Max(cos, 0.2f);
            }
            return total;
        }

        // Parts of any vehicle with a collider within 'radius' of 'from', except the exploding racks.
        private static void Collect(Dictionary<PartScript, Vector3> nearest, Vector3 from, float radius, HashSet<PartScript> exploding)
        {
            foreach (Collider collider in Physics.OverlapSphere(from, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                if (collider == null) continue;
                if (collider.GetComponentInParent<CannonProjectileScript>() != null) continue;   // a shell in flight
                PartScript part = collider.GetComponentInParent<PartScript>();
                if (part == null || exploding.Contains(part)) continue;

                MeshCollider mesh = collider as MeshCollider;
                Vector3 point = mesh != null && !mesh.convex ? collider.bounds.ClosestPoint(from) : collider.ClosestPoint(from);
                Vector3 known;
                if (!nearest.TryGetValue(part, out known) || (point - from).sqrMagnitude < (known - from).sqrMagnitude)
                    nearest[part] = point;
            }
        }

        // Damages a part on this game, or tells its owner when it is another player's (like fragments do).
        private static bool ApplyBlastDamage(PartScript part, float damage, Vector3 point, Vector3 direction, int? attacker)
        {
            try
            {
                AircraftScript aircraft = part.Aircraft;
                if (aircraft != null && aircraft.RemoteAircraft)
                {
                    if (aircraft.NetworkAircraft == null) return false;
                    aircraft.NetworkAircraft.DamagePart(attacker, part, damage, point, direction);
                    return true;
                }
                part.OnDamaged(attacker, damage, point, direction);
                return true;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack blast damage failed on '" + part.Part.Name + "': " + e.Message);
                return false;
            }
        }

        // Mills (1987): peak incident overpressure, kPa, at scaled distance z (m/kg^1/3) from a TNT charge.
        private static float Overpressure(float z)
        {
            return 1772f / (z * z * z) - 114f / (z * z) + 108f / z;
        }

        // The scaled distance at which the overpressure falls to this (it only falls with distance).
        private static float ScaledDistanceAt(float kPa)
        {
            float near = 0.05f, far = 200f;
            for (int i = 0; i < 60; i++)
            {
                float mid = 0.5f * (near + far);
                if (Overpressure(mid) > kPa) near = mid;
                else far = mid;
            }
            return 0.5f * (near + far);
        }

        private static string Fmt(float value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        // ---- Designer read-outs -------------------------------------------------------------

        private static readonly PartData[] NoParts = new PartData[0];

        // The parts of the aircraft 'anyPart' belongs to.
        private static IEnumerable<PartData> DesignParts(PartScript anyPart)
        {
            if (anyPart == null || anyPart.Aircraft == null || anyPart.Aircraft.Aircraft == null ||
                anyPart.Aircraft.Aircraft.Assembly == null) return NoParts;
            return anyPart.Aircraft.Aircraft.Assembly.Parts;
        }

        // An ammo rack that can hold rounds (armor can't).
        public static bool IsDesignRack(PartData data)
        {
            if (data == null || data.PartScript == null) return false;
            PartTags tags = TagCodec.Parse(data.Name);
            return tags.AmmoRack != null && tags.Armor == null;
        }

        private static string LabelOf(PartData data)
        {
            string label = TagCodec.Parse(data.Name).Label;
            return string.IsNullOrEmpty(label) ? data.PartType.Name : label;
        }

        private static int OrderOf(PartData rack)
        {
            AmmoRackTag tag = TagCodec.Parse(rack.Name).AmmoRack;
            return tag != null && tag.Order.HasValue ? tag.Order.Value : int.MaxValue;
        }

        // The racks a cannon draws from, in the order it empties them (numbered ones first, lowest up; then the ones
        // with no order, nearest first). Ids in its list that are not racks (any more) are left out.
        public static List<PartData> DesignCannonRacks(PartScript cannonPart)
        {
            List<PartData> racks = new List<PartData>();
            if (cannonPart == null || cannonPart.Part == null) return racks;
            CannonTag cannon = TagCodec.Parse(cannonPart.Part.Name).Cannon;
            if (cannon == null || cannon.Racks.Count == 0) return racks;

            foreach (PartData data in DesignParts(cannonPart))
                if (data != null && cannon.Racks.Contains(data.Id) && IsDesignRack(data)) racks.Add(data);
            racks.Sort((a, b) =>
            {
                int orderA = OrderOf(a), orderB = OrderOf(b);
                if (orderA != orderB) return orderA.CompareTo(orderB);
                Vector3 gunAt = cannonPart.transform.position;
                float da = (a.PartScript.transform.position - gunAt).sqrMagnitude;
                float db = (b.PartScript.transform.position - gunAt).sqrMagnitude;
                return da != db ? da.CompareTo(db) : a.Id.CompareTo(b.Id);
            });
            return racks;
        }

        // The cannons that draw from a rack.
        public static List<PartData> DesignRackCannons(PartScript rackPart)
        {
            List<PartData> cannons = new List<PartData>();
            if (rackPart == null || rackPart.Part == null) return cannons;
            int id = rackPart.Part.Id;
            foreach (PartData data in DesignParts(rackPart))
            {
                if (data == null || data.PartScript == null) continue;
                CannonTag cannon = TagCodec.Parse(data.Name).Cannon;
                if (cannon != null && cannon.Racks.Contains(id)) cannons.Add(data);
            }
            return cannons;
        }

        public static string DesignLabel(PartData data)
        {
            return data != null ? LabelOf(data) : "";
        }

        // The round a rack holds: that of the cannons drawing from it, the biggest if they differ. 'except' is
        // left out (a cannon about to pick the rack).
        public static bool DesignRound(PartScript rackPart, out AmmoRound round, out int cannons, out bool mixed,
                                       PartData except = null)
        {
            round = default(AmmoRound);
            cannons = 0;
            mixed = false;
            foreach (PartData data in DesignRackCannons(rackPart))
            {
                if (data == except) continue;
                AmmoRound cannonRound;
                if (!AmmoRound.TryFromPart(data.PartScript, out cannonRound)) continue;
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

        // Can this cannon add 'candidate' to its racks? A rack holds one size of round, so a rack other cannons
        // of another size already draw from is left out.
        public static bool DesignCanPick(PartScript cannonPart, PartData candidate)
        {
            if (cannonPart == null || cannonPart.Part == null || candidate == null || candidate == cannonPart.Part ||
                !IsDesignRack(candidate)) return false;
            CannonTag cannon = TagCodec.Parse(cannonPart.Part.Name).Cannon;
            if (cannon != null && cannon.Racks.Contains(candidate.Id)) return false;

            AmmoRound own, theirs;
            int cannons;
            bool mixed;
            if (!AmmoRound.TryFromPart(cannonPart, out own)) return true;
            if (!DesignRound(candidate.PartScript, out theirs, out cannons, out mixed, cannonPart.Part)) return true;
            return own.SameSize(theirs);
        }

        // Does this cannon draw from ammo racks? (Designer: then its own Ammo Count slider means nothing.)
        public static bool DesignFedByRacks(CannonData cannon)
        {
            PartData data = cannon != null ? cannon.Part : null;
            if (data == null || data.PartScript == null) return false;
            return DesignCannonRacks(data.PartScript).Count > 0;
        }

        // Cheap strings that change whenever a rack's read-outs or drawing would (designer).
        public static string DesignRackSignature(PartScript rack)
        {
            AmmoRound round;
            int cannons;
            bool mixed;
            string roundText = DesignRound(rack, out round, out cannons, out mixed)
                ? round.Describe() + mixed + cannons : "none";
            return roundText + "|" + ShapeSignature(rack) + "|" +
                   ArmorFragmentsPlugin.CaseDiameterRatio.Value + "/" + ArmorFragmentsPlugin.CaseLitresPerMJ.Value + "/" +
                   ArmorFragmentsPlugin.RoundSpacingPercent.Value + "|f" + RackFill(rack);
        }

        // The rack's Fullness (f=), percent.
        private static float RackFill(PartScript rack)
        {
            AmmoRackTag tag = rack != null && rack.Part != null ? TagCodec.Parse(rack.Part.Name).AmmoRack : null;
            return tag != null && tag.Fill.HasValue ? tag.Fill.Value : 100f;
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

        // Total rounds the racks of this cannon hold (designer). 'shared': one of them also feeds another cannon.
        public static int DesignCapacity(PartScript cannonPart, out int racks, out bool shared)
        {
            racks = 0;
            shared = false;
            int total = 0;
            foreach (PartData rack in DesignCannonRacks(cannonPart))
            {
                racks++;
                AmmoRound round;
                int cannons;
                bool mixed;
                if (!DesignRound(rack.PartScript, out round, out cannons, out mixed)) continue;
                if (cannons > 1) shared = true;
                total += RackGrid.Measure(rack.PartScript, round).Remaining;   // loaded, after Fullness
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
    // AircraftScript.QueueExplosion), skipping the burn. The damage is cut so the rack stops at 99.9% of its
    // health, which counts as "destroyed" for the cook-off.
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

    // A rack's bang is only for show: the game's blast would push the parts it reaches, knock off those where
    // its force beats a random threshold (30..95), armor included, and damage them, on every vehicle around. For
    // our blasts the game gets an empty list, so it does none of that; AmmoRacks.WreckInsides does the damage,
    // by blast physics, inside the vehicle and around it. Other explosions are untouched.
    [HarmonyPatch(typeof(BodyScript), "HandleExplosiveBlast")]
    internal static class AmmoRackBlastPatch
    {
        private static void Prefix(ref List<PartScript> parts, Vector3 blastOrigin)
        {
            try
            {
                AircraftScript owner;
                if (parts == null || !AmmoRacks.IsRackBlast(blastOrigin, out owner)) return;
                parts = new List<PartScript>();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack blast filter failed: " + e.Message);
            }
        }
    }

    // The game blows a vehicle up only because of damage (a part of the cockpit's group exploding, or the damage
    // limit). Off for a knocked-out vehicle (AmmoRacks.KnockOut), and for armored vehicles while the game's damage
    // explosions are off (VehicleRules).
    [HarmonyPatch(typeof(AircraftScript), "QueueExplosion")]
    internal static class KnockedOutVehicleExplosionPatch
    {
        private static bool Prefix(AircraftScript __instance)
        {
            return VehicleRules.GameMayExplode(__instance);
        }
    }

    // ExplodePart is also never allowed on a rack or a crew block (Crew.cs): every fuselage type has
    // damageDisconnect="true", so each new damage stage rolls a chance to ExplodePart it
    // (BodyScript.OnPartDamageLevelIncreased) before it reaches 0 health, and for a part in the cockpit's group
    // that blows the whole vehicle up at once.
    // ExplodePart is only ever called because of damage (the damage-stage roll, fuel tanks, propellers, explosion
    // chains): see VehicleRules for all of them. Same rule as above.
    [HarmonyPatch(typeof(BodyScript), "ExplodePart", new Type[] { typeof(PartScript) })]
    internal static class KnockedOutPartExplosionPatch
    {
        private static bool Prefix(PartScript part)
        {
            return part == null || (!AmmoRacks.IsRack(part) && !Crew.IsCrew(part) && VehicleRules.GameMayExplode(part.Aircraft));
        }
    }

    [HarmonyPatch(typeof(BodyScript), "ExplodePart", new Type[] { typeof(PartScript), typeof(float), typeof(int) })]
    internal static class KnockedOutPartExplosionCascadePatch
    {
        private static bool Prefix(PartScript part)
        {
            return part == null || (!AmmoRacks.IsRack(part) && !Crew.IsCrew(part) && VehicleRules.GameMayExplode(part.Aircraft));
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
