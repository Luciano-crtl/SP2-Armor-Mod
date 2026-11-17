using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Design;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // The weight of an armor block's plates: each armored face's area (measured on the block's own mesh, sorted
    // into faces by ArmorFaces.Classify, inner walls of a hollow block left out) times its thickness, in steel.
    // Whole-block armor (t=) weighs as every face at that thickness. The weight is added to the part's loaded mass
    // (the mass the game's physics, designer read-outs and wheels use) on top of the block's own.
    internal static class ArmorMass
    {
        public const float SteelKgPerM3 = 7850f;
        private const float GameUnitsPerKg = 0.01f;
        private const float RetryInterval = 0.5f;   // seconds between tries while the block's mesh is not ready

        private sealed class Entry
        {
            public string Name;          // the part name it was worked out for
            public bool Armor;           // that name has an armor tag
            public float ShapeKey;       // the block's own mass then (changes with its shape)
            public bool Ready;
            public float RetryAt;
            public float Kg;
            public float[] FaceKg = new float[ArmorFaces.Count];
            public float[] FaceAreaM2 = new float[ArmorFaces.Count];
            public Vector3[] FaceCenter = new Vector3[ArmorFaces.Count];   // area-weighted, part's local space
            public Vector3[] FaceNormal = new Vector3[ArmorFaces.Count];   // average outward, part's local space
            public Vector3 Center;       // the armor's center of mass, part's local space
            public float HoleWallKg;     // armored walls of holes carved into the block
        }

        private static readonly Dictionary<PartData, Entry> Cache = new Dictionary<PartData, Entry>();

        // Armor weight of the part in game mass units (kg x 0.01); 0 if it is not armor or not measured yet.
        public static float GameUnits(PartData part)
        {
            Entry entry = Get(part);
            return entry != null ? entry.Kg * GameUnitsPerKg : 0f;
        }

        // Total kg, and kg and m2 per face (null if the part is not armor or not measured yet).
        public static bool TryDescribe(PartData part, out float kg, out float[] faceKg, out float[] faceAreaM2)
        {
            Entry entry = Get(part);
            kg = 0f;
            faceKg = null;
            faceAreaM2 = null;
            if (entry == null || !entry.Ready) return false;
            kg = entry.Kg;
            faceKg = entry.FaceKg;
            faceAreaM2 = entry.FaceAreaM2;
            return true;
        }

        // Weight of the block's armored hole walls (kg); 0 if none or not measured yet.
        public static float HoleWallKg(PartData part)
        {
            Entry entry = Get(part);
            return entry != null && entry.Ready ? entry.HoleWallKg : 0f;
        }

        // Where the armor's weight sits, in the part's local space (as PartData.CenterOfMass), and how much it is
        // in game units. False if the part is not armor, all open, or not measured yet.
        public static bool TryCenter(PartData part, out Vector3 local, out float gameUnits)
        {
            Entry entry = Get(part);
            local = Vector3.zero;
            gameUnits = 0f;
            if (entry == null || !entry.Ready || entry.Kg <= 0f) return false;
            local = entry.Center;
            gameUnits = entry.Kg * GameUnitsPerKg;
            return true;
        }

        private static Entry Get(PartData part)
        {
            if (part == null) return null;
            string name = part.Name;

            Entry entry;
            bool known = Cache.TryGetValue(part, out entry);
            if (known && ReferenceEquals(entry.Name, name))
            {
                if (!entry.Armor) return null;
                // In flight a block's shape stays as built; in the designer it can be reshaped under its armor.
                // The designer object stays alive after a visit, so "a designer exists" is not "this part is being designed": a part of a
                // craft in flight never changes shape, and checking its shape on every read made every body's mass cost milliseconds a frame
                // (the game reads it for each body every frame) once the designer had been opened.
                bool designer = Designer.Instance != null && !InFlight(part);
                if (entry.Ready && (!designer || entry.ShapeKey == ShapeKey(part))) return entry;
                if (!entry.Ready && Time.unscaledTime < entry.RetryAt) return entry;
            }

            if (!known)
            {
                entry = new Entry();
                Cache[part] = entry;
            }

            // Not armor: remember that under this name, so the next look is one comparison.
            entry.Armor = !string.IsNullOrEmpty(name) && name.IndexOf("[ARM", System.StringComparison.Ordinal) >= 0;
            if (!entry.Armor)
            {
                entry.Name = name;
                entry.Kg = 0f;
                return null;
            }
            Measure(part, name, entry);
            return entry;
        }

        private static bool InFlight(PartData part)
        {
            PartScript script = part.PartScript;
            return script != null && script.Aircraft != null && script.Aircraft.LoadContext == Assets.Scripts.Craft.CraftLoadContext.Flight;
        }

        private static float ShapeKey(PartData part)
        {
            float key = 0f;
            foreach (PartModifierData modifier in part.Modifiers) key += modifier.Mass;

            // A hole carved into the block changes its mesh, not always its own mass.
            Mesh hull = null;
            if (part.PartScript != null)
            {
                Assets.Scripts.Craft.MeshGen.ProceduralPartMeshRenderer renderer = FaceHighlight.HullRenderer(part.PartScript);
                hull = renderer != null ? renderer.Mesh : null;
            }
            if (hull != null) key += hull.vertexCount * 1000f + hull.bounds.size.sqrMagnitude;

            // Its cutters moved, or their hole wall armor changed.
            if (part.PartScript != null && ArmorSurface.IsCarved(part.PartScript))
                key += ArmorSurface.CutterKey(part.PartScript).GetHashCode() * 1e-6f;
            return key;
        }

        private static void Measure(PartData part, string name, Entry entry)
        {
            entry.Name = name;
            entry.Kg = 0f;
            for (int i = 0; i < ArmorFaces.Count; i++)
            {
                entry.FaceKg[i] = 0f;
                entry.FaceAreaM2[i] = 0f;
            }

            ArmorTag armor = TagCodec.Parse(name).Armor;
            if (armor == null)
            {
                entry.Ready = true;
                entry.ShapeKey = float.NaN;
                return;
            }

            PartScript script = part.PartScript;
            if (script == null || !FaceAreas(script, entry))
            {
                entry.Ready = false;
                entry.RetryAt = Time.unscaledTime + RetryInterval;
                return;
            }

            // Each plate's weight sits at the middle of its face, half its thickness in from the surface.
            Vector3 weighted = Vector3.zero;
            for (int i = 0; i < ArmorFaces.Count; i++)
            {
                float mm = Mathf.Max(armor.PerFace ? armor.Faces[i] : armor.Thickness, 0f);
                entry.FaceKg[i] = entry.FaceAreaM2[i] * mm * 0.001f * SteelKgPerM3;
                entry.Kg += entry.FaceKg[i];
                weighted += entry.FaceKg[i] * (entry.FaceCenter[i] - entry.FaceNormal[i] * (mm * 0.0005f));
            }

            // Armored walls of holes carved into it ([HOL] on the cutter).
            Vector3 wallCenter;
            entry.HoleWallKg = ArmorSurface.WallKg(script, out wallCenter);
            entry.Kg += entry.HoleWallKg;
            weighted += entry.HoleWallKg * wallCenter;

            entry.Center = entry.Kg > 0f ? weighted / entry.Kg : Vector3.zero;
            entry.Ready = true;
            entry.ShapeKey = ShapeKey(part);

            if (Designer.Instance == null)
            {
                List<string> faces = new List<string>();
                for (int i = 0; i < ArmorFaces.Count; i++)
                    if (entry.FaceKg[i] > 0f)
                        faces.Add(ArmorFaces.Names[i] + " " + entry.FaceKg[i].ToString("F0") + " kg (" +
                                  entry.FaceAreaM2[i].ToString("F2") + " m2)");
                if (entry.HoleWallKg > 0f) faces.Add("hole walls " + entry.HoleWallKg.ToString("F0") + " kg");
                ArmorFragmentsPlugin.Log.LogInfo("Armor '" + name + "' weighs " + entry.Kg.ToString("F0") + " kg of steel" +
                    (faces.Count > 0
                        ? ": " + string.Join(", ", faces.ToArray()) + "; its weight sits at " + entry.Center.ToString("F2") +
                          " m in the part (right, up, forward)"
                        : " (every face open)"));
            }
        }

        // Outer surface area (m2) of each face of the block, its middle and its average outward direction (in the
        // part's local space), from the block's outer surface (ArmorSurface: its visible mesh without the walls of
        // carved holes, or its colliders).
        private static bool FaceAreas(PartScript part, Entry entry)
        {
            Vector3[] local = ArmorSurface.OuterLocal(part);
            if (local.Length == 0) return false;

            float[] into = entry.FaceAreaM2;
            Vector3[] centers = new Vector3[ArmorFaces.Count];
            Vector3[] normals = new Vector3[ArmorFaces.Count];
            ArmorFaces.Shape shape = ArmorFaces.ShapeOf(part);
            Transform frame = part.transform;
            bool any = false;
            for (int t = 0; t + 2 < local.Length; t += 3)
            {
                Vector3 a = frame.TransformPoint(local[t]);
                Vector3 b = frame.TransformPoint(local[t + 1]);
                Vector3 c = frame.TransformPoint(local[t + 2]);
                Vector3 cross = Vector3.Cross(b - a, c - a);
                float twice = cross.magnitude;
                if (twice < 1e-10f) continue;

                Vector3 middle = (a + b + c) / 3f;
                int face = ArmorFaces.Classify(shape, middle, cross / twice);
                if (face < 0) continue;   // an inner wall of a hollow block
                float area = 0.5f * twice;
                into[face] += area;
                centers[face] += middle * area;
                normals[face] += cross * 0.5f;   // area x outward direction
                any = true;
            }
            if (!any) return false;

            for (int i = 0; i < ArmorFaces.Count; i++)
            {
                if (into[i] <= 0f) continue;
                entry.FaceCenter[i] = frame.InverseTransformPoint(centers[i] / into[i]);
                entry.FaceNormal[i] = frame.InverseTransformDirection(normals[i] / into[i]);
            }
            return true;
        }

        // Parts that are gone (another craft loaded) are dropped now and then.
        private static float nextPrune;

        public static void Tick()
        {
            if (Time.unscaledTime < nextPrune) return;
            nextPrune = Time.unscaledTime + 10f;
            Prune();
            ArmorSurface.Prune();
        }

        private static void Prune()
        {
            if (Cache.Count == 0) return;
            List<PartData> gone = null;
            foreach (PartData part in Cache.Keys)
            {
                if (part.PartScript == null)
                {
                    if (gone == null) gone = new List<PartData>();
                    gone.Add(part);
                }
            }
            if (gone != null)
                foreach (PartData part in gone) Cache.Remove(part);
        }
    }

    // A part's center of mass (PartData.CenterOfMass, in its local space) moves toward where its armor's weight
    // sits, by the armor's share of the part's mass: the body's center of mass and the designer's marker follow it.
    // While the part is written into the craft file it reads as stored, so the shift is never saved.
    [HarmonyPatch(typeof(PartData), "CenterOfMass", MethodType.Getter)]
    internal static class ArmorCenterOfMassPatch
    {
        internal static int Saving;
        private static bool warned;

        private static void Postfix(PartData __instance, ref Vector3 __result)
        {
            if (Saving > 0) return;
            try
            {
                Vector3 armorCenter;
                float armorUnits;
                if (!ArmorMass.TryCenter(__instance, out armorCenter, out armorUnits)) return;
                float total = __instance.LoadedMass;   // the block's own mass plus the armor's
                if (!(total > 0f) || float.IsInfinity(total)) return;
                float own = Mathf.Max(total - armorUnits, 0f);
                Vector3 blended = (__result * own + armorCenter * armorUnits) / (own + armorUnits);
                if (float.IsFinite(blended.x) && float.IsFinite(blended.y) && float.IsFinite(blended.z))
                    __result = blended;
            }
            catch (System.Exception e)
            {
                if (!warned) ArmorFragmentsPlugin.Log.LogWarning("Armor center of mass left out: " + e);
                warned = true;
            }
        }
    }

    [HarmonyPatch(typeof(PartData), "GenerateXml")]
    internal static class ArmorCenterOfMassSavePatch
    {
        private static void Prefix()
        {
            ArmorCenterOfMassPatch.Saving++;
        }

        private static System.Exception Finalizer(System.Exception __exception)
        {
            ArmorCenterOfMassPatch.Saving--;
            return __exception;
        }
    }

    // Every reader of a part's mass goes through PartData.LoadedMass: the armor's weight is added there, so
    // nothing is written into the craft file.
    [HarmonyPatch(typeof(PartData), "LoadedMass", MethodType.Getter)]
    internal static class ArmorMassPatch
    {
        private static bool warned;

        private static void Postfix(PartData __instance, ref float __result)
        {
            try
            {
                __result += ArmorMass.GameUnits(__instance);
            }
            catch (System.Exception e)
            {
                if (!warned) ArmorFragmentsPlugin.Log.LogWarning("Armor weight left out: " + e);
                warned = true;
            }
        }
    }
}
