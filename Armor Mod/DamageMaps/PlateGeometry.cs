using System;
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using UnityEngine;

namespace ArmorFragments
{
    // The shape of an armor plate, as the damage maps need it: a plane with two axes (T1, T2) along its length and width
    // and a normal (N) across its thickness, in the PART's local frame (so it holds while the vehicle moves).
    //
    // They come from the plate's own mesh: the axes of a principal component analysis of its vertices (the thinnest
    // direction is the thickness). A flat slab, which is what a plate is, gets an exact frame; a curved part gets an
    // approximate one (it is flagged: the maps are flat).
    //
    // A point of the plate has plate coordinates (u, v) in meters from the corner of the plate's bounding rectangle
    // (0..SizeU, 0..SizeV). The face it is on is the front (+N) or the back (-N).
    internal sealed class PlateGeometry
    {
        public Vector3 Origin;    // the corner (u = 0, v = 0, middle of the thickness), in part-local space
        public Vector3 T1;        // along the plate's length
        public Vector3 T2;        // along its width
        public Vector3 N;         // across its thickness (the "front" side)
        public float SizeU;       // meters
        public float SizeV;
        public float Depth;       // the mesh's extent along N, meters
        public bool Curved;       // the mesh is not a slab: the flat maps only approximate it
        public int Axis;          // 0 = the plate itself; 1 and 2 = the geometry of the block's sides (see Side)

        public Vector2 ToPlane(Vector3 partLocal)
        {
            Vector3 d = partLocal - Origin;
            return new Vector2(Vector3.Dot(d, T1), Vector3.Dot(d, T2));
        }

        public Vector2 DirectionToPlane(Vector3 partLocalDirection)
        {
            return new Vector2(Vector3.Dot(partLocalDirection, T1), Vector3.Dot(partLocalDirection, T2));
        }

        // The sides of a thick block as plates of their own: axis 1 is the pair of faces across T1 (the ends along its length), axis 2 the pair
        // across T2. Each is a plate whose normal is that axis, so everything that works for the big faces (tiles, marks, the simulation, the
        // model's holes) works for them unchanged. Right-handed like the main frame.
        public static PlateGeometry Side(PlateGeometry m, int axis)
        {
            float depth = Mathf.Max(m.Depth, 0.01f);
            if (axis == 1)
                return new PlateGeometry
                {
                    T1 = m.T2, T2 = m.N, N = m.T1, SizeU = m.SizeV, SizeV = depth, Depth = m.SizeU, Axis = 1,
                    Origin = m.Origin - m.N * (depth * 0.5f) + m.T1 * (m.SizeU * 0.5f),
                };
            return new PlateGeometry
            {
                T1 = m.N, T2 = m.T1, N = m.T2, SizeU = depth, SizeV = m.SizeU, Depth = m.SizeV, Axis = 2,
                Origin = m.Origin - m.N * (depth * 0.5f) + m.T2 * (m.SizeV * 0.5f),
            };
        }

        // The geometry of a mesh. 'toPart' takes the mesh's vertices to the part's local frame.
        public static PlateGeometry Build(Mesh mesh, Matrix4x4 toPart)
        {
            int count = mesh.vertexCount;
            if (count < 3) return null;

            List<Vector3> vertices = new List<Vector3>(count);
            mesh.GetVertices(vertices);
            Vector3[] points = new Vector3[count];
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                points[i] = toPart.MultiplyPoint3x4(vertices[i]);
                mean += points[i];
            }
            mean /= count;

            // The covariance of the vertices.
            double[,] a = new double[3, 3];
            for (int i = 0; i < count; i++)
            {
                Vector3 d = points[i] - mean;
                a[0, 0] += d.x * d.x; a[0, 1] += d.x * d.y; a[0, 2] += d.x * d.z;
                a[1, 1] += d.y * d.y; a[1, 2] += d.y * d.z; a[2, 2] += d.z * d.z;
            }
            a[1, 0] = a[0, 1]; a[2, 0] = a[0, 2]; a[2, 1] = a[1, 2];

            double[,] vectors;
            double[] values;
            Eigen(a, out vectors, out values);

            // Smallest spread = thickness; the others, largest first, are the length and the width.
            int[] order = { 0, 1, 2 };
            Array.Sort(order, (x, y) => values[y].CompareTo(values[x]));
            Vector3 t1 = Column(vectors, order[0]);
            Vector3 n = Column(vectors, order[2]);
            Vector3 t2 = Vector3.Cross(n, t1).normalized;
            t1 = Vector3.Cross(t2, n).normalized;

            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            float minN = float.MaxValue, maxN = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                Vector3 d = points[i] - mean;
                float u = Vector3.Dot(d, t1), v = Vector3.Dot(d, t2), h = Vector3.Dot(d, n);
                if (u < minU) minU = u; if (u > maxU) maxU = u;
                if (v < minV) minV = v; if (v > maxV) maxV = v;
                if (h < minN) minN = h; if (h > maxN) maxN = h;
            }

            PlateGeometry geometry = new PlateGeometry
            {
                T1 = t1,
                T2 = t2,
                N = n,
                SizeU = Mathf.Max(maxU - minU, 0.01f),
                SizeV = Mathf.Max(maxV - minV, 0.01f),
                Depth = Mathf.Max(maxN - minN, 0f),
            };
            // The origin is the corner, in the middle of the thickness.
            geometry.Origin = mean + t1 * minU + t2 * minV + n * (0.5f * (minN + maxN));
            geometry.Curved = geometry.Depth > 0.35f * Mathf.Min(geometry.SizeU, geometry.SizeV);
            return geometry;
        }

        private static Vector3 Column(double[,] vectors, int column)
        {
            return new Vector3((float)vectors[0, column], (float)vectors[1, column], (float)vectors[2, column]).normalized;
        }

        // Eigenvalues and eigenvectors (the columns of 'vectors') of a symmetric 3x3 matrix: Jacobi rotations.
        private static void Eigen(double[,] a, out double[,] vectors, out double[] values)
        {
            double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            int[,] pairs = { { 0, 1 }, { 0, 2 }, { 1, 2 } };

            for (int sweep = 0; sweep < 40; sweep++)
            {
                double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                if (off < 1e-24) break;

                for (int k = 0; k < 3; k++)
                {
                    int p = pairs[k, 0], q = pairs[k, 1];
                    double apq = a[p, q];
                    if (Math.Abs(apq) < 1e-30) continue;

                    double theta = (a[q, q] - a[p, p]) / (2.0 * apq);
                    double t = (theta >= 0 ? 1.0 : -1.0) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    double c = 1.0 / Math.Sqrt(t * t + 1.0);
                    double s = t * c;

                    a[p, p] -= t * apq;
                    a[q, q] += t * apq;
                    a[p, q] = 0; a[q, p] = 0;
                    for (int r = 0; r < 3; r++)
                    {
                        if (r == p || r == q) continue;
                        double arp = a[r, p], arq = a[r, q];
                        a[r, p] = c * arp - s * arq; a[p, r] = a[r, p];
                        a[r, q] = c * arq + s * arp; a[q, r] = a[r, q];
                    }
                    for (int r = 0; r < 3; r++)
                    {
                        double vrp = v[r, p], vrq = v[r, q];
                        v[r, p] = c * vrp - s * vrq;
                        v[r, q] = c * vrq + s * vrp;
                    }
                }
            }

            vectors = v;
            values = new[] { a[0, 0], a[1, 1], a[2, 2] };
        }
    }
}
