// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using ArmorFragments.Fluid;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // Smoke that reaches the edge of its box does not vanish: a second box, bigger and with coarser cells, is made beyond that face and takes the
    // smoke that lies near it (the same hand-over the bubble and the trails use). The bigger box keeps the smoke going, rising for example, instead
    // of cutting it off at a flat lid. At most two such boxes live at a time, and they do not make boxes of their own.
    internal static partial class FluidSmoke
    {
        public static bool Overflow { get { return ArmorFragmentsPlugin.SmokeOverflow == null || ArmorFragmentsPlugin.SmokeOverflow.Value; } }
        private const int MaxOverflow = 2;
        private const float OverflowCellFactor = 1.6f;      // its cells are this much bigger, and so is its box, with the same number of cells
        private static int overflowLogged;

        // The occupancy of the blocks next to the faces of a cloud, looked at four times a second (a small read-back of the occupancy texture).
        private static void ProbeFaces(Cloud c, float frame)
        {
            c.probeAt -= frame;
            if (c.probeAt > 0f || c.probing || c.sim == null) return;
            c.probeAt = 0.25f;
            c.sim.UpdateOccupancy();
            if (c.sim.Occupancy == null) return;
            c.probing = true;
            Vector3Int o = c.sim.OccupancyDims;
            Cloud cloud = c;
            AsyncGPUReadback.Request(c.sim.Occupancy, 0, TextureFormat.RGBAFloat, rs =>
            {
                cloud.probing = false;
                if (rs.hasError || cloud.sim == null) return;
                try
                {
                    int[] counts = new int[6];
                    for (int z = 0; z < o.z; z++)
                    {
                        Unity.Collections.NativeArray<Vector4> layer = rs.GetData<Vector4>(z);
                        for (int y = 0; y < o.y; y++)
                            for (int x = 0; x < o.x; x++)
                            {
                                if (layer[y * o.x + x].x < 0.05f) continue;
                                // The two outermost blocks next to each face.
                                if (x <= 1) counts[0]++;
                                if (x >= o.x - 2) counts[1]++;
                                if (y >= o.y - 2) counts[3]++;
                                if (z <= 1) counts[4]++;
                                if (z >= o.z - 2) counts[5]++;
                            }
                    }
                    int mask = 0;
                    for (int f = 0; f < 6; f++) if (f != 2 && counts[f] >= 3) mask |= 1 << f;      // face 2 is the bottom, where the ground is
                    cloud.faceMask = mask;
                }
                catch (System.Exception) { }
            });
        }

        // Called every frame before the clouds are stepped.
        private static void UpdateOverflow(float frame)
        {
            if (!Overflow) return;
            int overflowCount = 0;
            foreach (Cloud c in Active) if (c.isOverflow) overflowCount++;

            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Cloud c = Active[i];
                if (c.sim == null || c.isBubble || c.isTail || c.isOverflow || c.age < 0.4f * c.timeScale || c.age >= c.end) continue;
                ProbeFaces(c, frame);
                for (int face = 0; face < 6 && overflowCount < MaxOverflow; face++)
                    if ((c.faceMask & (1 << face)) != 0 && (c.children & (1 << face)) == 0)
                    {
                        MakeOverflow(c, face);
                        c.children |= 1 << face;
                        overflowCount++;
                    }
            }

            // Smoke keeps passing to the boxes that were made.
            foreach (Cloud o in Active)
            {
                if (!o.isOverflow || o.parent == null || o.parent.sim == null || o.sim == null) continue;
                Cloud p = o.parent;
                Matrix4x4 dest = Matrix4x4.TRS(o.origin, o.rotation, Vector3.one), source = Matrix4x4.TRS(p.origin, p.rotation, Vector3.one);
                o.sim.CaptureFrom(p.sim, source.inverse * dest, dest.inverse * source, Matrix4x4.identity, Mathf.Max(o.sim.Params.edgeMeters, o.sim.Cell) + 0.5f, OverflowPlane(p, o));
                float remaining = Mathf.Max(p.end - p.age, 0f);
                if (o.age + remaining > o.end) o.end = o.age + remaining;
            }
        }

        // The plane (in the new box's space) that cuts off the 30% of the parent next to the face: only that part is handed over.
        private static Vector4 OverflowPlane(Cloud p, Cloud o)
        {
            int axis = o.faceAxis, sign = o.faceSign;
            // where the parent's box starts, along that axis, in the new box's space (the boxes have the same axes)
            Vector3 parentMin = Quaternion.Inverse(o.rotation) * (p.origin - o.origin);
            float start = axis == 0 ? parentMin.x : axis == 1 ? parentMin.y : parentMin.z;
            float length = axis == 0 ? p.extent.x : axis == 1 ? p.extent.y : p.extent.z;
            float cut = sign > 0 ? start + 0.7f * length : start + 0.3f * length;
            Vector3 normal = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            return sign > 0 ? new Vector4(normal.x, normal.y, normal.z, -cut) : new Vector4(-normal.x, -normal.y, -normal.z, cut);
        }

        private static void MakeOverflow(Cloud p, int face)
        {
            try
            {
                ComputeShader cs = VfxBundleTest.Compute("fluid");
                Material template = VfxBundleTest.Material("smokevolume");
                if (cs == null || template == null) return;
                int axis = face < 2 ? 0 : face < 4 ? 1 : 2;      // 0 -x, 1 +x, 2 -y, 3 +y, 4 -z, 5 +z
                int sign = (face & 1) == 1 ? 1 : -1;
                float cell = p.sim.Cell * OverflowCellFactor;
                Vector3 extent = p.extent * OverflowCellFactor;
                // In the parent's space: past the face along the axis (overlapping the parent by a third of its size there), and on the other
                // axes centred on the parent, except the height, which starts at the parent's floor (the ground is in the box).
                float overlap = 0.35f;
                Vector3 min = new Vector3(p.extent.x * 0.5f - extent.x * 0.5f, 0f, p.extent.z * 0.5f - extent.z * 0.5f);
                float pa = axis == 0 ? p.extent.x : axis == 1 ? p.extent.y : p.extent.z;
                float ea = axis == 0 ? extent.x : axis == 1 ? extent.y : extent.z;
                float aMin = sign > 0 ? pa * (1f - overlap) : pa * overlap - ea;
                if (axis == 0) min.x = aMin; else if (axis == 1) min.y = aMin; else min.z = aMin;
                Vector3 origin = p.origin + p.rotation * min;

                FluidSim sim = Acquire(cs, p.sim.Dims, cell, p.sim.IsHalf);
                sim.Cell = cell;
                sim.Clear();
                sim.Params = p.sim.Params;
                sim.Params.edgeMeters = p.sim.Params.edgeMeters * OverflowCellFactor;
                sim.CoarseSweeps = 10;
                Cloud o = new Cloud
                {
                    isOverflow = true, parent = p, faceAxis = axis, faceSign = sign, level = p.level, sim = sim, origin = origin, rotation = p.rotation, extent = extent,
                    scale = p.scale, timeScale = p.timeScale, age = 100f, end = 100f + Mathf.Max(p.end - p.age, 0f),
                    hasGround = p.hasGround, groundPoint = p.groundPoint, groundNormal = p.groundNormal,
                };
                o.material = new Material(template) { name = "ArmorFragments fluid smoke (overflow)" };
                o.material.CopyPropertiesFromMaterial(p.material);
                o.material.SetVector("_GridExtent", new Vector4(extent.x, extent.y, extent.z, 0f));
                if (boxMesh == null) boxMesh = MakeBox();
                o.box = new GameObject("ArmorFragments fluid smoke (overflow)");
                o.box.AddComponent<MeshFilter>().sharedMesh = boxMesh;
                MeshRenderer renderer = o.box.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = o.material;
                renderer.shadowCastingMode = SmokeShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                o.boxRenderer = renderer;
                renderer.receiveShadows = false;
                VfxBundleTest.SetLayer(o.box, ImpactCamera.EffectsLayer);
                Place(o);
                Active.Add(o);
                if (overflowLogged < 8)
                {
                    overflowLogged++;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] smoke overflow: smoke reached the " + (sign > 0 ? "+" : "-") + (axis == 0 ? "x" : axis == 1 ? "y" : "z") + " face of a cloud, a box of "
                        + extent.x.ToString("F0") + " x " + extent.y.ToString("F0") + " x " + extent.z.ToString("F0") + " m with cells of " + (cell * 100f).ToString("F0") + " cm takes it");
                }
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] smoke overflow could not be made: " + e.Message);
            }
        }
    }
}
// RELEASE-STRIP-END
