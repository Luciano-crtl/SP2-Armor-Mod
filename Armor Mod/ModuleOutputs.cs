using System;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // Damage modules ([MOD] parts) report how damaged they are to Funky Trees.
    //
    // Each module writes a number from 0 (intact) to 1 (all its health lost, or destroyed) into a variable
    // of the aircraft, every frame. A fuselage that has lost 55% of its health gives 0.55. The variable is
    // an ordinary one in the game's own variable system, so any Funky Trees expression can read it.
    //
    // The variable's name is the one in the tag ([MOD v=Name]) or, if there is none, the part's name
    // without its tags ("Left engine [MOD d=1]" -> Left_engine). Parts that end up with the same name
    // share the variable, which then holds the worst damage among them.
    internal static class ModuleOutputs
    {
        private sealed class Output
        {
            public string Name;
            public AircraftVariable Variable;
            public readonly List<PartScript> Parts = new List<PartScript>();
        }

        // One list of outputs for each aircraft's variable system.
        private static readonly Dictionary<VariableSystemScript, List<Output>> Systems =
            new Dictionary<VariableSystemScript, List<Output>>();

        private static readonly HashSet<string> Warned = new HashSet<string>();

        // ---- Names -----------------------------------------------------------------

        // The variable name of a module part (also what the designer shows).
        public static string NameFor(PartScript part, PartTags tags)
        {
            string raw = tags.Module != null && !string.IsNullOrEmpty(tags.Module.Variable)
                ? tags.Module.Variable : tags.Label;
            string name = Sanitize(raw);
            return name.Length > 0 ? name : "Damage_" + part.Part.Id;
        }

        // Letters, digits and underscores only, and not starting with a digit.
        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            StringBuilder name = new StringBuilder();
            foreach (char c in text)
            {
                if (c < 128 && (char.IsLetterOrDigit(c) || c == '_')) name.Append(c);
                else if (char.IsWhiteSpace(c) || c == '-') name.Append('_');
            }

            string result = name.ToString().Trim('_');
            if (result.Length > 0 && char.IsDigit(result[0])) result = "_" + result;
            return result;
        }

        // ---- Registering the variables ---------------------------------------------

        // Called after the game has (re)built an aircraft's variable list: it throws away every variable
        // nothing of its own uses, ours included, so they are made again here.
        public static void Register(VariableSystemScript system)
        {
            AircraftScript aircraft = system.AircraftScript;
            if (aircraft == null || aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return;

            Dictionary<string, Output> byName = new Dictionary<string, Output>();
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;

                PartTags tags = TagCodec.Parse(data.Name);
                if (tags.Module == null) continue;

                string name = NameFor(part, tags);
                Output output;
                if (!byName.TryGetValue(name, out output))
                {
                    output = new Output { Name = name };
                    byName[name] = output;
                }
                output.Parts.Add(part);
            }

            List<Output> outputs = new List<Output>();
            foreach (Output output in byName.Values)
            {
                output.Variable = CreateVariable(system, output.Name);
                if (output.Variable != null) outputs.Add(output);
            }

            if (outputs.Count > 0) Systems[system] = outputs;
            else Systems.Remove(system);
        }

        private static AircraftVariable CreateVariable(VariableSystemScript system, string name)
        {
            try
            {
                return system.AddVariable(name);
            }
            catch (Exception e)
            {
                // Most likely the name is one the game already uses ("Altitude"...): try a prefixed one.
                string alternative = "Damage_" + name;
                WarnOnce(name, "Module variable '" + name + "' is not allowed (" + e.Message +
                               "); using '" + alternative + "' instead");
                try
                {
                    return system.AddVariable(alternative);
                }
                catch (Exception e2)
                {
                    WarnOnce(alternative, "Module variable '" + alternative + "' is not allowed either: " + e2.Message);
                    return null;
                }
            }
        }

        private static void WarnOnce(string key, string message)
        {
            if (Warned.Add(key)) ArmorFragmentsPlugin.Log.LogWarning(message);
        }

        // The designer does not rebuild the variable list when only a part's name changes: ask it to.
        public static void RefreshDesignerVariables(PartScript part)
        {
            try
            {
                if (part == null || part.Aircraft == null || part.Aircraft.VariableSystem == null) return;
                part.Aircraft.VariableSystem.RefreshVariables();
            }
            catch (Exception e)
            {
                WarnOnce("refresh", "Could not refresh the designer's variables: " + e.Message);
            }
        }

        // ---- Writing the values ----------------------------------------------------

        // Called every frame by the plugin.
        public static void Tick()
        {
            if (Systems.Count == 0) return;

            List<VariableSystemScript> gone = null;
            foreach (KeyValuePair<VariableSystemScript, List<Output>> pair in Systems)
            {
                VariableSystemScript system = pair.Key;
                if (system == null || system.AircraftScript == null)
                {
                    if (gone == null) gone = new List<VariableSystemScript>();
                    gone.Add(system);
                    continue;
                }

                AircraftScript aircraft = system.AircraftScript;
                // Only a real flight, and only the aircraft this game owns (a remote copy takes no damage here).
                if (aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft) continue;

                foreach (Output output in pair.Value)
                    output.Variable.SetValue(WorstDamage(output), 0);
            }

            if (gone != null)
                foreach (VariableSystemScript system in gone) Systems.Remove(system);
        }

        // 0 = intact, 1 = all health lost. A part that no longer exists counts as destroyed.
        private static float WorstDamage(Output output)
        {
            float worst = 0f;
            foreach (PartScript part in output.Parts)
            {
                float level = part == null
                    ? 1f
                    : Mathf.Clamp01(part.PartDamage / Mathf.Max(part.MaxHealth, 1f));
                if (level > worst) worst = level;
            }
            return worst;
        }
    }

    // The game forgets variables nobody of its own uses each time it rebuilds the list.
    [HarmonyPatch(typeof(VariableSystemScript), "RefreshVariables")]
    internal static class ModuleVariablesPatch
    {
        private static void Postfix(VariableSystemScript __instance)
        {
            try
            {
                ModuleOutputs.Register(__instance);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Could not register the damage module variables: " + e.Message);
            }
        }
    }
}
