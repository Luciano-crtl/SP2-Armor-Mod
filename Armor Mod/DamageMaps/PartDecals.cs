using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using UnityEngine;

namespace ArmorFragments
{
    // Marks on the parts that are not armor (wings, fuselage with no tag, engines...). Those parts are merged into their group's mesh when a
    // flight starts, so their own mesh cannot be marked the way an armor plate's is. Instead each hit leaves a decal: a small mesh laid over the
    // surface (it follows the part's shape, built from rays cast at it) with a texture made by the same stamps as the plates' marks (a hole with a
    // torn rim, soot, bare metal, the dent's shading as a normal map). It rides on the part, so it moves with it and goes when the part does.
    //   entry   a hole where the shell went in (and the dent and soot round it)
    //   exit    a bigger, torn hole where it came out, if it went through
    //   burst   the scorch of a high-explosive shell that went off on the part
    // Only for what is seen from outside (a mark with another part of the vehicle right over it is not made), and each vehicle keeps the newest
    // few (the rest are taken off). Local only: other players' marks on ordinary parts are not sent.
    internal static class PartDecals
    {
        private sealed class Decal
        {
            public GameObject Object;
            public Mesh Mesh;
            public Material Material;
            public Texture2D Color, Normal;
            public AircraftScript Aircraft;
        }

        private static readonly List<Decal> All = new List<Decal>();
        private static bool failed;
        private static int counter;
        private const int Size = 128;          // pixels of a decal's texture, per side
        private const int Cells = 10;          // the mesh is Cells x Cells squares
        private const int MaxPerCall = 8;
        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        // ---- Asking for marks -------------------------------------------------------------------------

