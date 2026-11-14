using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using UnityEngine;

namespace ArmorFragments
{
    // Where the rounds sit inside one ammo rack, and which of them are still there.
    //
    // The rack's frame: Z along the fuselage's length (the direction of JFuselageData.Offset, which places
    // its slices), Y the part's "up", X across; measured in the part's rotation only (no scale), in meters.
    // Rounds lie along Z. Across, a grid with a pitch of one round diameter plus spacing (centered); each
    // row of that grid is packed along the length on its own, with as many whole rounds as fit where the
    // row's circle is inside the shape.
    //
    // Rounds are solid, not liquid: a round counts only if the whole cylinder is inside the part's shape,
    // read from its drawn mesh (FillFromMesh; the colliders are only a fallback). A tapered, rounded or
    // shifted fuselage loses the rounds that would poke out, and a rack shorter than one round holds none.
    //
    // Rounds are taken from the top layer down, so the filled part shrinks from the top: a hit only counts
    // if its line crosses a cell that still holds a round (HitsRounds). The real colliders never change.
    internal sealed class RackGrid
    {
        private struct Round
        {
            public Vector3 Center;   // frame coordinates, middle of the round
            public int Slot, Column, Layer;
        }

        // A grid bigger than this is not counted (it would take too long): tiny rounds in a huge rack.
        private const int MaxCandidates = 60000;
        private const float RingTolerance = 0.97f;   // colliders are faceted: test the ring slightly inside

        public int Slots, Columns, Layers;   // grid size before the shape check
        public string Problem;               // why the rack holds nothing (null = it was measured)
        public string Status = "OK";         // the same, in a few words (designer)
        public float InsideLength, InsideWidth, InsideHeight;   // the part's extent in the rack's frame (m)

        // Grid cells the shape cuts off (tapers, rounded corners...).
        public int LostToShape
        {
            get { return Problem == null || rounds.Count == 0 ? Mathf.Max(0, Slots * Columns * Layers - rounds.Count) : 0; }
        }

        public int Capacity
        {
            get { return rounds.Count; }
        }

        public int Remaining
        {
            get { return rounds.Count - taken; }
        }

        private Vector3 axisX, axisY, axisZ;
        private float radius, pitch, length;
        private float caseLength, shellRadius;   // for drawing: the case, then the narrower shell in front
        private float firstX, firstY, firstZ;   // center of column 0 / layer 0; start of slot 0
        private Vector3 boundsMin, boundsMax;   // frame coordinates

        private readonly List<Round> rounds = new List<Round>();
        private readonly Dictionary<long, List<int>> rows = new Dictionary<long, List<int>>();   // (column, layer) -> rounds
        private bool[] present = new bool[0];
        private int[] removalOrder = new int[0];
        private int taken;

        // ---- Measuring ---------------------------------------------------------------

        public static RackGrid Measure(PartScript part, AmmoRound round)
        {
            RackGrid grid = new RackGrid();
            grid.Problem = grid.Fill(part, round);
            if (grid.Problem != null)
            {
                grid.rounds.Clear();
                grid.rows.Clear();
            }
            grid.present = new bool[grid.rounds.Count];
            for (int i = 0; i < grid.present.Length; i++) grid.present[i] = true;
            grid.BuildRemovalOrder();
            return grid;
        }

