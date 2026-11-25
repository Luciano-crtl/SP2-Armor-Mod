using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.CarverParts;
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

        // Armor faces: the face being edited (BlockFace) with "Per face" on, or AllFaces with it off (every face at
        // once); shown on the block by FaceHighlight.
        public const int AllFaces = ArmorFaces.Count;
        public static int SelectedFace = AllFaces;

        // A selected hole cutter: the armored blocks it cuts (the hole wall armor option shows while there are any).
        private static bool builtIsCutter;
        private static List<PartScript> builtCutArmor;
        private static float nextCutterPoll;

        // The selected part while its armor is edited per face (null otherwise).
        public static PartScript PerFacePart
        {
            get
            {
                if (part == null || part.Part == null || container == null || !container.Visible) return null;
                // Only while the block is still the one selected in the designer: nothing selected, no tint.
                Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
                if (designer == null || designer.SelectedPart != part) return null;
                return ArmorFaces.IsPerFace(part) ? part : null;
            }
        }
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
            if (container != null && panel != null) container.Destroy();   // the game built the panel again: one section only
            panel = properties;
            Widget itemsParent = properties.Widget.FindWidget("items-parent");
            container = properties.Widget.Context.LoadWidgetFromXml(LayoutPath, itemsParent);
            container.SetIndex(1);   // right after the common properties (name, mass, id)
            container.Visible = false;
            part = null;
        }


        // A value dragged or typed whose last step (its undo step, the craft's new weight) has not come yet, because the
        // slider or box was not let go of or left: finished before the panel changes.
        private static PartTags openEdit;

        // switching: another part is being selected (the value is already in the name; the undo step covers the craft).
        private static void FinishEdit(bool switching)
        {
            PartTags tags = openEdit;
            openEdit = null;
            if (tags == null || part == null || part.Part == null) return;
            try
            {
                if (!switching)
                {
                    Commit(tags, true);
                    return;
                }
                Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
                if (designer == null) return;
                designer.SetAircraftStructureChanged();
                designer.CreateUndoStep("Armor tags", "armor-tags-" + part.Part.Id);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Couldn't finish the edited value: " + e.Message);
            }
        }

        // The left mouse button is down, or was let go this frame: a click is still being made.
        private static bool MouseBusy()
        {
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null && (mouse.leftButton.isPressed || mouse.leftButton.wasReleasedThisFrame);
        }

        public static void OnPartSelected(PartPropertiesPanelScript properties, PartScript selected)
        {
            if (properties != panel) return;
            FinishEdit(true);
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
                PollCutter();
                return;
            }
            // Not while a click is being made: the rebuild would destroy the control under it (a face button, say),
            // and the click would be lost.
            if (MouseBusy()) return;
            FinishEdit(false);
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

        // A hole cutter can be moved into or out of armor while selected: the hole wall armor option comes and goes.
        private static void PollCutter()
        {
            if (!builtIsCutter || part == null || container == null) return;
            if (Time.unscaledTime < nextCutterPoll) return;
            nextCutterPoll = Time.unscaledTime + 0.5f;
            try
            {
                List<PartScript> now = ArmoredBlocksCut(part);
                int before = builtCutArmor != null ? builtCutArmor.Count : 0;
                if (now.Count != before) rebuildPending = true;
            }
            catch (Exception e)
            {
                builtIsCutter = false;   // do not try again until the rows are rebuilt
                ArmorFragmentsPlugin.Log.LogWarning("Couldn't read what the hole cutter cuts: " + e.Message);
            }
        }

        // The armor blocks the selected hole cutter cuts into.
        private static List<PartScript> ArmoredBlocksCut(PartScript cutterPart)
        {
            List<PartScript> blocks = new List<PartScript>();
            MeshModifierBaseScript cutter = cutterPart != null ? cutterPart.GetComponentInChildren<MeshModifierBaseScript>() : null;
            if (cutter == null || cutterPart.Aircraft == null || cutterPart.Aircraft.Aircraft == null) return blocks;

            List<int> ids = cutter.GetPartIDs();
            if (ids == null || ids.Count == 0) return blocks;
            foreach (PartData data in cutterPart.Aircraft.Aircraft.Assembly.Parts)
            {
                if (data == null || data.PartScript == null || !ids.Contains(data.Id)) continue;
                if (TagCodec.Parse(data.Name).Armor != null) blocks.Add(data.PartScript);
            }
            return blocks;
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

            PartTags tags = TagCodec.ParseCopy(part.Part.Name);

            // Cook-off vents are fuselages, the same parts that can be armor or racks. A part of another kind already marked as a vent keeps its toggle, so the mark
            // can be taken off.
            bool canVent = canArmor || tags.Vent != null;

            // Crew members are seats (Seat, Seat Ejection, Car Seat). A part of another kind already marked as crew
            // keeps its toggle, so the mark can be taken off.
            bool canCrew = Has<SeatData>() || tags.Crew != null;
            // Rotators (hinge, small rotator, rotator) can be turned by the crew's Aim job.
            bool canAim = Has<JointRotatorData>() || tags.Aim != null;
            // The mouse aim turns rotators marked as turret and points the cannons marked as pointer (TurretAim.cs). A part of another kind already
            // marked keeps its toggle, so the mark can be taken off.
            bool canTurret = tags.Turret != null;
            bool canPointer = canCannon || tags.Pointer != null;
            // A hole cutter that cuts into armor can armor its holes' walls (a cutter already marked keeps its toggle,
            // so the mark can be taken off); one that cuts no armor shows nothing.
            builtIsCutter = Has<MeshModifierBaseData>();
            builtCutArmor = builtIsCutter ? ArmoredBlocksCut(part) : null;
            bool canHole = builtIsCutter && ((builtCutArmor != null && builtCutArmor.Count > 0) || tags.Hole != null);

            if (!canArmor && !canCannon && !canModule && !canVent && !canCrew && !canAim && !canHole && !canTurret && !canPointer)
            {
                container.Visible = false;
                return;
            }
            container.Visible = true;

            builtForName = part.Part.Name;
            PanelBuilder builder = new PanelBuilder(container);
            builder.Header("Armor Fragments");

            if (canArmor)
            {
                // Armor takes no damage, so it can't also be a damage module, an ammo rack or a cook-off vent.
                // Switching armor on switches them off, and switching one of them on switches armor off.
                builder.Toggle("Armor",
                    "Makes this part armor: shells that hit it can be blocked or can send fragments behind it. " +
                    "Armor takes no damage, so it can't also be a damage module, ammo rack or cook-off vent: those switch off.",
                    tags.Armor != null,
                    on => Switch(tags, () =>
                    {
                        // New armor: every face open until the player gives it a thickness, starting with all faces.
                        tags.Armor = on ? new ArmorTag { Faces = new float[ArmorFaces.Count] } : null;
                        if (!on) return;
                        SelectedFace = AllFaces;
                        tags.Module = null;
                        tags.AmmoRack = null;
                        tags.Vent = null;
                    }));
                if (tags.Armor != null)
                {
                    // Armor is always per face. Whole-block armor from before becomes every face at its thickness.
                    if (!tags.Armor.PerFace)
                    {
                        float thickness = tags.Armor.Thickness;
                        tags.Armor.Faces = new float[ArmorFaces.Count];
                        for (int i = 0; i < ArmorFaces.Count; i++) tags.Armor.Faces[i] = thickness;
                        Commit(tags, false);
                    }

                    FaceRows(builder, tags);
                    bool holeRow = ArmorSurface.IsCarved(part);   // a row for armored hole walls on a carved block
                    string[] weightLabels = new string[ArmorFaces.Count + 1 + (holeRow ? 1 : 0)];
                    weightLabels[0] = "Armor weight";
                    for (int i = 0; i < ArmorFaces.Count; i++) weightLabels[i + 1] = "  " + ArmorFaces.Names[i];
                    if (holeRow) weightLabels[ArmorFaces.Count + 1] = "  Hole walls";
                    Live(builder, weightLabels, () => string.Join("|", ArmorWeightTexts(holeRow)), () => ArmorWeightTexts(holeRow));
                    Slider(builder, tags, "Hardness", "How hard the armor is compared with the shell: harder armor deflects shells at lower angles. 100% = reference.",
                        10f, 300f, 5f, tags.Armor.Hardness, " %", v => tags.Armor.Hardness = v);
                    Slider(builder, tags, "Brittleness", "How easily the plate shatters into fragments. (Not used yet: energy model.)",
                        0f, 100f, 5f, tags.Armor.Brittleness, " %", v => tags.Armor.Brittleness = v);
                }
            }

            if (canHole)
            {
                builder.Toggle("Hole wall armor",
                    "The walls of the holes this cutter carves into armor blocks become plates of their own: a shell or " +
                    "fragment flying through the hole meets them, and they add to the block's armor weight. " +
                    "Off: the holes are open, with bare walls.",
                    tags.Hole != null,
                    on => Switch(tags, () => tags.Hole = on ? new HoleTag() : null));
                if (tags.Hole != null)
                    Slider(builder, tags, "Wall thickness", "Thickness of the hole walls' plates.",
                        1f, 500f, 1f, tags.Hole.Thickness, " mm", v => tags.Hole.Thickness = v);
                builder.Text("Cuts into armor", builtCutArmor != null && builtCutArmor.Count > 0
                    ? builtCutArmor.Count + " block" + (builtCutArmor.Count == 1 ? "" : "s") : "none");
            }

            if (canCannon)
            {
                // Ammunition: the mod's shell settings and the ammo racks, folded away (Mouse aim follows under its own header).
                builder.Header("Ammunition", collapsed: true);
                builder.Toggle("Mod shell settings",
                    "Describes the shells this cannon fires (hardness, fuse, filler, casing) and lets it draw from ammo racks.",
                    tags.Cannon != null,
                    on => Switch(tags, () => tags.Cannon = on ? new CannonTag() : null));

                // Estimated penetration, for every cannon: the energy model's own rule for a real hit, at the
                // muzzle, against armor of the current resistance. One short value per row.
                PartScript gunPart = part;
                Live(builder, new[] { "Shell energy", "Shell mass", "Muzzle velocity",
                                      "Penetration, 0 deg", "Penetration, 30 deg", "Penetration, 60 deg" },
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
                    builder.Text("Shell kind", Describe(kind));

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

                    // The ammo racks this cannon draws from ([CAN r=]), picked with the game's part picker, in the
                    // order it empties them. With racks, the game's Ammo Count slider is hidden
                    // (AmmoCountVisibilityPatch) and the rows below say how many rounds it really has.
                    PartScript cannonPart = part;
                    List<PartData> racks = AmmoRacks.DesignCannonRacks(cannonPart);
                    if (racks.Count == 0)
                        builder.Text("Ammo racks", "None: uses Ammo Count");
                    for (int i = 0; i < racks.Count; i++)
                        builder.Text("Rack " + (i + 1), AmmoRacks.DesignLabel(racks[i]) + " (id " + racks[i].Id + ")");

                    List<KeyValuePair<string, Action>> rackButtons = new List<KeyValuePair<string, Action>>
                    {
                        new KeyValuePair<string, Action>("Add rack", () => PickRack(cannonPart, true)),
                    };
                    if (racks.Count > 0)
                        rackButtons.Add(new KeyValuePair<string, Action>("Remove rack", () => PickRack(cannonPart, false)));
                    builder.Buttons(rackButtons);

                    Live(builder, new[] { "Rounds", "Round (W x L)", "Shell" },
                         () => CannonSignature(cannonPart), () => CannonTexts(cannonPart));
                    // Rack-fed cannons are loaded by the crew (Crew.cs).
                    if (racks.Count > 0)
                        Live(builder, new[] { "Reload", "Loaded by", "Round weight" },
                             () => string.Join("|", ReloadTexts(cannonPart)), () => ReloadTexts(cannonPart));
                }
            }

            if (canModule)
            {
                builder.Toggle("Damage module",
                    "Marks this part as one that reacts to fragment damage.",
                    tags.Module != null,
                    on => Switch(tags, () =>
                    {
                        tags.Module = on ? new ModuleTag() : null;
                        if (on) tags.Armor = null;
                    }));
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
                    "Makes this fuselage an ammo rack: it holds the rounds of the cannons that draw from it (pick it " +
                    "with Add rack on a cannon), and can catch fire and explode when its rounds are hit.",
                    tags.AmmoRack != null,
                    on => Switch(tags, () =>
                    {
                        tags.AmmoRack = on ? new AmmoRackTag() : null;
                        if (on) tags.Armor = null;
                    }));
                if (tags.AmmoRack != null)
                {
                    PartScript rackPart = part;
                    string prefix = AmmoRacks.VariablePrefix(AmmoRacks.GroupName(tags.AmmoRack.Group));
                    builder.Toggle("No order",
                        "On: cannons empty this rack by distance, their nearest unnumbered rack first. Off: give it an Empty order; " +
                        "cannons empty numbered racks first, lowest number up, then the unnumbered ones.",
                        !tags.AmmoRack.Order.HasValue,
                        on => Switch(tags, () => tags.AmmoRack.Order = on ? (int?)null : 1));
                    if (tags.AmmoRack.Order.HasValue)
                        Slider(builder, tags, "Empty order", "Cannons empty numbered racks from the lowest number up, before any rack with no order. Racks with the same number go nearest first.",
                            1f, 20f, 1f, tags.AmmoRack.Order.Value, "",
                            v => tags.AmmoRack.Order = Mathf.RoundToInt(v));
                    Slider(builder, tags, "Fullness", "How full the rack starts: the share of its rounds loaded at spawn. The top ones are left out, as if already fired.",
                        0f, 100f, 5f, tags.AmmoRack.Fill ?? 100f, " %",
                        v => tags.AmmoRack.Fill = v >= 99.5f ? (float?)null : v);
                    if (tags.Armor != null)
                        builder.Text("Warning", "Armor: holds no rounds");
                    Live(builder, new[] { "Status", "Feeds", "Rounds held", "Layout", "Lost to shape", "Space (L x W x H)", "Round (W x L)", "Shell", "Rounds weight" },
                         () => AmmoRacks.DesignRackSignature(rackPart) + "|" + FeedsText(rackPart),
                         () => RackTexts(rackPart, tags.Armor != null));
                    builder.Text("Rounds variable", prefix + "_rounds");
                    builder.Text("Fire variable", prefix + "_fire");
                    builder.Toggle("See-through racks",
                        "Draws every ammo rack see-through in the designer, with its rounds inside. Clicking still selects the part.",
                        ArmorFragmentsPlugin.ShowRacksInDesigner.Value,
                        on => ArmorFragmentsPlugin.ShowRacksInDesigner.Value = on);
                    builder.Text("Boom variable", prefix + "_boom (for detachers)");
                }
            }

            if (canVent)
            {
                builder.Toggle("Cook-off vent",
                    "Ammo rack cook-offs vent through this part: the bang and the fire jet come out of the vent nearest the " +
                    "rack, and every vent keeps burning afterwards. Mark a fuselage at the turret ring or a hatch (not " +
                    "armor). With no vent, it vents just above the rack.",
                    tags.Vent != null,
                    on => Switch(tags, () =>
                    {
                        tags.Vent = on ? new VentTag() : null;
                        if (on) tags.Armor = null;
                    }));
            }

            if (canCrew)
            {
                builder.Toggle("Crew",
                    "A crew member sits here in flight. Shells and fragments hit his body, not the seat. " +
                    "The vehicle dies with its last crew member.",
                    tags.Crew != null,
                    on => Switch(tags, () =>
                    {
                        tags.Crew = on ? new CrewTag() : null;
                        if (on) tags.Armor = null;
                    }));
                if (tags.Crew != null && !Has<SeatData>())
                    builder.Text("Warning", "Not a seat: only seats can be crew");
                else if (tags.Crew != null)
                    CrewRows(builder, tags);
            }

            if (canAim)
            {
                builder.Toggle("Aimed by crew",
                    "For a turret ring or gun elevation: turns at its speed x the aimer's efficiency, slower as crew are lost.",
                    tags.Aim != null,
                    on => Switch(tags, () => tags.Aim = on ? new AimTag() : null));
                if (tags.Aim != null)
                {
                    CrewJobs needed;
                    List<CrewSeat> seats = Crew.DesignCrew(part.Aircraft, out needed);
                    builder.Text("Turn speed", seats.Count == 0
                        ? "100% (no crew)"
                        : CrewModel.Percent(CrewModel.JobEfficiency(seats, CrewJobs.Aim)));
                }
            }

            if (Has<JointRotatorData>())
            {
                RotatorRows(builder, tags);
            }

            if (canTurret)
            {
                // Old mark: rotators are now picked as Traverse and Elevation on the gun ("Mouse aim" on a cannon); a rotator that still has the
                // old [TUR] mark keeps working and can be cleared here.
                builder.Toggle("Turret rotator (old mark)",
                    "This rotator carries the old turret mark. Rotators are now picked from the gun: select the cannon, turn on Mouse aim and pick its Traverse and Elevation. Turn this off to clear the old mark.",
                    tags.Turret != null,
                    on => Switch(tags, () => tags.Turret = on ? new TurretTag() : null));
            }

            if (canPointer)
            {
                if (canCannon) builder.Header("Mouse aim");
                PointerRows(builder, tags, canCannon);
            }
        }

        // ---- Turret groups (mouse aim) ---------------------------------------------------------------------------

        private static void PointerRows(PanelBuilder builder, PartTags tags, bool canCannon)
        {
            PartScript turretPart = part;

            // A cannon that an older turret lists as its auxiliary cannon stays that turret's until it is taken off there.
            if (tags.Pointer == null)
            {
                PartData owner = FixedOwner(turretPart.Part.Id);
                if (owner != null)
                {
                    builder.Text("Mouse aim", "Auxiliary cannon of turret " + TurretLabel(owner) + " (remove it there to aim it on its own)");
                    return;
                }
            }

            if (!canCannon)
            {
                if (tags.Pointer != null)
                    builder.Buttons(new List<KeyValuePair<string, Action>>
                    {
                        new KeyValuePair<string, Action>("Remove mouse aim mark", () => Switch(tags, () => tags.Pointer = null)),
                    });
                return;
            }

            builder.Toggle("Mouse aim",
                "This gun follows the mouse aim's reticle in flight: the mod turns its Traverse and Elevation rotators so the barrel points where you aim, and it fires with the button or key below. " +
                "Every Mouse aim gun follows the reticle together, unless you put guns in groups.",
                tags.Pointer != null,
                on => Switch(tags, () => tags.Pointer = on ? new PointerTag { Group = 0 } : null));
            if (tags.Pointer == null) return;

            // Traverse and elevation: the rotators the mouse aim drives. A slot left empty is left to the player's own controls.
            AxisRows(builder, turretPart, tags.Pointer, true);
            AxisRows(builder, turretPart, tags.Pointer, false);

            // Rotators linked the older way ("Add rotator"): still used, shown so they can be cleared.
            List<PartData> linked = PartsByIds(tags.Pointer.Rotators, IsRotatorPart);
            if (tags.Pointer.Rotators.Count > 0)
            {
                StringBuilder names = new StringBuilder();
                foreach (PartData rotator in linked) names.Append(names.Length > 0 ? ", " : "").Append(SightLabel(rotator)).Append(" (id ").Append(rotator.Id).Append(')');
                builder.Text("Older rotator links", names.Length > 0 ? names.ToString() : "Missing (ids not found)");
                builder.Buttons(new List<KeyValuePair<string, Action>>
                {
                    new KeyValuePair<string, Action>("Clear older links", () => Switch(tags, () => tags.Pointer.Rotators.Clear())),
                });
            }

            // What it fires with: the mouse button, or a key of its own.
            builder.Text("Fires with", tags.Pointer.ByKey ? "Key: " + FireKeyName(tags.Pointer) : "Left click (the fire mouse button)");
            builder.Buttons(new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Left click", () => Switch(tags, () => tags.Pointer.ByKey = false)),
                new KeyValuePair<string, Action>("Key", () => Switch(tags, () => tags.Pointer.ByKey = true)),
            }, tags.Pointer.ByKey ? 1 : 0);
            if (tags.Pointer.ByKey)
            {
                builder.TextInput("Fire key",
                    "The key this gun fires with in mouse aim. Type a letter, a digit or a key's name (Space, LeftCtrl, Tab, F, 1...).",
                    FireKeyName(tags.Pointer), 24, typed => SetFireKey(turretPart, typed), "Space");
            }

            // Gun sight: the eye of this gun's sniper view. One per gun: a Gun Sight part picked here, or the gun's own barrel. A Gun Sight part
            // belongs to one gun (or to the guns of one turret group). With neither, the gun has no sniper view.
            builder.Text("Gun sight", "");
            PartData sight = SightCameraOf(tags.Pointer);
            if (tags.Pointer.Camera.HasValue)
            {
                builder.Text("Sight", sight != null ? SightLabel(sight) + " (id " + sight.Id + ")" : "Missing (id " + tags.Pointer.Camera.Value + ")");
                builder.Buttons(new List<KeyValuePair<string, Action>>
                {
                    new KeyValuePair<string, Action>("Clear sight", () => PickSight(turretPart, true)),
                });
                // Guns of a group sharing this sight: one of them is the gun its range marks are worked out for (War Thunder style).
                if (tags.Pointer.Group >= 1 && SightSharers(tags.Pointer.Camera.Value, tags.Pointer.Group, turretPart.Part).Count > 0)
                {
                    builder.Toggle("Ranging gun",
                        "The guns of this group share this sight: its range marks are worked out for this gun's shell. Only one gun per sight; " +
                        "with none marked, the biggest gun is used.",
                        tags.Pointer.RangingGun,
                        on => SetRangingGun(turretPart, on));
                }
            }
            else
            {
                builder.Toggle("Gun as gun sight",
                    "The sniper view sits on this gun's barrel and looks down it, instead of through a Gun Sight part.",
                    tags.Pointer.GunAsSight,
                    on => Switch(tags, () => tags.Pointer.GunAsSight = on));
                if (!tags.Pointer.GunAsSight)
                {
                    builder.Text("Sight", "None (no sniper view)");
                    builder.Buttons(new List<KeyValuePair<string, Action>>
                    {
                        new KeyValuePair<string, Action>("Pick camera sight", () => PickSight(turretPart, false)),
                    });
                }
            }

            // Groups: off = this gun is in the vehicle's main set (every Mouse aim gun follows the reticle together).
            int group = tags.Pointer.Group;
            builder.Toggle("Group",
                "Put this gun in a turret group of its own (for ships and vehicles with separate batteries): in flight only one group follows the reticle at a time (Tab switches), " +
                "and the others can aim by themselves. Off = it is in the main set with every other Mouse aim gun.",
                group >= 1,
                on => Switch(tags, () =>
                {
                    if (on)
                    {
                        int id = NextGroupId();
                        tags.Pointer.Group = id;
                        tags.Pointer.Name = GroupNames.NextFree(UsedGroupNames(), id);
                        if (tags.Pointer.TurretName == null) tags.Pointer.TurretName = GroupNames.NextTurretName(UsedTurretNames());
                    }
                    else
                    {
                        tags.Pointer.Group = 0;
                        tags.Pointer.Name = null;
                    }
                }));
            if (group >= 1)
            {
                List<PartData> members = GroupMembers(group);
                builder.Text("Turret group", GroupNames.Display(tags.Pointer.Name, group) + " (" + members.Count + (members.Count == 1 ? " gun)" : " guns)"));
                builder.TextInput("Group name", "Rename the turret group (all its guns). New groups are named after the military phonetic alphabet; leave it empty to get the name of the group's number.",
                                  GroupNames.Display(tags.Pointer.Name, group), GroupNames.MaxLength, typed => RenameGroup(group, typed));
                builder.TextInput("Turret name", "This gun's name in its group. Leave it empty to show the part's id.",
                                  tags.Pointer.TurretName ?? "", GroupNames.MaxLength, typed => RenameTurret(turretPart, typed));
                for (int i = 0; i < members.Count; i++)
                    builder.Text("Gun " + (i + 1), TurretLabel(members[i]) + " (id " + members[i].Id + ")" + (members[i] == turretPart.Part ? " - this one" : ""));
                List<KeyValuePair<string, Action>> groupButtons = new List<KeyValuePair<string, Action>>
                {
                    new KeyValuePair<string, Action>("Add gun to group", () => PickTurret(turretPart, true)),
                    new KeyValuePair<string, Action>("Remove gun from group", () => PickTurret(turretPart, false)),
                };
                builder.Buttons(groupButtons);
                List<KeyValuePair<string, Action>> joins = new List<KeyValuePair<string, Action>>();
                foreach (int other in ExistingGroups())
                {
                    if (other == group) continue;
                    int joined = other;
                    joins.Add(new KeyValuePair<string, Action>("Move to " + GroupNames.Display(GroupNameOf(joined), joined),
                        () => Switch(tags, () => { tags.Pointer.Group = joined; tags.Pointer.Name = GroupNameOf(joined); })));
                }
                if (joins.Count > 0) builder.Buttons(joins);
            }

            // Auxiliary cannons linked the older way: they still move with this gun and fire with it. Shown only when there are some, so they
            // can be taken off (a gun on the same mount is better made a Mouse aim gun of its own).
            List<PartData> fixedCannons = PartsByIds(tags.Pointer.Fixed, IsCannonPart);
            if (tags.Pointer.Fixed.Count > 0)
            {
                foreach (PartData fixedCannon in fixedCannons)
                {
                    int fixedId = fixedCannon.Id;
                    builder.Toggle("Fire by key: " + SightLabel(fixedCannon) + " (auxiliary)",
                        "In mouse aim this auxiliary cannon fires with the fire key instead of the mouse button.",
                        tags.Pointer.FixedByKey.Contains(fixedId),
                        on => Switch(tags, () =>
                        {
                            tags.Pointer.FixedByKey.Remove(fixedId);
                            if (on) tags.Pointer.FixedByKey.Add(fixedId);
                        }));
                }
                builder.Buttons(new List<KeyValuePair<string, Action>>
                {
                    new KeyValuePair<string, Action>("Remove auxiliary cannon", () => PickFixed(turretPart, false)),
                });
            }
        }

        // One axis slot of a Mouse aim gun: the rotator picked for it, and the buttons to pick or clear it.
        private static void AxisRows(PanelBuilder builder, PartScript gunPart, PointerTag pointer, bool traverse)
        {
            int? id = traverse ? pointer.Traverse : pointer.Elevation;
            string label = traverse ? "Traverse" : "Elevation";
            PartData picked = null;
            if (id.HasValue)
                foreach (PartData candidate in AllParts())
                    if (candidate.Id == id.Value && IsRotatorPart(candidate)) { picked = candidate; break; }
            builder.Text(label, picked != null ? SightLabel(picked) + " (id " + picked.Id + ")"
                : id.HasValue ? "Missing (id " + id.Value + ")"
                : "None (left to your own controls)");
            List<KeyValuePair<string, Action>> buttons = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>(traverse ? "Pick traverse" : "Pick elevation", () => PickAxis(gunPart, traverse)),
            };
            if (id.HasValue)
                buttons.Add(new KeyValuePair<string, Action>("Clear", () =>
                {
                    PartTags fresh = TagCodec.ParseCopy(gunPart.Part.Name);
                    if (fresh.Pointer == null) return;
                    if (traverse) fresh.Pointer.Traverse = null; else fresh.Pointer.Elevation = null;
                    ApplyTags(gunPart.Part, fresh);
                }));
            builder.Buttons(buttons);
        }

        // Picks the rotator (or hinge) that turns this gun left and right, or up and down, with the game's part picker.
        private static void PickAxis(PartScript gunPart, bool traverse)
        {
            Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
            if (designer == null || gunPart == null || gunPart.Part == null) return;
            bool any = false;
            foreach (PartData candidate in AllParts())
                if (IsRotatorPart(candidate)) { any = true; break; }
            if (!any)
            {
                designer.ShowMessage("No rotator or hinge on the craft.", 7f);
                return;
            }
            designer.ShowMessage(traverse ? "Click the rotator that turns this gun left and right (the turret ring)."
                                          : "Click the hinge or rotator that raises and lowers this gun.", 7f);
            designer.Tools.SelectChoosePartTool(IsRotatorPart, false, -1, "No rotators", picked =>
            {
                try
                {
                    if (picked == null || gunPart == null || gunPart.Part == null) return;
                    PartTags fresh = TagCodec.ParseCopy(gunPart.Part.Name);
                    if (fresh.Pointer == null) return;
                    if (traverse) fresh.Pointer.Traverse = picked.Id; else fresh.Pointer.Elevation = picked.Id;
                    ApplyTags(gunPart.Part, fresh);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("Couldn't set the gun's " + (traverse ? "traverse" : "elevation") + " rotator: " + e);
                }
            }, false);
        }

        // The other guns of 'group' that picked the same sight part.
        private static List<PartData> SightSharers(int camera, int group, PartData except)
        {
            List<PartData> sharers = new List<PartData>();
            foreach (PartData other in AllParts())
            {
                if (other == except) continue;
                PointerTag pointer = TagCodec.Parse(other.Name).Pointer;
                if (pointer != null && pointer.Group == group && pointer.Camera == camera) sharers.Add(other);
            }
            return sharers;
        }

        // Marks this gun as its sight's ranging gun (and takes the mark off the others sharing the sight).
        private static void SetRangingGun(PartScript gunPart, bool on)
        {
            if (gunPart == null || gunPart.Part == null) return;
            PartTags fresh = TagCodec.ParseCopy(gunPart.Part.Name);
            if (fresh.Pointer == null || !fresh.Pointer.Camera.HasValue) return;
            if (on)
            {
                foreach (PartData other in SightSharers(fresh.Pointer.Camera.Value, fresh.Pointer.Group, gunPart.Part))
                {
                    PartTags otherTags = TagCodec.ParseCopy(other.Name);
                    if (otherTags.Pointer == null || !otherTags.Pointer.RangingGun) continue;
                    otherTags.Pointer.RangingGun = false;
                    RackLinks.SetName(other, TagCodec.Compose(otherTags, other.PartType.Name));
                }
            }
            fresh.Pointer.RangingGun = on;
            ApplyTags(gunPart.Part, fresh);
        }

        // The key a gun that fires by key uses: its own, or the fire key of the config.
        private static string FireKeyName(PointerTag pointer)
        {
            string own = TagCodec.CleanKey(pointer.FireKey);
            return own ?? ArmorFragmentsPlugin.TurretFireKey.Value.ToString();
        }

        private static void SetFireKey(PartScript gunPart, string typed)
        {
            if (gunPart == null || gunPart.Part == null) return;
            UnityEngine.InputSystem.Key key;
            if (!TurretAim.TryParseKey(typed, out key))
            {
                Assets.Scripts.Design.Designer.Instance.ShowMessage("Unknown key \"" + typed + "\". Type a letter, a digit or a key's name (Space, LeftCtrl, Tab...).", 7f);
                rebuildPending = true;   // the box shows the key in use again
                return;
            }
            PartTags fresh = TagCodec.ParseCopy(gunPart.Part.Name);
            if (fresh.Pointer == null) return;
            fresh.Pointer.FireKey = key == ArmorFragmentsPlugin.TurretFireKey.Value ? null : key.ToString();
            ApplyTags(gunPart.Part, fresh);
        }

        // ---- Rotators and hinges: Unlimited and the speed in degrees per second ----------------------------------------

        private static void RotatorRows(PanelBuilder builder, PartTags tags)
        {
            JointRotatorData data = null;
            foreach (PartModifierData modifier in part.Part.Modifiers)
                if (modifier is JointRotatorData) { data = (JointRotatorData)modifier; break; }
            if (data == null) return;

            // Unlimited: the game's own "shortest angle" mode of a rotator (it turns the short way round and has no end stop). Not on hinges.
            if (!RotatorSpeed.IsHinge(part.Part, data))
            {
                bool unlimited = data.ShortestAngle;
                builder.Text("Traverse", unlimited ? "Unlimited (turns all the way round, the short way)" : "Limited by Range (" + data.Range.ToString("0", CultureInfo.InvariantCulture) + " deg each way)");
                builder.Buttons(new List<KeyValuePair<string, Action>>
                {
                    new KeyValuePair<string, Action>("Limited", () => SetUnlimited(data, false)),
                    new KeyValuePair<string, Action>("Unlimited", () => SetUnlimited(data, true)),
                }, unlimited ? 1 : 0);
            }

            // Speed in degrees per second, in place of the game's Speed slider.
            float stock = RotatorSpeed.StockDegreesPerSecond(data);
            builder.Toggle("Speed in deg/s",
                "Set this part's top speed in degrees per second instead of the game's Speed slider (which gives " + stock.ToString("0.#", CultureInfo.InvariantCulture) + " deg/s now). " +
                "Aimed by crew still slows it.",
                tags.Speed != null,
                on => Switch(tags, () => tags.Speed = on ? new SpeedTag { DegreesPerSecond = Mathf.Round(stock * 10f) / 10f } : null));
            if (tags.Speed != null)
            {
                Slider(builder, tags, "Speed", "Top speed in degrees per second. Type a bigger number in the box below.",
                       0f, 360f, 0.5f, Mathf.Min(tags.Speed.DegreesPerSecond, 360f), " deg/s", v => tags.Speed.DegreesPerSecond = v, true);
                builder.TextInput("Exact speed (deg/s)", "Any speed, decimals or large numbers. Very fast heavy parts can tear their own joint.",
                    tags.Speed.DegreesPerSecond.ToString("0.###", CultureInfo.InvariantCulture), 12, typed =>
                    {
                        float value;
                        if (!float.TryParse(typed.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0f || float.IsInfinity(value))
                        {
                            Assets.Scripts.Design.Designer.Instance.ShowMessage("Type a speed in degrees per second, for example 12.5 or 720.", 7f);
                            rebuildPending = true;
                            return;
                        }
                        PartTags fresh = TagCodec.ParseCopy(part.Part.Name);
                        fresh.Speed = new SpeedTag { DegreesPerSecond = value };
                        Commit(fresh, true);
                        rebuildPending = true;
                    }, "30");
            }
            else builder.Text("Speed", stock.ToString("0.#", CultureInfo.InvariantCulture) + " deg/s (from the game's Speed slider)");
        }

        private static void SetUnlimited(JointRotatorData data, bool on)
        {
            if (data == null || data.ShortestAngle == on) return;
            data.ShortestAngle = on;
            Assets.Scripts.Design.Designer.Instance.CreateUndoStepForSelectedPart("Rotator unlimited", "rotator-unlimited-" + part.Part.Id);
            rebuildPending = true;
        }

        // The label of a turret: its name, or its part id when it has none.
        private static string TurretLabel(PartData data)
        {
            PointerTag pointer = TagCodec.Parse(data.Name).Pointer;
            string name = pointer != null ? GroupNames.Clean(pointer.TurretName) : null;
            return name != null ? name.Replace('_', ' ') : "id " + data.Id;
        }

        private static List<string> UsedTurretNames()
        {
            List<string> used = new List<string>();
            foreach (PartData candidate in AllParts())
            {
                PointerTag pointer = TagCodec.Parse(candidate.Name).Pointer;
                if (pointer != null && pointer.TurretName != null) used.Add(pointer.TurretName);
            }
            return used;
        }

        private static void RenameTurret(PartScript turretPart, string typed)
        {
            if (turretPart == null || turretPart.Part == null) return;
            PartTags fresh = TagCodec.ParseCopy(turretPart.Part.Name);
            if (fresh.Pointer == null) return;
            fresh.Pointer.TurretName = GroupNames.Clean(typed);
            ApplyTags(turretPart.Part, fresh);
        }

        // The turret this cannon is an auxiliary cannon of (null if none).
        private static PartData FixedOwner(int id)
        {
            foreach (PartData candidate in AllParts())
            {
                PointerTag pointer = TagCodec.Parse(candidate.Name).Pointer;
                if (pointer != null && pointer.Fixed.Contains(id)) return candidate;
            }
            return null;
        }

        private static List<int> ExistingGroups()
        {
            List<int> groups = new List<int>();
            foreach (PartData candidate in AllParts())
            {
                PointerTag pointer = TagCodec.Parse(candidate.Name).Pointer;
                if (pointer != null && pointer.Group >= 1 && !groups.Contains(pointer.Group)) groups.Add(pointer.Group);
            }
            groups.Sort();
            return groups;
        }

        private static string GroupNameOf(int group)
        {
            foreach (PartData member in GroupMembers(group))
            {
                PointerTag pointer = TagCodec.Parse(member.Name).Pointer;
                if (pointer != null && pointer.Name != null) return pointer.Name;
            }
            return null;
        }

        private static bool IsRotatorPart(PartData candidate)
        {
            if (candidate == null) return false;
            foreach (PartModifierData modifier in candidate.Modifiers)
                if (modifier is JointRotatorData) return true;
            return false;
        }

        // Adds a rotator to this turret (or takes one away), picked with the game's part picker.
        private static void PickRotator(PartScript turretPart, bool add)
        {
            Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
            if (designer == null || turretPart == null || turretPart.Part == null) return;
            PointerTag current = TagCodec.Parse(turretPart.Part.Name).Pointer;
            if (current == null) return;
            List<int> already = new List<int>(current.Rotators);

            Func<PartData, bool> filter = add
                ? (Func<PartData, bool>)(candidate => IsRotatorPart(candidate) && !already.Contains(candidate.Id))
                : candidate => already.Contains(candidate.Id);
            bool any = false;
            foreach (PartData candidate in AllParts())
                if (filter(candidate)) { any = true; break; }
            if (!any)
            {
                designer.ShowMessage(add ? "No rotator to add." : "No rotator linked to this turret.", 7f);
                return;
            }

            designer.ShowMessage(add ? "Click a rotator (ring, hinge): it turns this turret." : "Click the rotator to unlink from this turret.", 7f);
            designer.Tools.SelectChoosePartTool(filter, false, -1, "No rotators", picked =>
            {
                try
                {
                    if (picked == null || turretPart == null || turretPart.Part == null) return;
                    PartTags fresh = TagCodec.ParseCopy(turretPart.Part.Name);
                    if (fresh.Pointer == null) return;
                    if (add) { if (!fresh.Pointer.Rotators.Contains(picked.Id)) fresh.Pointer.Rotators.Add(picked.Id); }
                    else fresh.Pointer.Rotators.Remove(picked.Id);
                    ApplyTags(turretPart.Part, fresh);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("Couldn't change the turret's rotators: " + e);
                }
            }, false);
        }

        private static IEnumerable<PartData> AllParts()
        {
            Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
            if (designer == null) yield break;
            foreach (PartData candidate in designer.Aircraft.Aircraft.Assembly.Parts)
                if (candidate != null) yield return candidate;
        }

        private static List<PartData> GroupMembers(int group)
        {
            List<PartData> members = new List<PartData>();
            foreach (PartData candidate in AllParts())
            {
                PointerTag pointer = TagCodec.Parse(candidate.Name).Pointer;
                if (pointer != null && pointer.Group == group) members.Add(candidate);
            }
            return members;
        }

        // The names every group shows now (so a new group does not repeat one).
        private static List<string> UsedGroupNames()
        {
            Dictionary<int, string> byGroup = new Dictionary<int, string>();
            foreach (PartData candidate in AllParts())
            {
                PointerTag pointer = TagCodec.Parse(candidate.Name).Pointer;
                if (pointer != null && pointer.Group >= 1 && !byGroup.ContainsKey(pointer.Group)) byGroup[pointer.Group] = GroupNames.Display(pointer.Name, pointer.Group);
            }
            return new List<string>(byGroup.Values);
        }

        // The new name goes to every turret of the group (each one's name carries it).
        private static void RenameGroup(int group, string typed)
        {
            string clean = GroupNames.Clean(typed);
            foreach (PartData member in GroupMembers(group))
            {
                PartTags fresh = TagCodec.ParseCopy(member.Name);
                if (fresh.Pointer == null) continue;
                fresh.Pointer.Name = clean;
                ApplyTags(member, fresh);
            }
            rebuildPending = true;
        }

        private static int NextGroupId()
        {
            int highest = 0;
            foreach (PartData candidate in AllParts())
            {
                PointerTag pointer = TagCodec.Parse(candidate.Name).Pointer;
                if (pointer != null && pointer.Group > highest) highest = pointer.Group;
            }
            return highest + 1;
        }

        // Is this cannon already fixed to some turret?
        private static bool IsFixedAnywhere(int id)
        {
            foreach (PartData candidate in AllParts())
            {
                PointerTag pointer = TagCodec.Parse(candidate.Name).Pointer;
                if (pointer != null && pointer.Fixed.Contains(id)) return true;
            }
            return false;
        }

        private static List<PartData> PartsByIds(IList<int> ids, Func<PartData, bool> kind)
        {
            List<PartData> found = new List<PartData>();
            foreach (int id in ids)
                foreach (PartData candidate in AllParts())
                    if (candidate.Id == id && kind(candidate)) { found.Add(candidate); break; }
            return found;
        }

        // Writes new tags to a part: the selected one through the designer's usual path (with an undo step), any other one straight to its name.
        private static void ApplyTags(PartData target, PartTags fresh)
        {
            if (part != null && part.Part == target)
            {
                Commit(fresh, true);
                rebuildPending = true;
            }
            else
            {
                RackLinks.SetName(target, TagCodec.Compose(fresh, target.PartType.Name));
            }
        }

        // Adds a turret to this turret's group (or takes one out of it), picked with the game's part picker. Only turrets can join a group.
        private static void PickTurret(PartScript turretPart, bool add)
        {
            Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
            if (designer == null || turretPart == null || turretPart.Part == null) return;
            PointerTag current = TagCodec.Parse(turretPart.Part.Name).Pointer;
            if (current == null || current.Group < 1) return;
            int group = current.Group;

            Func<PartData, bool> filter = candidate =>
            {
                PointerTag other = TagCodec.Parse(candidate.Name).Pointer;
                if (other == null || candidate == turretPart.Part) return false;
                return add ? other.Group < 1 : other.Group == group;
            };
            bool any = false;
            foreach (PartData candidate in AllParts())
                if (filter(candidate)) { any = true; break; }
            if (!any)
            {
                designer.ShowMessage(add ? "No turret to add (only turrets that are in no group can be added; make a cannon a turret first)." : "No other turret in this group.", 7f);
                return;
            }

            designer.ShowMessage(add ? "Click a turret: it joins this group." : "Click the turret to take out of this group.", 7f);
            designer.Tools.SelectChoosePartTool(filter, false, -1, "No turrets", picked =>
            {
                try
                {
                    if (picked == null) return;
                    PartTags fresh = TagCodec.ParseCopy(picked.Name);
                    if (fresh.Pointer == null) return;
                    fresh.Pointer.Group = add ? group : 0;
                    fresh.Pointer.Name = add ? current.Name : null;
                    ApplyTags(picked, fresh);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("Couldn't change the turret group: " + e);
                }
            }, false);
        }

        // Adds a fixed cannon to this turret (or takes one away), picked with the game's part picker.
        private static void PickFixed(PartScript turretPart, bool add)
        {
            Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
            if (designer == null || turretPart == null || turretPart.Part == null) return;
            PointerTag current = TagCodec.Parse(turretPart.Part.Name).Pointer;
            if (current == null) return;
            List<int> already = new List<int>(current.Fixed);

            Func<PartData, bool> filter = add
                ? (Func<PartData, bool>)(candidate => IsCannonPart(candidate) && candidate != turretPart.Part &&
                                                      TagCodec.Parse(candidate.Name).Pointer == null && !IsFixedAnywhere(candidate.Id))
                : candidate => already.Contains(candidate.Id);
            bool any = false;
            foreach (PartData candidate in AllParts())
                if (filter(candidate)) { any = true; break; }
            if (!any)
            {
                designer.ShowMessage(add ? "No cannon to add (cannons that are turrets or already auxiliary can't be added)." : "No auxiliary cannon to remove.", 7f);
                return;
            }

            designer.ShowMessage(add ? "Click a cannon: it becomes an auxiliary cannon of this turret and fires with it." : "Click the auxiliary cannon to take off this turret.", 7f);
            designer.Tools.SelectChoosePartTool(filter, false, -1, "No cannons", picked =>
            {
                try
                {
                    if (picked == null || turretPart == null || turretPart.Part == null) return;
                    PartTags fresh = TagCodec.ParseCopy(turretPart.Part.Name);
                    if (fresh.Pointer == null) return;
                    if (add) { if (!fresh.Pointer.Fixed.Contains(picked.Id)) fresh.Pointer.Fixed.Add(picked.Id); }
                    else { fresh.Pointer.Fixed.Remove(picked.Id); fresh.Pointer.FixedByKey.Remove(picked.Id); }
                    ApplyTags(turretPart.Part, fresh);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("Couldn't change the turret's fixed cannons: " + e);
                }
            }, false);
        }

        // ---- Crew ----------------------------------------------------------------------------

        private static readonly CrewTrade[][] TradeRows =
        {
            new[] { CrewTrade.Commander, CrewTrade.Gunner, CrewTrade.Loader },
            new[] { CrewTrade.Driver, CrewTrade.Radio, CrewTrade.None },
        };

        // Trade buttons (the trade brings its own job), "Also does" toggles for the other jobs, then what this crew
        // member and the whole crew do as they would start a flight.
        private static void CrewRows(PanelBuilder builder, PartTags tags)
        {
            CrewJobs needed;
            List<CrewSeat> seats = Crew.DesignCrew(part.Aircraft, out needed);
            CrewSeat self = seats.Find(s => s.Data == part.Part);

            // One Driver, one Commander and one Radio operator per vehicle, and one crew member driving: what another
            // seat already has is marked * and can't be picked here.
            bool driveTaken = seats.Exists(s => s.Data != part.Part && (s.Designed & CrewJobs.Drive) != 0);
            bool anyTaken = false;

            builder.Text("Trained as", CrewModel.TradeName(tags.Crew.Trade));
            foreach (CrewTrade[] row in TradeRows)
            {
                List<KeyValuePair<string, Action>> buttons = new List<KeyValuePair<string, Action>>();
                int chosen = Array.IndexOf(row, tags.Crew.Trade);   // the seat's own trade: blue
                foreach (CrewTrade trade in row)
                {
                    CrewTrade picked = trade;
                    string text = trade == CrewTrade.Radio ? "Radio op." : trade == CrewTrade.None ? "No role" : trade.ToString();
                    bool taken = trade != tags.Crew.Trade &&
                                 ((CrewModel.Unique(trade) && seats.Exists(s => s.Data != part.Part && s.Trade == trade)) ||
                                  (trade == CrewTrade.Driver && driveTaken));
                    if (taken)
                    {
                        anyTaken = true;
                        buttons.Add(new KeyValuePair<string, Action>(text + "*", () => { }));
                        continue;
                    }
                    buttons.Add(new KeyValuePair<string, Action>(text, () => Switch(tags, () =>
                    {
                        tags.Crew.Trade = picked;
                        tags.Crew.Jobs &= ~CrewModel.OwnJob(picked);   // his own job now: not an extra
                    })));
                }
                builder.Buttons(buttons, chosen);
            }
            if (anyTaken) builder.Text("*", "another seat has it");

            CrewJobs own = CrewModel.OwnJob(tags.Crew.Trade);
            builder.Text("Own job", own == CrewJobs.None ? "none (spare)" : CrewModel.JobName(own));
            builder.Text("Also does", "");
            if (own != CrewJobs.Drive && driveTaken && (tags.Crew.Jobs & CrewJobs.Drive) == 0)
                builder.Text("   Driving", "another seat drives");
            else
                JobToggle(builder, tags, own, CrewJobs.Drive, "   Driving",
                    "Engines give their horsepower x the driver's efficiency. Only one crew member drives.");
            JobToggle(builder, tags, own, CrewJobs.Aim, "   Aiming",
                "Rotators with \"Aimed by crew\" turn at their speed x the aim efficiency. A second aimer adds 30% of his.");
            JobToggle(builder, tags, own, CrewJobs.Load, "   Loading",
                "Rack-fed cannons reload in Firing Delay / the load efficiency. A round takes one loader per 20 kg; extras keep helping, each less.");

            // One short row per job: the panel's rows are a label and a value side by side.
            if (self != null)
            {
                builder.Text("This seat", self.Jobs == CrewJobs.None ? "spare (no job)" : "");
                foreach (CrewJobs job in CrewModel.AllJobs)
                {
                    if ((self.Jobs & job) == 0) continue;
                    builder.Text("   " + CrewModel.JobName(job), CrewModel.Percent(CrewModel.Efficiency(self, job)) +
                                 ((self.Designed & job) == 0 ? " (auto)" : ""));
                }
            }

            builder.Text("Vehicle", seats.Count == 1 ? "1 crew" : seats.Count.ToString(CultureInfo.InvariantCulture) + " crew");
            foreach (CrewJobs job in CrewModel.AllJobs)
            {
                if ((needed & job) == 0) continue;
                int doing = CrewModel.Doing(seats, job);
                builder.Text("   " + CrewModel.JobName(job), CrewModel.Percent(CrewModel.JobEfficiency(seats, job)) +
                             (doing > 1 ? " (" + doing + " crew)" : ""));
            }
            builder.Text("Alive variable", Crew.AliveVariableName);
            builder.Text("Job variables", "Crew_drive/_aim/_load");
        }

        // A job besides the trade's own (none for the trade's own job, which he always does).
        private static void JobToggle(PanelBuilder builder, PartTags tags, CrewJobs own, CrewJobs job, string label, string tooltip)
        {
            if (job == own) return;
            builder.Toggle(label, tooltip + " A " + CrewModel.TradeName(tags.Crew.Trade) + " does it at " +
                                  CrewModel.Percent(CrewModel.SkillOf(tags.Crew.Trade, job)) +
                                  "; each job beyond his first takes 10% off all of them.",
                (tags.Crew.Jobs & job) != 0,
                on => Switch(tags, () => tags.Crew.Jobs = on ? tags.Crew.Jobs | job : tags.Crew.Jobs & ~job));
        }

        // A rack-fed cannon's reload with the crew as they would start a flight: the reload, who loads, and its round's
        // weight with the loaders it takes (CrewModel.LoadEfficiency).
        private static string[] ReloadTexts(PartScript cannonPart)
        {
            CannonData cannon = null;
            foreach (PartModifierData modifier in cannonPart.Part.Modifiers)
                if (modifier is CannonData) cannon = (CannonData)modifier;
            if (cannon == null) return new[] { "", "", "" };

            AmmoRound round = AmmoRound.FromCannon(cannon);
            float kg = round.RoundMass;
            int hands = CrewModel.Hands(kg);
            string weight = kg.ToString("0", CultureInfo.InvariantCulture) + " kg, " +
                            (hands > 1 ? hands + " loaders" : "1 loader");

            CrewJobs needed;
            List<CrewSeat> seats = Crew.DesignCrew(cannonPart.Aircraft, out needed);
            if (seats.Count == 0) return new[] { "won't fire", "no crew seat", weight };

            float load = CrewModel.LoadEfficiency(seats, kg);
            int loaders = CrewModel.Doing(seats, CrewJobs.Load);
            CrewSeat loader = seats.Find(s => CrewModel.Efficiency(s, CrewJobs.Load) > 0f);
            if (loader == null || load <= 0f) return new[] { "won't fire", "nobody", weight };
            return new[]
            {
                (cannon.FiringDelay / load).ToString("0.0", CultureInfo.InvariantCulture) + " s (base " +
                cannon.FiringDelay.ToString("0.0", CultureInfo.InvariantCulture) + " s)",
                (loaders > 1 ? loaders + " crew" : CrewModel.TradeName(loader.Trade)) + ", " + CrewModel.Percent(load),
                weight,
            };
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
            if (RackGrid.DesignerDragging()) return;   // re-measured once the part is let go
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

        // Status, Feeds, Rounds held, Layout, Lost to shape, Space (L x W x H), Round (W x L), Shell, Rounds weight
        private static string[] RackTexts(PartScript rackPart, bool isArmor)
        {
            if (isArmor) return new[] { "Armor: holds nothing", "-", "0", "-", "-", "-", "-", "-", "-" };

            string feeds = FeedsText(rackPart);
            AmmoRound round;
            int cannons;
            bool mixed;
            if (!AmmoRacks.DesignRound(rackPart, out round, out cannons, out mixed))
                return new[] { "No cannon draws from it", feeds, "0", "-", "-", "-", "-", "-", "-" };

            RackGrid grid = RackGrid.Measure(rackPart, round);
            bool measured = grid.Problem == null;
            bool hasGrid = grid.Slots > 0 && grid.Columns > 0 && grid.Layers > 0;
            return new[]
            {
                measured && mixed ? "OK (mixed: biggest round)" : grid.Status,
                feeds,
                grid.Remaining < grid.Capacity
                    ? grid.Remaining + " of " + grid.Capacity + " (" + grid.FullnessPercent.ToString("F0", CultureInfo.InvariantCulture) + "% full)"
                    : grid.Capacity.ToString(CultureInfo.InvariantCulture),
                hasGrid ? grid.Orientation + ", up to " + grid.Slots + " x " + grid.Columns + " x " + grid.Layers : "-",
                grid.LostToShape.ToString(CultureInfo.InvariantCulture),
                grid.InsideLength > 0f ? Cm(grid.InsideLength) + " x " + Cm(grid.InsideWidth) + " x " + Cm(grid.InsideHeight) + " cm" : "-",
                round.ShortSize(),
                round.ShortShell(),
                (grid.Remaining * round.RoundMass).ToString("F0", CultureInfo.InvariantCulture) + " kg (added in flight)",
            };
        }

        // Ammo from, Rounds, Round (W x L), Shell
        // Shell energy, Shell mass, Muzzle velocity, Penetration at 0, 30 and 60 deg.
        // A shell gets through a plate when its energy beats ArmorResistance x thickness / cos(angle) x its
        // cross-section in cm2 (EnergyModel.ArmorAbsorption), so the thickest plate it beats is
        // energy / (resistance x area) x cos(angle). At the muzzle (the shell slows down with distance);
        // ricochets and the plate's hardness are not counted.
        private static string[] PenetrationTexts(PartScript cannonPart)
        {
            AmmoRound round;
            if (!AmmoRound.TryFromPart(cannonPart, out round)) return new[] { "-", "-", "-", "-", "-", "-" };

            float energy = round.Energy;   // J
            float radiusCm = round.CaliberMm * 0.05f;
            float areaCm2 = Mathf.PI * radiusCm * radiusCm;
            float resistance = Mathf.Max(ArmorFragmentsPlugin.ArmorResistance.Value, 0.001f);
            float headOn = energy / (resistance * Mathf.Max(areaCm2, 0.0001f));   // mm

            // HE bursts on the first thing it touches (FragmentSpawner.BurstOnSurface): the shell itself goes
            // through nothing, only its fragments do. APHE goes through like AP.
            bool bursts = KindOfCannon(cannonPart) == AmmoKind.HE;
            string Mm(float angle) => bursts
                ? "none (HE bursts)"
                : (headOn * Mathf.Cos(angle * Mathf.Deg2Rad)).ToString("F0", CultureInfo.InvariantCulture) + " mm";
            return new[]
            {
                (energy / 1000f).ToString("F0", CultureInfo.InvariantCulture) + " kJ",
                round.ShellMass.ToString("F2", CultureInfo.InvariantCulture) + " kg",
                round.Velocity.ToString("F0", CultureInfo.InvariantCulture) + " m/s",
                Mm(0f),
                Mm(30f),
                Mm(60f),
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

        // Rounds, Round (W x L), Shell
        private static string[] CannonTexts(PartScript cannonPart)
        {
            AmmoRound own;
            bool hasOwn = AmmoRound.TryFromPart(cannonPart, out own);

            int racks;
            bool shared;
            int total = AmmoRacks.DesignCapacity(cannonPart, out racks, out shared);

            return new[]
            {
                racks == 0 ? "Ammo Count slider" : total + (shared ? " (shared)" : ""),
                hasOwn ? own.ShortSize() : "-",
                hasOwn ? own.ShortShell() : "-",
            };
        }

        // The cannons a rack feeds.
        private static string FeedsText(PartScript rackPart)
        {
            List<PartData> cannons = AmmoRacks.DesignRackCannons(rackPart);
            if (cannons.Count == 0) return "None (Add rack on a cannon)";
            if (cannons.Count == 1) return AmmoRacks.DesignLabel(cannons[0]) + " (id " + cannons[0].Id + ")";
            return cannons.Count + " cannons";
        }

        // Opens the game's part picker (the one wheels use to pick their engine): every part that can't be
        // picked is hidden until a rack is clicked or the picker is closed.
        // ---- The optical sight (a camera part) of a pointer cannon -----------------------------------------------

        private static bool IsCameraPart(PartData candidate)
        {
            if (candidate == null) return false;
            foreach (PartModifierData modifier in candidate.Modifiers)
                if (modifier is CameraVantageData) return true;
            return false;
        }

        private static PartData SightCameraOf(PointerTag pointer)
        {
            Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
            if (designer == null || pointer == null || !pointer.Camera.HasValue) return null;
            foreach (PartData candidate in designer.Aircraft.Aircraft.Assembly.Parts)
                if (candidate != null && candidate.Id == pointer.Camera.Value && IsCameraPart(candidate)) return candidate;
            return null;
        }

        private static string SightLabel(PartData camera)
        {
            string label = TagCodec.Parse(camera.Name).Label;
            return string.IsNullOrEmpty(label) ? camera.PartType.Name : label;
        }

        private static bool IsCannonPart(PartData candidate)
        {
            if (candidate == null) return false;
            foreach (PartModifierData modifier in candidate.Modifiers)
                if (modifier is CannonData) return true;
            return false;
        }

        // Picks the camera part this cannon's sight looks through (or, with 'clear', takes it away).
        private static void PickSight(PartScript cannonPart, bool clear)
        {
            Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
            if (designer == null || cannonPart == null || cannonPart.Part == null) return;

            Action<int?> apply = camera =>
            {
                try
                {
                    if (cannonPart == null || cannonPart.Part == null) return;
                    PartTags fresh = TagCodec.ParseCopy(cannonPart.Part.Name);
                    if (fresh.Pointer == null) return;
                    fresh.Pointer.Camera = camera;
                    if (part == cannonPart)
                    {
                        Commit(fresh, true);
                        rebuildPending = true;
                    }
                    else
                    {
                        RackLinks.SetName(cannonPart.Part, TagCodec.Compose(fresh, cannonPart.Part.PartType.Name));
                    }
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("Couldn't change the cannon's sight camera: " + e);
                }
            };

            if (clear)
            {
                apply(null);
                return;
            }

            // Only Gun Sight parts that no other gun uses (guns of the same turret group may share one).
            PointerTag mine = TagCodec.Parse(cannonPart.Part.Name).Pointer;
            int myGroup = mine != null ? mine.Group : 0;
            Func<PartData, bool> filter = candidate =>
            {
                if (!GunSightPart.Is(candidate)) return false;
                foreach (PartData other in designer.Aircraft.Aircraft.Assembly.Parts)
                {
                    if (other == null || other == cannonPart.Part) continue;
                    PointerTag pointer = TagCodec.Parse(other.Name).Pointer;
                    if (pointer == null || pointer.Camera != candidate.Id) continue;
                    if (myGroup < 1 || pointer.Group != myGroup) return false;
                }
                return true;
            };

            bool anySight = false, anyFree = false;
            foreach (PartData candidate in designer.Aircraft.Aircraft.Assembly.Parts)
            {
                if (!GunSightPart.Is(candidate)) continue;
                anySight = true;
                if (filter(candidate)) { anyFree = true; break; }
            }
            if (!anyFree)
            {
                designer.ShowMessage(anySight
                    ? "Every Gun Sight on the craft already belongs to another gun. Add another Gun Sight (Weapons, beside the Tank Cannon)."
                    : "No Gun Sight on the craft. Add one (Weapons, beside the Tank Cannon) where this gun's sight is, facing the way the gun points.", 7f);
                return;
            }

            // Ghost view while picking, so sights inside the turret show; the view the designer had comes back after the pick.
            Assets.Scripts.Design.DesignerViewMode before = designer.ViewMode;
            try { designer.ViewMode = Assets.Scripts.Design.DesignerViewMode.Ghost; } catch (Exception) { }
            designer.ShowMessage("Click the Gun Sight this gun's sniper view looks through.", 7f);
            designer.Tools.SelectChoosePartTool(filter, false, -1, "No free Gun Sights", picked =>
            {
                try { designer.ViewMode = before; } catch (Exception) { }
                if (picked != null) apply(picked.Id);
            }, false);
        }

        private static void PickRack(PartScript cannonPart, bool add)
        {
            Assets.Scripts.Design.Designer designer = Assets.Scripts.Design.Designer.Instance;
            if (designer == null || cannonPart == null || cannonPart.Part == null) return;

            List<PartData> current = AmmoRacks.DesignCannonRacks(cannonPart);
            Func<PartData, bool> filter;
            if (add)
                filter = candidate => AmmoRacks.DesignCanPick(cannonPart, candidate);
            else
                filter = candidate => current.Contains(candidate);

            bool any = false;
            foreach (PartData candidate in designer.Aircraft.Aircraft.Assembly.Parts)
            {
                if (candidate == null || !filter(candidate)) continue;
                any = true;
                break;
            }
            if (!any)
            {
                designer.ShowMessage(add
                    ? "No ammo rack to add. Make a fuselage an ammo rack first; a rack that already feeds a cannon of another round size can't be shared."
                    : "No ammo rack to remove.", 7f);
                return;
            }

            designer.ShowMessage(add ? "Click an ammo rack to feed this cannon from it."
                                     : "Click the ammo rack this cannon should stop drawing from.", 7f);
            designer.Tools.SelectChoosePartTool(filter, false, -1, "No ammo racks", picked =>
            {
                try
                {
                    if (picked == null || cannonPart == null || cannonPart.Part == null) return;
                    PartTags fresh = TagCodec.ParseCopy(cannonPart.Part.Name);
                    if (fresh.Cannon == null) return;
                    if (add)
                    {
                        if (!fresh.Cannon.Racks.Contains(picked.Id)) fresh.Cannon.Racks.Add(picked.Id);
                    }
                    else
                    {
                        fresh.Cannon.Racks.Remove(picked.Id);
                    }

                    if (part == cannonPart)
                    {
                        Commit(fresh, true);
                        rebuildPending = true;   // the rack rows and the Ammo Count slider
                    }
                    else
                    {
                        RackLinks.SetName(cannonPart.Part, TagCodec.Compose(fresh, cannonPart.Part.PartType.Name));
                    }
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("Couldn't change the cannon's ammo racks: " + e);
                }
            }, false);
        }

        private static string Cm(float meters)
        {
            return (meters * 100f).ToString("F1", CultureInfo.InvariantCulture);
        }

        // Changes whenever the cannon's round or one of its racks changes.
        private static string CannonSignature(PartScript cannonPart)
        {
            System.Text.StringBuilder signature = new System.Text.StringBuilder();
            AmmoRound own;
            if (AmmoRound.TryFromPart(cannonPart, out own)) signature.Append(own.Describe());

            foreach (PartData rack in AmmoRacks.DesignCannonRacks(cannonPart))
                signature.Append('|').Append(rack.Id).Append(AmmoRacks.DesignRackSignature(rack.PartScript));
            return signature.ToString();
        }

        // Armor thickness, as the game edits corners: by default one "Thickness" for every face at once. With
        // "Per face" on, two rows of face buttons (the one being edited blue, each with its thickness) and
        // the thickness of the picked face. Edited faces are tinted purple on the block, other armored ones blue.
        private static void FaceRows(PanelBuilder builder, PartTags tags)
        {
            float[] faces = tags.Armor.Faces;

            float thickest = 0f;
            bool same = true;
            for (int i = 0; i < ArmorFaces.Count; i++)
            {
                thickest = Mathf.Max(thickest, faces[i]);
                if (faces[i] != faces[0]) same = false;
            }

            // "Per face" stays as the player left it from block to block (a duplicate opens as its original was being
            // edited); a block whose faces differ always opens per face, since one Thickness can't show them.
            if (!same && SelectedFace == AllFaces) SelectedFace = 0;
            int picked = SelectedFace >= 0 && SelectedFace <= AllFaces ? SelectedFace : AllFaces;

            builder.Toggle("Per face",
                "Off: one thickness for every face of the block. On: each of its six faces gets its own thickness " +
                "(0 mm = open: shells and fragments pass it as empty space). Turning it off sets every face to the thickest one.",
                picked != AllFaces,
                on => Switch(tags, () =>
                {
                    SelectedFace = on ? 0 : AllFaces;
                    if (on) return;
                    for (int i = 0; i < ArmorFaces.Count; i++) tags.Armor.Faces[i] = thickest;
                }));

            if (picked == AllFaces)
            {
                Slider(builder, tags, "Thickness",
                    "Thickness of every face of the block (purple on the block). 0 mm = open. Turn on \"Per face\" to set faces on their own.",
                    0f, 500f, 1f, faces[0], " mm", v =>
                    {
                        for (int i = 0; i < ArmorFaces.Count; i++) tags.Armor.Faces[i] = v;
                    });
                return;
            }

            for (int row = 0; row < 2; row++)
            {
                List<KeyValuePair<string, Action>> buttons = new List<KeyValuePair<string, Action>>();
                for (int i = row * 3; i < row * 3 + 3; i++)
                {
                    int face = i;
                    string text = ArmorFaces.Names[face] + " " + Mm(faces[face]);
                    buttons.Add(new KeyValuePair<string, Action>(text, () => Pick(face)));
                }
                builder.Buttons(buttons, picked - row * 3);
            }

            Slider(builder, tags, ArmorFaces.Names[picked] + " face",
                "Thickness of the picked face (purple on the block). 0 mm = open: shells and fragments pass it as empty space.",
                0f, 500f, 1f, faces[picked], " mm", v => tags.Armor.Faces[picked] = v, true);
        }

        // Armor weight, then one row per face: the steel of the armored faces (area on the block x thickness).
        private static string[] ArmorWeightTexts(bool holeRow)
        {
            string[] texts = new string[ArmorFaces.Count + 1 + (holeRow ? 1 : 0)];
            float kg;
            float[] faceKg;
            float[] faceArea;
            if (part == null || !ArmorMass.TryDescribe(part.Part, out kg, out faceKg, out faceArea))
            {
                texts[0] = "measuring...";
                for (int i = 1; i < texts.Length; i++) texts[i] = "-";
                return texts;
            }

            texts[0] = Kg(kg);
            for (int i = 0; i < ArmorFaces.Count; i++)
                texts[i + 1] = faceKg[i] > 0f ? Kg(faceKg[i]) : "open";
            if (holeRow)
            {
                float wallKg = ArmorMass.HoleWallKg(part.Part);
                texts[ArmorFaces.Count + 1] = wallKg > 0f ? Kg(wallKg) : "open";
            }
            return texts;
        }

        private static string Kg(float kg)
        {
            return kg >= 1000f
                ? (kg / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " t"
                : kg.ToString("0", CultureInfo.InvariantCulture) + " kg";
        }

        private static void Pick(int face)
        {
            SelectedFace = face;
            rebuildPending = true;
        }

        private static string Mm(float mm)
        {
            return mm.ToString("0", CultureInfo.InvariantCulture);
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
                                   Action<float> assign, bool rebuildOnRelease = false)
        {
            int positions = Mathf.RoundToInt((max - min) / step) + 1;
            string format = step >= 1f ? "0" : "0.0";

            builder.Slider(label, tooltip, min, max, positions, value, unit.Trim(),
                x => x.ToString(format, CultureInfo.InvariantCulture),
                x =>
                {
                    assign(Mathf.Clamp(Mathf.Round(x / step) * step, min, max));
                    Commit(tags, false);       // while dragging: update the name, no undo step yet
                    openEdit = tags;
                },
                x =>
                {
                    openEdit = null;
                    Commit(tags, true);        // on release: one undo step
                    if (rebuildOnRelease) rebuildPending = true;   // rows that show the value elsewhere
                });
        }

        // Writes the tags into the part's name, the same way the game's name field does.
        private static void Commit(PartTags tags, bool createUndoStep)
        {
            if (part == null) return;

            string name = TagCodec.Compose(tags, part.Part.PartType.Name);
            bool armorBefore = part.Part.Name != null && (part.Part.Name.Contains("[ARM") || part.Part.Name.Contains("[HOL"));
            bool cannonBefore = part.Part.Name != null && part.Part.Name.Contains("[CAN");
            part.Part.Name = name;
            foreach (PartModifierData modifier in part.Part.Modifiers)
                modifier.OnPartNameChanged(name);
            builtForName = name;

            // Parts linked to this one by symmetry get the same armor (mirrored ones with their sides swapped).
            bool armorRelated = armorBefore || tags.Armor != null || tags.Hole != null;
            int partners = armorRelated ? ArmorSymmetry.SyncPartners(part, tags) : 0;

            // Cannons linked by symmetry get the same cannon settings and the matching racks.
            bool cannonRelated = cannonBefore || tags.Cannon != null;
            if (cannonRelated) partners += ArmorSymmetry.SyncCannonPartners(part, tags);

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

            // Armor weighs: the craft's mass and center of mass change with it (as with the game's Dead Weight).
            if (createUndoStep && armorRelated)
                Assets.Scripts.Design.Designer.Instance.SetAircraftStructureChanged();

            if (createUndoStep)
            {
                // With symmetry partners changed too, the undo step covers the whole craft, so one undo reverts all.
                if (partners > 0)
                    Assets.Scripts.Design.Designer.Instance.CreateUndoStep("Armor tags", "armor-tags-" + part.Part.Id);
                else
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
