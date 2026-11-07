using System;
using UnityEngine;

namespace ArmorFragments
{
    // Fakes a "rim light" on an X-ray ghost without a custom shader: vertex colors are
    // computed from the angle between the surface and the camera. Surfaces seen edge-on get the
    // bright rim color and more opacity; surfaces facing the camera stay mostly transparent.
    //
    // This is plain CPU work, so it is kept cheap:
    //  - nothing is recomputed while the camera stays (nearly) still;
    //  - when it has to be recomputed, the work is split across frames: XRayView gives each
    //    frame a limited number of vertices to process (see Step);
    //  - the math avoids Vector3 operators and Mathf.Pow, and the mesh gets compact Color32.
    // The mesh is the ghost's OWN copy, so the game's real meshes are never touched.
    internal sealed class RimGhost
    {
        public readonly Transform Transform;
        public readonly Mesh Mesh;

        private readonly Vector3[] vertices;
        private readonly Vector3[] normals;
        private readonly Color32[] colors;
        private readonly int count;

        // Body and rim colors as 0..255 floats: r, g, b, a.
        private float br, bg, bb, ba;
        private float rr, rg, rb, ra;

        private readonly float moveThresholdSqr;

        private bool passActive;
        private int cursor;                  // next vertex of the pass in progress
        private float cx, cy, cz;            // camera position (in the ghost's space) for this pass
        private bool hasLast;
        private bool restartPass;            // the look changed during a pass: do it again
        private Vector3 lastCamera;

        public int VertexCount
        {
            get { return count; }
        }

        public RimGhost(Transform transform, Mesh copy, Color body, float bodyAlpha, Color rim, float rimAlpha)
        {
            Transform = transform;
            Mesh = copy;

            vertices = copy.vertices;
            normals = copy.normals;
            if (normals.Length != vertices.Length)
            {
                copy.RecalculateNormals();
                normals = copy.normals;
            }
            count = vertices.Length;

            SetColors(body, bodyAlpha, rim, rimAlpha);

            // Recalculate only when the camera has moved a bit relative to the part's size.
            float radius = copy.bounds.extents.magnitude;
            float threshold = Mathf.Max(0.05f, 0.03f * radius);
            moveThresholdSqr = threshold * threshold;

            // Until the first pass is done the ghost shows the flat body color.
            Color32 flat = new Color32((byte)br, (byte)bg, (byte)bb, (byte)ba);
            colors = new Color32[count];
            for (int i = 0; i < count; i++) colors[i] = flat;
            copy.MarkDynamic();   // it will be re-uploaded: tell Unity
            copy.colors32 = colors;
        }

        private void SetColors(Color body, float bodyAlpha, Color rim, float rimAlpha)
        {
            br = body.r * 255f; bg = body.g * 255f; bb = body.b * 255f; ba = bodyAlpha * 255f;
            rr = rim.r * 255f; rg = rim.g * 255f; rb = rim.b * 255f; ra = rimAlpha * 255f;
        }

        // Changes the colors (a part that has just been damaged). The mesh is recalculated with
        // them on the next frames, even if the camera does not move.
        public void SetLook(Color body, float bodyAlpha, Color rim, float rimAlpha)
        {
            SetColors(body, bodyAlpha, rim, rimAlpha);
            if (passActive) restartPass = true;
            else hasLast = false;
        }

        // True if this ghost has (more) work to do for a camera at 'cameraWorld'.
        // Starts a new pass when the camera has moved enough since the last finished one.
        public bool NeedsWork(Vector3 cameraWorld)
        {
            if (passActive) return true;

            Vector3 local = Transform.InverseTransformPoint(cameraWorld);
            if (hasLast && (local - lastCamera).sqrMagnitude < moveThresholdSqr) return false;

            cx = local.x; cy = local.y; cz = local.z;
            lastCamera = local;
            cursor = 0;
            passActive = true;
            return true;
        }

        // Processes up to 'budget' vertices and returns how many it did.
        // When the pass is finished the colors are uploaded to the GPU in one go.
        public int Step(int budget)
        {
            int end = Math.Min(count, cursor + budget);
            int done = end - cursor;

            for (int i = cursor; i < end; i++)
            {
                float dx = cx - vertices[i].x;
                float dy = cy - vertices[i].y;
                float dz = cz - vertices[i].z;
                float lengthSqr = dx * dx + dy * dy + dz * dz;

                // 1 where the surface faces the camera, 0 at the silhouette edge.
                float facing = 1f;
                if (lengthSqr > 0.0000001f)
                {
                    float dot = normals[i].x * dx + normals[i].y * dy + normals[i].z * dz;
                    facing = Math.Abs(dot) / (float)Math.Sqrt(lengthSqr);
                    if (facing > 1f) facing = 1f;
                }

                float rim = 1f - facing;
                rim *= rim;   // thin rim: squared is much cheaper than a general power

                colors[i] = new Color32(
                    (byte)(br + (rr - br) * rim),
                    (byte)(bg + (rg - bg) * rim),
                    (byte)(bb + (rb - bb) * rim),
                    (byte)(ba + (ra - ba) * rim));
            }

            cursor = end;
            if (cursor >= count && restartPass)
            {
                restartPass = false;   // the look changed while this pass ran: go around again
                cursor = 0;
            }
            else if (cursor >= count)
            {
                Mesh.colors32 = colors;
                passActive = false;
                hasLast = true;
                XRayStats.Passes++;
            }
            return done;
        }
    }
}
