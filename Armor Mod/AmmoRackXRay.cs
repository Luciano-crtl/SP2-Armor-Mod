using System;
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArmorFragments
{
    // The X-ray view (your vehicle with the F8 key, or the parts around a simulated shot) also shows the
    // rounds still in each of YOUR ammo racks: the same cylinders as in the designer, inside the rack's
    // see-through ghost. They follow the rack while the view is on: rounds vanish from the top layer down
    // as the cannon fires, a burning rack's cases glow orange, an exploded or emptied rack shows none.
    //
    // Only racks of the aircraft this game owns: other players' racks live in their game (not synced).
    internal static class AmmoRackXRay
    {
        private sealed class Shown
        {
            public PartScript Part;
            public GameObject Object;
            public Mesh Mesh;
            public int Remaining;
            public bool Burning;
        }

        private static readonly List<Shown> Racks = new List<Shown>();
        private static float nextCheck;
        private const float CheckInterval = 0.2f;
        private static bool warned;

        // Called by XRayView once its ghosts are built.
        public static void OnShown(IEnumerable<PartScript> parts)
        {
            Clear();
            try
            {
                if (!ArmorFragmentsPlugin.AmmoRacksEnabled.Value || !RackVisualizer.Materials()) return;
                foreach (PartScript part in parts)
                {
                    RackGrid grid;
                    bool burning;
                    if (part == null || !AmmoRacks.LiveRack(part, out grid, out burning)) continue;
                    Shown shown = new Shown { Part = part };
                    Build(shown, grid, burning);
                    Racks.Add(shown);
                }
                if (Racks.Count > 0)
                    ArmorFragmentsPlugin.Log.LogInfo("X-ray view shows the rounds of " + Racks.Count + " ammo rack(s)");
            }
            catch (Exception e)
            {
                Warn(e);
            }
        }

        // Called by XRayView.Hide.
        public static void Clear()
        {
            foreach (Shown shown in Racks) Destroy(shown);
            Racks.Clear();
        }

        // Every frame: follow the racks while the view is on.
        public static void Tick()
        {
            if (Racks.Count == 0) return;
            if (!XRayView.IsOn)
            {
                Clear();
                return;
            }
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + CheckInterval;

            try
            {
                foreach (Shown shown in Racks)
                {
                    RackGrid grid;
                    bool burning;
                    if (shown.Part == null || !AmmoRacks.LiveRack(shown.Part, out grid, out burning))
                    {
                        Destroy(shown);
                        continue;
                    }
                    if (grid.Remaining == shown.Remaining && burning == shown.Burning) continue;
                    Destroy(shown);
                    Build(shown, grid, burning);
                }
            }
            catch (Exception e)
            {
                Warn(e);
            }
        }

        private static void Build(Shown shown, RackGrid grid, bool burning)
        {
            shown.Remaining = grid.Remaining;
            shown.Burning = burning;
            if (grid.Remaining <= 0) return;

            shown.Mesh = RackVisualizer.BuildMesh(grid);
            shown.Object = RackVisualizer.MakeRoundsObject(shown.Part, shown.Mesh, LayerOf(shown.Part), burning);
        }

        // The layer the part is drawn on (the X-ray ghosts copy it, so the main camera sees them).
        private static int LayerOf(PartScript part)
        {
            foreach (MeshRenderer renderer in XRayView.GhostSources(part))
                if (renderer != null) return renderer.gameObject.layer;
            return part.gameObject.layer;
        }

        private static void Destroy(Shown shown)
        {
            if (shown.Object != null) Object.Destroy(shown.Object);
            if (shown.Mesh != null) Object.Destroy(shown.Mesh);
            shown.Object = null;
            shown.Mesh = null;
        }

        private static void Warn(Exception e)
        {
            if (!warned) ArmorFragmentsPlugin.Log.LogWarning("Ammo racks in the X-ray view failed: " + e);
            warned = true;
        }
    }
}
