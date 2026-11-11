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
            if (!rebuildPending) return;
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

        private static void Rebuild()
        {
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
                    Slider(builder, tags, "Fragments", "How many fragment rays a shell makes when it penetrates armor.",
                        1f, 200f, 1f, tags.Cannon.Fragments, "", v => tags.Cannon.Fragments = Mathf.RoundToInt(v));
                    Slider(builder, tags, "Shell hardness", "Hardness of the shell: a harder shell is deflected less by the armor. 100% = reference.",
                        10f, 300f, 5f, tags.Cannon.ShellHardness, " %", v => tags.Cannon.ShellHardness = v);
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
