using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ArmorFragments
{
    // The names of turret groups. A group is a number (the g= of the [PTR] tag) and may have a name (n=); a new group gets the next free name
    // of the military (NATO) phonetic alphabet, and the player can rename it in the designer. A group without a name shows the one its number
    // gives: Alpha for 1, Bravo for 2... (Alpha 2 for 27 ...).
    internal static class GroupNames
    {
        public static readonly string[] Phonetic =
        {
            "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliet", "Kilo", "Lima", "Mike",
            "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu",
        };

        public const int MaxLength = 16;

        // The name a group number gives.
        public static string Default(int number)
        {
            int index = Mathf.Max(1, number) - 1;
            string name = Phonetic[index % Phonetic.Length];
            int round = index / Phonetic.Length;
            return round == 0 ? name : name + " " + (round + 1);
        }

        // What is shown for a group: its stored name (the tag keeps spaces as underscores), or the one its number gives.
        public static string Display(string stored, int number)
        {
            return string.IsNullOrEmpty(stored) ? Default(number) : stored.Replace('_', ' ');
        }

        // A typed name made fit for the tag (letters, digits, _ - . only; spaces become _; at most MaxLength); null when nothing is left, which
        // means "use the name the number gives".
        public static string Clean(string typed)
        {
            if (string.IsNullOrEmpty(typed)) return null;
            StringBuilder clean = new StringBuilder();
            foreach (char c in typed.Trim())
            {
                if (clean.Length >= MaxLength) break;
                if (c == ' ') clean.Append('_');
                else if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') clean.Append(c);
            }
            return clean.Length == 0 ? null : clean.ToString();
        }

        // The name a new turret gets: the smallest whole number (1, 2, 3...) that no turret uses yet.
        public static string NextTurretName(ICollection<string> used)
        {
            for (int number = 1; ; number++)
            {
                string candidate = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                bool taken = false;
                foreach (string name in used)
                    if (name == candidate) { taken = true; break; }
                if (!taken) return candidate;
            }
        }

        // The first name of the alphabet that no group uses yet (compared as shown, ignoring case); the number's own name when all are taken.
        public static string NextFree(ICollection<string> usedDisplayNames, int number)
        {
            foreach (string candidate in Phonetic)
            {
                bool taken = false;
                foreach (string used in usedDisplayNames)
                    if (string.Equals(used, candidate, System.StringComparison.OrdinalIgnoreCase)) { taken = true; break; }
                if (!taken) return candidate;
            }
            return Default(number);
        }
    }
}
