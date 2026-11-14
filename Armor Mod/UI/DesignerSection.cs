using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Powertrain;
using Assets.Scripts.Craft.Parts.Modifiers.Propulsion.Jet;
using Assets.Scripts.Craft.Parts.Modifiers.Propulsion.Propeller;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Design.UI.PartProperties;
using Assets.Scripts.UI.Controls;
using HarmonyLib;
using Jundroo.Juicy.Widgets;
using UnityEngine;

namespace ArmorFragments.UI
{
    // The "Armor Fragments" section inside the designer's part properties panel.
    // The player turns tag kinds on or off; each kind shows its own properties.
    // Every change rewrites the part's NAME (see PartTags.cs), the same way the game's
    // own name field does, so undo and saving work as usual.
    internal static class DesignerSection
    {
        public const string LayoutPath = "Mods/ArmorFragments/DesignerSection";

        private static readonly MethodInfo UpdateCommonProperties =
            AccessTools.Method(typeof(PartPropertiesPanelScript), "UpdateCommonPartProperties");

        // Re-asks every property's visibility callback (hides a rack-fed cannon's Ammo Count at once).
        private static readonly MethodInfo RefreshPropertyVisibilities =
            AccessTools.Method(typeof(GenericPartPropertiesScript), "RefreshPropertyVisibilities");

        private static void RefreshGenericPanels()
        {
            if (RefreshPropertyVisibilities == null) return;
            foreach (GenericPartPropertiesScript script in
                     UnityEngine.Object.FindObjectsByType<GenericPartPropertiesScript>(FindObjectsSortMode.None))
            {
                try
                {
                    RefreshPropertyVisibilities.Invoke(script, null);
                }
                catch (Exception)
                {
                    // a panel being torn down: nothing to refresh
                }
            }
        }

        private static PartPropertiesPanelScript panel;   // the designer's properties panel
        private static Widget container;                  // our section inside it
        private static PartScript part;                   // the selected part
        private static string builtForName;               // the name our rows were built from
        private static bool applying;                     // true while WE are changing the name
        private static bool rebuildPending;
        private static bool builtExplosive;               // was the cannon's Projectile Type 'Explosive' when the rows were made?
        private static bool builtCannonRows;              // ... and did the rows include the cannon's ammunition ones?
        private static float nextPoll;                    // (unscaled time) of the next look at the cannon's Projectile Type

        // Rows whose text follows the part's shape (ammo rack capacity): re-measured when it changes.
        private sealed class LiveBlock
        {
            public TextControl[] Controls;
            public Func<string> Signature;   // cheap: changes when the texts would
            public Func<string[]> Texts;     // may be slow (measures racks)
            public string LastSignature;
        }

        private static readonly System.Collections.Generic.List<LiveBlock> liveBlocks =
            new System.Collections.Generic.List<LiveBlock>();
        private static float nextLiveCheck;
        private const float LiveInterval = 0.5f;

        // Called once when the designer's properties panel is created.
        public static void Attach(PartPropertiesPanelScript properties)
        {
            panel = properties;
            Widget itemsParent = properties.Widget.FindWidget("items-parent");
            container = properties.Widget.Context.LoadWidgetFromXml(LayoutPath, itemsParent);
            container.SetIndex(1);   // right after the common properties (name, mass, id)
            container.Visible = false;
            part = null;
        }

        public static void OnPartSelected(PartPropertiesPanelScript properties, PartScript selected)
        {
            if (properties != panel) return;
            part = selected;
            rebuildPending = true;
        }

        // The game refreshed the name field (selection, undo, structure change...).
        public static void OnCommonPropertiesUpdated(PartPropertiesPanelScript properties)
        {
            if (properties != panel || applying || part == null) return;
            if (builtForName != part.Part.Name) rebuildPending = true;
        }

        // Called every frame by the plugin. Rebuilding is done here, never inside a
        // click handler, because rebuilding destroys the very control being clicked.
        public static void Tick()
        {
            RefreshLiveRows();
            if (!rebuildPending)
            {
                PollProjectileType();
                return;
            }
            rebuildPending = false;
            try
            {
                Rebuild();
                RefreshGenericPanels();   // tags changed or another part was selected
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Couldn't build the designer section: " + e);
            }
        }