        // The ordinary parts a real shell's ray went through: each gets a hole where the shell went in and, if it came out, where it came out.
        public static void FromCrossings(BallisticRay.Result ray, Vector3 from, Vector3 direction, float caliberMm)
        {
            if (failed || !ArmorFragmentsPlugin.PartDecalsEnabled.Value || ray.Crossings == null || ray.Crossings.Count == 0) return;
            try
            {
                float caliber = Mathf.Max(caliberMm * 0.001f, 0.01f);
                int made = 0;
                foreach (BallisticRay.Crossed crossing in ray.Crossings)
                {
                    if (made >= MaxPerCall) break;
                    PartScript part = crossing.Part;
                    if (!Markable(part)) continue;

                    float strength = Mathf.Clamp01(crossing.Dissipated / Mathf.Max(crossing.EnergyIn, 1f));
                    float chord = Mathf.Max(crossing.Exit - crossing.Distance, 0.005f);
                    bool through = crossing.EnergyOut > 0f;

                    Vector3 entry = from + direction * Mathf.Max(crossing.Distance, 0f);
                    Vector3 normal;
                    if (!SurfaceNormal(part, entry - direction * 0.1f, direction, 0.35f, out normal)) normal = -direction;
                    if (Exposed(part, entry, normal) && Place(part, DamageKind.Penetrated, 0, entry, direction, normal, caliber, strength, chord, false)) made++;

                    if (through)
                    {
                        Vector3 exit = from + direction * crossing.Exit;
                        Vector3 outward;
                        if (!SurfaceNormal(part, exit + direction * 0.1f, -direction, 0.35f, out outward)) outward = direction;
                        if (Exposed(part, exit, outward) && Place(part, DamageKind.Penetrated, 1, exit, direction, outward, caliber, strength, chord, true)) made++;
                    }
                }
            }
            catch (System.Exception e)
            {
                failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[DECAL] marks on ordinary parts switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // A high-explosive shell went off on an ordinary part.
        public static void Burst(PartScript part, Vector3 point, Vector3 direction, Vector3 normal, float caliberMm, float strength)
        {
            if (failed || !ArmorFragmentsPlugin.PartDecalsEnabled.Value || !Markable(part)) return;
            try
            {
                Vector3 outward = Vector3.Dot(normal, direction) > 0f ? -normal : normal;
                if (Exposed(part, point, outward))
                    Place(part, DamageKind.Burst, 0, point, direction, outward, Mathf.Max(caliberMm * 0.001f, 0.01f), Mathf.Clamp01(strength), 0.02f, false);
            }
            catch (System.Exception e)
            {
                failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[DECAL] marks on ordinary parts switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool Markable(PartScript part)
        {
            if (part == null || part.Aircraft == null || part.Aircraft.LoadContext != CraftLoadContext.Flight) return false;
            if (CrewBodies.IsBody(part)) return false;
            return part.GetComponentInChildren<Renderer>() != null;
        }

        // ---- Finding the surface ----------------------------------------------------------------------

        // The nearest surface of the part's own colliders along a ray, and its normal.
        private static bool SurfaceHit(Collider[] colliders, Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit best)
        {
            best = default(RaycastHit);
            float nearest = float.MaxValue;
            bool found = false;
            Ray ray = new Ray(origin, direction);
            foreach (Collider c in colliders)
            {
                RaycastHit hit;
                if (c == null || c.isTrigger || !c.enabled || !c.Raycast(ray, out hit, maxDistance)) continue;
                if (hit.distance < nearest) { nearest = hit.distance; best = hit; found = true; }
            }
            return found;
        }

        private static bool SurfaceNormal(PartScript part, Vector3 origin, Vector3 direction, float maxDistance, out Vector3 normal)
        {
            RaycastHit hit;
            normal = Vector3.up;
            if (!SurfaceHit(part.GetComponentsInChildren<Collider>(), origin, direction, maxDistance, out hit)) return false;
            normal = hit.normal;
            return true;
        }

        // Is the spot seen from outside? Not if another part of the vehicle is right over it (inside the hull, under a wing...).
        private static bool Exposed(PartScript part, Vector3 point, Vector3 normal)
        {
            int count = Physics.RaycastNonAlloc(point + normal * 0.03f, normal, Hits, 1.2f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                PartScript other = Hits[i].collider.GetComponentInParent<PartScript>();
                if (other != null && other != part && other.Aircraft == part.Aircraft) return false;
            }
            return true;
        }

        // ---- Making a decal ---------------------------------------------------------------------------

        // 'face' 0 is the side the shell came from, 1 the side it came out of (the torn hole). 'normal' points out of the part there.
        private static bool Place(PartScript part, DamageKind kind, int face, Vector3 point, Vector3 direction, Vector3 normal,
                                  float caliber, float strength, float thickness, bool flipV)
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null || !lit.isSupported)
            {
                failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[DECAL] the URP Lit shader is not available: no marks on ordinary parts");
                return false;
            }

            normal = normal.normalized;
            Vector3 u = Vector3.ProjectOnPlane(direction, normal);
            if (u.sqrMagnitude < 0.0001f) u = Vector3.ProjectOnPlane(Vector3.up, normal);
            if (u.sqrMagnitude < 0.0001f) u = Vector3.ProjectOnPlane(Vector3.right, normal);
            u.Normalize();
            Vector3 v = Vector3.Cross(normal, u);   // u x v = normal: the mesh faces the way the surface does
            float angle = Mathf.Clamp(Vector3.Angle(-direction, normal), 0f, 85f);

            float window = Mathf.Clamp(caliber * 10f, 0.15f, 0.8f);

            // The texture: the same stamps as an armor plate's marks, on a small plate of its own (no flexing: that is for whole plates).
            Color color; float metallic, smoothness;
            VehicleMaps.ReadPaint(part.Aircraft.Theme, part, out color, out metallic, out smoothness);
            PlateCpu cpu = new PlateCpu
            {
                Width = Size, Height = Size,
                PxU = (Size - 2 * PlateCpu.Border) / window, PxV = (Size - 2 * PlateCpu.Border) / window,
                PaintMode = true, Paint = new Vector3(color.r, color.g, color.b), PaintMetallic = metallic, PaintSmooth = smoothness,
                Thickness = Mathf.Clamp(thickness, 0.005f, 0.1f),
            };
            PlateImpact impact = new PlateImpact
            {
                Kind = kind, Axis = 0, Front = true, U = window * 0.5f, V = window * 0.5f, DirU = 1f, DirV = 0f,
                Angle = face == 1 ? 0f : angle, Caliber = caliber, Strength = strength, Thickness = cpu.Thickness,
                Seed = unchecked(part.Part.Id * 7919 + (++counter) * 104729),
            };
            PlateStamps.Apply(cpu, impact, false);
            PlateCpu.Face f = cpu.Faces[face];
            if (f == null) return false;

            Texture2D colorMap, normalMap;
            MakeTextures(cpu, f, out colorMap, out normalMap);

            // The mesh, laid on the surface: each vertex is where a ray cast at the part from above meets it.
            Collider[] colliders = part.GetComponentsInChildren<Collider>();
            Vector3[] vertices = new Vector3[(Cells + 1) * (Cells + 1)];
            Vector3[] normals = new Vector3[vertices.Length];
            Vector4[] tangents = new Vector4[vertices.Length];
            Vector2[] uvs = new Vector2[vertices.Length];
            Vector3 center = point + normal * 0.003f;
            for (int j = 0; j <= Cells; j++)
            {
                for (int i = 0; i <= Cells; i++)
                {
                    float x = ((float)i / Cells - 0.5f) * window, y = ((float)j / Cells - 0.5f) * window;
                    Vector3 flat = point + u * x + v * y;
                    Vector3 at = flat;
                    Vector3 n = normal;
                    RaycastHit hit;
                    if (SurfaceHit(colliders, flat + normal * 0.15f, -normal, 0.3f, out hit)) { at = hit.point; n = hit.normal; }
                    int k = j * (Cells + 1) + i;
                    vertices[k] = at + n * 0.003f - center;   // a hair over the surface, so it is not lost in it
                    normals[k] = n;
                    tangents[k] = new Vector4(u.x, u.y, u.z, flipV ? -1f : 1f);
                    uvs[k] = new Vector2((float)i / Cells, flipV ? 1f - (float)j / Cells : (float)j / Cells);
                }
            }
            int[] triangles = new int[Cells * Cells * 6];
            int t = 0;
            for (int j = 0; j < Cells; j++)
            {
                for (int i = 0; i < Cells; i++)
                {
                    int a = j * (Cells + 1) + i, b = a + 1, c = a + Cells + 1, d = c + 1;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }
            }
            Mesh mesh = new Mesh { name = "ArmorFragments decal" };
            mesh.vertices = vertices; mesh.normals = normals; mesh.tangents = tangents; mesh.uv = uvs; mesh.triangles = triangles;
            mesh.RecalculateBounds();

            Material material = new Material(lit) { name = "ArmorFragments decal" };
            material.SetTexture("_BaseMap", colorMap);
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BumpMap", normalMap);
            material.SetFloat("_BumpScale", 1.5f);
            material.EnableKeyword("_NORMALMAP");
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.3f);
            material.SetFloat("_Surface", 1f);     // transparent, alpha blended, no depth writes (what the shader's own inspector sets)
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", 5f);
            material.SetFloat("_DstBlend", 10f);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", 2f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            GameObject go = new GameObject("ArmorFragments decal");
            go.layer = part.gameObject.layer;
            go.transform.position = center;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            go.transform.SetParent(part.transform, true);

            Decal decal = new Decal { Object = go, Mesh = mesh, Material = material, Color = colorMap, Normal = normalMap, Aircraft = part.Aircraft };
            All.Add(decal);
            Trim(part.Aircraft);
            return true;
        }

        // The texture's color and alpha (paint, scorch, bare metal, a dark hole) and its normal map, from the stamped heights.
        private static void MakeTextures(PlateCpu cpu, PlateCpu.Face f, out Texture2D colorMap, out Texture2D normalMap)
        {
            byte[] color = new byte[Size * Size * 4];
            byte[] normal = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int at = y * Size + x, o = at * 4;
                    byte r, g, b;
                    PlateStamps.PixelColor(cpu, f, at, out r, out g, out b);
                    float depth = Mathf.Abs(f.Height[at]);
                    float dent = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((depth - 0.0004f) / 0.0012f)) * 0.85f;
                    float alpha = Mathf.Max(Mathf.Max(f.Soot[at] / 255f * 0.95f, f.Bare[at] / 255f), dent);
                    if (f.Mask[at] < 128) { r = 12; g = 12; b = 14; alpha = 1f; }   // a hole: dark inside
                    color[o] = r; color[o + 1] = g; color[o + 2] = b; color[o + 3] = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);

