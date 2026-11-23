// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Flight;
using ArmorFragments.Fluid;
using UnityEngine;

namespace ArmorFragments
{
    // A small box of fluid that follows the player's vehicle, so that smoke the vehicle drives through (or drags along behind it) is not left
    // behind in the box of the shot that made it. While a cloud's box reaches the player, the bubble takes the smoke that lies inside it (and
    // the cloud loses it); from then on that smoke lives in the bubble and travels with the vehicle. The smoke outside the bubble stays where it
    // was, as it should. The bubble is a cloud like the others (its own box, shadow, solids of the vehicle, wake, wind), aligned with the world
    // and moved in whole cells (FluidSim.ShiftBy), so nothing is resampled while it follows. It exists only while there is smoke to take.
    internal static partial class FluidSmoke
    {
        private static Cloud bubble;
        private static float bubbleGroundAt;
        private static int bubbleLogged;
        private static readonly List<Cloud> Reaching = new List<Cloud>();

        public static bool BubbleEnabled { get { return ArmorFragmentsPlugin.SmokePlayerBubble == null || ArmorFragmentsPlugin.SmokePlayerBubble.Value; } }
        public static float BubbleSize { get { return ArmorFragmentsPlugin.SmokePlayerBubbleSize != null ? Mathf.Clamp(ArmorFragmentsPlugin.SmokePlayerBubbleSize.Value, 6f, 30f) : 12f; } }

        public static string BubbleStatus
        {
            get
            {
                if (!BubbleEnabled) return "off";
                if (bubble == null) return "none now (it appears when a cloud reaches your vehicle)";
                return bubble.sim != null ? bubble.sim.Dims.x + "x" + bubble.sim.Dims.y + "x" + bubble.sim.Dims.z + " cells of " + (bubble.sim.Cell * 100f).ToString("F0") + " cm, lives " + Mathf.Max(bubble.end - bubble.age, 0f).ToString("F1") + " s more" : "starting";
            }
        }

        private static bool PlayerVehicle(out Vector3 center)
        {
            center = Vector3.zero;
            FlightSceneScript scene = FlightSceneScript.Instance;
            AircraftScript aircraft = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
            if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight || aircraft.MainCockpit == null) return false;
            center = aircraft.MainCockpit.transform.position;
            return !float.IsNaN(center.x) && !float.IsNaN(center.y) && !float.IsNaN(center.z);
        }

        // Does a sphere around `center` touch the cloud's box?
        private static bool Reaches(Cloud c, Vector3 center, float radius)
        {
            Vector3 local = Quaternion.Inverse(c.rotation) * (center - c.origin);
            Vector3 closest = new Vector3(Mathf.Clamp(local.x, 0f, c.extent.x), Mathf.Clamp(local.y, 0f, c.extent.y), Mathf.Clamp(local.z, 0f, c.extent.z));
            return (local - closest).sqrMagnitude < radius * radius;
        }

        // Where the corner of a box of this extent and cell is when it is centred on `center`, on the lattice of whole cells of the world.
        private static Vector3 BubbleOrigin(Vector3 center, Vector3 extent, float cell)
        {
            Vector3 corner = center - extent * 0.5f;
            return new Vector3(Mathf.Round(corner.x / cell) * cell, Mathf.Round(corner.y / cell) * cell, Mathf.Round(corner.z / cell) * cell);
        }

        // Called every frame before the clouds are stepped.
        private static void UpdateBubble(float frame)
        {
            if (!BubbleEnabled) { DropBubble("it was switched off"); return; }
            Vector3 center;
            if (!PlayerVehicle(out center)) { DropBubble("there is no vehicle of yours in flight"); return; }

            float reach = BubbleSize * 0.9f;       // a little more than half the diagonal of the bubble
            Reaching.Clear();
            foreach (Cloud c in Active)
                if (c != bubble && c.sim != null && c.age > 0.05f && Reaches(c, center, reach)) Reaching.Add(c);

            if (bubble == null)
            {
                if (Reaching.Count == 0 || Active.Count >= MaxActive + 1) return;
                MakeBubble(Reaching[0], center);
                if (bubble == null) return;
            }

            Cloud b = bubble;
            float h = b.sim.Cell;
            // Follow the vehicle: move the window in whole cells once it is two or more cells off.
            Vector3 desired = BubbleOrigin(center, b.extent, h);
            Vector3 off = (desired - b.origin) / h;
            Vector3Int n = new Vector3Int(Mathf.RoundToInt(off.x), Mathf.RoundToInt(off.y), Mathf.RoundToInt(off.z));
            int biggest = Mathf.Max(Mathf.Abs(n.x), Mathf.Max(Mathf.Abs(n.y), Mathf.Abs(n.z)));
            if (biggest >= b.sim.Dims.x / 2) { b.sim.Clear(); b.origin = desired; }          // it jumped (a teleport, a respawn): start over
            else if (biggest >= 2) { b.sim.ShiftBy(n); b.origin += new Vector3(n.x, n.y, n.z) * h; }

            // The ground under the vehicle.
            if (Time.time >= bubbleGroundAt)
            {
                bubbleGroundAt = Time.time + 0.2f;
                RaycastHit hit;
                b.hasGround = FindGround(center, null, out hit);
                if (b.hasGround)
                {
                    b.groundPoint = hit.point;
                    b.groundNormal = hit.normal.sqrMagnitude > 0.5f ? hit.normal.normalized : Vector3.up;
                }
            }

            // Take the smoke of every cloud that reaches it.
            Matrix4x4 dest = Matrix4x4.TRS(b.origin, b.rotation, Vector3.one);
            float margin = Mathf.Max(b.sim.Params.edgeMeters, h) + 0.5f;
            foreach (Cloud c in Reaching)
            {
                Matrix4x4 source = Matrix4x4.TRS(c.origin, c.rotation, Vector3.one);
                b.sim.CaptureFrom(c.sim, source.inverse * dest, dest.inverse * source, Matrix4x4.Rotate(Quaternion.Inverse(b.rotation) * c.rotation), margin);
                float remaining = Mathf.Max(c.end - c.age, 0f);
                if (b.age + remaining > b.end)
                {
                    b.end = b.age + remaining;
                    if (c.scale > b.scale)
                    {
                        b.scale = c.scale; b.timeScale = c.timeScale;
                        b.material.SetFloat("_NoiseScale", 1.1f / b.scale);
                    }
                }
                if (c.material != null) b.material.SetColor("_DustColor", c.material.GetColor("_DustColor"));
            }
            Place(b);
        }

