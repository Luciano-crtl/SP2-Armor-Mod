using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ArmorFragments
{
    // The properties we store inside a part's NAME, so no part has to be changed.
    // In the name they look like:   Front plate [ARM t=20 h=100] [MOD d=1]
    //                               Hull front [ARM h=100 b=50 fr=80 lt=20 rt=20]
    // Everything outside the [...] blocks stays the player's own label.

    // Fuselage used as armor. Two kinds:
    //  - per face (Faces set): the block is a hollow shell whose six faces (see ArmorFaces) are plates of their
    //    own thickness (fr bk lt rt tp bm, millimeters; 0 = open). A plate is met from outside or from inside
    //    the block; an open face and the inside of the block are empty space.
    //  - whole block (Faces null): the whole part is one plate of thickness t, met wherever the block is entered.
    internal sealed class ArmorTag
    {
        public float Thickness = 20f;   // t: millimeters (whole block; in a face's plate, that face's thickness)
        public float Hardness = 100f;   // h: percent, 100 = reference steel (ricochet)
        public float Brittleness = 50f; // b: percent, how easily the plate shatters into fragments
        public float[] Faces;           // millimeters per face, indexed by BlockFace (null = whole block)

        public bool PerFace
        {
            get { return Faces != null; }
        }

        // One face's plate: this armor's material with that face's thickness.
        public ArmorTag Plate(float thicknessMm)
        {
            return new ArmorTag { Thickness = thicknessMm, Hardness = Hardness, Brittleness = Brittleness };
        }
    }

    // Cannon: describes the shells it fires. (The shell's energy comes from its real mass and
    // speed, so there is no penetration value any more; an old p= in a name is simply ignored.
    // So is an old f= (fragment count): how many fragments a plate makes is the plate's doing.)
    internal sealed class CannonTag
    {
        public float ShellHardness = 100f; // k: percent, 100 = reference (ricochet)

        // r: part ids of the ammo racks it draws from, comma-separated (empty = the game's Ammo Count).
        public List<int> Racks = new List<int>();

        // Explosive ammunition only (the game's Projectile Type: Explosive):
        public float? Fuse;                // z: meters after the impact at which the shell bursts (none = no fuse: HE)
        public float? FillerPercent;       // e: explosive filler, percent of the shell's mass (none = the kind's default)
        public float? Brittleness;         // q: how easily the casing breaks into small fragments, percent (none = the setting's default)
    }

    // Fuselage used as an ammo rack: it holds the rounds of the cannons that list it in [CAN r=] (see AmmoRacks.cs).
    internal sealed class AmmoRackTag
    {
        public string Group;               // g: names its Funky Trees variables, <group>_rounds... (null = the default group)
        public int? Order;                 // o: a cannon empties its racks from the lowest number up (null = after the numbered ones)
        public float? Fill;                // f: percent of its rounds loaded at spawn, the top ones left out (null = full)
    }

    // Part that reacts to fragment damage (engine, tank, propeller, some fuselages).
    internal sealed class ModuleTag
    {
        public string Variable;        // v: name of the Funky Trees variable that carries the part's damage (null = from the part's name)
    }

    internal sealed class PartTags
    {
        public string Label = "";      // the name without our blocks
        public ArmorTag Armor;         // null = the part is not armor
        public CannonTag Cannon;
        public ModuleTag Module;
        public AmmoRackTag AmmoRack;
        public VentTag Vent;
        public CrewTag Crew;
        public AimTag Aim;
        public HoleTag Hole;           // null = the walls of the holes this cutter carves are no armor
        public TurretTag Turret;       // a rotator the mouse aim turns (see TurretAim.cs)
        public PointerTag Pointer;     // a cannon the mouse aim points at the cursor, with its marker on the screen
        public SpeedTag Speed;         // a rotator or hinge with its own top speed
    }

    // Rotator (turret ring, gun elevation) that the mouse aim turns so that the pointer cannons point at the cursor.
    internal sealed class TurretTag
    {
    }

    // A rotator or hinge whose top speed is set in degrees per second instead of the game's Speed slider (see RotatorSpeedPatch).
    internal sealed class SpeedTag
    {
        public float DegreesPerSecond = 30f;   // s
    }

    // A cannon under the mouse aim (the designer's "Mouse aim" toggle on a cannon). With no group (g=0) it is one of the vehicle's main set: every
    // such cannon follows the reticle and fires together. With a group number it is in that turret group (every turret of a group is aimed at the
    // same time, at the same point; they are equals). The screen shows where each one really points.
    internal sealed class PointerTag
    {
        public int Group = 1;   // g: the turret group's number (turrets with the same number are one group); 0 = no group: the main set
        public int? Traverse;   // tr: id of the rotator that turns the gun left and right (none = left to the player's own controls)
        public int? Elevation;  // el: id of the rotator or hinge that raises and lowers the gun (none = left to the player's own controls)
        public string FireKey;  // fk: the key this cannon fires with when it fires by key (a Key name; null = the fire key in the config)
        public bool GunAsSight; // gs: the gun's own barrel is its sight (sniper view from the muzzle, looking down the barrel)
        public bool RangingGun; // rg: in a turret group whose guns share a Gun Sight, the gun that sight's range marks are worked out for
        public string Name;     // n: the group's name (letters, digits, _ - . ; null = the one its number gives, see GroupNames)
        public string TurretName; // t: this turret's own name (same characters as n; null = its number among the turrets, see TurretNames)
        public List<int> Rotators = new List<int>(); // r: ids of the rotators that turn this turret (none = the mod works out which ones move it)
        public int? Camera;     // c: id of the camera part used as this turret's optical sight (none = no sight)
        public bool ByKey;      // f: fires with the fire key instead of the mouse button

        // x: ids of the cannons fixed to this turret (they move with it but are not aimed by themselves); k: those of them that fire by key.
        public List<int> Fixed = new List<int>();
        public List<int> FixedByKey = new List<int>();
    }

    // A carving part (hole cutter): the walls of the holes it carves into armor blocks are plates of this thickness.
    internal sealed class HoleTag
    {
        public float Thickness = 20f;  // t: millimeters
    }

    // Rotator turned by the crew's Aim job (see Crew.cs): it turns at its speed x the aim efficiency.
    internal sealed class AimTag
    {
    }

    // Seat used as a crew member (see Crew.cs): dies when the seat has lost all its health; the vehicle dies
    // with its last crew member.
    internal sealed class CrewTag
    {
        public CrewTrade Trade = CrewTrade.None;       // t: what the crewman is trained as (none: no role yet)
        public CrewJobs Jobs = CrewJobs.None;          // j: jobs he also does besides his trade's own (drive,aim,load)
    }

    internal enum CrewTrade
    {
        Commander,
        Gunner,
        Loader,
        Driver,
        Radio,
        None,        // no role: a new, copied or mirrored crew seat until the player picks one
    }

    [System.Flags]
    internal enum CrewJobs
    {
        None = 0,
        Drive = 1,   // engines' power
        Aim = 2,     // rotators' speed
        Load = 4,    // reload of rack-fed cannons
    }

    // Any part: ammo rack cook-offs vent through the nearest one of these (a turret ring, a hatch).
    internal sealed class VentTag
    {
    }

    internal static class TagCodec
    {
        // [ARM t=20 h=100]   -> kind + a list of key=value pairs
        private static readonly Regex Block =
            new Regex(@"\[(ARM|CAN|MOD|AMM|VNT|CRW|AIM|HOL|TUR|PTR|SPD)((?:\s+[a-z]+=[^\s\]]+)*)\s*\]");
        private static readonly Regex Pair = new Regex(@"([a-z]+)=([^\s\]]+)");
        private static readonly Regex ExtraSpaces = new Regex(@"\s{2,}");

        // Names are parsed on every shell impact, so results are remembered.
        private static readonly Dictionary<string, PartTags> Cache = new Dictionary<string, PartTags>();
        private const int CacheLimit = 256;

        // For reading only: the returned object is shared, do not change it.
        public static PartTags Parse(string name)
        {
            string key = name ?? "";
            PartTags tags;
            if (Cache.TryGetValue(key, out tags)) return tags;

            if (Cache.Count >= CacheLimit) Cache.Clear();
            tags = ParseCopy(key);
            Cache[key] = tags;
            return tags;
        }

        // A fresh object you may change (used by the designer section).
        public static PartTags ParseCopy(string name)
        {
            string text = name ?? "";
            PartTags tags = new PartTags();

            foreach (Match block in Block.Matches(text))
            {
                string kind = block.Groups[1].Value;
                Dictionary<string, float> values = ReadPairs(block.Groups[2].Value);

                if (kind == "ARM")
                {
                    tags.Armor = new ArmorTag
                    {
                        Thickness = Get(values, "t", 20f),
                        Hardness = Get(values, "h", 100f),
                        Brittleness = Get(values, "b", 50f),
                        Faces = ReadFaces(values),
                    };
                }
                else if (kind == "CAN")
                {
                    tags.Cannon = new CannonTag
                    {
                        ShellHardness = Get(values, "k", 100f),
                        Fuse = Optional(values, "z"),
                        FillerPercent = Optional(values, "e"),
                        Brittleness = Optional(values, "q"),
                        Racks = ReadIds(ReadText(block.Groups[2].Value, "r")),
                    };
                }
                else if (kind == "MOD")
                {
                    tags.Module = new ModuleTag { Variable = ReadText(block.Groups[2].Value, "v") };
                }
                else if (kind == "VNT")
                {
                    tags.Vent = new VentTag();
                }
                else if (kind == "AIM")
                {
                    tags.Aim = new AimTag();
                }
                else if (kind == "HOL")
                {
                    tags.Hole = new HoleTag { Thickness = Get(values, "t", 20f) };
                }
                else if (kind == "TUR")
                {
                    tags.Turret = new TurretTag();
                }
                else if (kind == "SPD")
                {
                    tags.Speed = new SpeedTag { DegreesPerSecond = Mathf.Max(0f, Get(values, "s", 30f)) };
                }
                else if (kind == "PTR")
                {
                    float camera, group, byKey, traverse, elevation, gunSight, ranging;
                    tags.Pointer = new PointerTag
                    {
                        Group = values.TryGetValue("g", out group) && group >= 0f ? Mathf.RoundToInt(group) : 1,
                        Traverse = values.TryGetValue("tr", out traverse) && traverse >= 0f ? Mathf.RoundToInt(traverse) : (int?)null,
                        Elevation = values.TryGetValue("el", out elevation) && elevation >= 0f ? Mathf.RoundToInt(elevation) : (int?)null,
                        FireKey = CleanKey(ReadText(block.Groups[2].Value, "fk")),
                        GunAsSight = values.TryGetValue("gs", out gunSight) && gunSight > 0f,
                        RangingGun = values.TryGetValue("rg", out ranging) && ranging > 0f,
                        Name = GroupNames.Clean(ReadText(block.Groups[2].Value, "n")),
                        TurretName = GroupNames.Clean(ReadText(block.Groups[2].Value, "t")),
                        Rotators = ReadIds(ReadText(block.Groups[2].Value, "r")),
                        Camera = values.TryGetValue("c", out camera) && camera >= 0f ? Mathf.RoundToInt(camera) : (int?)null,
                        ByKey = values.TryGetValue("f", out byKey) && byKey > 0f,
                        Fixed = ReadIds(ReadText(block.Groups[2].Value, "x")),
                        FixedByKey = ReadIds(ReadText(block.Groups[2].Value, "k")),
                    };
                }
                else if (kind == "CRW")
                {
                    tags.Crew = new CrewTag
                    {
                        Trade = ReadTrade(ReadText(block.Groups[2].Value, "t")),
                        Jobs = ReadJobs(ReadText(block.Groups[2].Value, "j")),
                    };
                }
                else if (kind == "AMM")
                {
                    float order;
                    tags.AmmoRack = new AmmoRackTag
                    {
                        Group = ReadText(block.Groups[2].Value, "g"),
                        Order = values.TryGetValue("o", out order) && order >= 0.5f ? Mathf.RoundToInt(order) : (int?)null,   // none or 0 = no order
                        Fill = Optional(values, "f"),
                    };
                }
            }

            string label = Block.Replace(text, "");
            tags.Label = ExtraSpaces.Replace(label, " ").Trim();
            return tags;
        }

        // Label first, then one block per active kind. 'fallback' is used if nothing is left.
        public static string Compose(PartTags tags, string fallback)
        {
            StringBuilder name = new StringBuilder(tags.Label ?? "");

            if (tags.Armor != null)
                Append(name, "[ARM" + (tags.Armor.PerFace ? "" : " t=" + Number(tags.Armor.Thickness)) +
                       " h=" + Number(tags.Armor.Hardness) + " b=" + Number(tags.Armor.Brittleness) +
                       FacePairs(tags.Armor.Faces) + "]");
            if (tags.Cannon != null)
                Append(name, "[CAN k=" + Number(tags.Cannon.ShellHardness) +
                       (tags.Cannon.Fuse.HasValue ? " z=" + Number(tags.Cannon.Fuse.Value) : "") +
                       (tags.Cannon.FillerPercent.HasValue ? " e=" + Number(tags.Cannon.FillerPercent.Value) : "") +
                       (tags.Cannon.Brittleness.HasValue ? " q=" + Number(tags.Cannon.Brittleness.Value) : "") +
                       IdsPair(tags.Cannon.Racks) + "]");
            if (tags.Module != null)
                Append(name, string.IsNullOrEmpty(tags.Module.Variable)
                    ? "[MOD]" : "[MOD v=" + tags.Module.Variable + "]");
            if (tags.AmmoRack != null)
                Append(name, "[AMM" +
                       (string.IsNullOrEmpty(tags.AmmoRack.Group) ? "" : " g=" + tags.AmmoRack.Group) +
                       (tags.AmmoRack.Order.HasValue ? " o=" + tags.AmmoRack.Order.Value : "") +
                       (tags.AmmoRack.Fill.HasValue && tags.AmmoRack.Fill.Value < 100f ? " f=" + Number(tags.AmmoRack.Fill.Value) : "") + "]");
            if (tags.Vent != null)
                Append(name, "[VNT]");
            if (tags.Crew != null)
                Append(name, "[CRW" + (tags.Crew.Trade == CrewTrade.None ? "" : " t=" + tags.Crew.Trade.ToString().ToLowerInvariant()) +
                       JobsPair(tags.Crew.Jobs) + "]");
            if (tags.Aim != null)
                Append(name, "[AIM]");
            if (tags.Hole != null)
                Append(name, "[HOL t=" + Number(tags.Hole.Thickness) + "]");
            if (tags.Turret != null)
                Append(name, "[TUR]");
            if (tags.Pointer != null)
                Append(name, "[PTR g=" + Mathf.Max(0, tags.Pointer.Group).ToString(CultureInfo.InvariantCulture) +
                       (GroupNames.Clean(tags.Pointer.Name) != null ? " n=" + GroupNames.Clean(tags.Pointer.Name) : "") +
                       (GroupNames.Clean(tags.Pointer.TurretName) != null ? " t=" + GroupNames.Clean(tags.Pointer.TurretName) : "") +
                       IdsPair(tags.Pointer.Rotators, "r") +
                       (tags.Pointer.Camera.HasValue ? " c=" + tags.Pointer.Camera.Value.ToString(CultureInfo.InvariantCulture) : "") +
                       (tags.Pointer.ByKey ? " f=1" : "") + IdsPair(tags.Pointer.Fixed, "x") + IdsPair(tags.Pointer.FixedByKey, "k") +
                       (tags.Pointer.Traverse.HasValue ? " tr=" + tags.Pointer.Traverse.Value.ToString(CultureInfo.InvariantCulture) : "") +
                       (tags.Pointer.Elevation.HasValue ? " el=" + tags.Pointer.Elevation.Value.ToString(CultureInfo.InvariantCulture) : "") +
                       (CleanKey(tags.Pointer.FireKey) != null ? " fk=" + CleanKey(tags.Pointer.FireKey) : "") + (tags.Pointer.GunAsSight ? " gs=1" : "") + (tags.Pointer.RangingGun ? " rg=1" : "") + "]");
            if (tags.Speed != null)
                Append(name, "[SPD s=" + Number(tags.Speed.DegreesPerSecond) + "]");

            return name.Length == 0 ? fallback : name.ToString();
        }

        // The name with only the [CAN] block's r= list replaced (removed when 'racks' is empty); the rest of
        // the name is kept exactly as it is.
        public static string WithCannonRacks(string name, IList<int> racks)
        {
            string text = name ?? "";
            Match block = CannonBlock.Match(text);
            if (!block.Success) return text;

            string pairs = RacksPair.Replace(block.Groups[1].Value, "") + IdsPair(racks);
            return text.Substring(0, block.Index) + "[CAN" + pairs + "]" + text.Substring(block.Index + block.Length);
        }

        // The name with only the [PTR] block's part ids replaced: c= (the sight camera), x= (the fixed cannons) and k= (the ones of them that fire
        // by key); the rest is kept. (An older a= or s= is dropped.)
        public static string WithPointerLinks(string name, int? camera, IList<int> fixedIds, IList<int> fixedByKey)
        {
            return WithPointerLinks(name, camera, fixedIds, fixedByKey, null);
        }

        // The same, and r= (the rotators of the turret) too; null keeps the rotators as they are.
        public static string WithPointerLinks(string name, int? camera, IList<int> fixedIds, IList<int> fixedByKey, IList<int> rotators)
        {
            string text = name ?? "";
            Match block = PointerBlock.Match(text);
            if (!block.Success) return text;
            string kept = rotators == null ? block.Groups[1].Value : PointerRotatorPair.Replace(block.Groups[1].Value, "");
            string pairs = PointerIdPair.Replace(kept, "") +
                           (camera.HasValue ? " c=" + camera.Value.ToString(CultureInfo.InvariantCulture) : "") + IdsPair(fixedIds, "x") + IdsPair(fixedByKey, "k") +
                           (rotators == null ? "" : IdsPair(rotators, "r"));
            return text.Substring(0, block.Index) + "[PTR" + pairs + "]" + text.Substring(block.Index + block.Length);
        }

        // The name with only the [PTR] block's tr= and el= (the traverse and elevation rotators) replaced; the rest is kept.
        public static string WithPointerAxes(string name, int? traverse, int? elevation)
        {
            string text = name ?? "";
            Match block = PointerBlock.Match(text);
            if (!block.Success) return text;
            string pairs = PointerAxisPair.Replace(block.Groups[1].Value, "") +
                           (traverse.HasValue ? " tr=" + traverse.Value.ToString(CultureInfo.InvariantCulture) : "") +
                           (elevation.HasValue ? " el=" + elevation.Value.ToString(CultureInfo.InvariantCulture) : "");
            return text.Substring(0, block.Index) + "[PTR" + pairs + "]" + text.Substring(block.Index + block.Length);
        }

        // A key name as the tags keep it: letters and digits only (a Key of the input system: Space, F, Digit1, LeftCtrl...); null if empty.
        public static string CleanKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            StringBuilder clean = new StringBuilder();
            foreach (char c in text) if (char.IsLetterOrDigit(c) && c < 128) clean.Append(c);
            return clean.Length > 0 ? clean.ToString() : null;
        }

        private static readonly Regex PointerAxisPair = new Regex(@"\s+(?:tr|el)=[^\s\]]+");
        private static readonly Regex PointerBlock = new Regex(@"\[PTR((?:\s+[a-z]+=[^\s\]]+)*)\s*\]");
        private static readonly Regex PointerIdPair = new Regex(@"\s+(?:c|x|k|a|s)=[^\s\]]+");
        private static readonly Regex PointerRotatorPair = new Regex(@"\s+r=[^\s\]]+");

        private static readonly Regex CannonBlock = new Regex(@"\[CAN((?:\s+[a-z]+=[^\s\]]+)*)\s*\]");
        private static readonly Regex RacksPair = new Regex(@"\s+r=[^\s\]]+");

        private static string IdsPair(IList<int> ids)
        {
            return IdsPair(ids, "r");
        }

        private static string IdsPair(IList<int> ids, string key)
        {
            if (ids == null || ids.Count == 0) return "";
            StringBuilder text = new StringBuilder(" " + key + "=");
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(ids[i].ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        // t=commander|gunner|loader|driver|radio (none or anything else: no role).
        private static CrewTrade ReadTrade(string text)
        {
            if (!string.IsNullOrEmpty(text))
                foreach (CrewTrade trade in (CrewTrade[])System.Enum.GetValues(typeof(CrewTrade)))
                    if (string.Equals(trade.ToString(), text, System.StringComparison.OrdinalIgnoreCase)) return trade;
            return CrewTrade.None;
        }

        // j=drive,aim,load
        private static CrewJobs ReadJobs(string text)
        {
            CrewJobs jobs = CrewJobs.None;
            if (string.IsNullOrEmpty(text)) return jobs;
            foreach (string item in text.Split(','))
            {
                if (string.Equals(item, "drive", System.StringComparison.OrdinalIgnoreCase)) jobs |= CrewJobs.Drive;
                else if (string.Equals(item, "aim", System.StringComparison.OrdinalIgnoreCase)) jobs |= CrewJobs.Aim;
                else if (string.Equals(item, "load", System.StringComparison.OrdinalIgnoreCase)) jobs |= CrewJobs.Load;
            }
            return jobs;
        }

        private static string JobsPair(CrewJobs jobs)
        {
            List<string> names = new List<string>();
            if ((jobs & CrewJobs.Drive) != 0) names.Add("drive");
            if ((jobs & CrewJobs.Aim) != 0) names.Add("aim");
            if ((jobs & CrewJobs.Load) != 0) names.Add("load");
            return names.Count == 0 ? "" : " j=" + string.Join(",", names.ToArray());
        }

        // "12,15" -> [12, 15] (no repeats, in order).
        private static List<int> ReadIds(string text)
        {
            List<int> ids = new List<int>();
            if (string.IsNullOrEmpty(text)) return ids;
            foreach (string item in text.Split(','))
            {
                int id;
                if (int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && !ids.Contains(id))
                    ids.Add(id);
            }
            return ids;
        }

        // fr= bk= lt= rt= tp= bm=: any of them makes the armor per face (a face not listed is open).
        private static float[] ReadFaces(Dictionary<string, float> values)
        {
            float[] faces = null;
            for (int i = 0; i < ArmorFaces.Count; i++)
            {
                float value;
                if (!values.TryGetValue(ArmorFaces.Keys[i], out value)) continue;
                if (faces == null) faces = new float[ArmorFaces.Count];
                faces[i] = Mathf.Max(value, 0f);
            }
            return faces;
        }

        // The armored faces only. With every face open it writes fr=0, so the armor stays per face.
        private static string FacePairs(float[] faces)
        {
            if (faces == null) return "";
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < faces.Length && i < ArmorFaces.Count; i++)
                if (faces[i] > 0f) text.Append(' ').Append(ArmorFaces.Keys[i]).Append('=').Append(Number(faces[i]));
            return text.Length > 0 ? text.ToString() : " " + ArmorFaces.Keys[0] + "=0";
        }

        private static Dictionary<string, float> ReadPairs(string text)
        {
            Dictionary<string, float> values = new Dictionary<string, float>();
            foreach (Match pair in Pair.Matches(text))
            {
                float number;
                if (float.TryParse(pair.Groups[2].Value, NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out number))
                    values[pair.Groups[1].Value] = number;
            }
            return values;
        }

        // The text after 'key=' in a block, or null.
        private static string ReadText(string text, string key)
        {
            foreach (Match pair in Pair.Matches(text))
                if (pair.Groups[1].Value == key) return pair.Groups[2].Value;
            return null;
        }

        private static float? Optional(Dictionary<string, float> values, string key)
        {
            float value;
            if (values.TryGetValue(key, out value)) return value;
            return null;
        }

        private static float Get(Dictionary<string, float> values, string key, float fallback)
        {
            float value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        private static void Append(StringBuilder name, string block)
        {
            if (name.Length > 0) name.Append(' ');
            name.Append(block);
        }

        private static string Number(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