        // Returns null if measured, or the reason the rack can hold nothing.
        private string Fill(PartScript part, AmmoRound round)
        {
            if (part == null || part.Part == null) return Fail("Part is gone", "the part is gone");

            JFuselageData fuselage = null;
            foreach (PartModifierData modifier in part.Part.Modifiers)
            {
                fuselage = modifier as JFuselageData;
                if (fuselage != null) break;
            }
            if (fuselage == null) return Fail("Not a fuselage", "only fuselages can be ammo racks");
            if (fuselage.IsHollow) return Fail("Hollow: holds nothing", "hollow fuselages can't hold rounds");
            if (IsCut(fuselage)) return Fail("Cut: holds nothing", "cut fuselages can't hold rounds");
            if (round.Diameter <= 0f || round.Length <= 0f)
                return Fail("No cannon in group", "no cannon in this rack's group");

            // The frame, in the part's local axes.
            Vector3 offset = fuselage.Offset;
            axisZ = offset.sqrMagnitude > 1e-8f ? offset.normalized : Vector3.forward;
            axisX = Vector3.ProjectOnPlane(Vector3.right, axisZ);
            if (axisX.sqrMagnitude < 1e-6f) axisX = Vector3.ProjectOnPlane(Vector3.up, axisZ);
            axisX.Normalize();
            axisY = Vector3.Cross(axisZ, axisX).normalized;

            // The shape: the fuselage's own drawn mesh (exact for any shape). The colliders are only a
            // fallback: in the designer a fuselage's colliders can be a plain box around it, which made
            // every cell of the box look "inside" (seen on a wedge-shaped rack).
            List<Collider> colliders = null;
            bool useMesh = GatherMesh(part);
            if (useMesh)
            {
                MeshExtent();
                Method = "mesh";
            }
            else
            {
                colliders = Colliders(part);
                if (colliders.Count == 0 || !Extent(part, colliders)) return Fail("Can't measure shape", NoColliders);
                Method = "colliders";
            }

            radius = round.Diameter * 0.5f;
            length = round.Length;
            caseLength = round.CaseLength;
            shellRadius = round.CaliberMm / 2000f;
            pitch = round.Diameter * (1f + ArmorFragmentsPlugin.RoundSpacingPercent.Value / 100f);

            float width = boundsMax.x - boundsMin.x;
            float height = boundsMax.y - boundsMin.y;
            float span = boundsMax.z - boundsMin.z;
            InsideLength = span;
            InsideWidth = width;
            InsideHeight = height;

            Slots = Mathf.FloorToInt(span / length + 1e-4f);
            Columns = width >= round.Diameter ? Mathf.FloorToInt((width - round.Diameter) / pitch + 1e-4f) + 1 : 0;
            Layers = height >= round.Diameter ? Mathf.FloorToInt((height - round.Diameter) / pitch + 1e-4f) + 1 : 0;

            if (Slots == 0)
                return Fail("Too short", "too short: a round needs " + Cm(length) + ", the rack is " + Cm(span) + " long");
            if (Columns == 0 || Layers == 0)
                return Fail("Too narrow", "too narrow: a round needs " + Cm(round.Diameter) + ", the rack is " +
                            Cm(width) + " x " + Cm(height));
            if ((long)Slots * Columns * Layers > MaxCandidates)
                return Fail("Too many to count", "too many rounds to count (over " + MaxCandidates + ")");

            firstX = boundsMin.x + (width - ((Columns - 1) * pitch + round.Diameter)) * 0.5f + radius;
            firstY = boundsMin.y + (height - ((Layers - 1) * pitch + round.Diameter)) * 0.5f + radius;
            firstZ = boundsMin.z + (span - Slots * length) * 0.5f;

            if (useMesh)
            {
                FillFromMesh();
            }
            else
            {
                // Sample points along one round: both ends, and every ~15 cm between (the part may be made of
                // several convex colliders, and a round crossing from one to the next must be inside both).
                int alongSamples = Mathf.Max(2, Mathf.CeilToInt(length / 0.15f) + 1);
                Transform frame = part.transform;

                for (int slot = 0; slot < Slots; slot++)
                    for (int column = 0; column < Columns; column++)
                        for (int layer = 0; layer < Layers; layer++)
                        {
                            Vector3 center = new Vector3(firstX + column * pitch, firstY + layer * pitch,
                                                         firstZ + (slot + 0.5f) * length);
                            if (!RoundFits(frame, colliders, center, alongSamples)) continue;
                            AddRound(center, column, layer);
                        }
            }

            if (rounds.Count == 0)
                return Fail("Shape too tight", "the shape leaves no room for a whole round (" + Slots + " slots x " +
                            Columns + " x " + Layers + " grid)");
            Status = "OK";
            return null;
        }

        public const string NoColliders = "no mesh or colliders to measure the shape";

        public string Method = "-";   // "mesh" or "colliders": what the shape was measured from

        private void AddRound(Vector3 center, int column, int layer)
        {
            List<int> row;
            long key = RowKey(column, layer);
            if (!rows.TryGetValue(key, out row))
            {
                row = new List<int>();
                rows[key] = row;
            }
            row.Add(rounds.Count);
            rounds.Add(new Round { Center = center, Slot = row.Count - 1, Column = column, Layer = layer });
        }

        private static long RowKey(int column, int layer)
        {
            return (long)column * 65536L + layer;
        }

