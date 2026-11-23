// RELEASE-STRIP-BEGIN
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace ArmorFragments
{
    // Draws the particles of a smoke effect as ONE continuous cloud instead of a heap of sprites.
    //
    // SmokeSim keeps moving the particles (wind, buoyancy, temperature). Here, every frame, each particle is splatted as a soft ball
    // into a small 3D grid (density and temperature) that covers the cloud. A ray-marching shader (ArmorMod/SmokeVolume in the effect
    // bundle) is drawn on a box that covers the grid; it reads the grid, carves puffy detail out of a 3D noise texture, lights the
    // cloud and makes the hot gas glow. The particle system's own renderer is switched off while this runs.
    [DefaultExecutionOrder(50)]   // after SmokeSim has moved the particles this frame
    internal sealed class SmokeVolume : MonoBehaviour
    {
        public static bool Enabled = true;
        public static int GridSize = 40;           // cells along each side of the grid
        public static float BallRadius = 0.62f;    // radius of a particle's ball, as a fraction of its size
        public static float DetailPerSize = 0.5f;  // noise detail: one noise tile = this many puff sizes

        public static void Toggle()
        {
            Enabled = !Enabled;
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] smoke volume " + (Enabled ? "on (one ray-marched cloud)" : "off (sprites)"));
        }

        private static Mesh boxMesh;
        private static bool depthChecked, depthAvailable;

        private ParticleSystem system;
        private ParticleSystemRenderer billboards;
        private ParticleSystem.Particle[] particles;
        private GameObject box;
        private Material material;
        private Texture3D grid;
        private byte[] bytes;
        private float[] density, temperatureSum, weightSum;
        private Vector3 noiseOffset;
        private bool built, failed, loggedFirst;
        private readonly Stopwatch watch = new Stopwatch();
        private double msTotal;
        private int frames;

        private void Awake()
        {
            system = GetComponent<ParticleSystem>();
            billboards = GetComponent<ParticleSystemRenderer>();
        }

        private void LateUpdate()
        {
            if (failed || system == null) return;
            try
            {
                if (!Enabled)
                {
                    Show(false);
                    return;
                }

                Material template = VfxBundleTest.Material("smokevolume");
                if (template == null) { Show(false); return; }   // no bundle material: the sprites stay

                int max = system.main.maxParticles;
                if (particles == null || particles.Length < max) particles = new ParticleSystem.Particle[max];
                int count = system.GetParticles(particles);
                if (count == 0)
                {
                    if (box != null) box.SetActive(false);
                    return;
                }

                watch.Restart();
                if (!built) Build(template);
                Splat(count);
                watch.Stop();
                msTotal += watch.Elapsed.TotalMilliseconds;
                frames++;
                Show(true);
            }
            catch (System.Exception e)
            {
                failed = true;
                Show(false);
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] the smoke volume stopped (the sprites are used): " + e);
            }
        }

        // The volume, or the sprites.
        private void Show(bool volume)
        {
            if (billboards != null) billboards.enabled = !volume;
            if (box != null) box.SetActive(volume);
        }

        private void Build(Material template)
        {
            int n = GridSize;
            grid = new Texture3D(n, n, n, TextureFormat.RG16, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "ArmorFragments smoke grid"
            };
            bytes = new byte[n * n * n * 2];
            density = new float[n * n * n];
            temperatureSum = new float[n * n * n];
            weightSum = new float[n * n * n];

            material = new Material(template) { name = "ArmorFragments smoke volume" };
            material.SetTexture("_Grid", grid);
            material.SetFloat("_UseDepth", DepthTextureAvailable() ? 1f : 0f);
            material.SetFloat("_Fade", 1f);

            if (boxMesh == null) boxMesh = MakeBox();
            box = new GameObject("ArmorFragments smoke volume");
            box.AddComponent<MeshFilter>().sharedMesh = boxMesh;
            MeshRenderer renderer = box.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            box.layer = ImpactCamera.EffectsLayer;
            built = true;
        }

        private void Splat(int count)
        {
            int n = GridSize;

            // The box: a cube around all the balls.
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), maxp = -min;
            float sizeSum = 0f;
            Vector3 velocitySum = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                float r = BallRadius * particles[i].GetCurrentSize(system);
                Vector3 p = particles[i].position;
                min = Vector3.Min(min, p - Vector3.one * r);
                maxp = Vector3.Max(maxp, p + Vector3.one * r);
                sizeSum += particles[i].GetCurrentSize(system);
                velocitySum += particles[i].velocity;
            }
            Vector3 extent3 = maxp - min;
            float extent = Mathf.Max(0.5f, Mathf.Max(extent3.x, Mathf.Max(extent3.y, extent3.z)));
            Vector3 center = (min + maxp) * 0.5f;
            Vector3 origin = center - Vector3.one * (extent * 0.5f);
            float cell = extent / n;

            System.Array.Clear(density, 0, density.Length);
            System.Array.Clear(temperatureSum, 0, temperatureSum.Length);
            System.Array.Clear(weightSum, 0, weightSum.Length);

            for (int i = 0; i < count; i++)
            {
                ParticleSystem.Particle q = particles[i];
                float r = BallRadius * q.GetCurrentSize(system);
                float amount = q.GetCurrentColor(system).a / 255f;
                if (amount <= 0.001f || r <= 0f) continue;
                float heat = q.startColor.r / 255f;
                Vector3 local = (q.position - origin) / cell;
                float rc = r / cell;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(local.x - rc)), x1 = Mathf.Min(n - 1, Mathf.CeilToInt(local.x + rc));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(local.y - rc)), y1 = Mathf.Min(n - 1, Mathf.CeilToInt(local.y + rc));
                int z0 = Mathf.Max(0, Mathf.FloorToInt(local.z - rc)), z1 = Mathf.Min(n - 1, Mathf.CeilToInt(local.z + rc));
                float inv = 1f / (rc * rc);
                for (int z = z0; z <= z1; z++)
                {
                    float dz = z + 0.5f - local.z;
                    for (int y = y0; y <= y1; y++)
                    {
                        float dy = y + 0.5f - local.y;
                        int row = (z * n + y) * n;
                        for (int x = x0; x <= x1; x++)
                        {
                            float dx = x + 0.5f - local.x;
                            float d2 = (dx * dx + dy * dy + dz * dz) * inv;
                            if (d2 >= 1f) continue;
                            float k = 1f - d2;
                            float w = k * k * amount;
                            density[row + x] += w;
                            temperatureSum[row + x] += w * heat;
                            weightSum[row + x] += w;
                        }
                    }
                }
            }

            for (int i = 0; i < density.Length; i++)
            {
                float d = Mathf.Min(1f, density[i]);
                float t = weightSum[i] > 1e-5f ? temperatureSum[i] / weightSum[i] : 0f;
                bytes[i * 2] = (byte)(d * 255f + 0.5f);
                bytes[i * 2 + 1] = (byte)(Mathf.Clamp01(t) * 255f + 0.5f);
            }
            grid.SetPixelData(bytes, 0);
            grid.Apply(false);

            // The noise travels with the smoke, so the detail moves with the cloud instead of the cloud sliding through it.
            noiseOffset += velocitySum / count * Time.deltaTime;
            float meanSize = Mathf.Max(0.05f, sizeSum / count);

            material.SetVector("_GridMin", new Vector4(origin.x, origin.y, origin.z, 0f));
            material.SetFloat("_GridSize", extent);
            material.SetVector("_NoiseOffset", new Vector4(noiseOffset.x, noiseOffset.y, noiseOffset.z, 0f));
            material.SetFloat("_NoiseScale", 1f / (DetailPerSize * 2f * meanSize));

            box.transform.position = center;
            box.transform.localScale = Vector3.one * extent;

            if (!loggedFirst)
            {
                loggedFirst = true;
                ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] smoke volume: " + count + " ball(s) into a " + n + "^3 grid over "
                    + extent.ToString("F1") + " m (cells of " + (cell * 100f).ToString("F0") + " cm), depth texture "
                    + (DepthTextureAvailable() ? "available" : "NOT available"));
            }
            else if (frames == 120)
            {
                ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] smoke volume: updating the grid costs " + (msTotal / frames).ToString("F2") + " ms per frame on average");
            }
        }

        // Whether the scene's depth texture exists, so the smoke can stop at solid things. If the render pipeline does not make it
        // and the camera is allowed, ask the main camera for it.
        private static bool DepthTextureAvailable()
        {
            if (depthChecked) return depthAvailable;
            depthChecked = true;
            try
            {
                UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
                depthAvailable = asset != null && asset.supportsCameraDepthTexture;
                if (!depthAvailable)
                {
                    Camera cam = Camera.main;
                    UniversalAdditionalCameraData data = cam != null ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
                    if (data != null)
                    {
                        data.requiresDepthOption = CameraOverrideOption.On;
                        depthAvailable = true;
                        ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] the render pipeline does not make a depth texture; asked the main camera for one");
                    }
                }
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] could not check the depth texture: " + e.Message);
            }
            return depthAvailable;
        }

        private static Mesh MakeBox()
        {
            Mesh mesh = new Mesh { name = "ArmorFragments unit box" };
            Vector3[] v =
            {
                new Vector3(-.5f, -.5f, -.5f), new Vector3(.5f, -.5f, -.5f), new Vector3(.5f, .5f, -.5f), new Vector3(-.5f, .5f, -.5f),
                new Vector3(-.5f, -.5f, .5f), new Vector3(.5f, -.5f, .5f), new Vector3(.5f, .5f, .5f), new Vector3(-.5f, .5f, .5f),
            };
            int[] t =
            {
                0, 2, 1, 0, 3, 2,   // back
                4, 5, 6, 4, 6, 7,   // front
                0, 4, 7, 0, 7, 3,   // left
                1, 2, 6, 1, 6, 5,   // right
                3, 7, 6, 3, 6, 2,   // top
                0, 1, 5, 0, 5, 4,   // bottom
            };
            mesh.vertices = v;
            mesh.triangles = t;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            return mesh;
        }

        private void OnDestroy()
        {
            if (box != null) Destroy(box);
            if (material != null) Destroy(material);
            if (grid != null) Destroy(grid);
        }
    }
}
// RELEASE-STRIP-END
