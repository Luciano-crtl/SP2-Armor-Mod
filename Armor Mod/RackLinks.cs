using System;
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Design;
using Assets.Scripts.Design.Symmetry;
using Assets.Scripts.Design.Tools;
using HarmonyLib;

namespace ArmorFragments
{
    // A cannon names its ammo racks by part id ([CAN r=]). The game gives parts new ids when they are added (a
    // subassembly or a paste), duplicated, or made symmetric, and tells the parts that point at other parts
    // through IPartIDChangedListener (wheels and their engines, targeting parts). A part's name doesn't get that,
    // so these patches do the same for [CAN r=]: ids in the game's old -> new map are rewritten, the others are
    // kept, as the game does for its own links. A deleted part is taken out of every cannon's list, so a part
    // that later gets its id isn't picked up.
    internal static class RackLinks
    {
        // Rewrites the rack ids in this part's [CAN r=] through 'idMap' (old id -> new id).
        public static void Remap(PartData part, Dictionary<int, int> idMap)
        {
            if (part == null || idMap == null || idMap.Count == 0) return;

            // The sight camera ([PTR c=]) and the fixed cannons ([PTR x=], [PTR k=]) of a turret are part ids too.
            PointerTag pointer = TagCodec.Parse(part.Name).Pointer;
            if (pointer != null)
            {
                int? camera = pointer.Camera;
                bool linksChanged = false;
                int mapped;
                if (camera.HasValue && idMap.TryGetValue(camera.Value, out mapped) && mapped != camera.Value) { camera = mapped; linksChanged = true; }
                List<int> fixedIds = MapIds(pointer.Fixed, idMap, ref linksChanged);
                List<int> byKey = MapIds(pointer.FixedByKey, idMap, ref linksChanged);
                List<int> rotators = MapIds(pointer.Rotators, idMap, ref linksChanged);
                if (linksChanged) SetName(part, TagCodec.WithPointerLinks(part.Name, camera, fixedIds, byKey, rotators));

                // The traverse and elevation rotators ([PTR tr= el=]).
                int? traverse = pointer.Traverse, elevation = pointer.Elevation;
                bool axesChanged = false;
                if (traverse.HasValue && idMap.TryGetValue(traverse.Value, out mapped) && mapped != traverse.Value) { traverse = mapped; axesChanged = true; }
                if (elevation.HasValue && idMap.TryGetValue(elevation.Value, out mapped) && mapped != elevation.Value) { elevation = mapped; axesChanged = true; }
                if (axesChanged) SetName(part, TagCodec.WithPointerAxes(part.Name, traverse, elevation));
            }

            CannonTag cannon = TagCodec.Parse(part.Name).Cannon;
            if (cannon == null || cannon.Racks.Count == 0) return;

            List<int> ids = new List<int>();
            bool changed = false;
            foreach (int id in cannon.Racks)
            {
                int newId;
                int kept = idMap.TryGetValue(id, out newId) ? newId : id;
                if (kept != id) changed = true;
                if (!ids.Contains(kept)) ids.Add(kept);
            }
            if (changed) SetName(part, TagCodec.WithCannonRacks(part.Name, ids));
        }

        private static List<int> MapIds(List<int> ids, Dictionary<int, int> idMap, ref bool changed)
        {
            List<int> result = new List<int>();
            foreach (int id in ids)
            {
                int newId;
                int kept = idMap.TryGetValue(id, out newId) ? newId : id;
                if (kept != id) changed = true;
                if (!result.Contains(kept)) result.Add(kept);
            }
            return result;
        }

        // Takes 'removedId' out of the rack list of every cannon among 'parts'.
        public static void Forget(IEnumerable<PartData> parts, int removedId)
        {
            foreach (PartData part in parts)
            {
                if (part == null) continue;
                PointerTag pointer = TagCodec.Parse(part.Name).Pointer;
                if (pointer != null && (pointer.Camera == removedId || pointer.Fixed.Contains(removedId) || pointer.FixedByKey.Contains(removedId) || pointer.Rotators.Contains(removedId)))
                {
                    List<int> fixedIds = new List<int>(pointer.Fixed);
                    fixedIds.Remove(removedId);
                    List<int> byKey = new List<int>(pointer.FixedByKey);
                    byKey.Remove(removedId);
                    List<int> rotators = new List<int>(pointer.Rotators);
                    rotators.Remove(removedId);
                    SetName(part, TagCodec.WithPointerLinks(part.Name, pointer.Camera == removedId ? null : pointer.Camera, fixedIds, byKey, rotators));
                }
                if (pointer != null && (pointer.Traverse == removedId || pointer.Elevation == removedId))
                    SetName(part, TagCodec.WithPointerAxes(part.Name, pointer.Traverse == removedId ? null : pointer.Traverse,
                                                           pointer.Elevation == removedId ? null : pointer.Elevation));

                CannonTag cannon = TagCodec.Parse(part.Name).Cannon;
                if (cannon == null || !cannon.Racks.Contains(removedId)) continue;

                List<int> ids = new List<int>(cannon.Racks);
                ids.Remove(removedId);
                SetName(part, TagCodec.WithCannonRacks(part.Name, ids));
            }
        }