        // ---- Shape from the drawn mesh -------------------------------------------------------
        //
        // The fuselage's triangles, in the rack's frame. A round fits if, along its whole length, its circle
        // lies inside the outline of the mesh cut at that depth. The outline is read with horizontal
        // scanlines through the circle (5 per round: center, +-0.7 r, +-0.95 r): on each scanline the
        // circle's chord must sit inside one "inside" stretch of the outline.
        //
        // Inside = nonzero winding: each cut segment is oriented by its triangle's facing (outward normal on
        // its right), and a point is inside when the segments around it don't cancel out. All of the part's
        // meshes count together. The first version used the even-odd rule per mesh, which a mesh split in
        // pieces, or with doubled faces, breaks: on Jakniphe's wedge it emptied everything left of a straight
        // line. Nonzero winding doesn't care about splits, doubled faces (same facing) or mirrored parts.

        private struct Triangle
        {
            public Vector3 A, B, C;
            public Vector2 Facing;   // the outward normal's part across the cut (x, y)
        }

        private List<Triangle> triangles;
        private int meshCount;
        private readonly List<string> meshSources = new List<string>();   // for the shape dump

        private static readonly float[] ScanOffsets = { -0.95f, -0.7f, 0f, 0.7f, 0.95f };

        private bool GatherMesh(PartScript part)
        {
            triangles = new List<Triangle>();
            meshCount = 0;
            meshSources.Clear();
            HashSet<Mesh> seen = new HashSet<Mesh>();
            foreach (MeshRenderer renderer in XRayView.GhostSources(part))
            {
                if (renderer == null) continue;
                string name = renderer.name;
                if (name == "AmmoRackGhost" || name == "AmmoRackRounds" || name == "ArmorXRayGhost") continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !mesh.isReadable) continue;

                Vector3[] vertices = mesh.vertices;
                int[] indices = mesh.triangles;
                meshSources.Add(renderer.name + " (mesh '" + mesh.name + "', " + vertices.Length + " vertices, " +
                                indices.Length / 3 + " triangles, " + mesh.subMeshCount + " submeshes, enabled " +
                                renderer.enabled + (seen.Contains(mesh) ? ", SAME MESH AS ANOTHER: skipped" : "") + ")");
                if (!seen.Add(mesh)) continue;   // the same mesh drawn twice is one solid, not two
                if (indices.Length < 12) continue;

                Vector3[] inFrame = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                    inFrame[i] = ToFrame(part.transform, renderer.transform.TransformPoint(vertices[i]));
                for (int k = 0; k + 2 < indices.Length; k += 3)
                {
                    Vector3 a = inFrame[indices[k]], b = inFrame[indices[k + 1]], c = inFrame[indices[k + 2]];
                    Vector3 normal = Vector3.Cross(b - a, c - a);   // Unity: front faces clockwise -> outward
                    triangles.Add(new Triangle { A = a, B = b, C = c, Facing = new Vector2(normal.x, normal.y) });
                }
                meshCount++;
            }
            return triangles.Count > 0;
        }

        private void MeshExtent()
        {
            boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (Triangle t in triangles)
            {
                Include(t.A);
                Include(t.B);
                Include(t.C);
            }
        }

        // Each row of rounds (one column and layer of the cross grid) is packed on its own: the mesh is cut
        // every ~1 cm along the length, which gives, for every row, the stretches of length where a round's
        // circle fits; each stretch holds as many whole rounds as its length allows (centered in it). Rows
        // don't share lengthwise slots, so a slanted, stepped or pointed end still gets filled as far as
        // whole rounds go (first version used shared slots, which left blocks of a stepped rack empty).
        private const int MaxDepthCuts = 1500;