                    float left = f.Height[y * Size + Mathf.Max(x - 1, 0)], right = f.Height[y * Size + Mathf.Min(x + 1, Size - 1)];
                    float down = f.Height[Mathf.Max(y - 1, 0) * Size + x], up = f.Height[Mathf.Min(y + 1, Size - 1) * Size + x];
                    float nx = -(right - left) * 0.5f * cpu.PxU, ny = -(up - down) * 0.5f * cpu.PxV;
                    float inverse = 1f / Mathf.Sqrt(nx * nx + ny * ny + 1f);
                    normal[o] = Encode(nx * inverse); normal[o + 1] = Encode(ny * inverse); normal[o + 2] = Encode(inverse); normal[o + 3] = 255;
                }
            }
            colorMap = new Texture2D(Size, Size, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments decal color" };
            colorMap.SetPixelData(color, 0);
            colorMap.Apply(true, true);
            normalMap = new Texture2D(Size, Size, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments decal normals" };
            normalMap.SetPixelData(normal, 0);
            normalMap.Apply(true, true);
        }

        private static byte Encode(float component)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt((component * 0.5f + 0.5f) * 255f), 0, 255);
        }

        // ---- Keeping them in check --------------------------------------------------------------------

        // Each vehicle keeps its newest decals; the rest (and those whose part is gone) are taken off.
        private static void Trim(AircraftScript aircraft)
        {
            for (int i = All.Count - 1; i >= 0; i--)
                if (All[i].Object == null) { Free(All[i]); All.RemoveAt(i); }

            int most = Mathf.Max(ArmorFragmentsPlugin.PartDecalsMax.Value, 4);
            int own = 0;
            foreach (Decal d in All) if (d.Aircraft == aircraft) own++;
            for (int i = 0; i < All.Count && own > most; i++)
            {
                if (All[i].Aircraft != aircraft) continue;
                Free(All[i]);
                All.RemoveAt(i);
                i--;
                own--;
            }
        }

        private static void Free(Decal d)
        {
            if (d.Object != null) Object.Destroy(d.Object);
            if (d.Mesh != null) Object.Destroy(d.Mesh);
            if (d.Material != null) Object.Destroy(d.Material);
            if (d.Color != null) Object.Destroy(d.Color);
            if (d.Normal != null) Object.Destroy(d.Normal);
        }

        // The plugin is unloading, or the panel's Clear: every decal goes.
        public static void Shutdown()
        {
            foreach (Decal d in All) Free(d);
            All.Clear();
        }
    }
}
