using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;

namespace ArmorFragments
{
    // Five saved sets of the turret control's gains (the settings that decide how the turrets chase the aim, for the direct control and for the
    // gain-based one). Pick a profile with its button: its numbers replace the current ones. "Save" writes the current numbers into the picked
    // profile. They live in their own text file next to the config, so a profile survives a reset to defaults and is easy to copy to a friend.
    //
    // Which settings: only the ones that shape the control (not keys, zoom, firing or the automatic aim's choices). The first time, all five profiles
    // are filled with the numbers in use then, so picking one never leaves the turrets with nothing.
    internal static class TurretProfiles
    {
        internal const int Count = 5;

        private static readonly object Lock = new object();
        private static bool loaded;
        private static int selected = 1;
        private static readonly Dictionary<string, string>[] Values = new Dictionary<string, string>[Count];
        private static readonly string[] Names = new string[Count];   // null = the default name ("Profile N")

        internal const int MaxNameLength = 16;

        // What a profile is called on its button and in the status line.
        internal static string NameOf(int number)
        {
            EnsureLoaded();
            string name = Names[number - 1];
            return string.IsNullOrEmpty(name) ? "Profile " + number : name;
        }

        // The name the player typed for a profile, as it is kept: no line breaks or control characters, trimmed, at most MaxNameLength long.
        // Empty = back to the default name.
        internal static void Rename(int number, string name)
        {
            if (number < 1 || number > Count) return;
            lock (Lock)
            {
                EnsureLoaded();
                StringBuilder clean = new StringBuilder();
                foreach (char c in name ?? "")
                    if (!char.IsControl(c)) clean.Append(c);
                string text = clean.ToString().Trim();
                if (text.Length > MaxNameLength) text = text.Substring(0, MaxNameLength).TrimEnd();
                Names[number - 1] = text.Length == 0 || text == "Profile " + number ? null : text;
                WriteFile();
                ArmorFragmentsPlugin.Log.LogInfo("Turret profile " + number + " is now called '" + NameOf(number) + "'.");
            }
        }

        // The settings a profile holds: a short key (the file's) and the entry.
        private static IEnumerable<KeyValuePair<string, ConfigEntryBase>> Gains()
        {
            yield return new KeyValuePair<string, ConfigEntryBase>("DirectControl", ArmorFragmentsPlugin.TurretDirect);
            yield return new KeyValuePair<string, ConfigEntryBase>("DirectRamp", ArmorFragmentsPlugin.TurretDirectRamp);
            yield return new KeyValuePair<string, ConfigEntryBase>("DirectGain", ArmorFragmentsPlugin.TurretDirectGain);
            yield return new KeyValuePair<string, ConfigEntryBase>("DirectBraking", ArmorFragmentsPlugin.TurretDirectBrake);
            yield return new KeyValuePair<string, ConfigEntryBase>("DirectDeadband", ArmorFragmentsPlugin.TurretDirectDeadband);
            yield return new KeyValuePair<string, ConfigEntryBase>("Response", ArmorFragmentsPlugin.TurretResponse);
            yield return new KeyValuePair<string, ConfigEntryBase>("Braking", ArmorFragmentsPlugin.TurretDerivative);
            yield return new KeyValuePair<string, ConfigEntryBase>("Damping", ArmorFragmentsPlugin.TurretDamping);
            yield return new KeyValuePair<string, ConfigEntryBase>("SlowSharedHull", ArmorFragmentsPlugin.TurretShareHull);
            yield return new KeyValuePair<string, ConfigEntryBase>("SharedHullFullGain", ArmorFragmentsPlugin.TurretShareStrength);
            yield return new KeyValuePair<string, ConfigEntryBase>("ErrorFilter", ArmorFragmentsPlugin.TurretErrorFilter);
            yield return new KeyValuePair<string, ConfigEntryBase>("MoveStagger", ArmorFragmentsPlugin.TurretMoveStagger);
        }

        private static string FilePath
        {
            get { return Path.Combine(Paths.ConfigPath, "com.yourname.armorfragments.turret-profiles.txt"); }
        }

        internal static int Selected
        {
            get { EnsureLoaded(); return selected; }
        }

        // One line for the panel: which profile, and whether the numbers in use still are its.
        internal static string Status
        {
            get
            {
                try
                {
                    EnsureLoaded();
                    bool same = Matches(selected);
                    return NameOf(selected) + " (" + selected + "): " + (same ? "as saved" : "changed, not saved");
                }
                catch (Exception e)
                {
                    return "Profiles unavailable: " + e.Message;
                }
            }
        }

