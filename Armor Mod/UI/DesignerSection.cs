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

        private static PartPropertiesPanelScript panel;   // the designer's properties panel
        private static Widget container;                  // our section inside it
        private static PartScript part;                   // the selected part
        private static string builtForName;               // the name our rows were built from
        private static bool applying;                     // true while WE are changing the name
        private static bool rebuildPending;
        private static bool builtExplosive;               // was the cannon's Projectile Type 'Explosive' when the rows were made?
        private static bool builtCannonRows;              // ... and did the rows include the cannon's ammunition ones?
        private static float nextPoll;                    // (unscaled time) of the next look at the cannon's Projectile Type

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
            if (!rebuildPending)
            {
                PollProjectileType();
                return;
            }
            rebuildPending = false;
            try
            {
                Rebuild();
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

            if (!canArmor && !canCannon && !canModule)
            {
                container.Visible = false;
                return;
            }
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