        private void FillFromMesh()
        {
            List<Vector4> outline = new List<Vector4>();
            List<Vector2>[] stretches = new List<Vector2>[ScanOffsets.Length];
            for (int k = 0; k < stretches.Length; k++) stretches[k] = new List<Vector2>();
            float[] halfChord = new float[ScanOffsets.Length];
            for (int k = 0; k < ScanOffsets.Length; k++)
                halfChord[k] = radius * Mathf.Sqrt(1f - ScanOffsets[k] * ScanOffsets[k]);

            const float EndInset = 0.002f;   // stay off the end caps, which lie in the cutting plane
            float zStart = boundsMin.z + EndInset;
            float zEnd = boundsMax.z - EndInset;
            int cuts = Mathf.Clamp(Mathf.CeilToInt((zEnd - zStart) / Mathf.Clamp(length / 10f, 0.005f, 0.02f)) + 1,
                                   2, MaxDepthCuts);
            float step = (zEnd - zStart) / (cuts - 1);

            bool[,] inRun = new bool[Columns, Layers];
            float[,] runFrom = new float[Columns, Layers];
            float[,] runTo = new float[Columns, Layers];

            for (int s = 0; s < cuts; s++)
            {
                float z = zStart + s * step;
                Cut(z, outline);

                for (int layer = 0; layer < Layers; layer++)
                {
                    float y = firstY + layer * pitch;
                    for (int k = 0; k < ScanOffsets.Length; k++)
                        Stretches(outline, y + ScanOffsets[k] * radius, stretches[k]);

                    for (int column = 0; column < Columns; column++)
                    {
                        float x = firstX + column * pitch;
                        bool fits = true;
                        for (int k = 0; k < ScanOffsets.Length && fits; k++)
                            fits = Covered(stretches[k], x - halfChord[k], x + halfChord[k]);

                        if (fits)
                        {
                            if (!inRun[column, layer])
                            {
                                inRun[column, layer] = true;
                                runFrom[column, layer] = z;
                            }
                            runTo[column, layer] = z;
                        }
                        else if (inRun[column, layer])
                        {
                            PlaceRun(column, layer, runFrom[column, layer], runTo[column, layer]);
                            inRun[column, layer] = false;
                        }
                    }
                }
            }

            for (int column = 0; column < Columns; column++)
                for (int layer = 0; layer < Layers; layer++)
                    if (inRun[column, layer]) PlaceRun(column, layer, runFrom[column, layer], runTo[column, layer]);

            // "Slots" becomes the longest row actually filled (for the read-outs).
            int longest = 0;
            foreach (List<int> row in rows.Values) longest = Mathf.Max(longest, row.Count);
            Slots = longest;
        }

        // As many whole rounds as fit between from and to (where the row's circle fits), centered.
        private void PlaceRun(int column, int layer, float from, float to)
        {
            int count = Mathf.FloorToInt((to - from) / length + 1e-4f);
            if (count <= 0) return;
            float start = from + ((to - from) - count * length) * 0.5f;
            for (int i = 0; i < count; i++)
                AddRound(new Vector3(firstX + column * pitch, firstY + layer * pitch, start + (i + 0.5f) * length), column, layer);
        }

        // The mesh cut by the plane at depth z: one 2D segment (x1, y1, x2, y2) per crossing triangle,
        // oriented with the triangle's outward side on its right (so the outline runs counterclockwise).
        private void Cut(float z, List<Vector4> outline)
        {
            outline.Clear();
            foreach (Triangle t in triangles)
            {
                bool a = t.A.z > z, b = t.B.z > z, c = t.C.z > z;
                if (a == b && b == c) continue;

                Vector2 p, q;
                if (a == b) { p = Cross(t.A, t.C, z); q = Cross(t.B, t.C, z); }
                else if (a == c) { p = Cross(t.A, t.B, z); q = Cross(t.C, t.B, z); }
                else { p = Cross(t.B, t.A, z); q = Cross(t.C, t.A, z); }

                Vector2 d = q - p;
                if (d.sqrMagnitude < 1e-12f) continue;   // the plane only touches it
                // Right of d is (d.y, -d.x); flip the segment if the outward side is on its left.
                if (t.Facing.x * d.y - t.Facing.y * d.x < 0f) { Vector2 swap = p; p = q; q = swap; }
                outline.Add(new Vector4(p.x, p.y, q.x, q.y));
            }
        }

        private static Vector2 Cross(Vector3 from, Vector3 to, float z)
        {
            float t = (z - from.z) / (to.z - from.z);
            return new Vector2(from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t);
        }

        // The stretches of the line at height y that are inside the outline. Each crossing counts +1 (segment
        // going up) or -1 (going down). A gap between crossings is inside when the winding counted from the
        // left end AND the one counted from the right end are both nonzero (they are equal for a clean closed
        // outline; requiring both keeps a doubled face, which leaves the two ends unequal, from turning the
        // whole outside into "inside"). Tested outside the game on a wedge: closed, mirrored, split in two
        // along its length, and with doubled faces all give the right stretches.
        private static readonly List<Vector2> Crossings = new List<Vector2>();   // (x, +1 / -1)

