using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ArmorFragments
{
    // The properties we store inside a part's NAME, so no part has to be changed.
    // In the name they look like:   Front plate [ARM t=20 h=100] [MOD d=1]
    // Everything outside the [...] blocks stays the player's own label.

    // Fuselage used as armor.
    internal sealed class ArmorTag
    {
        public float Thickness = 20f;   // t: millimeters
        public float Hardness = 100f;   // h: percent, 100 = reference steel (ricochet)
        public float Brittleness = 50f; // b: percent, how easily the plate shatters into fragments
    }

    // Cannon: describes the shells it fires. (The shell's energy comes from its real mass and
    // speed, so there is no penetration value any more; an old p= in a name is simply ignored.
    // So is an old f= (fragment count): how many fragments a plate makes is the plate's doing.)
    internal sealed class CannonTag
    {
        public float ShellHardness = 100f; // k: percent, 100 = reference (ricochet)

        // Explosive ammunition only (the game's Projectile Type: Explosive):
        public float? Fuse;                // z: meters after the impact at which the shell bursts (none = no fuse: HE)
        public float? FillerPercent;       // e: explosive filler, percent of the shell's mass (none = the kind's default)
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
    }

    internal static class TagCodec
    {
        // [ARM t=20 h=100]   -> kind + a list of key=value pairs
        private static readonly Regex Block =
            new Regex(@"\[(ARM|CAN|MOD)((?:\s+[a-z]+=[^\s\]]+)*)\s*\]");
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
                    };
                }
                else if (kind == "CAN")
                {
                    tags.Cannon = new CannonTag
                    {
                        ShellHardness = Get(values, "k", 100f),
                        Fuse = Optional(values, "z"),
                        FillerPercent = Optional(values, "e"),
                    };
                }
                else if (kind == "MOD")
                {
                    tags.Module = new ModuleTag { Variable = ReadText(block.Groups[2].Value, "v") };
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
                Append(name, "[ARM t=" + Number(tags.Armor.Thickness) + " h=" + Number(tags.Armor.Hardness) +
                       " b=" + Number(tags.Armor.Brittleness) + "]");
            if (tags.Cannon != null)
                Append(name, "[CAN k=" + Number(tags.Cannon.ShellHardness) +
                       (tags.Cannon.Fuse.HasValue ? " z=" + Number(tags.Cannon.Fuse.Value) : "") +
                       (tags.Cannon.FillerPercent.HasValue ? " e=" + Number(tags.Cannon.FillerPercent.Value) : "") + "]");
            if (tags.Module != null)
                Append(name, string.IsNullOrEmpty(tags.Module.Variable)
                    ? "[MOD]" : "[MOD v=" + tags.Module.Variable + "]");

            return name.Length == 0 ? fallback : name.ToString();
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
