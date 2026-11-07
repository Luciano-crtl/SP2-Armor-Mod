using UnityEngine;

namespace ArmorFragments
{
    // Numbers about the X-ray view's cost, shown in the panel ("X-ray performance"), so it is
    // possible to see what is expensive instead of guessing.
    internal static class XRayStats
    {
        // Whole-game frame time, smoothed. Updated every frame, X-ray on or not.
        public static float FrameMs;

        // Smoothed milliseconds per frame spent recalculating rim colors (0 when idle).
        public static float RimMs;

        // Last time Show() took to build the view.
        public static float BuildMs;

        public static int Ghosts;          // see-through copies in the world (= extra draw calls)
        public static int RimGhosts;       // of those, rim-lit (recalculated by the CPU)
        public static int FlatGhosts;      // of those, flat color (no CPU work after creation)
        public static int TotalVertices;   // vertices of all ghosts
        public static int RimVertices;     // vertices of rim-lit ghosts
        public static int Passes;          // finished rim recalculations since the view was shown

        public static void OnFrame()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            FrameMs = FrameMs <= 0f ? ms : Mathf.Lerp(FrameMs, ms, 0.05f);
        }

        public static void OnRimCost(float ms)
        {
            RimMs = Mathf.Lerp(RimMs, ms, 0.1f);
        }

        public static void ResetView()
        {
            Ghosts = 0;
            RimGhosts = 0;
            FlatGhosts = 0;
            TotalVertices = 0;
            RimVertices = 0;
            Passes = 0;
            RimMs = 0f;
        }
    }
}
