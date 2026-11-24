// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace ArmorFragments
{
    // The ground and the things standing on it, for the smoke. Until now the ground was a flat plane under the muzzle and only vehicle parts
    // were solid, so smoke went through hills and walls. Now:
    //  - the ground is a height map: a grid of rays cast down over the box's floor finds terrain (a TerrainCollider, or a mesh that is not
    //    convex: roads, islands, hills) and the box gets solid cells under that surface (Fluid.compute, Obstacles);
    //  - boxes, spheres, capsules and convex meshes that are not vehicle parts and do not move (walls, buildings, rocks, trees if they have
    //    such colliders) are solid shapes like the parts are.
    internal static partial class FluidSmoke
    {
        public static bool WorldSolids { get { return ArmorFragmentsPlugin.SmokeTerrain == null || ArmorFragmentsPlugin.SmokeTerrain.Value; } }

        private const int GroundN = 24;                  // the height map has this many samples along each side of the box's floor
        private const int GroundHits = 4;
        private static NativeArray<RaycastCommand> groundCommands;
        private static NativeArray<RaycastHit> groundResults;
        private static readonly float[] GroundHeights = new float[GroundN * GroundN];
        private static readonly Dictionary<int, bool> TerrainKinds = new Dictionary<int, bool>();
        private static int groundLogged;
        private static readonly int NotVehicles = ~((1 << 21) | (1 << 13));        // the layers of vehicle parts: aircraft and cars

        // Is this collider ground (terrain or a mesh that is not convex), as opposed to a thing standing on it?
        private static bool IsGroundCollider(Collider col)
        {
            if (col == null || col.isTrigger) return false;
            if (col is TerrainCollider) return true;
            MeshCollider mesh = col as MeshCollider;
            if (mesh == null || mesh.convex) return false;
            return true;
        }

        // A thing standing on the ground that should stop the gas: not a vehicle part, not a projectile, not ground, not moving.
        private static bool IsWorldShape(Collider col)
        {
            if (col == null || col.isTrigger || col.attachedRigidbody != null || IsGroundCollider(col)) return false;
            if (!(col is BoxCollider || col is SphereCollider || col is CapsuleCollider || col is MeshCollider)) return false;
            return true;
        }

        // Casts the grid of rays and gives the height map to the cloud. Called when it is due (about once a second, more often for the bubble).
        private static void RefreshGround(Cloud c)
        {
            if (!WorldSolids) { if (c.useHeight) { c.sim.SetGround(null, 0, 0, 0, Vector3.zero); c.useHeight = false; } return; }
            if (Time.time < c.groundAt) return;
            c.groundAt = Time.time + 1.5f;
            if (!groundCommands.IsCreated)
            {
                groundCommands = new NativeArray<RaycastCommand>(GroundN * GroundN, Allocator.Persistent);
                groundResults = new NativeArray<RaycastHit>(GroundN * GroundN * GroundHits, Allocator.Persistent);
            }
            Vector3 down = c.rotation * Vector3.down;
            QueryParameters query = new QueryParameters(NotVehicles, false, QueryTriggerInteraction.Ignore, false);
            float reach = c.extent.y + 2f;
            for (int j = 0; j < GroundN; j++)
                for (int i = 0; i < GroundN; i++)
                {
                    Vector3 local = new Vector3((i + 0.5f) / GroundN * c.extent.x, c.extent.y + 1f, (j + 0.5f) / GroundN * c.extent.z);
                    groundCommands[j * GroundN + i] = new RaycastCommand(c.origin + c.rotation * local, down, query, reach);
                }
            RaycastCommand.ScheduleBatch(groundCommands, groundResults, 32, GroundHits).Complete();

            int found = 0;
            for (int k = 0; k < GroundN * GroundN; k++)
            {
                float best = float.MaxValue;
                for (int h = 0; h < GroundHits; h++)
                {
                    RaycastHit hit = groundResults[k * GroundHits + h];
                    if (hit.collider == null) break;
                    if (hit.distance >= best) continue;
                    bool ground;
                    int id = hit.collider.GetInstanceID();
                    if (!TerrainKinds.TryGetValue(id, out ground))
                    {
                        ground = IsGroundCollider(hit.collider) && hit.collider.GetComponentInParent<PartScript>() == null && hit.collider.GetComponentInParent<CannonProjectileScript>() == null;
                        if (TerrainKinds.Count > 2000) TerrainKinds.Clear();
                        TerrainKinds[id] = ground;
                    }
                    if (ground) best = hit.distance;
                }
                if (best < float.MaxValue) { GroundHeights[k] = c.extent.y + 1f - best; found++; }
                else GroundHeights[k] = -100f;
            }
            bool use = found >= GroundN * GroundN * 6 / 10;
            if (use)
            {
                // Holes in the map (the ray hit nothing that counts) take the height of the lowest sample, so they do not become pits of air.
                float lowest = float.MaxValue;
                for (int k = 0; k < GroundHeights.Length; k++) if (GroundHeights[k] > -50f && GroundHeights[k] < lowest) lowest = GroundHeights[k];
                for (int k = 0; k < GroundHeights.Length; k++) if (GroundHeights[k] <= -50f) GroundHeights[k] = lowest;
                c.sim.SetGround(GroundHeights, GroundN, c.extent.x, c.extent.z, Quaternion.Inverse(c.rotation) * (-c.drift));
            }
            else if (c.useHeight) c.sim.SetGround(null, 0, 0, 0, Vector3.zero);
            if (use != c.useHeight && groundLogged < 6)
            {
                groundLogged++;
                ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] ground: " + (use ? "a height map of the terrain under the box is used (" + found + " of " + (GroundN * GroundN) + " rays found it)" : "no terrain under the box that counts (" + found + " of " + (GroundN * GroundN) + " rays), a flat ground is used"));
            }
            c.useHeight = use;
        }

        private static void ShutdownTerrain()
        {
            if (groundCommands.IsCreated) groundCommands.Dispose();
            if (groundResults.IsCreated) groundResults.Dispose();
            TerrainKinds.Clear();
            groundLogged = 0;
        }
    }
}
// RELEASE-STRIP-END