        // Sets a part's name the way the game's name field does.
        public static void SetName(PartData part, string name)
        {
            if (part.Name == name) return;
            part.Name = name;
            foreach (PartModifierData modifier in part.Modifiers)
            {
                try
                {
                    modifier.OnPartNameChanged(name);
                }
                catch (Exception)
                {
                    // a modifier that isn't set up yet: it reads the name when it is
                }
            }
        }
    }

    // Parts added to the craft (a subassembly, a paste): the game renumbers them all.
    [HarmonyPatch(typeof(Assembly), "Absorb")]
    internal static class RackLinksAbsorbPatch
    {
        // The absorbed parts with their ids before the game renumbers them.
        private static void Prefix(Assembly assembly, out List<KeyValuePair<PartData, int>> __state)
        {
            __state = null;
            try
            {
                if (assembly == null) return;
                __state = new List<KeyValuePair<PartData, int>>();
                foreach (PartData part in assembly.Parts)
                    if (part != null) __state.Add(new KeyValuePair<PartData, int>(part, part.Id));
            }
            catch (Exception e)
            {
                __state = null;
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (added parts) failed: " + e.Message);
            }
        }

        private static void Postfix(List<KeyValuePair<PartData, int>> __state)
        {
            if (__state == null || __state.Count == 0) return;
            try
            {
                Dictionary<int, int> idMap = new Dictionary<int, int>();
                foreach (KeyValuePair<PartData, int> pair in __state) idMap[pair.Value] = pair.Key.Id;
                foreach (KeyValuePair<PartData, int> pair in __state) RackLinks.Remap(pair.Key, idMap);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (added parts) failed: " + e.Message);
            }
        }
    }

    // Duplicated parts (clone): copy i of the result is part i of the selection.
    [HarmonyPatch(typeof(SymmetryUtility), "DuplicateParts")]
    internal static class RackLinksDuplicatePatch
    {
        private static void Prefix(PartSelection partSelection, out List<int> __state)
        {
            __state = null;
            try
            {
                if (partSelection == null || partSelection.Parts == null) return;
                __state = new List<int>();
                foreach (PartScript part in partSelection.Parts)
                    __state.Add(part != null && part.Part != null ? part.Part.Id : -1);
            }
            catch (Exception e)
            {
                __state = null;
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (duplicated parts) failed: " + e.Message);
            }
        }

        private static void Postfix(List<PartData> __result, List<int> __state)
        {
            if (__result == null || __state == null || __state.Count != __result.Count) return;
            try
            {
                Dictionary<int, int> idMap = new Dictionary<int, int>();
                for (int i = 0; i < __state.Count; i++)
                    if (__state[i] >= 0 && __result[i] != null) idMap[__state[i]] = __result[i].Id;
                foreach (PartData copy in __result) RackLinks.Remap(copy, idMap);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (duplicated parts) failed: " + e.Message);
            }
        }
    }