        // The cannon's Projectile Type (Basic / Explosive) is the game's own control, which we do not hear from.
        // So we look at it a few times a second: if it changed, the ammunition rows change (an explosive shell
        // has a fuse and a filler, a solid one has neither), and the filler goes back to the new kind's default.
        private static void PollProjectileType()
        {
            if (!builtCannonRows || part == null || container == null || !container.Visible) return;
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 0.25f;

            try
            {
                if (IsExplosive() == builtExplosive) return;

                PartTags tags = TagCodec.ParseCopy(part.Part.Name);
                if (tags.Cannon != null)
                {
                    tags.Cannon.FillerPercent = null;   // the default of the new kind
                    Commit(tags, false);
                }
                rebuildPending = true;
            }
            catch (Exception e)
            {
                builtCannonRows = false;   // do not try again until the rows are rebuilt
                ArmorFragmentsPlugin.Log.LogWarning("Couldn't read the cannon's Projectile Type: " + e.Message);
            }
        }

        // The game's 'Projectile Type' setting of the selected cannon.
        private static bool IsExplosive()
        {
            foreach (PartModifierData modifier in part.Part.Modifiers)
            {
                CannonData data = modifier as CannonData;
                if (data != null) return data.AmmoType == CannonData.ProjectileType.Explosive;
            }
            return false;
        }

        private static string Describe(AmmoKind kind)
        {
            switch (kind)
            {
                case AmmoKind.APHE: return "APHE (explosive, fuse)";
                case AmmoKind.HE: return "HE (explosive, no fuse)";
                default: return "AP (solid shell)";
            }
        }