        private static void MakeBubble(Cloud first, Vector3 center)
        {
            try
            {
                ComputeShader cs = VfxBundleTest.Compute("fluid");
                Material template = VfxBundleTest.Material("smokevolume");
                if (cs == null || template == null) return;
                int level = LevelFor(0f);
                QualityLevel q = Levels[level];
                float size = BubbleSize;
                Vector3 target = new Vector3(size, size * 0.6f, size);
                float h = BaseCell * q.cellMultiplier * 1.2f;
                Vector3Int d;
                while (true)
                {
                    d = new Vector3Int(RoundUp8(target.x / h), RoundUp8(target.y / h), RoundUp8(target.z / h));
                    if ((long)d.x * d.y * d.z <= CellBudget[level] / 2) break;
                    h *= 1.06f;
                }
                Vector3 extent = new Vector3(d.x, d.y, d.z) * h;

                Stack<FluidSim> pool;
                FluidSim sim = Pools.TryGetValue(d, out pool) && pool.Count > 0 ? pool.Pop() : new FluidSim(cs, d.x, d.y, d.z, h);
                sim.Cell = h;
                sim.Clear();
                sim.Params.cycles = q.cycles;
                sim.Params.edgeMeters = 0.8f;
                sim.CoarseSweeps = 10;

                Cloud b = new Cloud
                {
                    isBubble = true, level = level, sim = sim, origin = BubbleOrigin(center, extent, h), rotation = Quaternion.identity, extent = extent,
                    scale = first.scale, timeScale = first.timeScale,
                    age = 100f, end = 100f + Mathf.Max(first.end - first.age, 0f),       // its age is large: the blast's hard brake is long over
                };
                b.material = new Material(template) { name = "ArmorFragments fluid smoke (player)" };
                b.material.SetFloat("_UseDepth", DepthTextureAvailable() ? 1f : 0f);
                b.material.SetFloat("_Extinction", 8f);
                b.material.SetFloat("_Erosion", 0.55f);
                b.material.SetFloat("_NoiseScale", 1.1f / b.scale);
                b.material.SetFloat("_UsePhi", 1f);
                b.material.SetColor("_DustColor", first.material != null ? first.material.GetColor("_DustColor") : new Color(0.62f, 0.50f, 0.38f, 1f));
                b.material.SetVector("_GridExtent", new Vector4(extent.x, extent.y, extent.z, 0f));
                if (boxMesh == null) boxMesh = MakeBox();
                b.box = new GameObject("ArmorFragments fluid smoke (player)");
                b.box.AddComponent<MeshFilter>().sharedMesh = boxMesh;
                MeshRenderer renderer = b.box.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = b.material;
                renderer.shadowCastingMode = SmokeShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                b.boxRenderer = renderer;
                renderer.receiveShadows = false;
                VfxBundleTest.SetLayer(b.box, ImpactCamera.EffectsLayer);
                Place(b);
                bubbleGroundAt = 0f;
                Active.Add(b);
                bubble = b;
                EnsureDriver();
                if (bubbleLogged < 6)
                {
                    bubbleLogged++;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] player bubble: made, " + extent.x.ToString("F1") + " x " + extent.y.ToString("F1") + " x " + extent.z.ToString("F1") + " m, "
                        + d.x + " x " + d.y + " x " + d.z + " cells of " + (h * 100f).ToString("F1") + " cm, a cloud of the " + first.scale.ToString("F1") + "x blast reaches the vehicle");
                }
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] player bubble failed and is off: " + e);
                if (ArmorFragmentsPlugin.SmokePlayerBubble != null) ArmorFragmentsPlugin.SmokePlayerBubble.Value = false;
                if (bubble != null) DropBubble("it failed");
            }
        }

        private static void DropBubble(string why)
        {
            if (bubble == null) return;
            Cloud b = bubble;
            bubble = null;
            Active.Remove(b);
            Retire(b);
            if (bubbleLogged < 12) { bubbleLogged++; ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] player bubble: dropped, " + why); }
        }
    }
}
// RELEASE-STRIP-END
