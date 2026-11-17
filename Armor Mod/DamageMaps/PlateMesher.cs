using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // REAL GEOMETRY FOR A DAMAGED PLATE (experimental). The plate's own mesh is rewritten, in place: the triangles of its two big faces
    // (the ones the damage maps cover) are cut into small ones WHERE THE DAMAGE IS, and every new vertex is pushed in or out along its
    // normal by the damage's height field. A hole is small triangles that are not there. Nothing about the plate's shape is assumed:
    // its outline, its taper or rounded corners and a face that is not quite flat are all kept, because the new triangles come from
    // the old ones.
    //
    // The cutting is longest-edge bisection (Rivara): a triangle that must be refined is split across its longest edge, and the
    // triangle on the other side of that edge is split with it (after being refined itself first if the edge is not its longest too),
    // so the two always share the new vertex and there are no cracks. Triangles away from the damage are never touched, so the cost
    // follows the size of the damage, not of the plate.
    //
    // The rest of the mesh (the sides, the ends) is left exactly as it was, and every vertex keeps what the game put on it (UV1 holds
    // the paint's material, the decal layer and the part's id). The plate keeps its own material, so its paint, its texture and its
    // decals stay, and a hole needs no other material. The normal map's UV0 is written as the maps do.
    //
    // Only PlateMeshSource.Read touches a Unity object; Build runs on the worker thread, from arrays.

    // A plate's original mesh as plain arrays, with the part-space copies the rewriting works in.
    internal sealed class PlateMeshSource
    {
        public Vector3[] Vertices;        // mesh space
        public Vector3[] Normals;
        public Vector2[] Uv0;             // as the maps left them (into the atlas)
        public Vector4[] Uv1;             // null if the mesh has none
        public int Uv1Dimension;          // 0 = none
        public Vector4[] Uv2;             // null if the mesh has none (the game's newer plate meshes have a third UV set)
        public int Uv2Dimension;          // 0 = none
        public int[] Triangles;
        public Vector3[] PartPositions;   // the same vertices and normals in the part's frame (where the plate's axes are)
        public Vector3[] PartNormals;
        public sbyte[] Face;              // per triangle: 0 = on the front face, 1 = on the back face, -1 = neither (a side, an end)
        public int FaceTriangles;
        public Matrix4x4 FromPart;        // part-local space to mesh space
        public string Why;                // null = the plate can be rewritten; else why not

        public bool Ok
        {
            get { return Why == null; }
        }

        // The vertex channels a rewritten mesh can carry (anything else would be lost).
        private static bool Supported(VertexAttribute attribute)
        {
            return attribute == VertexAttribute.Position || attribute == VertexAttribute.Normal || attribute == VertexAttribute.Tangent ||
                   attribute == VertexAttribute.TexCoord0 || attribute == VertexAttribute.TexCoord1 || attribute == VertexAttribute.TexCoord2;
        }

        public static PlateMeshSource Read(PlateInfo info)
        {
            PlateMeshSource s = new PlateMeshSource();
            try
            {
                Fill(s, info);
            }
            catch (Exception e)
            {
                s.Why = "it could not be read: " + e.Message;
            }
            return s;
        }

        private static void Fill(PlateMeshSource s, PlateInfo info)
        {
            Mesh mesh = info.Mesh;
            PlateGeometry g = info.Geometry;
            if (mesh == null || !mesh.isReadable) { s.Why = "its mesh is not readable"; return; }
            if (mesh.subMeshCount != 1) { s.Why = "its mesh has " + mesh.subMeshCount + " submeshes"; return; }

            foreach (VertexAttributeDescriptor attribute in mesh.GetVertexAttributes())
            {
                if (!Supported(attribute.attribute)) { s.Why = "its mesh has a " + attribute.attribute + " channel"; return; }
            }

            s.Vertices = mesh.vertices;
            s.Normals = mesh.normals;
            s.Triangles = mesh.GetTriangles(0);
            int count = s.Vertices.Length;
            if (s.Normals.Length != count || s.Triangles.Length < 3) { s.Why = "its mesh has no normals"; return; }

            List<Vector2> uv0 = new List<Vector2>(count);
            mesh.GetUVs(0, uv0);
            if (uv0.Count != count) { s.Why = "its mesh has no UV0"; return; }
            s.Uv0 = uv0.ToArray();

            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord1))
            {
                s.Uv1Dimension = mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord1);
                List<Vector4> uv1 = new List<Vector4>(count);
                mesh.GetUVs(1, uv1);
                if (uv1.Count == count) s.Uv1 = uv1.ToArray();
                else s.Uv1Dimension = 0;
            }
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord2))
            {
                s.Uv2Dimension = mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord2);
                List<Vector4> uv2 = new List<Vector4>(count);
                mesh.GetUVs(2, uv2);
                if (uv2.Count == count) s.Uv2 = uv2.ToArray();
                else s.Uv2Dimension = 0;
            }

            s.FromPart = info.ToPart.inverse;
            s.PartPositions = new Vector3[count];
            s.PartNormals = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                s.PartPositions[i] = info.ToPart.MultiplyPoint3x4(s.Vertices[i]);
                s.PartNormals[i] = info.ToPart.MultiplyVector(s.Normals[i]).normalized;
            }
            Classify(s, g);
        }

        // Which triangles are on the big faces of the plate whose geometry is 'g' (front +N, back -N), by their normals.
        private static void Classify(PlateMeshSource s, PlateGeometry g)
        {
            int count = s.PartNormals.Length;
            sbyte[] facing = new sbyte[count];
            for (int i = 0; i < count; i++)
            {
                float along = Vector3.Dot(s.PartNormals[i], g.N);
                facing[i] = (sbyte)(along > 0.8f ? 1 : along < -0.8f ? -1 : 0);
            }

            int triangles = s.Triangles.Length / 3;
            s.Face = new sbyte[triangles];
            s.FaceTriangles = 0;
            for (int t = 0; t < triangles; t++)
            {
                sbyte a = facing[s.Triangles[3 * t]], b = facing[s.Triangles[3 * t + 1]], c = facing[s.Triangles[3 * t + 2]];
                if (a == 1 && b == 1 && c == 1) { s.Face[t] = 0; s.FaceTriangles++; }
                else if (a == -1 && b == -1 && c == -1) { s.Face[t] = 1; s.FaceTriangles++; }
                else s.Face[t] = -1;
            }
            s.Why = s.FaceTriangles == 0 ? "it has no triangles on its big faces" : null;
        }

        // The mesh a first pass made (the plate's own faces cut and pushed), read as the source of a second pass for ANOTHER face pair of the same
        // block (a side): the flat vertices and normals of the first result are the second one's, so a block's faces are cut one pair after the
        // other and the pushes add up (they are on different triangles). 'g' is the second pair's geometry, 'toPart' the mesh-to-part matrix.
        public static PlateMeshSource Derive(PlateGeometry g, Matrix4x4 toPart, PlateMeshSource basis, MeshData first)
        {
            PlateMeshSource s = new PlateMeshSource();
            int count = first.BaseVertices.Length;
            s.Vertices = first.BaseVertices;
            s.Normals = first.BaseNormals;
            s.Triangles = first.Triangles;
            s.Uv0 = first.Uv0;
            s.Uv1 = first.Uv1; s.Uv1Dimension = basis.Uv1Dimension;
            s.Uv2 = first.Uv2; s.Uv2Dimension = basis.Uv2Dimension;
            s.FromPart = basis.FromPart;
            s.PartPositions = new Vector3[count];
            s.PartNormals = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                s.PartPositions[i] = toPart.MultiplyPoint3x4(first.BaseVertices[i]);
                s.PartNormals[i] = toPart.MultiplyVector(first.BaseNormals[i]).normalized;
            }
            Classify(s, g);
            return s;
        }
    }

    // A rewritten plate mesh, ready to be put into the game's mesh.
    internal sealed class MeshData
    {
        public PlateVisual Plate;
        public Vector3[] Vertices;       // mesh space, pushed by the damage
        public Vector3[] Normals;
        public Vector3[] BaseVertices;   // the same without the push (the plate as it was): blending between the two is the transition
        public Vector3[] BaseNormals;
        public int[] Moved;              // the vertices the push moves (the others are the same in both)
        public Vector2[] Uv0;
        public Vector4[] Uv1;            // null if the mesh has none
        public Vector4[] Uv2;            // null if the mesh has none
        public int[] Triangles;
        public float DeliverAt;          // Time.time from which it may be shown (the shell's ray has to arrive first)
        public double BuildMs;
        public int Before, After;        // triangles of the big faces before and after the cutting
        public float MaxHeight;          // the biggest push, meters (how much a view could see of it)
    }

    internal static class PlateMesher
    {
        // The plate rewritten with its big faces cut, around the damage, into triangles of about 'cellMeters' (fewer if the mesh would
        // pass 'maxVertices').
        public static MeshData Build(PlateCpu cpu, PlateGeometry g, PlateMeshSource s, VehicleMaps owner, float cellMeters, int maxVertices)
        {
            return Build(cpu, g, s, owner, cellMeters, 0f, maxVertices);
        }

        // The two passes' results as one: 'second' was built from 'first' (PlateMeshSource.Derive), so its vertices begin with first's. A vertex
        // the first pass pushed (and the second did not) keeps its push.
        public static MeshData Combine(MeshData first, MeshData second)
        {
            bool[] movedBySecond = new bool[second.Vertices.Length];
            foreach (int i in second.Moved) movedBySecond[i] = true;
            List<int> moved = new List<int>(second.Moved);
            foreach (int i in first.Moved)
            {
                if (movedBySecond[i]) continue;
                second.Vertices[i] = first.Vertices[i];
                second.Normals[i] = first.Normals[i];
                moved.Add(i);
            }
            second.Moved = moved.ToArray();
            second.Before += first.Before;   // the faces cut in the first pass count too (the second's source holds them as they were left)
            second.MaxHeight = Mathf.Max(second.MaxHeight, first.MaxHeight);
            second.BuildMs += first.BuildMs;
            return second;
        }

        // 'fineMeters' (0 = none): around holes and sharp features (cracks, rims) the cut goes down to this size instead.
        public static MeshData Build(PlateCpu cpu, PlateGeometry g, PlateMeshSource s, VehicleMaps owner, float cellMeters, float fineMeters, int maxVertices)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            Refiner r = new Refiner(cpu, g, s, Mathf.Max(cellMeters, 0.002f), fineMeters > 0.0005f ? Mathf.Max(fineMeters, 0.001f) : 0f, maxVertices);
            r.Run();
            return r.Finish(owner, watch);
        }

        // ---- The refinement ----------------------------------------------------------------------

        private sealed class Refiner
        {
            private readonly PlateCpu cpu;
            private readonly PlateGeometry g;
            private readonly PlateMeshSource s;
            private readonly float cell;
            private readonly float fineCell;         // the cell size around holes and sharp features (0 or >= cell: no finer zone)
            private readonly int maxVertices;
            private const int Block = 4;             // the fine zone is kept in blocks of this many pixels
            private int blocksX, blocksY;
            private readonly bool[][] fine = new bool[2][];
            private readonly bool[][] zone = new bool[2][];   // the blocks worth real geometry: a hole, or a dent deeper than ZoneDepth (and their surroundings)
            private const float ZoneDepth = 0.0015f;           // shallower relief (pits, scratches) is left to the normal and bump maps

            private readonly List<Vector3> pos;      // base positions and normals, part frame (the originals, then the new ones)
            private readonly List<Vector3> nrm;
            private readonly List<int> own;          // the original vertex a vertex takes its UV1 from
            private readonly List<int> weld;         // vertices at the same place on the same face share one (so seams are not cracks)
            private readonly List<sbyte> vface;      // the face a vertex belongs to, -1 if none
            private readonly List<int> tri = new List<int>();      // three per triangle (only the big faces' triangles are kept here)
            private readonly List<sbyte> tface = new List<sbyte>();
            private readonly List<bool> alive = new List<bool>();
            private readonly Dictionary<long, int[]> edges = new Dictionary<long, int[]>();
            private readonly Dictionary<long, int> midpoints = new Dictionary<long, int>();
            private readonly Queue<int> queue = new Queue<int>();
            private readonly bool[] hasRegion = new bool[2];
            private readonly int original;

            public Refiner(PlateCpu cpu, PlateGeometry g, PlateMeshSource s, float cell, float fineCell, int maxVertices)
            {
                this.cpu = cpu; this.g = g; this.s = s; this.cell = cell; this.maxVertices = maxVertices;
                this.fineCell = fineCell > 0f && fineCell < cell ? fineCell : 0f;
                int count = s.Vertices.Length;
                pos = new List<Vector3>(s.PartPositions);
                nrm = new List<Vector3>(s.PartNormals);
                own = new List<int>(count);
                weld = new List<int>(count);
                vface = new List<sbyte>(count);

                Dictionary<Vector4, int> seen = new Dictionary<Vector4, int>();
                for (int i = 0; i < count; i++)
                {
                    own.Add(i);
                    vface.Add(-1);
                    Vector3 p = s.PartPositions[i];
                    float side = Vector3.Dot(s.PartNormals[i], g.N) > 0f ? 1f : -1f;
                    Vector4 key = new Vector4(Mathf.Round(p.x * 10000f), Mathf.Round(p.y * 10000f), Mathf.Round(p.z * 10000f), side);
                    int rep;
                    if (!seen.TryGetValue(key, out rep)) { seen[key] = i; rep = i; }
                    weld.Add(rep);
                }

                for (int f = 0; f < 2; f++) FindRegion(f);

                int triangles = s.Triangles.Length / 3;
                for (int t = 0; t < triangles; t++)
                {
                    if (s.Face[t] < 0) continue;
                    original++;
                    AddTriangle(s.Triangles[3 * t], s.Triangles[3 * t + 1], s.Triangles[3 * t + 2], s.Face[t]);
                }
            }

            // Where a face is damaged: the plate coordinates of the box around everything that is not flat or not solid.
            private void FindRegion(int f)
            {
                PlateCpu.Face face = cpu.Faces[f];
                if (face == null) return;
                blocksX = (cpu.Width + Block - 1) / Block;
                blocksY = (cpu.Height + Block - 1) / Block;
                bool[] marks = new bool[blocksX * blocksY];
                int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
                bool any = false;
                for (int y = 0; y < cpu.Height; y++)
                {
                    for (int x = 0; x < cpu.Width; x++)
                    {
                        int at = y * cpu.Width + x;
                        bool hole = face.Mask[at] < 255;
                        if (!hole && Mathf.Abs(face.Height[at]) < ZoneDepth) continue;
                        marks[(y / Block) * blocksX + x / Block] = true;
                        any = true;
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
                }
                if (!any) return;   // only shallow relief: the maps show it, the mesh stays as it is

                // Grown by about two cells so the push has somewhere to fall off to.
                int grow = Mathf.Max(1, Mathf.CeilToInt(cell * 2f * cpu.PxU / Block));
                bool[] grown = new bool[marks.Length];
                for (int by = 0; by < blocksY; by++)
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        if (!marks[by * blocksX + bx]) continue;
                        for (int dy = -grow; dy <= grow; dy++)
                            for (int dx = -grow; dx <= grow; dx++)
                            {
                                int nx = bx + dx, ny = by + dy;
                                if (nx >= 0 && ny >= 0 && nx < blocksX && ny < blocksY) grown[ny * blocksX + nx] = true;
                            }
                    }
                zone[f] = grown;
                hasRegion[f] = true;
                if (fineCell > 0f) FindFine(f, x0, y0, x1, y1);
            }

            // Where the fine zone is: the blocks (of Block x Block pixels) that hold a hole or its soft edge, or a sharp feature (a crack or a
            // rim: the height differs from the mean of its 5 x 5 pixels by more than 0.4 mm; a smooth bowl does not), and the blocks next to
            // them. Only inside the damaged box.
            private void FindFine(int f, int x0, int y0, int x1, int y1)
            {
                PlateCpu.Face face = cpu.Faces[f];
                bool[] marks = new bool[blocksX * blocksY];
                int ex0 = Mathf.Max(0, x0 - 3), ey0 = Mathf.Max(0, y0 - 3), ex1 = Mathf.Min(cpu.Width - 1, x1 + 3), ey1 = Mathf.Min(cpu.Height - 1, y1 + 3);
                for (int y = ey0; y <= ey1; y++)
                {
                    for (int x = ex0; x <= ex1; x++)
                    {
                        int at = y * cpu.Width + x;
                        bool feature = face.Mask[at] != 255;
                        if (!feature)
                        {
                            float sum = 0f;
                            int taken = 0;
                            for (int dy = -2; dy <= 2; dy++)
                            {
                                int yy = Mathf.Clamp(y + dy, 0, cpu.Height - 1);
                                for (int dx = -2; dx <= 2; dx++)
                                {
                                    sum += face.Height[yy * cpu.Width + Mathf.Clamp(x + dx, 0, cpu.Width - 1)];
                                    taken++;
                                }
                            }
                            feature = Mathf.Abs(face.Height[at] - sum / taken) > 0.0004f;
                        }
                        if (feature) marks[(y / Block) * blocksX + x / Block] = true;
                    }
                }
                bool[] grown = (bool[])marks.Clone();
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        if (!marks[by * blocksX + bx]) continue;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = bx + dx, ny = by + dy;
                                if (nx >= 0 && ny >= 0 && nx < blocksX && ny < blocksY) grown[ny * blocksX + nx] = true;
                            }
                    }
                }
                for (int i = 0; i < grown.Length; i++) grown[i] &= zone[f][i];   // fine only where the geometry is
                fine[f] = grown;
            }

            // Does the triangle touch a block of the zone worth real geometry?
            private bool InZone(int f, float minU, float maxU, float minV, float maxV)
            {
                bool[] z = zone[f];
                if (z == null) return false;
                int bx0 = Mathf.Clamp((int)((PlateCpu.Border + minU * cpu.PxU) / Block), 0, blocksX - 1);
                int bx1 = Mathf.Clamp((int)((PlateCpu.Border + maxU * cpu.PxU) / Block), 0, blocksX - 1);
                int by0 = Mathf.Clamp((int)((PlateCpu.Border + minV * cpu.PxV) / Block), 0, blocksY - 1);
                int by1 = Mathf.Clamp((int)((PlateCpu.Border + maxV * cpu.PxV) / Block), 0, blocksY - 1);
                for (int by = by0; by <= by1; by++)
                    for (int bx = bx0; bx <= bx1; bx++)
                        if (z[by * blocksX + bx]) return true;
                return false;
            }

            // The cell size that applies to a triangle: the fine one if it touches the fine zone.
            private float CellOf(int t, float minU, float maxU, float minV, float maxV)
            {
                int f = tface[t];
                if (fineCell <= 0f || fine[f] == null) return cell;
                int bx0 = Mathf.Clamp((int)((PlateCpu.Border + minU * cpu.PxU) / Block), 0, blocksX - 1);
                int bx1 = Mathf.Clamp((int)((PlateCpu.Border + maxU * cpu.PxU) / Block), 0, blocksX - 1);
                int by0 = Mathf.Clamp((int)((PlateCpu.Border + minV * cpu.PxV) / Block), 0, blocksY - 1);
                int by1 = Mathf.Clamp((int)((PlateCpu.Border + maxV * cpu.PxV) / Block), 0, blocksY - 1);
                for (int by = by0; by <= by1; by++)
                    for (int bx = bx0; bx <= bx1; bx++)
                        if (fine[f][by * blocksX + bx]) return fineCell;
                return cell;
            }

            // ---- Triangles and edges ----

            private static long Key(int a, int b)
            {
                return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            }

            private long EdgeKey(int a, int b)
            {
                return Key(weld[a], weld[b]);
            }

            private int AddTriangle(int a, int b, int c, sbyte face)
            {
                int t = tface.Count;
                tri.Add(a); tri.Add(b); tri.Add(c);
                tface.Add(face);
                alive.Add(true);
                Link(EdgeKey(a, b), t); Link(EdgeKey(b, c), t); Link(EdgeKey(c, a), t);
                if (vface[a] < 0) vface[a] = face;
                if (vface[b] < 0) vface[b] = face;
                if (vface[c] < 0) vface[c] = face;
                queue.Enqueue(t);
                return t;
            }

            private void Kill(int t)
            {
                alive[t] = false;
                int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                Unlink(EdgeKey(a, b), t); Unlink(EdgeKey(b, c), t); Unlink(EdgeKey(c, a), t);
            }

            private void Link(long key, int t)
            {
                int[] slots;
                if (!edges.TryGetValue(key, out slots)) { slots = new[] { -1, -1 }; edges[key] = slots; }
                if (slots[0] < 0) slots[0] = t;
                else if (slots[1] < 0) slots[1] = t;
                // a third triangle on one edge (a mesh that is not a surface) is not linked: that edge is never refined across
            }

            private void Unlink(long key, int t)
            {
                int[] slots;
                if (!edges.TryGetValue(key, out slots)) return;
                if (slots[0] == t) slots[0] = -1;
                if (slots[1] == t) slots[1] = -1;
            }

            private int Other(long key, int t)
            {
                int[] slots;
                if (!edges.TryGetValue(key, out slots)) return -1;
                return slots[0] == t ? slots[1] : slots[1] == t ? slots[0] : -1;
            }

            // ---- Refining ----

            private float Length(int a, int b)
            {
                return (pos[a] - pos[b]).magnitude;
            }

            // The index (0..2) of the triangle's longest edge: edge k joins vertex k and k + 1. Ties go the same way for both triangles
            // of an edge (longer first, then the bigger key).
            private int Longest(int t)
            {
                int best = 0;
                float bestLength = -1f;
                long bestKey = -1;
                for (int k = 0; k < 3; k++)
                {
                    int a = tri[3 * t + k], b = tri[3 * t + (k + 1) % 3];
                    float length = Length(a, b);
                    long key = EdgeKey(a, b);
                    if (length > bestLength + 1e-9f || (Mathf.Abs(length - bestLength) <= 1e-9f && key > bestKey))
                    {
                        best = k; bestLength = length; bestKey = key;
                    }
                }
                return best;
            }

            // Does this triangle still have to be cut? It does if it is over the damage and its longest edge is longer than the cell.
            private bool Needs(int t)
            {
                int f = tface[t];
                if (!hasRegion[f]) return false;
                int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                float longest = Mathf.Max(Length(a, b), Mathf.Max(Length(b, c), Length(c, a)));
                if (longest <= (fineCell > 0f ? fineCell : cell)) return false;

                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 d = pos[tri[3 * t + k]] - g.Origin;
                    float u = Vector3.Dot(d, g.T1), v = Vector3.Dot(d, g.T2);
                    if (u < minU) minU = u; if (u > maxU) maxU = u;
                    if (v < minV) minV = v; if (v > maxV) maxV = v;
                }
                if (!InZone(f, minU, maxU, minV, maxV)) return false;
                return longest > CellOf(t, minU, maxU, minV, maxV);
            }

            public void Run()
            {
                while (queue.Count > 0 && pos.Count + 4 < maxVertices)
                {
                    int t = queue.Dequeue();
                    if (!alive[t] || !Needs(t)) continue;
                    Refine(t, 0);
                }
            }

            // Cuts the triangle across its longest edge, after making sure the neighbor across that edge is cut with it.
            private void Refine(int t, int depth)
            {
                while (alive[t])
                {
                    int k = Longest(t);
                    int p = tri[3 * t + k], q = tri[3 * t + (k + 1) % 3];
                    long key = EdgeKey(p, q);
                    int n = Other(key, t);
                    if (n < 0) { Bisect(t, k, -1); return; }

                    int kn = Longest(n);
                    if (EdgeKey(tri[3 * n + kn], tri[3 * n + (kn + 1) % 3]) == key) { Bisect(t, k, n); return; }
                    if (depth > 80) return;
                    Refine(n, depth + 1);
                }
            }

            private int Midpoint(int p, int q, sbyte face)
            {
                long key = EdgeKey(p, q);
                int m;
                if (midpoints.TryGetValue(key, out m)) return m;

                m = pos.Count;
                int lo = weld[p] < weld[q] ? p : q;
                pos.Add((pos[p] + pos[q]) * 0.5f);
                nrm.Add((nrm[p] + nrm[q]).normalized);
                own.Add(own[lo]);
                weld.Add(m);
                vface.Add(face);
                midpoints[key] = m;
                return m;
            }

            // Splits triangle t across its edge k (and its neighbor n across the same edge, if there is one) at the edge's middle.
            private void Bisect(int t, int k, int n)
            {
                sbyte face = tface[t];
                int p = tri[3 * t + k], q = tri[3 * t + (k + 1) % 3], r = tri[3 * t + (k + 2) % 3];
                int m = Midpoint(p, q, face);
                Kill(t);
                AddTriangle(p, m, r, face);
                AddTriangle(m, q, r, face);

                if (n < 0) return;
                for (int j = 0; j < 3; j++)
                {
                    int a = tri[3 * n + j], b = tri[3 * n + (j + 1) % 3], c = tri[3 * n + (j + 2) % 3];
                    if (weld[a] == weld[q] && weld[b] == weld[p])
                    {
                        Kill(n);
                        AddTriangle(a, m, c, tface[n]);
                        AddTriangle(m, b, c, tface[n]);
                        return;
                    }
                    if (weld[a] == weld[p] && weld[b] == weld[q])
                    {
                        Kill(n);
                        AddTriangle(a, m, c, tface[n]);
                        AddTriangle(m, b, c, tface[n]);
                        return;
                    }
                }
            }

            // ---- The result ----

            public MeshData Finish(VehicleMaps owner, System.Diagnostics.Stopwatch watch)
            {
                int count = pos.Count;
                float atlas = owner.AtlasSize;
                int innerW = cpu.Width - 2 * PlateCpu.Border, innerH = cpu.Height - 2 * PlateCpu.Border;

                Vector3[] baseV = new Vector3[count], baseN = new Vector3[count], pushedV = new Vector3[count], pushedN = new Vector3[count];
                Vector2[] uv0 = new Vector2[count];
                Vector4[] uv1 = s.Uv1 != null ? new Vector4[count] : null;
                Vector4[] uv2 = s.Uv2 != null ? new Vector4[count] : null;
                float[] plateU = new float[count], plateV = new float[count];
                List<int> moved = new List<int>();
                float maxHeight = 0f;

                for (int i = 0; i < count; i++)
                {
                    Vector3 p = pos[i], n = nrm[i];
                    Vector3 d = p - g.Origin;
                    float u = Vector3.Dot(d, g.T1), v = Vector3.Dot(d, g.T2);
                    plateU[i] = u; plateV[i] = v;

                    baseV[i] = s.FromPart.MultiplyPoint3x4(p);
                    baseN[i] = s.FromPart.MultiplyVector(n).normalized;
                    pushedV[i] = baseV[i];
                    pushedN[i] = baseN[i];
                    if (uv1 != null) uv1[i] = s.Uv1[own[i]];
                    if (uv2 != null) uv2[i] = s.Uv2[own[i]];

                    int f = vface[i];
                    if (f < 0)
                    {
                        uv0[i] = i < s.Uv0.Length ? s.Uv0[i] : Vector2.zero;   // not on a big face: as the game and the maps left it
                        continue;
                    }

                    uv0[i] = new Vector2((cpu.TileX[f] + PlateCpu.Border + Mathf.Clamp01(u / g.SizeU) * innerW) / atlas,
                                         (cpu.TileY[f] + PlateCpu.Border + Mathf.Clamp01(v / g.SizeV) * innerH) / atlas);

                    PlateCpu.Face damage = cpu.Faces[f];
                    if (damage == null) continue;
                    float height = Sample(cpu, damage, u, v);
                    float eu = 1f / cpu.PxU, ev = 1f / cpu.PxV;
                    float hu = (Sample(cpu, damage, u + eu, v) - Sample(cpu, damage, u - eu, v)) / (2f * eu);
                    float hv = (Sample(cpu, damage, u, v + ev) - Sample(cpu, damage, u, v - ev)) / (2f * ev);
                    if (Mathf.Abs(height) < 0.000001f && Mathf.Abs(hu) < 0.00001f && Mathf.Abs(hv) < 0.00001f) continue;

                    Vector3 pushedPart = p + n * height;
                    Vector3 pushedNormalPart = (n - g.T1 * hu - g.T2 * hv).normalized;
                    pushedV[i] = s.FromPart.MultiplyPoint3x4(pushedPart);
                    pushedN[i] = s.FromPart.MultiplyVector(pushedNormalPart).normalized;
                    moved.Add(i);
                    if (Mathf.Abs(height) > maxHeight) maxHeight = Mathf.Abs(height);
                }

                // The triangles: the ones that are not on a big face as they were; the big faces' own, as cut, except over a hole.
                List<int> output = new List<int>(s.Triangles.Length + 6 * tface.Count);
                int whole = s.Triangles.Length / 3;
                for (int t = 0; t < whole; t++)
                {
                    if (s.Face[t] >= 0) continue;
                    output.Add(s.Triangles[3 * t]); output.Add(s.Triangles[3 * t + 1]); output.Add(s.Triangles[3 * t + 2]);
                }
                int after = 0;
                for (int t = 0; t < tface.Count; t++)
                {
                    if (!alive[t]) continue;
                    int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                    after++;
                    if (Hole(cpu, cpu.Faces[tface[t]], plateU[a] + plateU[b] + plateU[c], plateV[a] + plateV[b] + plateV[c])) continue;
                    output.Add(a); output.Add(b); output.Add(c);
                }

                return new MeshData
                {
                    Vertices = pushedV, Normals = pushedN, BaseVertices = baseV, BaseNormals = baseN, Moved = moved.ToArray(),
                    Uv0 = uv0, Uv1 = uv1, Uv2 = uv2, Triangles = output.ToArray(),
                    BuildMs = watch.Elapsed.TotalMilliseconds, Before = original, After = after, MaxHeight = maxHeight,
                };
            }
        }

        // The height of a face at (u, v) meters of the plate, smoothly between the pixels.
        private static float Sample(PlateCpu c, PlateCpu.Face f, float u, float v)
        {
            float x = Mathf.Clamp(PlateCpu.Border + u * c.PxU - 0.5f, 0f, c.Width - 1.001f);   // (pixel x is centered at Border + u Px - 0.5)
            float y = Mathf.Clamp(PlateCpu.Border + v * c.PxV - 0.5f, 0f, c.Height - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            float a = f.Height[y0 * c.Width + x0], b = f.Height[y0 * c.Width + x0 + 1];
            float d = f.Height[(y0 + 1) * c.Width + x0], e = f.Height[(y0 + 1) * c.Width + x0 + 1];
            return (a * (1f - fx) + b * fx) * (1f - fy) + (d * (1f - fx) + e * fx) * fy;
        }

        // Is a small triangle over a hole? Its middle decides ('sumU' and 'sumV' are the three corners' plate coordinates added). The
        // mask is read smoothly between the pixels: the simulation writes a hole's edge as a distance (128 = on the edge), so the edge falls
        // between the pixels, not on them.
        private static bool Hole(PlateCpu c, PlateCpu.Face f, float sumU, float sumV)
        {
            if (f == null) return false;
            float u = sumU / 3f, v = sumV / 3f;
            float x = Mathf.Clamp(PlateCpu.Border + u * c.PxU - 0.5f, 0f, c.Width - 1.001f);
            float y = Mathf.Clamp(PlateCpu.Border + v * c.PxV - 0.5f, 0f, c.Height - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            float a = f.Mask[y0 * c.Width + x0], b = f.Mask[y0 * c.Width + x0 + 1];
            float d = f.Mask[(y0 + 1) * c.Width + x0], e = f.Mask[(y0 + 1) * c.Width + x0 + 1];
            return (a * (1f - fx) + b * fx) * (1f - fy) + (d * (1f - fx) + e * fx) * fy < 128f;
        }
    }
}