        private static void Rebuild()
        {
            builtCannonRows = false;
            if (container == null) return;   // the designer was closed

            liveBlocks.Clear();
            foreach (Widget child in container.Widgets.ToList())
                child.Destroy();

            if (part == null)
            {
                container.Visible = false;
                return;
            }

            bool canArmor = Has<JFuselageData>();
            bool canCannon = Has<CannonData>();
            bool canModule = Has<JEngineData>() || Has<TurboshaftData>() || Has<JetEngineData>() ||
                             Has<FuelTankData>() || Has<PropellerAssemblyData>() || Has<JFuselageData>();

            // Every part can be an ammo rack cook-off vent, so the section always shows (other parts: only that).
            container.Visible = true;

            PartTags tags = TagCodec.ParseCopy(part.Part.Name);
            builtForName = part.Part.Name;
            PanelBuilder builder = new PanelBuilder(container);
            builder.Header("Armor Fragments");

            if (canArmor)
            {
                builder.Toggle("Armor",
                    "Makes this part armor: shells that hit it can be blocked or can send fragments behind it.",
                    tags.Armor != null,
                    on => Switch(tags, () => tags.Armor = on ? new ArmorTag() : null));
                if (tags.Armor != null)
                {
                    Slider(builder, tags, "Thickness", "Armor thickness.",
                        1f, 500f, 1f, tags.Armor.Thickness, " mm", v => tags.Armor.Thickness = v);
                    Slider(builder, tags, "Hardness", "How hard the armor is compared with the shell: harder armor deflects shells at lower angles. 100% = reference.",
                        10f, 300f, 5f, tags.Armor.Hardness, " %", v => tags.Armor.Hardness = v);
                    Slider(builder, tags, "Brittleness", "How easily the plate shatters into fragments. (Not used yet: energy model.)",
                        0f, 100f, 5f, tags.Armor.Brittleness, " %", v => tags.Armor.Brittleness = v);
                }
            }

            if (canCannon)
            {
                builder.Toggle("Cannon",
                    "Describes the shells this cannon fires.",
                    tags.Cannon != null,
                    on => Switch(tags, () => tags.Cannon = on ? new CannonTag() : null));

                // Estimated penetration (Jakniphe: "so we can see its stats"), for every cannon: the energy
                // model's own rule for a real hit, at the muzzle, against armor of the current resistance.
                PartScript gunPart = part;
                Live(builder, new[] { "Shell energy", "Penetration 0 / 30 / 60 deg" },
                     () => PenetrationSignature(gunPart), () => PenetrationTexts(gunPart));

                if (tags.Cannon != null)
                {
                    Slider(builder, tags, "Shell hardness", "Hardness of the shell: a harder shell is deflected less by the armor. 100% = reference.",
                        10f, 300f, 5f, tags.Cannon.ShellHardness, " %", v => tags.Cannon.ShellHardness = v);

                    // What kind of shell it is. The game's own Projectile Type says if it is explosive; the fuse
                    // tells the two explosive kinds apart.
                    bool explosive = IsExplosive();
                    builtExplosive = explosive;
                    builtCannonRows = true;
                    AmmoKind kind = Ammunition.KindOf(explosive, tags.Cannon);
                    builder.Text("Ammunition", Describe(kind));

                    if (explosive)
                    {
                        builder.Toggle("Fuse",
                            "Gives the shell a fuse (APHE): it goes through armor and bursts inside, after the distance below. Without one (HE) it bursts on the first thing it touches. Switching it resets the filler to the new kind's default.",
                            tags.Cannon.Fuse.HasValue,
                            on => Switch(tags, () =>
                            {
                                tags.Cannon.Fuse = on ? ArmorFragmentsPlugin.DefaultFuse.Value : (float?)null;
                                tags.Cannon.FillerPercent = null;
                            }));
                        if (tags.Cannon.Fuse.HasValue)
                            Slider(builder, tags, "Fuse distance", "Meters the shell flies after its first impact before it bursts.",
                                0.1f, 10f, 0.1f, tags.Cannon.Fuse.Value, " m", v => tags.Cannon.Fuse = v);
                        Slider(builder, tags, "Filler", "Explosive filler, as a percent of the shell's mass. The rest is the casing, which breaks into the fragments. It starts at the default of the kind of shell (HE and APHE have their own).",
                            0f, 50f, 0.5f, tags.Cannon.FillerPercent ?? Ammunition.DefaultFillerPercent(kind), " %",
                            v => tags.Cannon.FillerPercent = v);
                        Slider(builder, tags, "Shell brittleness", "How easily the shell's casing breaks into small fragments. A brittle casing makes more, smaller fragments (a stronger burst, with more filler, breaks it up more too). A bigger caliber makes bigger fragments.",
                            0f, 100f, 5f, tags.Cannon.Brittleness ?? ArmorFragmentsPlugin.DefaultShellBrittleness.Value, " %",
                            v => tags.Cannon.Brittleness = v);
                    }

                    // Ammo racks feeding this cannon (the group is edited as g= in the name). When racks feed
                    // it, the game's Ammo Count slider is hidden (AmmoCountVisibilityPatch) and these rows say
                    // how many rounds it really has.
                    PartScript cannonPart = part;
                    string group = AmmoRacks.GroupName(tags.Cannon.Group);
                    builder.Text("Ammo group", group);
                    Live(builder, new[] { "Ammo from", "Rounds", "Round (W x L)", "Shell" },
                         () => GroupSignature(cannonPart, group), () => CannonTexts(cannonPart, group));
                }
            }

            if (canModule)
            {
                builder.Toggle("Damage module",
                    "Marks this part as one that reacts to fragment damage.",
                    tags.Module != null,
                    on => Switch(tags, () => tags.Module = on ? new ModuleTag() : null));
                if (tags.Module != null)
                {
                    // The module reports how damaged it is (0 = intact, 1 = destroyed) as a Funky Trees variable.
                    builder.Text("Funky Trees variable", ModuleOutputs.NameFor(part, tags));
                    builder.Text("Value", "0 to 1: share of health lost");
                }
            }

            if (canArmor)   // fuselages: the same parts that can be armor
            {
                builder.Toggle("Ammo rack",
                    "Makes this fuselage an ammo rack: it holds the rounds of the cannons in its group ([CAN g=]), " +
                    "and can catch fire and explode when its rounds are hit.",
                    tags.AmmoRack != null,
                    on => Switch(tags, () => tags.AmmoRack = on ? new AmmoRackTag() : null));
                if (tags.AmmoRack != null)
                {
                    PartScript rackPart = part;
                    string group = AmmoRacks.GroupName(tags.AmmoRack.Group);
                    string prefix = AmmoRacks.VariablePrefix(group);
                    builder.Text("Ammo group", group);
                    Slider(builder, tags, "Empty order", "Racks of a group empty from the lowest number up. 0 = after the numbered ones.",
                        0f, 20f, 1f, tags.AmmoRack.Order ?? 0, "",
                        v => tags.AmmoRack.Order = v < 0.5f ? (int?)null : Mathf.RoundToInt(v));
                    if (tags.Armor != null)
                        builder.Text("Warning", "Armor: holds no rounds");
                    Live(builder, new[] { "Status", "Rounds held", "Layout", "Lost to shape", "Space (L x W x H)", "Round (W x L)", "Shell", "Rounds weight" },
                         () => AmmoRacks.DesignRackSignature(rackPart, group), () => RackTexts(rackPart, group, tags.Armor != null));
                    builder.Text("Rounds variable", prefix + "_rounds");
                    builder.Text("Fire variable", prefix + "_fire");
                    builder.Toggle("See-through racks",
                        "Draws every ammo rack see-through in the designer, with its rounds inside. Clicking still selects the part.",
                        ArmorFragmentsPlugin.ShowRacksInDesigner.Value,
                        on => ArmorFragmentsPlugin.ShowRacksInDesigner.Value = on);
                    builder.Text("Boom variable", prefix + "_boom (for detachers)");
                }
            }

            builder.Toggle("Cook-off vent",
                "Ammo rack cook-offs vent through this part: the pillar of fire runs from the rack to the nearest vent, " +
                "then straight up. Mark a turret ring or a hatch. With no vent, it rises straight up from the rack.",
                tags.Vent != null,
                on => Switch(tags, () => tags.Vent = on ? new VentTag() : null));
        }

