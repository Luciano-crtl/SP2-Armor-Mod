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
    // Designer only: on the selected block with per-face armor, the face being edited is tinted purple and the
    // other armored faces blue (open faces are left as they are). The tint is the block's own mesh, triangle by
    // triangle, sorted into faces by ArmorFaces.Classify: the same faces the shells and fragments meet.
    // The tint is drawn each frame with Graphics.DrawMesh at the block's frame; it is not an object in the scene,
    // so nothing the game does to the block's renderers (paint, outlines, picking) reaches it or is changed by it.
    internal static class FaceHighlight
    {
        private const string OverlayName = "ArmorFaceHighlight";
        private const float RefreshInterval = 0.2f;
        private const float Lift = 0.002f;   // meters off the surface, so the tint is not hidden in it
        // The inside copy of a face goes a little deeper: where two plates meet back to back, the inside of one and
        // the outside of the other would otherwise lie on the same spot, and the one on the camera's side wins.
        private const float InsideLift = 0.003f;

        private static Mesh overlayMesh;
        private static Transform overlayFrame;
        private static Transform overlayLayerSource;   // the object whose layer the block is drawn on
        private static string shownSignature;
        private static float nextRefresh;
        private static bool warned;
        private static PartScript overlayPart;          // the block the tint was built for
        private static Material pickedMaterial, armoredMaterial;
        private static Material pickedSolidMaterial;   // the face being set over the armor view's colors (purple)
        private static float nextMaterialTry;

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
            // With the armor view on, every armored part is colored by thickness, this one too: only the face being set
            // is tinted over it (purple), and with "Per face" off (every face at once) nothing is.
            PartScript part = ArmorView.On && UI.DesignerSection.SelectedFace == UI.DesignerSection.AllFaces
                ? null : UI.DesignerSection.PerFacePart;
            if (part == null)
            {
                Clear();
                reported = null;   // nothing to show: not a failure
                return;
            }
            if (!Materials())
            {
                Clear();
                Report("its materials could not be made (no see-through shader)");
                return;
            }

            List<KeyValuePair<Mesh, Transform>> filters = Sources(part);
            string signature = Signature(part, filters, UI.DesignerSection.SelectedFace);
            if (signature == shownSignature && overlayMesh != null && overlayPart == part) return;

            Clear();
            Build(part, filters);
            shownSignature = signature;
            if (overlayMesh == null) Report("the block has no armored face to tint");
        }

        // Writes to the log why the tint of the selected per-face block is not drawn, once each time the reason
        // changes (null: it is drawn).
        private static string reported;
        private static void Report(string reason)
        {
            if (reason == reported) return;
            if (reason != null)
                ArmorFragmentsPlugin.Log.LogInfo("[TINT] Face tint not drawn: " + reason);
            else if (reported != null)
                ArmorFragmentsPlugin.Log.LogInfo("[TINT] Face tint drawn again");
            reported = reason;
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
            // Whether the block is hidden is the game's own flag for the part (PartMaterialScript.Visible), not its
            // hull renderer: the game also switches that renderer off when it draws the part as part of a combined
            // mesh, or while it rebuilds the mesh, and the block is still on screen then.
            if (Assets.Scripts.Flight.FlightSceneScript.Instance != null)
            {
                Report("a flight is loaded");
                return;
            }
            if (overlayPart == null || !overlayPart.gameObject.activeInHierarchy)
            {
                Report("the block is not in the scene");
                return;
            }
            if (overlayPart.PartMaterialScript != null && !overlayPart.PartMaterialScript.Visible)
            {
                Report("the block is hidden in the designer");
                return;
            }
            if (pickedMaterial == null || armoredMaterial == null || pickedSolidMaterial == null)
            {
                Clear();
                return;
            }
            Report(null);
            Matrix4x4 matrix = overlayFrame.localToWorldMatrix;
            // The layer the block is drawn on right now (the game moves a part being dragged, such as a fresh
            // duplicate, to another layer and back), so the tint is always drawn where the block is.
            int layer = overlayLayerSource != null ? overlayLayerSource.gameObject.layer : overlayFrame.gameObject.layer;
            Material picked = ArmorView.On ? pickedSolidMaterial : pickedMaterial;
            Material[] materials = { picked, armoredMaterial, picked, armoredMaterial };   // outside, then inside
            bool xray = XRay;
            for (int sub = 0; sub < materials.Length && sub < overlayMesh.subMeshCount; sub++)
            {
                if (overlayMesh.GetIndexCount(sub) == 0) continue;
                if (sub % 2 == 1 && ArmorView.On) continue;   // the other faces show their thickness color instead
                // Insides in the X-ray view only with the armor view on (its solid colors, nearest surface only, cover
                // what is behind them); otherwise they would color over the see-through parts within the craft.
                if (sub >= InsideSubmesh && xray && !ArmorView.On) continue;
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
                if (IsDecal(filter)) continue;
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

        // A decal or text laid on a part: the game puts its object (a decal projector, or a text mesh) under the part it
        // is on, so it is found among that part's children. It is not the part's surface.
        internal static bool IsDecal(Component component)
        {
            if (component == null) return false;
            if (component.GetComponent<TMPro.TMP_Text>() != null) return true;
            return component.GetComponentInParent<Assets.Scripts.Craft.Decals.PartMeshDecalObject>() != null;
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

        // Changes when the part's name (its armor), the face picked (`picked`), or its shape changes.
        internal static string Signature(PartScript part, List<KeyValuePair<Mesh, Transform>> filters, int picked)
        {
            StringBuilder text = new StringBuilder();
            text.Append(part.GetInstanceID()).Append('|').Append(part.Part.Name).Append('|').Append(picked);
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
            if (!ArmorFaces.IsPerFace(part)) return;
            Transform frame;
            overlayMesh = BuildMesh(part, filters, UI.DesignerSection.SelectedFace, out frame);
            if (overlayMesh == null) return;
            overlayFrame = frame;
            overlayLayerSource = filters.Count > 0 && filters[0].Value != null ? filters[0].Value : part.transform;
            overlayPart = part;
        }

        // The tint of an armored part, in the space of `frame`. Submeshes: 0 the faces picked (purple), 1 the other
        // armored faces (blue); 2 and 3 the same faces seen from inside the block. picked = AllFaces: every armored
        // face is picked (1 and 3 are empty). A plate is met from both sides, so it is tinted on both: a solid
        // block's face gets a copy facing in, just under its surface; a hollow block's inner wall is the inside of
        // the face it backs. The inside (2, 3) is left out of the game's X-ray view, where nothing in front hides it.
        // A block armored per face: its armored faces and the armored walls of holes carved into it; open faces
        // are left out. A part armored as a whole: all of its surface. Null when nothing of it is armored.
        // mm (if given) gets the thickness of the armor under each vertex, in the order of the mesh's vertices.
        internal const int InsideSubmesh = 2;

        internal static Mesh BuildMesh(PartScript part, List<KeyValuePair<Mesh, Transform>> filters, int picked,
                                       out Transform frame, List<float> mm = null)
        {
            frame = null;
            if (part == null || part.Part == null) return null;
            ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;
            if (armor == null) return null;

            List<Vector3> vertices = new List<Vector3>();
            List<int>[] sets = { new List<int>(), new List<int>(), new List<int>(), new List<int>() };
            bool all = picked == UI.DesignerSection.AllFaces;

            if (!armor.PerFace)
            {
                if (armor.Thickness <= 0f) return null;
                frame = part.transform;
                foreach (KeyValuePair<Mesh, Transform> source in filters)
                {
                    Mesh mesh = source.Key;
                    Transform from = source.Value;
                    if (mesh == null || from == null || !mesh.isReadable) continue;
                    Vector3[] points = mesh.vertices;
                    int[] triangles = mesh.triangles;
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                        AddTriangle(vertices, sets[0], sets[2], mm, armor.Thickness, frame, from.TransformPoint(points[triangles[t]]),
                            from.TransformPoint(points[triangles[t + 1]]), from.TransformPoint(points[triangles[t + 2]]));
                }
            }
            else
            {
                ArmorFaces.Shape shape = ArmorFaces.ShapeOf(part);
                frame = shape.Frame;

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
                    bool inner = face < 0;   // an inner wall of a hollow block: the inside of the face it backs
                    if (inner)
                    {
                        if (!shape.Hollow) continue;
                        face = ArmorFaces.Classify(shape, (a + b + c) / 3f, -normal);
                        if (face < 0) continue;
                    }
                    int set;
                    if (all) set = armor.Faces[face] > 0f ? 0 : -1;
                    else if (face == picked) set = 0;
                    else set = armor.Faces[face] > 0f ? 1 : -1;
                    if (set < 0) continue;
                    if (inner) AddTriangle(vertices, sets[set + 2], null, mm, armor.Faces[face], frame, a, b, c);
                    else AddTriangle(vertices, sets[set], shape.Hollow ? null : sets[set + 2], mm, armor.Faces[face], frame, a, b, c);
                }

                // Armored walls of holes carved into it ([HOL] on the cutter): blue, or purple with every face picked.
                float[] wallMm;
                Vector3[] walls = ArmorSurface.WallsLocal(part, out wallMm);
                int wallSet = all ? 0 : 1;
                for (int t = 0; t + 2 < walls.Length; t += 3)
                    AddTriangle(vertices, sets[wallSet], sets[wallSet + 2], mm, t / 3 < wallMm.Length ? wallMm[t / 3] : 0f, frame,
                        partFrame.TransformPoint(walls[t]), partFrame.TransformPoint(walls[t + 1]), partFrame.TransformPoint(walls[t + 2]));
            }
            if (vertices.Count == 0 || frame == null) return null;

            Mesh overlay = new Mesh { name = OverlayName };
            if (vertices.Count > 65000) overlay.indexFormat = IndexFormat.UInt32;
            overlay.SetVertices(vertices);
            overlay.subMeshCount = sets.Length;
            for (int s = 0; s < sets.Length; s++) overlay.SetTriangles(sets[s], s);
            overlay.RecalculateBounds();
            return overlay;
        }

        // One triangle (world points), lifted off the surface along its normal, into `frame`'s space. inside (if
        // given): also a copy facing the other way, just under the surface, seen from the other side (tints draw
        // one side only).
        private static void AddTriangle(List<Vector3> vertices, List<int> target, List<int> inside, List<float> mm,
                                        float thickness, Transform frame, Vector3 a, Vector3 b, Vector3 c,
                                        float offset = Lift)
        {
            if (inside != null) AddTriangle(vertices, inside, null, mm, thickness, frame, a, c, b, InsideLift);
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 1e-14f) return;
            if (mm != null) { mm.Add(thickness); mm.Add(thickness); mm.Add(thickness); }
            Vector3 lift = normal.normalized * offset;
            target.Add(vertices.Count);
            vertices.Add(frame.InverseTransformPoint(a + lift));
            target.Add(vertices.Count);
            vertices.Add(frame.InverseTransformPoint(b + lift));
            target.Add(vertices.Count);
            vertices.Add(frame.InverseTransformPoint(c + lift));
        }

        // Is the game's X-ray view on in the designer?
        internal static bool XRay
        {
            get
            {
                Designer designer = Designer.Instance;
                return designer != null && designer.ViewMode == DesignerViewMode.Ghost;
            }
        }

        // Is the edit tint drawn over this part right now (the armor view then leaves it to it)?
        internal static bool Showing(PartScript part)
        {
            return part != null && overlayPart == part && overlayMesh != null;
        }

        // The colors: the face being edited purple, other armored faces blue. Drawn after the
        // game's see-through parts (its X-ray view), so the tint stays on them.
        internal static readonly Color PickedColor = new Color(0.40f, 0.10f, 0.70f, 0.75f);
        internal static readonly Color ArmoredColor = new Color(0.15f, 0.45f, 1f, 0.55f);
        // The face being set over the armor view: the same purple (not on its thickness scale), solid like the view.
        internal static readonly Color PickedOverViewColor = new Color(0.40f, 0.10f, 0.70f, 1f);
        internal const int RenderQueue = 3500;

        // Drawn on the part's surface only where it is seen: the depth of what is in front is tested and the back of
        // each triangle is left out (Unity's built-in Hidden/Internal-Colored, which takes those as settings and
        // colors by the material and the vertices); without it, the rays' see-through shader.
        internal static Material MakeTintMaterial(Color color)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                if (!shaderWarned) ArmorFragmentsPlugin.Log.LogWarning("[TINT] Hidden/Internal-Colored not found: tints use the see-through shader");
                shaderWarned = true;
                Shader fallback = RayDrawer.GetShader();
                return fallback != null ? MakeMaterial(fallback, color) : null;
            }
            Material material = new Material(shader);
            material.SetColor("_Color", color);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_Cull", (int)CullMode.Back);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            material.renderQueue = RenderQueue;
            return material;
        }
        private static bool shaderWarned;

        // Writes only depth, no color: drawn just before the tints, a part with it hides the tints behind it.
        internal static Material MakeDepthMaterial(int renderQueue = RenderQueue - 100)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;
            Material material = new Material(shader);
            material.SetColor("_Color", Color.white);
            material.SetInt("_SrcBlend", (int)BlendMode.Zero);
            material.SetInt("_DstBlend", (int)BlendMode.One);
            material.SetInt("_Cull", (int)CullMode.Back);
            material.SetInt("_ZWrite", 1);
            material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            material.renderQueue = renderQueue;
            return material;
        }

        // The plugin is being unloaded: the tint's mesh and materials are freed (nothing of it is in the scene).
        internal static void Shutdown()
        {
            Clear();
            if (pickedMaterial != null) Object.Destroy(pickedMaterial);
            if (pickedSolidMaterial != null) Object.Destroy(pickedSolidMaterial);
            pickedSolidMaterial = null;
            if (armoredMaterial != null) Object.Destroy(armoredMaterial);
            pickedMaterial = armoredMaterial = null;
            nextMaterialTry = 0f;
        }

        private static void Clear()
        {
            if (overlayMesh != null) Object.Destroy(overlayMesh);
            overlayMesh = null;
            overlayFrame = null;
            overlayLayerSource = null;
            overlayPart = null;
            shownSignature = null;
        }

        // Made once; made again if they were lost (Unity can free them when scenes change), with a short wait
        // between tries so a missing shader is not looked for every frame.
        private static bool Materials()
        {
            if (pickedMaterial != null && armoredMaterial != null && pickedSolidMaterial != null) return true;
            if (Time.unscaledTime < nextMaterialTry) return false;
            nextMaterialTry = Time.unscaledTime + 2f;
            if (pickedMaterial != null) Object.Destroy(pickedMaterial);
            if (pickedSolidMaterial != null) Object.Destroy(pickedSolidMaterial);
            if (armoredMaterial != null) Object.Destroy(armoredMaterial);
            pickedMaterial = armoredMaterial = pickedSolidMaterial = null;

            pickedMaterial = MakeTintMaterial(PickedColor);
            pickedSolidMaterial = MakeTintMaterial(PickedOverViewColor);
            armoredMaterial = MakeTintMaterial(ArmoredColor);
            if (pickedMaterial == null || pickedSolidMaterial == null || armoredMaterial == null) return false;
            pickedSolidMaterial.renderQueue = RenderQueue + 1;   // over the armor view's colors
            return true;
        }

        private static Material MakeMaterial(Shader shader, Color color)
        {
            Material material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            material.renderQueue = RenderQueue;
            return material;
        }
    }
}
