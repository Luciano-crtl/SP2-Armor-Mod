using System.Collections.Generic;
using UnityEngine;

namespace ArmorFragments
{
    // THE TRANSITION from the maps to the geometry. A marked plate that has been rewritten as real geometry (PlateMesher) shows its relief
    // in two ways, and this decides how much of each, every frame, from where the cameras are:
    //
    //   maps (normal map and/or bump map)  <------ blend ------>  geometry (the vertices pushed by the damage)
    //
    // Looking at a dent head-on, its depth cannot be seen: the maps are enough. The geometry matters when it is seen from the side and
    // close up, where the dent's edge and its silhouette stand out against what is behind and the parallax of a bump map breaks down. So
    // how much of the geometry is shown follows the SCREEN-SPACE ERROR the maps would make:
    //
    //   error in pixels = the biggest push x tan(angle between the view and the plate's normal) x pixels per meter at that distance
    //
    // weighted by how grazing the view is (the angle gate), and taken for the camera that sees it worst (the main one and the impact
    // camera). Below 'start' pixels the maps are used, above 'full' the geometry; in between they are mixed, and the blend moves toward
    // that goal at a limited speed, so a camera swinging round a plate sees the dent grow out of the surface instead of popping.
    internal static class GeometryView
    {
        private static int round;
        private static readonly List<PlateVisual> Plates = new List<PlateVisual>();

        // 0..1: how much of the geometry this plate should show now. 'maxHeight' is the plate's biggest push (meters).
        public static float Need(PlateVisual plate, float maxHeight)
        {
            if (maxHeight < 0.0005f) return 0f;   // under half a millimeter there is nothing to see
            Renderer renderer = plate.Info.Map.Renderer;
            if (renderer == null) return 0f;

            Bounds bounds = renderer.bounds;
            Vector3 normal = plate.Info.Part.transform.TransformDirection(plate.Info.Geometry.N);

            float error = Error(MainCamera(), bounds, normal, maxHeight);
            float impact = Error(ImpactCamera.ActiveCamera, bounds, normal, maxHeight);
            if (impact > error) error = impact;

            float start = ArmorFragmentsPlugin.GeometryStartPixels.Value;
            float full = Mathf.Max(ArmorFragmentsPlugin.GeometryFullPixels.Value, start + 0.01f);
            float t = Mathf.Clamp01((error - start) / (full - start));
            return t * t * (3f - 2f * t);
        }

        private static Camera MainCamera()
        {
            Assets.Scripts.Flight.Cameras.CameraManagerScript manager = Assets.Scripts.Flight.Cameras.CameraManagerScript.Instance;
            return manager != null && manager.MainCamera != null ? manager.MainCamera : Camera.main;
        }

        // What the maps would get wrong, in pixels, for one camera (0 if it does not look at the plate grazingly enough to matter).
        private static float Error(Camera camera, Bounds bounds, Vector3 normal, float maxHeight)
        {
            if (camera == null || !camera.isActiveAndEnabled) return 0f;

            Vector3 eye = camera.transform.position;
            Vector3 nearest = bounds.ClosestPoint(eye);
            Vector3 toCamera = eye - nearest;
            float distance = Mathf.Max(toCamera.magnitude, 0.2f);
            float cos = Mathf.Abs(Vector3.Dot(normal, toCamera / Mathf.Max(toCamera.magnitude, 0.0001f)));
            cos = Mathf.Clamp(cos, 0f, 1f);

            // The angle gate: nothing below 'angle start' degrees from the normal (the maps do well there), all of it from 'angle full'.
            float angle = Mathf.Acos(cos) * Mathf.Rad2Deg;
            float lo = ArmorFragmentsPlugin.GeometryAngleStart.Value, hi = Mathf.Max(ArmorFragmentsPlugin.GeometryAngleFull.Value, lo + 0.1f);
            float gate = Mathf.Clamp01((angle - lo) / (hi - lo));
            if (gate <= 0f) return 0f;

            float tan = Mathf.Min(Mathf.Sqrt(Mathf.Max(1f - cos * cos, 0f)) / Mathf.Max(cos, 0.1f), 8f);
            float heightPixels = camera.targetTexture != null ? camera.targetTexture.height : camera.pixelHeight;
            float pixelsPerMeter = heightPixels / (2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            return maxHeight * tan * pixelsPerMeter * gate;
        }

        // Every frame: each plate with geometry moves toward what it needs, at a limited speed, a few plates a frame (the ones that are
        // furthest from their goal first would be ideal; going round them is simple and nothing starves).
        public static void Tick(ICollection<VehicleMaps> vehicles)
        {
            if (!ArmorFragmentsPlugin.DamageMapsGeometry.Value) return;

            Plates.Clear();
            foreach (VehicleMaps maps in vehicles)
            {
                if (maps.Disposed) continue;
                foreach (PlateVisual plate in maps.Plates.Values)
                    if (plate.HasGeometry) Plates.Add(plate);
            }
            if (Plates.Count == 0) return;

            float now = Time.unscaledTime;
            float seconds = Mathf.Max(ArmorFragmentsPlugin.GeometryBlendSeconds.Value, 0.02f);
            int budget = 4;
            for (int k = 0; k < Plates.Count && budget > 0; k++)
            {
                PlateVisual plate = Plates[(round + k) % Plates.Count];
                float target = Need(plate, plate.MaxHeight);
                float step = Mathf.Max(now - plate.BlendTime, 0f) / seconds;
                float next = Mathf.MoveTowards(plate.Blend, target, step);
                if (Mathf.Abs(next - plate.Blend) < 0.01f && !(next == target && next != plate.Blend)) continue;

                plate.SetBlend(next, now);
                budget--;
            }
            round++;
        }
    }
}