        // ---- Ammo rack read-outs ---------------------------------------------------------

        // Several short rows that are measured together and refreshed when 'signature' changes.
        private static void Live(PanelBuilder builder, string[] labels, Func<string> signature, Func<string[]> texts)
        {
            LiveBlock block = new LiveBlock { Signature = signature, Texts = texts, Controls = new TextControl[labels.Length] };
            block.LastSignature = Safe(signature);
            string[] values = SafeTexts(texts, labels.Length);
            for (int i = 0; i < labels.Length; i++)
                block.Controls[i] = builder.Text(labels[i], values[i]);
            liveBlocks.Add(block);
        }

        private static void RefreshLiveRows()
        {
            if (liveBlocks.Count == 0 || container == null || part == null) return;
            if (Time.unscaledTime < nextLiveCheck) return;
            nextLiveCheck = Time.unscaledTime + LiveInterval;

            foreach (LiveBlock block in liveBlocks)
            {
                string signature = Safe(block.Signature);
                if (signature == block.LastSignature) continue;
                block.LastSignature = signature;
                string[] values = SafeTexts(block.Texts, block.Controls.Length);
                for (int i = 0; i < block.Controls.Length; i++)
                    block.Controls[i].ValueText.Text = values[i];
            }
        }

        private static string Safe(Func<string> text)
        {
            try
            {
                return text();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack read-out failed: " + e.Message);
                return "?";
            }
        }

        private static string[] SafeTexts(Func<string[]> texts, int count)
        {
            string[] values = null;
            try
            {
                values = texts();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack read-out failed: " + e.Message);
            }
            string[] result = new string[count];
            for (int i = 0; i < count; i++)
                result[i] = values != null && i < values.Length && values[i] != null ? values[i] : "?";
            return result;
        }

