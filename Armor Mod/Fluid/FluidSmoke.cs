// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using ArmorFragments.Fluid;
using Assets.Scripts.Environment.Wind;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    // The smoke of a cannon shot as a real fluid: every shot starts a small GPU simulation of the hot gas bursting out of the barrel
    // (FluidSim, the compute shader Fluid.compute in the effect bundle). It is drawn by the ray-marching shader (SmokeVolume) on a box
    // that lies along the barrel. The simulation lives about 4 seconds, then its memory goes back to a pool.
    //
    // The box is fixed to the world where the shot happened (the vehicle drives away from the smoke) and drifts with the wind there.
    internal static class FluidSmoke
    {
        public static bool Enabled = true;
        public static int MaxActive = 3;               // simulations running at once (each costs about a millisecond a frame)
        public static float MinInterval = 0.5f;        // seconds between the clouds of one cannon
        public static float Lifetime = 4f;
        public static float FadeSeconds = 0.8f;
        public static float MaxCameraDistance = 400f;  // farther than this: no cloud

        // The box: 96 x 56 x 56 cells; the barrel's axis is its +x, "up" its +y.
        private const int Nx = 96, Ny = 56, Nz = 56;
        private const float BaseCell = 0.125f;

        private sealed class Cloud
        {
            public FluidSim sim;
            public GameObject box;
            public Material material;
            public Vector3 origin;          // world position of the box corner
            public Quaternion rotation;     // box axes -> world
            public Vector3 extent;
            public Vector3 muzzle;          // inside the box
            public float scale;
            public float age;
        }

        private static readonly List<Cloud> Active = new List<Cloud>();
        private static readonly Stack<FluidSim> Pool = new Stack<FluidSim>();
        private static readonly Dictionary<int, float> LastShot = new Dictionary<int, float>();
        private static Mesh boxMesh;
        private static Driver driver;
        private static bool loggedFirst;

        public static void Toggle()
        {
            Enabled = !Enabled;
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid muzzle smoke " + (Enabled ? "on" : "off"));
        }

        public static void OnShot(CannonScript cannon)
        {
            if (!Enabled || cannon == null) return;
            try
            {
                if (Active.Count >= MaxActive) return;
                int id = cannon.GetInstanceID();
                float now = Time.time, last;
                if (LastShot.TryGetValue(id, out last) && now - last < MinInterval) return;

                ComputeShader cs = VfxBundleTest.Compute("fluid");
                Material template = VfxBundleTest.Material("smokevolume");
                if (cs == null || template == null || !SystemInfo.supportsComputeShaders) return;
                Camera cam = Camera.main;
                Vector3 tip = cannon.TipPosition;
                if (cam != null && (cam.transform.position - tip).sqrMagnitude > MaxCameraDistance * MaxCameraDistance) return;
                LastShot[id] = now;

                float caliber = MuzzleSmoke.CaliberMillimeters(cannon);
                float scale = Mathf.Clamp(caliber / 75f, 0.5f, 2f);
                float cell = BaseCell * scale;

                // The box's axes: x along the barrel, y as close to "up" as it can be, z across.
                Vector3 ex = cannon.transform.forward.normalized;
                Vector3 ey = Vector3.up - Vector3.Dot(Vector3.up, ex) * ex;
                if (ey.sqrMagnitude < 1e-4f) ey = Vector3.forward - Vector3.Dot(Vector3.forward, ex) * ex;
                ey.Normalize();
                Vector3 ez = Vector3.Cross(ex, ey);
                Quaternion rot = Quaternion.LookRotation(ez, ey);

                Vector3 extent = new Vector3(Nx, Ny, Nz) * cell;
                Vector3 muzzleLocal = new Vector3(1.6f * scale, extent.y * 0.5f, extent.z * 0.5f);
                Vector3 origin = tip - rot * muzzleLocal;

                FluidSim sim = Pool.Count > 0 ? Pool.Pop() : new FluidSim(cs, Nx, Ny, Nz, cell);
                if (Mathf.Abs(sim.Cell - cell) > 1e-5f) { sim.Dispose(); sim = new FluidSim(cs, Nx, Ny, Nz, cell); }
                else sim.Clear();

                Cloud cloud = new Cloud { sim = sim, origin = origin, rotation = rot, extent = extent, muzzle = muzzleLocal, scale = scale };
                cloud.material = new Material(template) { name = "ArmorFragments fluid smoke" };
                cloud.material.SetFloat("_UseDepth", SmokeVolume.DepthTextureAvailable() ? 1f : 0f);
                cloud.material.SetFloat("_Extinction", 8f);
                cloud.material.SetFloat("_Erosion", 0.5f);
                cloud.material.SetFloat("_NoiseScale", 0.9f / scale);
                cloud.material.SetVector("_GridExtent", new Vector4(extent.x, extent.y, extent.z, 0f));
                cloud.material.SetVector("_NoiseOffset", Vector4.zero);

                if (boxMesh == null) boxMesh = MakeBox();
                cloud.box = new GameObject("ArmorFragments fluid smoke");
                cloud.box.AddComponent<MeshFilter>().sharedMesh = boxMesh;
                MeshRenderer renderer = cloud.box.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = cloud.material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                VfxBundleTest.SetLayer(cloud.box, ImpactCamera.EffectsLayer);
                Place(cloud);

                Active.Add(cloud);
                EnsureDriver();

                if (!loggedFirst)
                {
                    loggedFirst = true;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] first fluid smoke: caliber " + caliber.ToString("F0") + " mm, box "
                        + extent.x.ToString("F1") + " x " + extent.y.ToString("F1") + " x " + extent.z.ToString("F1") + " m, cells of "
                        + (cell * 100f).ToString("F1") + " cm, " + (Nx * Ny * Nz) + " cells");
                }
            }
            catch (System.Exception e)
            {
                Enabled = false;
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] fluid smoke failed and is now off: " + e);
            }
        }

        // Puts the box, and the matrix that takes world points into the box's own space, where the cloud is.
        private static void Place(Cloud c)
        {
            c.box.transform.SetPositionAndRotation(c.origin + c.rotation * (c.extent * 0.5f), c.rotation);
            c.box.transform.localScale = c.extent;
            c.material.SetMatrix("_WorldToGrid", Matrix4x4.TRS(c.origin, c.rotation, Vector3.one).inverse);
        }

        // Called every frame while a cloud lives.
        private static void Tick()
        {
            float frame = Mathf.Min(Time.deltaTime, 0.03f);
            WindQueryManager wind = WindQueryManager.Instance;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Cloud c = Active[i];
                try
                {
                    if (c.age >= Lifetime || c.box == null) { Retire(c); Active.RemoveAt(i); continue; }
                    if (frame > 0f)
                    {
                        // Gravity in the box's axes; the blast slows fast, then the smoke drifts.
                        Quaternion inverse = Quaternion.Inverse(c.rotation);
                        c.sim.Params.buoyDir = inverse * Vector3.up;
                        c.sim.Params.drag = c.age < 0.3f ? 7f : 1.5f;

                        int substeps = c.age < 0.3f ? Mathf.Clamp(Mathf.CeilToInt(frame / 0.006f), 1, 3) : 1;
                        float dt = frame / substeps;
                        for (int s = 0; s < substeps; s++)
                        {
                            c.sim.AddBlobs(Blobs(c, c.age + dt * s));
                            c.sim.Step(dt);
                        }
                        c.age += frame;

                        // The wind carries the whole box.
                        if (wind != null)
                        {
                            Vector3 center = c.origin + c.rotation * (c.extent * 0.5f);
                            c.origin += wind.QueryWindVelocity(center) * frame;
                            Place(c);
                        }
                    }
                    c.material.SetTexture("_Grid", c.sim.Scalars);
                    float fade = Mathf.Clamp01((Lifetime - c.age) / FadeSeconds);
                    c.material.SetFloat("_Fade", fade);
                }
                catch (System.Exception e)
                {
                    Enabled = false;
                    ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] fluid smoke failed and is now off: " + e);
                    Retire(c);
                    Active.RemoveAt(i);
                }
            }
        }

        // The sources, in the box's axes: a hot fireball bursting forward, then the smoke that keeps coming out of the barrel.
        private static List<FluidSim.Blob> Blobs(Cloud c, float t)
        {
            List<FluidSim.Blob> list = new List<FluidSim.Blob>();
            float s = c.scale;
            if (t < 0.12f)
            {
                float fade = 1f - t / 0.12f;
                for (int k = 0; k < 4; k++)
                    list.Add(new FluidSim.Blob { center = c.muzzle + new Vector3((0.7f + 0.9f * k) * s, 0, 0), radius = (0.9f + 0.25f * k) * s, rho = 0.9f, temp = 1f,
                                                 vel = new Vector3(14f * fade, 0, 0), radial = 10f * fade, noise = 0.15f, falloff = 1.3f });
            }
            if (t < 0.8f)
            {
                float fade = 1f - t / 0.8f;
                list.Add(new FluidSim.Blob { center = c.muzzle + new Vector3(0.9f * s, 0, 0), radius = 1.3f * s, rho = 1.0f * fade, temp = 0.3f * fade,
                                             vel = new Vector3(4f * fade, 0, 0), radial = 1.0f, noise = 0.15f, falloff = 1.1f });
            }
            return list;
        }

        private static void Retire(Cloud c)
        {
            if (c.box != null) Object.Destroy(c.box);
            if (c.material != null) Object.Destroy(c.material);
            if (c.sim != null) Pool.Push(c.sim);
            c.sim = null;
        }

        public static void Shutdown()
        {
            foreach (Cloud c in Active)
            {
                if (c.box != null) Object.Destroy(c.box);
                if (c.material != null) Object.Destroy(c.material);
                if (c.sim != null) c.sim.Dispose();
            }
            Active.Clear();
            while (Pool.Count > 0) Pool.Pop().Dispose();
            LastShot.Clear();
            if (driver != null) Object.Destroy(driver.gameObject);
            driver = null;
            loggedFirst = false;
        }

        private static void EnsureDriver()
        {
            if (driver != null) return;
            GameObject go = new GameObject("ArmorFragments fluid smoke driver");
            driver = go.AddComponent<Driver>();
        }

        private sealed class Driver : MonoBehaviour
        {
            private void Update() { FluidSmoke.Tick(); }
        }

        private static Mesh MakeBox()
        {
            Mesh mesh = new Mesh { name = "ArmorFragments unit box" };
            mesh.vertices = new[]
            {
                new Vector3(-.5f, -.5f, -.5f), new Vector3(.5f, -.5f, -.5f), new Vector3(.5f, .5f, -.5f), new Vector3(-.5f, .5f, -.5f),
                new Vector3(-.5f, -.5f, .5f), new Vector3(.5f, -.5f, .5f), new Vector3(.5f, .5f, .5f), new Vector3(-.5f, .5f, .5f),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5, 3, 7, 6, 3, 6, 2, 0, 1, 5, 0, 5, 4,
            };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            return mesh;
        }
    }
}
// RELEASE-STRIP-END
