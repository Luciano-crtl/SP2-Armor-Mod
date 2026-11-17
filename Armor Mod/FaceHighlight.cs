using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Assets.Scripts.Craft.MeshGen;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Design;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ArmorFragments
{
    // Designer only: on the selected block with per-face armor, the face being edited is tinted orange and the
    // other armored faces blue (open faces are left as they are). The tint is the block's own mesh, triangle by
    // triangle, sorted into faces by ArmorFaces.Classify: the same faces the shells and fragments meet.
    // The tint is drawn each frame with Graphics.DrawMesh at the block's frame; it is not an object in the scene,
    // so nothing the game does to the block's renderers (paint, outlines, picking) reaches it or is changed by it.
    internal static class FaceHighlight
    {
        private const string OverlayName = "ArmorFaceHighlight";
        private const float RefreshInterval = 0.2f;
        private const float Lift = 0.002f;   // meters off the surface, so the tint is not hidden in it

        private static Mesh overlayMesh;
        private static Transform overlayFrame;
        private static Transform overlayLayerSource;   // the object whose layer the block is drawn on
        private static string shownSignature;
        private static float nextRefresh;
        private static bool warned;
        private static Material pickedMaterial, armoredMaterial;
        private static bool materialsTried;

        public static void Tick()
        {
            try
            {
                if (Designer.Instance == null)
                {
                    Clear();
                    return;
                }
                if (Time.unscaledTime >= nextRefresh)
                {
                    nextRefresh = Time.unscaledTime + RefreshInterval;
                    Refresh();
                }
                Draw();
            }
            catch (Exception e)
            {
                if (!warned) ArmorFragmentsPlugin.Log.LogWarning("Armor face highlight failed: " + e);
                warned = true;
                Clear();
            }
        }

        private static void Refresh()
        {
            PartScript part = UI.DesignerSection.PerFacePart;
            if (part == null || !Materials())
            {
                Clear();
                return;
            }

            List<KeyValuePair<Mesh, Transform>> filters = Sources(part);
            string signature = Signature(part, filters);
            if (signature == shownSignature && overlayMesh != null) return;

            Clear();
            Build(part, filters);
            shownSignature = signature;
        }

        // Drawn at the frame's full matrix every frame, so the tint follows the block as it is moved or resized.
        private static void Draw()
        {
            if (overlayMesh == null) return;
            if (overlayFrame == null)
            {
                Clear();
                return;
            }
            // Only over the block as it is seen in the designer: not during a flight (the designer can stay loaded
            // behind it), and not while the block itself is hidden.
            if (Assets.Scripts.Flight.FlightSceneScript.Instance != null) return;
            if (overlayLayerSource != null)
            {
                Renderer shown = overlayLayerSource.GetComponent<Renderer>();
                if (!overlayLayerSource.gameObject.activeInHierarchy || (shown != null && !shown.enabled)) return;
            }
            Matrix4x4 matrix = overlayFrame.localToWorldMatrix;
            // The layer the block is drawn on right now (the game moves a part being dragged, such as a fresh
            // duplicate, to another layer and back), so the tint is always drawn where the block is.
            int layer = overlayLayerSource != null ? overlayLayerSource.gameObject.layer : overlayFrame.gameObject.layer;
            Material[] materials = { pickedMaterial, armoredMaterial };
            for (int sub = 0; sub < materials.Length; sub++)
            {
                if (overlayMesh.GetIndexCount(sub) == 0) continue;
                Graphics.DrawMesh(overlayMesh, matrix, materials[sub], layer, null, sub, null,
                    ShadowCastingMode.Off, false);
            }
        }

        // The game's fuselage renderer (JFuselageScript._renderer, a ProceduralPartMeshRenderer) holds the hull mesh.
        private static readonly FieldInfo FuselageRendererField =
            AccessTools.Field(typeof(JFuselageScript), "_renderer");

        // The block's own surface. A fuselage: the hull mesh the game generates for it, or else its colliders (in
        // the designer the visible shape too); never decals, text or anything else attached to it. Other parts:
        // their visible meshes (not another part's), or else their colliders.
        internal static List<KeyValuePair<Mesh, Transform>> Sources(PartScript part)
        {
            List<KeyValuePair<Mesh, Transform>> sources = new List<KeyValuePair<Mesh, Transform>>();

            JFuselageScript fuselage = FuselageOf(part);
            if (fuselage != null)
            {
                ProceduralPartMeshRenderer renderer = HullRenderer(part);
                Mesh hull = renderer != null ? renderer.Mesh : null;
                if (hull != null && hull.isReadable && hull.vertexCount > 0 && renderer.Transform != null)
                {
                    sources.Add(new KeyValuePair<Mesh, Transform>(hull, renderer.Transform));
                    return sources;
                }
                foreach (MeshCollider collider in fuselage.GetComponentsInChildren<MeshCollider>())
                {
                    if (collider == null || collider.sharedMesh == null || !collider.sharedMesh.isReadable) continue;
                    if (collider.GetComponentInParent<PartScript>() != part) continue;
                    sources.Add(new KeyValuePair<Mesh, Transform>(collider.sharedMesh, collider.transform));
                }
                return sources;
            }

            foreach (MeshFilter filter in part.GetComponentsInChildren<MeshFilter>())
            {
                if (filter == null || filter.sharedMesh == null) continue;
                if (filter.GetComponentInParent<PartScript>() != part) continue;
                if (filter.GetComponent<MeshRenderer>() == null || !filter.sharedMesh.isReadable) continue;
                sources.Add(new KeyValuePair<Mesh, Transform>(filter.sharedMesh, filter.transform));
            }
            if (sources.Count > 0) return sources;

            foreach (MeshCollider collider in part.GetComponentsInChildren<MeshCollider>())
            {
                if (collider == null || collider.sharedMesh == null || !collider.sharedMesh.isReadable) continue;
                if (collider.GetComponentInParent<PartScript>() != part) continue;
                sources.Add(new KeyValuePair<Mesh, Transform>(collider.sharedMesh, collider.transform));
            }
            return sources;
        }

        // The game's renderer of a fuselage block's hull (null for other parts).
        internal static ProceduralPartMeshRenderer HullRenderer(PartScript part)
        {
            JFuselageScript fuselage = FuselageOf(part);
            if (fuselage == null || FuselageRendererField == null) return null;
            return FuselageRendererField.GetValue(fuselage) as ProceduralPartMeshRenderer;
        }

        internal static JFuselageScript FuselageOf(PartScript part)
        {
            if (part == null || part.Part == null) return null;
            foreach (PartModifierData modifier in part.Part.Modifiers)
            {
                JFuselageData data = modifier as JFuselageData;
                if (data != null && data.JFuselageScript != null) return data.JFuselageScript;
            }
            return null;
        }

        // Changes when the block's name (its faces), the face being edited, or its shape changes.
        private static string Signature(PartScript part, List<KeyValuePair<Mesh, Transform>> filters)
        {
            StringBuilder text = new StringBuilder();
            text.Append(part.GetInstanceID()).Append('|').Append(part.Part.Name).Append('|').Append(UI.DesignerSection.SelectedFace);
            foreach (KeyValuePair<Mesh, Transform> filter in filters)
            {
                Mesh mesh = filter.Key;
                text.Append('|').Append(mesh.GetInstanceID()).Append(':').Append(mesh.vertexCount).Append(':').Append(mesh.bounds.ToString("F4"));
            }
            text.Append('|').Append(ArmorSurface.CutterKey(part));   // its cutters' hole wall armor
            return text.ToString();
        }

        private static void Build(PartScript part, List<KeyValuePair<Mesh, Transform>> filters)
        {
            ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;
            if (armor == null || !armor.PerFace) return;

            ArmorFaces.Shape shape = ArmorFaces.ShapeOf(part);
            Transform frame = shape.Frame;
            int picked = UI.DesignerSection.SelectedFace;   // AllFaces: every armored face is the one edited
            bool all = picked == UI.DesignerSection.AllFaces;

            List<Vector3> vertices = new List<Vector3>();
            List<int> pickedTriangles = new List<int>();
            List<int> armoredTriangles = new List<int>();

            // The block's outer surface (the walls of carved holes left out), in its local space.
            Vector3[] local = ArmorSurface.OuterLocal(part);
            Transform partFrame = part.transform;
            for (int t = 0; t + 2 < local.Length; t += 3)
            {
                Vector3 a = partFrame.TransformPoint(local[t]);
                Vector3 b = partFrame.TransformPoint(local[t + 1]);
                Vector3 c = partFrame.TransformPoint(local[t + 2]);
                Vector3 normal = Vector3.Cross(b - a, c - a);
                if (normal.sqrMagnitude < 1e-14f) continue;
                normal.Normalize();

                int face = ArmorFaces.Classify(shape, (a + b + c) / 3f, normal);
                if (face < 0) continue;   // an inner wall of a hollow block
                List<int> target;
                if (all) target = armor.Faces[face] > 0f ? pickedTriangles : null;
                else if (face == picked) target = pickedTriangles;
                else target = armor.Faces[face] > 0f ? armoredTriangles : null;
                if (target == null) continue;

                Vector3 lift = normal * Lift;
                target.Add(vertices.Count);
                vertices.Add(frame.InverseTransformPoint(a + lift));
                target.Add(vertices.Count);
                vertices.Add(frame.InverseTransformPoint(b + lift));
                target.Add(vertices.Count);
                vertices.Add(frame.InverseTransformPoint(c + lift));
            }

            // Armored walls of holes carved into it ([HOL] on the cutter): blue, or orange with every face edited.
            float[] wallMm;
            Vector3[] walls = ArmorSurface.WallsLocal(part, out wallMm);
            List<int> wallTarget = all ? pickedTriangles : armoredTriangles;
            for (int t = 0; t + 2 < walls.Length; t += 3)
            {
                Vector3 a = partFrame.TransformPoint(walls[t]);
                Vector3 b = partFrame.TransformPoint(walls[t + 1]);
                Vector3 c = partFrame.TransformPoint(walls[t + 2]);
                Vector3 normal = Vector3.Cross(b - a, c - a);
                if (normal.sqrMagnitude < 1e-14f) continue;
                Vector3 lift = normal.normalized * Lift;
                wallTarget.Add(vertices.Count);
                vertices.Add(frame.InverseTransformPoint(a + lift));
                wallTarget.Add(vertices.Count);
                vertices.Add(frame.InverseTransformPoint(b + lift));
                wallTarget.Add(vertices.Count);
                vertices.Add(frame.InverseTransformPoint(c + lift));
            }
            if (vertices.Count == 0) return;

            overlayMesh = new Mesh { name = OverlayName };
            if (vertices.Count > 65000) overlayMesh.indexFormat = IndexFormat.UInt32;
            overlayMesh.SetVertices(vertices);
            overlayMesh.subMeshCount = 2;
            overlayMesh.SetTriangles(pickedTriangles, 0);
            overlayMesh.SetTriangles(armoredTriangles, 1);
            overlayMesh.RecalculateBounds();
            overlayFrame = frame;
            overlayLayerSource = filters.Count > 0 && filters[0].Value != null ? filters[0].Value : part.transform;
        }

        // The plugin is being unloaded: the tint's mesh and materials are freed (nothing of it is in the scene).
        internal static void Shutdown()
        {
            Clear();
            if (pickedMaterial != null) Object.Destroy(pickedMaterial);
            if (armoredMaterial != null) Object.Destroy(armoredMaterial);
            pickedMaterial = armoredMaterial = null;
            materialsTried = false;
        }

        private static void Clear()
        {
            if (overlayMesh != null) Object.Destroy(overlayMesh);
            overlayMesh = null;
            overlayFrame = null;
            overlayLayerSource = null;
            shownSignature = null;
        }

        private static bool Materials()
        {
            if (materialsTried) return pickedMaterial != null;
            materialsTried = true;

            Shader shader = RayDrawer.GetShader();
            if (shader == null) return false;
            pickedMaterial = MakeMaterial(shader, new Color(1f, 0.55f, 0.1f, 0.55f));
            armoredMaterial = MakeMaterial(shader, new Color(0.15f, 0.45f, 1f, 0.55f));
            return true;
        }

        private static Material MakeMaterial(Shader shader, Color color)
        {
            Material material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            material.renderQueue = 3000;
            return material;
        }
    }
}