        // Status, Rounds held, Layout, Lost to shape, Space (L x W x H), Round (W x L), Shell
        private static string[] RackTexts(PartScript rackPart, string group, bool isArmor)
        {
            if (isArmor) return new[] { "Armor: holds nothing", "0", "-", "-", "-", "-", "-", "-" };

            AmmoRound round;
            int cannons;
            bool mixed;
            if (!AmmoRacks.DesignRound(rackPart, group, out round, out cannons, out mixed))
                return new[] { "No cannon in group", "0", "-", "-", "-", "-", "-", "-" };

            RackGrid grid = RackGrid.Measure(rackPart, round);
            bool measured = grid.Problem == null;
            bool hasGrid = grid.Slots > 0 && grid.Columns > 0 && grid.Layers > 0;
            return new[]
            {
                measured && mixed ? "OK (mixed cannons)" : grid.Status,
                grid.Capacity.ToString(CultureInfo.InvariantCulture),
                hasGrid ? "up to " + grid.Slots + " long x " + grid.Columns + " wide x " + grid.Layers + " high" : "-",
                grid.LostToShape.ToString(CultureInfo.InvariantCulture),
                grid.InsideLength > 0f ? Cm(grid.InsideLength) + " x " + Cm(grid.InsideWidth) + " x " + Cm(grid.InsideHeight) + " cm" : "-",
                round.ShortSize(),
                round.ShortShell(),
                (grid.Capacity * round.RoundMass).ToString("F0", CultureInfo.InvariantCulture) + " kg (added in flight)",
            };
        }

        // Ammo from, Rounds, Round (W x L), Shell
        // Shell energy, Penetration 0 / 30 / 60 deg.
        // A shell gets through a plate when its energy beats ArmorResistance x thickness / cos(angle) x its
        // cross-section in cm2 (EnergyModel.ArmorAbsorption), so the thickest plate it beats is
        // energy / (resistance x area) x cos(angle). At the muzzle (the shell slows down with distance);
        // ricochets and the plate's hardness are not counted.
        private static string[] PenetrationTexts(PartScript cannonPart)
        {
            AmmoRound round;
            if (!AmmoRound.TryFromPart(cannonPart, out round)) return new[] { "-", "-" };

            float energy = round.Energy;   // J
            float radiusCm = round.CaliberMm * 0.05f;
            float areaCm2 = Mathf.PI * radiusCm * radiusCm;
            float resistance = Mathf.Max(ArmorFragmentsPlugin.ArmorResistance.Value, 0.001f);
            float headOn = energy / (resistance * Mathf.Max(areaCm2, 0.0001f));   // mm

            string Mm(float angle) => (headOn * Mathf.Cos(angle * Mathf.Deg2Rad)).ToString("F0", CultureInfo.InvariantCulture);
            return new[]
            {
                (energy / 1000f).ToString("F0", CultureInfo.InvariantCulture) + " kJ (" +
                    round.ShellMass.ToString("F2", CultureInfo.InvariantCulture) + " kg at " +
                    round.Velocity.ToString("F0", CultureInfo.InvariantCulture) + " m/s)",
                // HE bursts on the first thing it touches (FragmentSpawner.BurstOnSurface): the shell itself
                // goes through nothing, only its fragments do. APHE goes through like AP.
                KindOfCannon(cannonPart) == AmmoKind.HE
                    ? "none: HE bursts on contact (fragments only)"
                    : Mm(0f) + " / " + Mm(30f) + " / " + Mm(60f) + " mm",
            };
        }

        private static AmmoKind KindOfCannon(PartScript cannonPart)
        {
            bool explosive = false;
            foreach (PartModifierData modifier in cannonPart.Part.Modifiers)
            {
                CannonData data = modifier as CannonData;
                if (data == null) continue;
                explosive = data.AmmoType == CannonData.ProjectileType.Explosive;
                break;
            }
            return Ammunition.KindOf(explosive, TagCodec.Parse(cannonPart.Part.Name).Cannon);
        }

        private static string PenetrationSignature(PartScript cannonPart)
        {
            AmmoRound round;
            return (AmmoRound.TryFromPart(cannonPart, out round) ? round.Describe() + round.ShellMass : "") +
                   "|" + ArmorFragmentsPlugin.ArmorResistance.Value + "|" + KindOfCannon(cannonPart);
        }

        private static string[] CannonTexts(PartScript cannonPart, string group)
        {
            AmmoRound own;
            bool hasOwn = AmmoRound.TryFromPart(cannonPart, out own);

            AmmoRound round;
            int cannons;
            bool mixed;
            int racks = 0;
            int total = 0;
            if (AmmoRacks.DesignRound(cannonPart, group, out round, out cannons, out mixed))
                total = AmmoRacks.DesignCapacity(cannonPart, group, round, out racks);

            return new[]
            {
                racks == 0 ? "Ammo Count slider" : racks + (racks == 1 ? " rack" : " racks"),
                racks == 0 ? "-" : total + (cannons > 1 ? " (shared by " + cannons + ")" : ""),
                hasOwn ? own.ShortSize() : "-",
                hasOwn ? own.ShortShell() : "-",
            };
        }