        private static void Stretches(List<Vector4> outline, float y, List<Vector2> result)
        {
            result.Clear();
            Crossings.Clear();
            foreach (Vector4 s in outline)
            {
                bool above1 = s.y > y, above2 = s.w > y;
                if (above1 == above2) continue;
                float x = s.x + (y - s.y) * (s.z - s.x) / (s.w - s.y);
                Crossings.Add(new Vector2(x, s.w > s.y ? 1f : -1f));
            }
            if (Crossings.Count < 2) return;
            Crossings.Sort((a, b) => a.x.CompareTo(b.x));

            int total = 0;
            foreach (Vector2 c in Crossings) total += c.y > 0f ? 1 : -1;

            int fromLeft = 0;
            bool open = false;
            float start = 0f;
            for (int i = 0; i + 1 < Crossings.Count; i++)
            {
                fromLeft += Crossings[i].y > 0f ? 1 : -1;
                bool inside = fromLeft != 0 && total - fromLeft != 0;   // the gap after crossing i
                if (inside && !open)
                {
                    open = true;
                    start = Crossings[i].x;
                }
                else if (!inside && open)
                {
                    open = false;
                    if (Crossings[i].x > start) result.Add(new Vector2(start, Crossings[i].x));
                }
            }
            if (open) result.Add(new Vector2(start, Crossings[Crossings.Count - 1].x));
        }

        private static bool Covered(List<Vector2> stretches, float from, float to)
        {
            foreach (Vector2 s in stretches)
                if (s.x <= from && s.y >= to) return true;
            return false;
        }

        private string Fail(string status, string problem)
        {
            Status = status;
            return problem;
        }

        private static bool IsCut(JFuselageData fuselage)
        {
            for (int i = 0; i < fuselage.NumSlices; i++)
            {
                JFuselageData.CuttingParams cut = fuselage.GetCutting(i);
                if ((cut.x.HasValue && cut.x.Value != 0m) || (cut.y.HasValue && cut.y.Value != 0m) ||
                    (cut.z.HasValue && cut.z.Value != 0m) || (cut.w.HasValue && cut.w.Value != 0m))
                    return true;
            }
            return false;
        }

        // The part's own solid colliders (the same ones the fragments hit).
        private static List<Collider> Colliders(PartScript part)
        {
            List<Collider> list = new List<Collider>();
            foreach (Collider collider in part.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (collider.GetComponentInParent<PartScript>() != part) continue;
                list.Add(collider);
            }
            return list;
        }

        // The colliders' extent in frame coordinates.
        private bool Extent(PartScript part, List<Collider> colliders)
        {
            boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;

            foreach (Collider collider in colliders)
            {
                MeshCollider mesh = collider as MeshCollider;
                BoxCollider box = collider as BoxCollider;
                if (mesh != null && mesh.sharedMesh != null)
                {
                    foreach (Vector3 vertex in mesh.sharedMesh.vertices)
                    {
                        Include(ToFrame(part.transform, collider.transform.TransformPoint(vertex)));
                        any = true;
                    }
                }
                else if (box != null)
                {
                    Vector3 half = box.size * 0.5f;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 corner = box.center + new Vector3(
                            (i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                        Include(ToFrame(part.transform, box.transform.TransformPoint(corner)));
                    }
                    any = true;
                }
                else
                {
                    Bounds bounds = collider.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 corner = new Vector3(
                            (i & 1) == 0 ? bounds.min.x : bounds.max.x, (i & 2) == 0 ? bounds.min.y : bounds.max.y,
                            (i & 4) == 0 ? bounds.min.z : bounds.max.z);
                        Include(ToFrame(part.transform, corner));
                    }
                    any = true;
                }
            }
            return any;
        }

        private void Include(Vector3 point)
        {
            boundsMin = Vector3.Min(boundsMin, point);
            boundsMax = Vector3.Max(boundsMax, point);
        }

        private bool RoundFits(Transform frame, List<Collider> colliders, Vector3 center, int alongSamples)
        {
            float start = center.z - length * 0.5f;
            float r = radius * RingTolerance;

            // Ends first: that is where a round usually pokes out.
            for (int pass = 0; pass < 2; pass++)
                for (int s = 0; s < alongSamples; s++)
                {
                    bool isEnd = s == 0 || s == alongSamples - 1;
                    if ((pass == 0) != isEnd) continue;

                    float z = start + length * (0.002f + 0.996f * s / (alongSamples - 1));
                    for (int a = 0; a < 8; a++)
                    {
                        float angle = a * Mathf.PI * 0.25f;
                        Vector3 point = new Vector3(center.x + Mathf.Cos(angle) * r, center.y + Mathf.Sin(angle) * r, z);
                        if (!Inside(colliders, FromFrame(frame, point))) return false;
                    }
                }
            return true;
        }