        // The settings in use now equal the profile's.
        private static bool Matches(int number)
        {
            Dictionary<string, string> saved = Values[number - 1];
            foreach (KeyValuePair<string, ConfigEntryBase> gain in Gains())
            {
                string stored;
                if (!saved.TryGetValue(gain.Key, out stored)) continue;
                if (!string.Equals(stored, gain.Value.GetSerializedValue(), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        // Puts a profile's numbers into the settings (and makes it the picked one).
        internal static void Select(int number)
        {
            if (number < 1 || number > Count) return;
            lock (Lock)
            {
                EnsureLoaded();
                Dictionary<string, string> saved = Values[number - 1];
                ConfigFile file = ArmorFragmentsPlugin.Instance.Config;
                bool original = file.SaveOnConfigSet;
                file.SaveOnConfigSet = false;
                int applied = 0;
                try
                {
                    foreach (KeyValuePair<string, ConfigEntryBase> gain in Gains())
                    {
                        string stored;
                        if (!saved.TryGetValue(gain.Key, out stored)) continue;   // a setting added since the profile was saved keeps its value
                        try
                        {
                            gain.Value.SetSerializedValue(stored);
                            applied++;
                        }
                        catch (Exception e)
                        {
                            ArmorFragmentsPlugin.Log.LogWarning("Turret profile " + number + ": '" + gain.Key + "' = '" + stored + "' could not be read: " + e.Message);
                        }
                    }
                }
                finally
                {
                    file.SaveOnConfigSet = original;
                }
                selected = number;
                NetSync.SaveConfig();
                WriteFile();
                ArmorFragmentsPlugin.Log.LogInfo("Turret profile " + number + " loaded (" + applied + " settings).");
            }
        }

        // Writes the settings in use into the picked profile.
        internal static void Save()
        {
            lock (Lock)
            {
                EnsureLoaded();
                Capture(selected);
                WriteFile();
                ArmorFragmentsPlugin.Log.LogInfo("Turret profile " + selected + " saved.");
            }
        }

        private static void Capture(int number)
        {
            Dictionary<string, string> saved = new Dictionary<string, string>();
            foreach (KeyValuePair<string, ConfigEntryBase> gain in Gains())
                saved[gain.Key] = gain.Value.GetSerializedValue();
            Values[number - 1] = saved;
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            lock (Lock)
            {
                if (loaded) return;
                for (int i = 0; i < Count; i++) Values[i] = new Dictionary<string, string>();
                bool read = false;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line[0] == '#') continue;
                            int equals = line.IndexOf('=');
                            if (equals <= 0) continue;
                            string key = line.Substring(0, equals).Trim();
                            string value = line.Substring(equals + 1).Trim();
                            if (key == "selected")
                            {
                                int n;
                                if (int.TryParse(value, out n) && n >= 1 && n <= Count) selected = n;
                                continue;
                            }
                            int dot = key.IndexOf('.');
                            int number;
                            if (dot <= 0 || !int.TryParse(key.Substring(0, dot), out number) || number < 1 || number > Count) continue;
                            string setting = key.Substring(dot + 1);
                            if (setting == "name") { Names[number - 1] = value.Length == 0 ? null : value; read = true; continue; }
                            Values[number - 1][setting] = value;
                            read = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Turret profiles: the file could not be read (" + e.Message + "); starting with the current settings.");
                }

                if (!read)
                {
                    // The first time: every profile starts as the numbers in use now.
                    for (int i = 1; i <= Count; i++) Capture(i);
                    selected = 1;
                    loaded = true;
                    WriteFile();
                }
                loaded = true;
            }
        }

        private static void WriteFile()
        {
            try
            {
                StringBuilder text = new StringBuilder();
                text.AppendLine("# Armor Fragments: saved gains of the turret control (profile number . setting = value). Edit with the game closed, or use the panel.");
                text.AppendLine("selected=" + selected);
                for (int i = 1; i <= Count; i++)
                {
                    if (!string.IsNullOrEmpty(Names[i - 1])) text.AppendLine(i + ".name=" + Names[i - 1]);
                    foreach (KeyValuePair<string, string> pair in Values[i - 1])
                        text.AppendLine(i + "." + pair.Key + "=" + pair.Value);
                }
                File.WriteAllText(FilePath, text.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret profiles: the file could not be written: " + e.Message);
            }
        }
    }
}
