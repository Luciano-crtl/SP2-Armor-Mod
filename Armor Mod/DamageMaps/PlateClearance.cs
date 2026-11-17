using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using UnityEngine;

namespace ArmorFragments
{
    // Plates that overlap or are clipped into each other: a mark on one must not push its metal through the other. Before a job goes to the
    // worker, this asks the physics engine (the one thing that can only be done on the main thread) how much room each point of the marked
    // plate's two faces has, out and in, before it would meet another armor plate, and hands the answer over as ClearPatches. The worker keeps
    // the heights within that room (PlateStamps.LimitDepth), so the bump map and the real geometry both stay inside.
    //   Out  how far the surface can rise before it goes through a plate in front of it (0 where the surface is buried in one)
    //   In   how far a dent can go before it goes through a plate behind it
    // Only armor plates that touch the marked one are asked (found once per plate); a plate with no such neighbor costs nothing.
    internal static class PlateClearance
    {
        private const float Margin = 0.0005f;      // the probe starts this far outside the surface
        private const float MaxReach = 0.5f;       // nothing further than this changes the answer
        private const float MaxRadius = 0.35f;     // the part of the plate looked at around an impact

        public static List<ClearPatch> Prepare(PlateVisual plate, PlateImpact[] impacts)
        {
            if (plate == null || impacts == null || impacts.Length == 0 || !ArmorFragmentsPlugin.DamageMapsAvoidOverlap.Value) return null;
            try
            {
                List<Collider> neighbors = NeighborsOf(plate);
                if (neighbors.Count == 0) return null;

                PlateGeometry g = plate.Info.Geometry;
                // Only a flat slab has its surface at the plane this measures (the face of its bounding box). A thick or curved block is "not a slab": its
                // surface is somewhere inside that box, the probes land inside the plates around it, the whole area reads as buried and every mark was
                // flattened (the ricochets and slanted shots on such blocks lost their relief). Those blocks get no limit; they have no real geometry either.
                if (g.Curved) return null;
                if (plate.ClearKnown == null)
                {
                    plate.ClearKnown = new ClearGrid[2];
                    plate.ClearDone = new bool[2][];
                    for (int face = 0; face < 2; face++)
                    {
                        plate.ClearKnown[face] = ClearGrid.Make(g.SizeU, g.SizeV);
                        plate.ClearDone[face] = new bool[plate.ClearKnown[face].W * plate.ClearKnown[face].H];
                    }
                }

                List<ClearPatch> patches = null;
                foreach (PlateImpact impact in impacts)
                {
                    float caliber = Mathf.Max(impact.Caliber, 0.01f);
                    float angle = Mathf.Clamp(impact.Angle, 0f, 85f) * Mathf.Deg2Rad;
                    float reach = impact.Kind == DamageKind.Penetrated ? Mathf.Min(impact.Thickness * Mathf.Tan(angle), 0.35f)
                                : impact.Kind == DamageKind.Ricochet ? Mathf.Min(caliber * (1.5f + 8f * (impact.Angle / 90f) * (impact.Angle / 90f)), 0.35f)
                                : 0f;
                    float radius = Mathf.Min(MaxRadius, 6f * caliber + 0.04f) + reach * 0.5f;
                    Vector2 dir = new Vector2(impact.DirU, impact.DirV);
                    dir = dir.sqrMagnitude < 0.0001f ? Vector2.zero : dir.normalized;
                    float cu = impact.U + dir.x * reach * 0.5f, cv = impact.V + dir.y * reach * 0.5f;

                    for (int face = 0; face < 2; face++)
                    {
                        ClearPatch patch = Fill(plate, g, neighbors, face, cu, cv, radius);
                        if (patch == null) continue;
                        if (patches == null) patches = new List<ClearPatch>();
                        patches.Add(patch);
                    }
                }
                return patches;
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] the room around a plate could not be measured: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        // Measures the cells of one face that were never measured, inside the square around (cu, cv). Null if there was nothing new.
        private static ClearPatch Fill(PlateVisual plate, PlateGeometry g, List<Collider> neighbors, int face, float cu, float cv, float radius)
        {
            ClearGrid known = plate.ClearKnown[face];
            bool[] done = plate.ClearDone[face];
            int x0 = Mathf.Max(0, Mathf.FloorToInt((cu - radius) / known.Cell)), x1 = Mathf.Min(known.W - 1, Mathf.FloorToInt((cu + radius) / known.Cell));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((cv - radius) / known.Cell)), y1 = Mathf.Min(known.H - 1, Mathf.FloorToInt((cv + radius) / known.Cell));
            if (x1 < x0 || y1 < y0) return null;

            Transform tr = plate.Info.Part.transform;
            Vector3 normalLocal = face == 0 ? g.N : -g.N;
            Vector3 worldNormal = tr.TransformDirection(normalLocal).normalized;
            bool any = false;
            List<int> fresh = new List<int>();
            int buried = 0;
            for (int j = y0; j <= y1; j++)
            {
                for (int i = x0; i <= x1; i++)
                {
                    int at = j * known.W + i;
                    if (done[at]) continue;
                    done[at] = true;
                    any = true;
                    fresh.Add(at);

                    float u = (i + 0.5f) * known.Cell, v = (j + 0.5f) * known.Cell;
                    Vector3 local = g.Origin + g.T1 * u + g.T2 * v + normalLocal * (g.Depth * 0.5f);
                    Vector3 probe = tr.TransformPoint(local) + worldNormal * Margin;
                    float outRoom = ClearGrid.Free, inRoom = ClearGrid.Free;
                    foreach (Collider c in neighbors)
                    {
                        if (c == null || !c.gameObject.activeInHierarchy) continue;
                        if (c.bounds.SqrDistance(probe) > MaxReach * MaxReach) continue;
                        if (Inside(c, probe)) { outRoom = 0f; inRoom = 0f; break; }   // the surface is inside another plate: nothing of it shows
                        RaycastHit hit;
                        if (c.Raycast(new Ray(probe, worldNormal), out hit, MaxReach)) outRoom = Mathf.Min(outRoom, hit.distance);
                        if (c.Raycast(new Ray(probe, -worldNormal), out hit, MaxReach)) inRoom = Mathf.Min(inRoom, hit.distance);
                    }
                    known.Out[at] = outRoom;
                    known.In[at] = inRoom;
                    if (outRoom <= 0f) buried++;
                }
            }
            if (!any) return null;

            // Almost all of the area reading as buried is not an overlap, it is a measure that does not fit this plate (a plate lying inside others):
            // it is not trusted, the area stays free.
            if (fresh.Count >= 6 && buried > fresh.Count * 0.85f)
            {
                foreach (int at in fresh) { known.Out[at] = ClearGrid.Free; known.In[at] = ClearGrid.Free; }
                ArmorFragmentsPlugin.Log.LogInfo("[DMAP] plate '" + plate.Info.Part.Part.Name + "': " + buried + " of " + fresh.Count +
                                                 " cells round the mark read as buried in other plates; not trusted, the marks are not limited there");
            }
            else if (buried > 0)
            {
                ArmorFragmentsPlugin.Log.LogInfo("[DMAP] plate '" + plate.Info.Part.Part.Name + "': " + buried + " of " + fresh.Count +
                                                 " cells round the mark are buried in other plates (no marks there)");
            }

            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            ClearPatch patch = new ClearPatch { Face = face, FullW = known.W, FullH = known.H, Cell = known.Cell, X0 = x0, Y0 = y0, W = w, H = h, Out = new float[w * h], In = new float[w * h] };
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    patch.Out[j * w + i] = known.Out[(y0 + j) * known.W + x0 + i];
                    patch.In[j * w + i] = known.In[(y0 + j) * known.W + x0 + i];
                }
            }
            return patch;
        }

        // Is the point inside the collider? Only the shapes the physics engine can answer for (convex ones).
        private static bool Inside(Collider c, Vector3 point)
        {
            if (!c.bounds.Contains(point)) return false;
            return (c.ClosestPoint(point) - point).sqrMagnitude < 1e-9f;
        }

        // The colliders of the other armor plates of the vehicle that reach the marked plate (found once).
        internal static List<Collider> NeighborsOf(PlateVisual plate)
        {
            if (plate.Neighbors != null) return plate.Neighbors;
            List<Collider> found = new List<Collider>();
            plate.Neighbors = found;

            PartScript part = plate.Info.Part;
            AircraftScript aircraft = plate.Owner.Aircraft;
            if (part == null || aircraft == null) return found;

            Bounds own = new Bounds();
            bool any = false;
            foreach (Collider c in part.GetComponentsInChildren<Collider>())
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (!any) { own = c.bounds; any = true; }
                else own.Encapsulate(c.bounds);
            }
            if (!any) return found;
            own.Expand(0.1f);

            foreach (PartData data in aircraft.Parts)
            {
                PartScript other = data != null ? data.PartScript : null;
                if (other == null || other == part || TagCodec.Parse(data.Name).Armor == null) continue;
                foreach (Collider c in other.GetComponentsInChildren<Collider>())
                {
                    if (c == null || c.isTrigger || !c.enabled || !c.bounds.Intersects(own)) continue;
                    MeshCollider mesh = c as MeshCollider;
                    if (mesh != null && !mesh.convex) continue;   // the physics engine cannot answer "inside?" for a concave mesh
                    found.Add(c);
                }
            }
            if (found.Count > 0)
                ArmorFragmentsPlugin.Log.LogInfo("[DMAP] plate '" + part.Part.Name + "' overlaps or touches " + found.Count +
                                                 " collider(s) of other armor plates: its marks are kept from going through them");
            return found;
        }
    }
}