        private static bool Inside(List<Collider> colliders, Vector3 world)
        {
            foreach (Collider collider in colliders)
            {
                MeshCollider mesh = collider as MeshCollider;
                bool exact = mesh == null ? (collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider)
                                          : mesh.convex;
                if (exact)
                {
                    if ((collider.ClosestPoint(world) - world).sqrMagnitude < 1e-8f) return true;
                }
                else if (collider.bounds.Contains(world))
                {
                    return true;   // non-convex mesh: ClosestPoint is not available, the box will do
                }
            }
            return false;
        }

        // ---- Frame <-> world ---------------------------------------------------------

        // Rotation only, so a scaled transform doesn't change the meters.
        private Vector3 ToFrame(Transform part, Vector3 world)
        {
            Vector3 local = Quaternion.Inverse(part.rotation) * (world - part.position);
            return new Vector3(Vector3.Dot(local, axisX), Vector3.Dot(local, axisY), Vector3.Dot(local, axisZ));
        }

        private Vector3 FromFrame(Transform part, Vector3 frame)
        {
            Vector3 local = axisX * frame.x + axisY * frame.y + axisZ * frame.z;
            return part.position + part.rotation * local;
        }

        private Vector3 DirectionToFrame(Transform part, Vector3 worldDirection)
        {
            Vector3 local = Quaternion.Inverse(part.rotation) * worldDirection;
            return new Vector3(Vector3.Dot(local, axisX), Vector3.Dot(local, axisY), Vector3.Dot(local, axisZ));
        }

        // The middle of the rack in the world (where the explosion goes).
        public Vector3 WorldCenter(Transform part)
        {
            return FromFrame(part, (boundsMin + boundsMax) * 0.5f);
        }

        // ---- Using rounds ------------------------------------------------------------