        private static string Cm(float meters)
        {
            return (meters * 100f).ToString("F1", CultureInfo.InvariantCulture);
        }

        // Changes whenever the cannon's round or any rack of its group changes.
        private static string GroupSignature(PartScript anyPart, string group)
        {
            System.Text.StringBuilder signature = new System.Text.StringBuilder();
            AmmoRound own;
            if (AmmoRound.TryFromPart(anyPart, out own)) signature.Append(own.Describe());

            AmmoRound round;
            int cannons;
            bool mixed;
            if (AmmoRacks.DesignRound(anyPart, group, out round, out cannons, out mixed))
                signature.Append(round.Describe()).Append(cannons).Append(mixed);
            if (anyPart.Aircraft != null && anyPart.Aircraft.Aircraft != null && anyPart.Aircraft.Aircraft.Assembly != null)
            {
                foreach (PartData data in anyPart.Aircraft.Aircraft.Assembly.Parts)
                {
                    if (data == null || data.PartScript == null) continue;
                    PartTags tags = TagCodec.Parse(data.Name);
                    if (tags.AmmoRack == null || AmmoRacks.GroupName(tags.AmmoRack.Group) != group) continue;
                    signature.Append('|').Append(data.Id).Append(tags.Armor != null)
                             .Append(AmmoRacks.ShapeSignature(data.PartScript));
                }
            }
            signature.Append(ArmorFragmentsPlugin.CaseDiameterRatio.Value).Append(ArmorFragmentsPlugin.CaseLitresPerMJ.Value)
                     .Append(ArmorFragmentsPlugin.RoundSpacingPercent.Value);
            return signature.ToString();
        }

        // ---- Controls ------------------------------------------------------------

        private static void Switch(PartTags tags, Action change)
        {
            change();
            Commit(tags, true);
            rebuildPending = true;   // show or hide that kind's properties
        }

        private static void Slider(PanelBuilder builder, PartTags tags, string label, string tooltip,
                                   float min, float max, float step, float value, string unit,
                                   Action<float> assign)
        {
            int positions = Mathf.RoundToInt((max - min) / step) + 1;
            string format = step >= 1f ? "0" : "0.0";

            builder.Slider(label, tooltip, min, max, positions, value,
                x => x.ToString(format, CultureInfo.InvariantCulture) + unit,
                x =>
                {
                    assign(Mathf.Clamp(Mathf.Round(x / step) * step, min, max));
                    Commit(tags, false);       // while dragging: update the name, no undo step yet
                },
                x => Commit(tags, true));      // on release: one undo step
        }

        // Writes the tags into the part's name, the same way the game's name field does.
        private static void Commit(PartTags tags, bool createUndoStep)
        {
            if (part == null) return;

            string name = TagCodec.Compose(tags, part.Part.PartType.Name);
            part.Part.Name = name;
            foreach (PartModifierData modifier in part.Part.Modifiers)
                modifier.OnPartNameChanged(name);
            builtForName = name;

            // Make the game's own name field show the new name.
            applying = true;
            try
            {
                if (UpdateCommonProperties != null) UpdateCommonProperties.Invoke(panel, null);
            }
            finally
            {
                applying = false;
            }

            // Make the module's variable show up (or go away) in the designer's variable lists right away.
            if (createUndoStep) ModuleOutputs.RefreshDesignerVariables(part);

            if (createUndoStep)
            {
                Assets.Scripts.Design.Designer.Instance.CreateUndoStepForSelectedPart(
                    "Armor tags", "armor-tags-" + part.Part.Id);
            }
        }

        private static bool Has<T>() where T : PartModifierData
        {
            foreach (PartModifierData modifier in part.Part.Modifiers)
                if (modifier is T) return true;
            return false;
        }
    }
}