    // Symmetric copies: copy i of a cannon points at copy i of its racks, if those are symmetric too.
    [HarmonyPatch(typeof(SymmetryUtility), "RemapSymmetricPartIDs")]
    internal static class RackLinksSymmetryPatch
    {
        private static void Postfix(IReadOnlyList<ValueTuple<PartData, IReadOnlyList<PartData>>> symmetricPartGroups)
        {
            if (symmetricPartGroups == null || symmetricPartGroups.Count == 0) return;
            try
            {
                int copies = 0;
                foreach (ValueTuple<PartData, IReadOnlyList<PartData>> group in symmetricPartGroups)
                    if (group.Item2 != null && group.Item2.Count > copies) copies = group.Item2.Count;

                for (int i = 0; i < copies; i++)
                {
                    Dictionary<int, int> idMap = new Dictionary<int, int>();
                    foreach (ValueTuple<PartData, IReadOnlyList<PartData>> group in symmetricPartGroups)
                        if (group.Item1 != null && group.Item2 != null && i < group.Item2.Count && group.Item2[i] != null)
                            idMap[group.Item1.Id] = group.Item2[i].Id;
                    if (idMap.Count == 0) continue;

                    foreach (ValueTuple<PartData, IReadOnlyList<PartData>> group in symmetricPartGroups)
                        if (group.Item2 != null && i < group.Item2.Count) RackLinks.Remap(group.Item2[i], idMap);
                }

                // Racks made symmetric earlier: each copy of a cannon takes the copy of the rack nearest to it.
                foreach (ValueTuple<PartData, IReadOnlyList<PartData>> group in symmetricPartGroups)
                    if (group.Item2 != null)
                        foreach (PartData copy in group.Item2) ArmorSymmetry.MatchRacks(copy);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (symmetry) failed: " + e.Message);
            }
        }
    }

    // A part deleted in the designer.
    [HarmonyPatch(typeof(Assembly), "RemovePart")]
    internal static class RackLinksRemovePatch
    {
        private static void Postfix(Assembly __instance, PartData part)
        {
            if (part == null) return;
            try
            {
                Designer designer = Designer.Instance;
                if (designer == null || designer.Aircraft == null || designer.Aircraft.Aircraft == null ||
                    designer.Aircraft.Aircraft.Assembly != __instance) return;
                RackLinks.Forget(__instance.Parts, part.Id);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (deleted part) failed: " + e.Message);
            }
        }
    }
}

namespace ArmorFragments
{
    // Rack matching runs when an ammo rack or a marked cannon is dropped onto the craft or connected in the
    // designer, and at no other time (not on load or undo): each such cannon, and every cannon using such a rack or
    // one of its symmetry copies, takes the copies nearest to it (ArmorSymmetry.RematchAround). Other parts:
    // nothing.

    // Drag and drop (dragged, cloned, pasted or added, with or without symmetry): the parts being placed, once
    // they are in their final place.
    [HarmonyPatch(typeof(MovePartTool), "EndPartMovement")]
    internal static class RackLinksDropPatch
    {
        private static readonly System.Reflection.FieldInfo SelectionField =
            AccessTools.Field(typeof(MovePartTool), "_partSelection");
        private static readonly System.Reflection.FieldInfo SymmetricField =
            AccessTools.Field(typeof(MovePartTool), "_symmetricPartSelections");

        // The parts being placed, before the tool lets go of them.
        private static void Prefix(MovePartTool __instance, out List<PartData> __state)
        {
            __state = new List<PartData>();
            try
            {
                Add(SelectionField != null ? SelectionField.GetValue(__instance) as PartSelection : null, __state);
                System.Collections.IEnumerable symmetric =
                    SymmetricField != null ? SymmetricField.GetValue(__instance) as System.Collections.IEnumerable : null;
                if (symmetric != null)
                    foreach (object selection in symmetric) Add(selection as PartSelection, __state);
            }
            catch (Exception e)
            {
                __state.Clear();
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (dropped parts) failed: " + e.Message);
            }
        }

        private static void Postfix(List<PartData> __state)
        {
            if (__state == null || __state.Count == 0) return;
            try
            {
                foreach (PartData part in __state) ArmorSymmetry.RematchAround(part);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (dropped parts) failed: " + e.Message);
            }
        }

        private static void Add(PartSelection selection, List<PartData> parts)
        {
            if (selection == null || selection.Parts == null) return;
            foreach (PartScript part in selection.Parts)
                if (part != null && part.Part != null && !parts.Contains(part.Part)) parts.Add(part.Part);
        }
    }

    // A connection made in the designer (the part connections tool, or a part snapping on): both ends.
    [HarmonyPatch(typeof(MovePartTool), "ConnectPartToAttachPoint")]
    internal static class RackLinksConnectPatch
    {
        private static void Postfix(AttachPointScript attachPointScript, AttachPointScript targetAttachPointScript)
        {
            try
            {
                if (Designer.Instance == null) return;
                PartScript a = attachPointScript != null ? attachPointScript.PartScript : null;
                PartScript b = targetAttachPointScript != null ? targetAttachPointScript.PartScript : null;
                if (a != null) ArmorSymmetry.RematchAround(a.Part);
                if (b != null && b != a) ArmorSymmetry.RematchAround(b.Part);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (connected parts) failed: " + e.Message);
            }
        }
    }
}