        // Top layer first, then left to right, then front to back.
        private void BuildRemovalOrder()
        {
            List<int> order = new List<int>();
            for (int i = 0; i < rounds.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                Round ra = rounds[a], rb = rounds[b];
                if (ra.Layer != rb.Layer) return rb.Layer.CompareTo(ra.Layer);
                if (ra.Column != rb.Column) return ra.Column.CompareTo(rb.Column);
                return ra.Center.z.CompareTo(rb.Center.z);
            });
            removalOrder = order.ToArray();
            taken = 0;
        }

        public bool TakeRound()
        {
            if (taken >= removalOrder.Length) return false;
            present[removalOrder[taken]] = false;
            taken++;
            return true;
        }

        public void EmptyAll()
        {
            for (int i = 0; i < present.Length; i++) present[i] = false;
            taken = removalOrder.Length;
        }

        // Does the line through this point (both ways along the direction) cross a cell that still holds a
        // round? Fragments give their direction of travel; the game's own shell impact gives the surface
        // normal, so the line is taken both ways and clipped to the rack. thickness (m): the piece's own radius,
        // so a shell is a tube of its caliber and can't thread between packed rounds (0 = a thin line).
        public bool HitsRounds(Transform part, Vector3 worldPoint, Vector3 worldDirection, float thickness = 0f)
        {
            if (Remaining <= 0) return false;
            thickness = Mathf.Max(thickness, 0f);

            Vector3 p = ToFrame(part, worldPoint);
            Vector3 d = DirectionToFrame(part, worldDirection);
            if (d.sqrMagnitude < 1e-8f) return RoundAt(p, thickness);
            d.Normalize();

            // Clip the line to the rack's box (grown by a radius and the piece's own).
            Vector3 min = boundsMin - Vector3.one * (radius + thickness), max = boundsMax + Vector3.one * (radius + thickness);
            float t0 = float.MinValue, t1 = float.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(d[axis]) < 1e-6f)
                {
                    if (p[axis] < min[axis] || p[axis] > max[axis]) return false;
                    continue;
                }
                float a = (min[axis] - p[axis]) / d[axis], b = (max[axis] - p[axis]) / d[axis];
                t0 = Mathf.Max(t0, Mathf.Min(a, b));
                t1 = Mathf.Min(t1, Mathf.Max(a, b));
            }
            if (t0 > t1) return false;

            float step = Mathf.Max(radius * 0.5f, 0.005f);
            int steps = Mathf.Min(4000, Mathf.CeilToInt((t1 - t0) / step) + 1);
            for (int i = 0; i <= steps; i++)
                if (RoundAt(p + d * Mathf.Lerp(t0, t1, (float)i / steps), thickness)) return true;
            return false;
        }

        // Is there a round still in the rack within 'thickness' of this point (frame coordinates)? With a
        // thickness the neighboring columns and layers are looked at too.
        private bool RoundAt(Vector3 q, float thickness = 0f)
        {
            float reach = radius * 1.0488f + thickness;   // 1.0488^2 = 1.1: the old margin of a thin line
            int c0 = Mathf.Max(0, Mathf.FloorToInt((q.x - reach - firstX) / pitch + 0.5f));
            int c1 = Mathf.Min(Columns - 1, Mathf.FloorToInt((q.x + reach - firstX) / pitch + 0.5f));
            int l0 = Mathf.Max(0, Mathf.FloorToInt((q.y - reach - firstY) / pitch + 0.5f));
            int l1 = Mathf.Min(Layers - 1, Mathf.FloorToInt((q.y + reach - firstY) / pitch + 0.5f));

            for (int column = c0; column <= c1; column++)
            {
                for (int layer = l0; layer <= l1; layer++)
                {
                    List<int> row;
                    if (!rows.TryGetValue(RowKey(column, layer), out row)) continue;
                    foreach (int index in row)
                    {
                        if (!present[index]) continue;
                        Round round = rounds[index];
                        if (Mathf.Abs(q.z - round.Center.z) > length * 0.5f + thickness) continue;
                        float dx = q.x - round.Center.x, dy = q.y - round.Center.y;
                        if (dx * dx + dy * dy <= reach * reach) return true;
                    }
                }
            }
            return false;
        }

        // ---- Drawing (designer) ----------------------------------------------------------

        // The rounds still in the rack as cylinders: submesh 0 = cases, submesh 1 = shells (narrower, in
        // front, toward the fuselage's front). Coordinates are in the part's rotation without its scale:
        // put the mesh on a child of the part whose scale cancels the part's.
        public Mesh BuildRoundsMesh(int maxRounds, int sides)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<int> cases = new List<int>();
            List<int> shells = new List<int>();

            int drawn = 0;
            for (int i = 0; i < rounds.Count && drawn < maxRounds; i++)
            {
                if (!present[i]) continue;
                drawn++;
                Vector3 c = rounds[i].Center;
                float z0 = c.z - length * 0.5f;
                float zCase = z0 + caseLength;
                float z1 = c.z + length * 0.5f;
                AddCylinder(vertices, normals, cases, c.x, c.y, z0, zCase, radius, sides, true, true);
                AddCylinder(vertices, normals, shells, c.x, c.y, zCase, z1, shellRadius, sides, false, true);
            }

            Mesh mesh = new Mesh { name = "AmmoRackRounds" };
            if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(cases, 0);
            mesh.SetTriangles(shells, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        private void AddCylinder(List<Vector3> vertices, List<Vector3> normals, List<int> triangles,
                                 float x, float y, float z0, float z1, float r, int sides, bool backCap, bool frontCap)
        {
            // Side: two rings with outward normals.
            int start = vertices.Count;
            for (int s = 0; s <= sides; s++)
            {
                float angle = s * Mathf.PI * 2f / sides;
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                Vector3 normal = Local(new Vector3(cos, sin, 0f));
                vertices.Add(LocalPoint(new Vector3(x + cos * r, y + sin * r, z0)));
                normals.Add(normal);
                vertices.Add(LocalPoint(new Vector3(x + cos * r, y + sin * r, z1)));
                normals.Add(normal);
            }
            for (int s = 0; s < sides; s++)
            {
                int a = start + s * 2;
                // Unity's front faces are clockwise seen from outside.
                triangles.Add(a); triangles.Add(a + 2); triangles.Add(a + 1);
                triangles.Add(a + 1); triangles.Add(a + 2); triangles.Add(a + 3);
            }

            if (backCap) AddCap(vertices, normals, triangles, x, y, z0, r, sides, -1f);
            if (frontCap) AddCap(vertices, normals, triangles, x, y, z1, r, sides, 1f);
        }

        private void AddCap(List<Vector3> vertices, List<Vector3> normals, List<int> triangles,
                            float x, float y, float z, float r, int sides, float facing)
        {
            Vector3 normal = Local(new Vector3(0f, 0f, facing));
            int center = vertices.Count;
            vertices.Add(LocalPoint(new Vector3(x, y, z)));
            normals.Add(normal);
            for (int s = 0; s <= sides; s++)
            {
                float angle = s * Mathf.PI * 2f / sides;
                vertices.Add(LocalPoint(new Vector3(x + Mathf.Cos(angle) * r, y + Mathf.Sin(angle) * r, z)));
                normals.Add(normal);
            }
            for (int s = 0; s < sides; s++)
            {
                int a = center + 1 + s;
                // Clockwise seen from the side the cap faces.
                if (facing > 0f) { triangles.Add(center); triangles.Add(a); triangles.Add(a + 1); }
                else { triangles.Add(center); triangles.Add(a + 1); triangles.Add(a); }
            }
        }

        private Vector3 LocalPoint(Vector3 frame)
        {
            return axisX * frame.x + axisY * frame.y + axisZ * frame.z;
        }

        private Vector3 Local(Vector3 frameDirection)
        {
            return (axisX * frameDirection.x + axisY * frameDirection.y + axisZ * frameDirection.z).normalized;
        }

        // ---- Shape dump (AmmoRacks.DebugDumpShape) ----------------------------------------
        //
        // Writes what was measured to an OBJ file: the part's triangles in the rack's frame (meters; Z along
        // the length, Y up), the round centers as points, and the measurement as comments. For checking the
        // measuring code against a real fuselage outside the game.
        public void DumpShape(string path, string title)
        {
            System.Text.StringBuilder obj = new System.Text.StringBuilder();
            CultureInfo inv = CultureInfo.InvariantCulture;
            obj.AppendLine("# Ammo rack shape dump: " + title);
            obj.AppendLine("# method " + Method + ", status " + Status + (Problem != null ? ", problem: " + Problem : ""));
            obj.AppendLine("# axes (part local) X " + axisX.ToString("F4") + " Y " + axisY.ToString("F4") + " Z " + axisZ.ToString("F4"));
            obj.AppendLine("# bounds " + boundsMin.ToString("F4") + " .. " + boundsMax.ToString("F4"));
            obj.AppendLine("# round radius " + radius.ToString("F4", inv) + " length " + length.ToString("F4", inv) +
                           " pitch " + pitch.ToString("F4", inv) + "; first x " + firstX.ToString("F4", inv) +
                           " y " + firstY.ToString("F4", inv));
            obj.AppendLine("# grid " + Columns + " columns x " + Layers + " layers, longest row " + Slots + ", capacity " + Capacity);
            foreach (string source in meshSources) obj.AppendLine("# mesh " + source);

            System.Text.StringBuilder rowsText = new System.Text.StringBuilder("# rounds per row (layer: columns):");
            for (int layer = 0; layer < Layers; layer++)
            {
                rowsText.Append("\n#  L").Append(layer).Append(':');
                for (int column = 0; column < Columns; column++)
                {
                    List<int> row;
                    rowsText.Append(' ').Append(rows.TryGetValue(RowKey(column, layer), out row) ? row.Count : 0);
                }
            }
            obj.AppendLine(rowsText.ToString());

            if (triangles != null)
            {
                obj.AppendLine("o shape");
                foreach (Triangle t in triangles)
                {
                    obj.AppendLine("v " + V(t.A, inv));
                    obj.AppendLine("v " + V(t.B, inv));
                    obj.AppendLine("v " + V(t.C, inv));
                }
                for (int i = 0; i < triangles.Count; i++)
                    obj.AppendLine("f " + (3 * i + 1) + " " + (3 * i + 2) + " " + (3 * i + 3));
            }

            int first = (triangles != null ? triangles.Count * 3 : 0) + 1;
            obj.AppendLine("o rounds");
            foreach (Round round in rounds) obj.AppendLine("v " + V(round.Center, inv));
            for (int i = 0; i < rounds.Count; i++) obj.AppendLine("p " + (first + i));

            System.IO.File.WriteAllText(path, obj.ToString());
        }

        private static string V(Vector3 v, CultureInfo inv)
        {
            return v.x.ToString("F5", inv) + " " + v.y.ToString("F5", inv) + " " + v.z.ToString("F5", inv);
        }

        // ---- Text ----------------------------------------------------------------------

        // "2 slots x 6 x 2 = 24 rounds", or "2 slots x 6 x 2 grid, 18 fit the shape" (slots = longest row)
        public string Describe()
        {
            if (Problem != null) return Problem;
            string grid = Slots + (Slots == 1 ? " slot" : " slots") + " x " + Columns + " x " + Layers;
            int full = Slots * Columns * Layers;
            return full == Capacity
                ? grid + " = " + Capacity + " rounds"
                : grid + " grid, " + Capacity + " fit the shape";
        }

        private static string Cm(float meters)
        {
            return (meters * 100f).ToString("F1", CultureInfo.InvariantCulture) + " cm";
        }
    }
}
