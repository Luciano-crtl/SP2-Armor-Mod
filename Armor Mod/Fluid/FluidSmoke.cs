// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using System.Diagnostics;
using ArmorFragments.Fluid;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Environment.Wind;
using HarmonyLib;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace ArmorFragments
{
    // A cannon fires (this is also what the game calls to show the muzzle flash of other players' cannons).
    [HarmonyPatch(typeof(CannonScript), "PlayMuzzleFlash")]
    internal static class FluidSmokePatch
    {
        private static void Postfix(CannonScript __instance)
        {
            FluidSmoke.OnShot(__instance);
        }
    }

    // The smoke of a cannon shot as a real fluid: every shot starts a small GPU simulation of the hot gas bursting out of the barrel
    // (FluidSim, the compute shader Fluid.compute in the effect bundle). It is drawn by the ray-marching shader (SmokeVolume) on a box
    // that lies along the barrel. A cloud lives about 9 seconds (a little more for big guns), then its memory goes back to a pool.
    //
    // The box fits the gun: its size, the size of its cells and the time scale follow the calibre (a small gun gets a small, fine, cheap box).
    // The box stays where the shot happened. The wind of the game acts on the gas inside it (a different wind in every part of the box, gusts
    // and the shelter of vehicles included, see FluidSim.SetWind); once the shot is over the box also drifts with most of the mean wind so
    // the smoke does not simply leave it.
    // A second shot from the same place while the cloud is young adds to the same cloud (rapid fire makes one dense cloud, not many).
    //
    // Cost control: three quality levels (cell count and ray steps), chosen by distance and, in the automatic mode, by the frame time.
    internal static partial class FluidSmoke
    {
        public static bool Enabled = true;
        // Simulations of shots running at once (Smoke/MaxClouds; 0 = no limit). Past it the oldest cloud is made to fade away quickly and the new
        // one is made at once.
        public static int MaxActive { get { int v = ArmorFragmentsPlugin.SmokeMaxClouds != null ? ArmorFragmentsPlugin.SmokeMaxClouds.Value : 6; return v <= 0 ? 100000 : v; } }
        public const float EvictSeconds = 0.9f;        // how long the oldest cloud takes to vanish when a new one needs its place
        private const int MaxFading = 3;               // clouds fading out because of a new one, at most (their memory is still in use)
        // The brake on the air (per second), set in the config (section Smoke): the first BlastSeconds are braked hard so the blast pushes the air
        // aside and stops; after that the smoke only has to keep drifting. At 1.5 it was almost still after 3 s, at 0.4 it still moves at 0.7 m/s
        // after 6 s.
        public const float BlastBrake = 7f;
        public const float BlastSeconds = 0.3f;
        public const float LateBrake = 0.4f;
        public static bool SmokeGroundDust { get { return ArmorFragmentsPlugin.SmokeGroundDust == null || ArmorFragmentsPlugin.SmokeGroundDust.Value; } }
        // How much bigger (or smaller) than the standard the box of a new cloud is, in every direction. The number of cells is limited by the
        // quality level, so a bigger box has coarser cells.
        public static float BoxSize { get { return ArmorFragmentsPlugin.SmokeBoxSize != null ? Mathf.Clamp(ArmorFragmentsPlugin.SmokeBoxSize.Value, 0.5f, 3f) : 1f; } }
        // Faster drawing (Smoke/FastDrawing): blocks of the grid with no smoke are skipped, and the walk toward the sun and the game's shadow map
        // are looked up every second sample that holds smoke. Faster solids (Smoke/FastSolids): when nothing near the cloud moves, the solids
        // are measured every second frame.
        public static bool FastDrawing { get { return ArmorFragmentsPlugin.SmokeFastDrawing == null || ArmorFragmentsPlugin.SmokeFastDrawing.Value; } }
        public static bool FastSolids { get { return ArmorFragmentsPlugin.SmokeFastSolids == null || ArmorFragmentsPlugin.SmokeFastSolids.Value; } }
        // Balancing the smoke by calibre (Smoke/SizeThinning, Smoke/SizeCooling): a small gun makes a thin puff (the less gas, the shorter the
        // path the light takes through it and the less opaque it is), and its heat is lost faster (the smaller the puff, the faster it mixes
        // with the air), so it does not rise as far or last as long as a big gun's. The scale is calibre / 75 mm.
        public const float SizeThinning = 0.45f;
        public const float SizeCooling = 0.8f;
        public static float CaliberAmount(float scale) { return Mathf.Clamp(Mathf.Pow(Mathf.Max(scale, 0.05f), SizeThinning), 0.1f, 1.4f); }
        public static float CaliberLoss(float scale) { return Mathf.Clamp(Mathf.Pow(Mathf.Max(scale, 0.2f), -SizeCooling), 0.6f, 5f); }
        public static string LastProfileText = "no shot yet";      // how the last new cloud's cannon shaped its smoke (shown in the panel)
        public const float MuzzleSparksStrength = 1f;
        public static float HeatHazeStrength { get { return ArmorFragmentsPlugin.SmokeHeatHaze != null ? ArmorFragmentsPlugin.SmokeHeatHaze.Value : 1f; } }
        public static bool SmokeShadows { get { return ArmorFragmentsPlugin.SmokeShadows == null || ArmorFragmentsPlugin.SmokeShadows.Value; } }
        public static float ShadowStrength { get { return ArmorFragmentsPlugin.SmokeShadowStrength != null ? ArmorFragmentsPlugin.SmokeShadowStrength.Value : 0.45f; } }
        private static void ReadBlastSettings()
        {
            if (ArmorFragmentsPlugin.SmokeJetFocus != null) SmokeBlast.Focus = ArmorFragmentsPlugin.SmokeJetFocus.Value;
            if (ArmorFragmentsPlugin.SmokeJetSpeed != null) SmokeBlast.JetSpeed = ArmorFragmentsPlugin.SmokeJetSpeed.Value;
            if (ArmorFragmentsPlugin.SmokeDensity != null) SmokeBlast.Density = ArmorFragmentsPlugin.SmokeDensity.Value;
            if (ArmorFragmentsPlugin.SmokeBlastDust != null) SmokeBlast.BlastDust = ArmorFragmentsPlugin.SmokeBlastDust.Value;
            if (ArmorFragmentsPlugin.SmokeResidualHeat != null) SmokeBlast.ResidualHeat = Mathf.Clamp(ArmorFragmentsPlugin.SmokeResidualHeat.Value, 0f, 0.35f);
        }
        public static bool ShowSolid;                  // debugging view: paint what the smoke treats as solid (the parts of vehicles, the ground)
        public static void ToggleShowSolid() { ShowSolid = !ShowSolid; }
        public static float MinInterval = 0.1f;        // seconds between the shots one cannon adds
        public static float Lifetime { get { return ArmorFragmentsPlugin.SmokeLifetime != null ? ArmorFragmentsPlugin.SmokeLifetime.Value : 6.5f; } }   // seconds, for a 75 mm gun (scaled by the gun's time scale)
        public static float MaxLifetime { get { return Lifetime * 1.8f; } }
        public static float FadeSeconds = 2.2f;
        public static float DriftShare = 0.7f;         // how much of the mean wind the box drifts with once the shot is over
        public static float MaxCameraDistance = 400f;  // farther than this: no cloud
        public static float MergeSeconds = 1.2f;       // a shot this soon after a cloud began, inside its box, joins it

        // Quality: 0 = ultra, 1 = high, 2 = medium, 3 = low. `Chosen` is the best level the player allows; Auto lets the frame time lower it, and
        // never goes above high (ultra is only for when it is picked). Ultra, high and medium keep their fields in 16 bits (see HalfPrecision).
        public struct QualityLevel { public int nx, ny, nz; public float cellMultiplier; public int steps; public int cycles; public string name; public bool half; }
        public static readonly QualityLevel[] Levels =
        {
            new QualityLevel { nx = 208, ny = 104, nz = 104, cellMultiplier = 0.550f, steps = 56, cycles = 2, name = "ultra", half = true },
            new QualityLevel { nx = 160, ny = 80, nz = 80, cellMultiplier = 0.650f, steps = 48, cycles = 2, name = "high", half = true },
            new QualityLevel { nx = 128, ny = 72, nz = 72, cellMultiplier = 0.800f, steps = 40, cycles = 2, name = "medium", half = true },
            new QualityLevel { nx = 96, ny = 56, nz = 56, cellMultiplier = 1.000f, steps = 32, cycles = 2, name = "low" },
        };
        private const int AutoBest = 1;                // the best level the automatic mode uses: high
        public static int Chosen = AutoBest;
        public static bool Auto = true;
        private static int autoLevel = AutoBest;       // what the automatic mode currently allows
        private const float BaseCell = 0.125f;
        public static readonly int[] CellBudget = { 2400000, 1500000, 900000, 450000 };   // most cells a cloud of each quality may have
        // How far up the box the muzzle sits: the ground is 2 m or so below it, and the heat of the smoke takes it up, so most of the box is above.
        private const float MuzzleHeightFraction = 0.3f;
        private static readonly Vector3 BaseExtent = new Vector3(18f, 9f, 9f);  // metres, for a 75 mm gun

        // How big a cloud for this gun is: the box, its cells, and how quickly things happen.
        private struct Sizing
        {
            public float scale;             // caliber / 75 mm (clamped): the size of the blast
            public float timeScale;         // scale^0.3: bigger blasts last a little longer and move a little faster
            public Vector3Int dims;
            public float cell;
            public Vector3 extent;
        }

        private static Sizing SizeFor(float caliberMm, int level)
        {
            Sizing z = new Sizing();
            z.scale = Mathf.Clamp(caliberMm / 75f, 0.2f, 3.5f);
            z.timeScale = Mathf.Pow(z.scale, 0.3f);
            Vector3 target = BaseExtent * z.scale * BoxSize;
            float h = BaseCell * Mathf.Sqrt(z.scale) * Levels[level].cellMultiplier;
            Vector3Int d;
            while (true)
            {
                d = new Vector3Int(RoundUp8(target.x / h), RoundUp8(target.y / h), RoundUp8(target.z / h));
                if ((long)d.x * d.y * d.z <= CellBudget[level]) break;
                h *= 1.06f;
            }
            z.dims = d;
            z.cell = h;
            z.extent = new Vector3(d.x, d.y, d.z) * h;
            return z;
        }

        private static int RoundUp8(float cells) { return Mathf.Max(24, Mathf.CeilToInt(cells / 8f) * 8); }

        private sealed class Shot
        {
            public Vector3 muzzleWorld;      // where the muzzle was: the sources stay there while the box may drift
            public float start;
            public Vector3 flashWorld;       // where the flash of light is (a little ahead of the muzzle)
            public bool hasGround;
            public Vector3 groundNormal = Vector3.up;
            public Vector3 footWorld;        // the point of the ground under the muzzle
            public float height;             // the muzzle's height above it
            public int dustCount;            // dust spots along the line of fire
            public readonly Vector3[] dustWorld = new Vector3[8];
            public readonly float[] dustAmount = new float[8];     // 0..1: less when the line of fire is high above the ground
            public bool hasColor;
            public Color groundColor;
            public float amount = 1f;        // how much smoke, heat and dust this shot makes compared with a 75 mm gun's (see CaliberAmount)
            public float push = 1f;          // how hard its jets push the air: less for each shot of a burst that is still going on
            public float size = 1f, shock = 1f, brake = 1f;     // from the cannon's own settings (CannonProfile.cs)
        }

        private sealed class Cloud
        {
            public int level;
            public FluidSim sim;
            public GameObject box;
            public MeshRenderer boxRenderer;
            public bool isOverflow;          // a bigger, coarser box made beyond a face of another cloud (Overflow.cs)
            public Cloud parent;
            public int faceAxis, faceSign;   // which face of the parent it was made beyond
            public int faceMask;             // faces of this cloud that smoke has reached (bit 0 -x, 1 +x, 2 -y, 3 +y, 4 -z, 5 +z)
            public int children;             // faces that already have an overflow box
            public float probeAt;
            public bool probing;
            public bool useHeight;           // the ground is a height map (Terrain.cs)
            public float groundAt;
            public float lastPeakSpeed;      // the fastest part speed the last time the solids were measured
            public int solidFrame;
            public bool evicted;             // fading out fast because the limit of clouds was reached and a newer one took its place
            public float evictFade, evictEnd;
            public readonly Dictionary<AircraftScript, Bounds> crafts = new Dictionary<AircraftScript, Bounds>();     // vehicles with a part in the box, and where
            public Material material;
            public Vector3 origin;          // world position of the box corner
            public Quaternion rotation;     // box axes -> world
            public Vector3 extent;
            public float scale, timeScale;
            public float age;
            public float end;
            public Vector3 drift;            // world velocity of the box
            public float accumulated;        // time not simulated yet (old clouds step every other frame)
            public int frameCounter;
            public readonly List<Shot> shots = new List<Shot>();
            public PartScript cannonPart;    // its own colliders do not block its smoke
            public bool hasGround;
            public Vector3 groundPoint, groundNormal;
            public float shapePeak;          // the fastest part speed that went to the solver (m/s), for the log
            public float windPeak;           // the strongest ambient wind the game reported over the box (m/s), for the log
            public int diagFrames;           // frames since it began, for the first few clouds' diagnostics
            public int overlapsSeen, partsSeen, shapesSent;
        }

        private static readonly List<Cloud> Active = new List<Cloud>();
        private static readonly Dictionary<Vector3Int, Stack<FluidSim>> Pools = new Dictionary<Vector3Int, Stack<FluidSim>>();
        private static NativeArray<float3> windPositions, windResults;
        private static readonly Vector3[] AmbientBuffer = new Vector3[64];
        private static readonly Dictionary<int, float> LastShot = new Dictionary<int, float>();
        private static Mesh boxMesh;
        private static Driver driver;
        private static bool loggedFirst;
        private static int checkClouds;       // how many clouds have had their air speed and smoke measured
        private static int diagClouds;       // how many clouds have had their surroundings logged

        // Frame-time watching (for the automatic quality and for the log).
        private static float frameEma, baselineEma, slowTimer, goodTimer, logTimer, cpuMsSum;
        private static int cpuMsCount;
        private static readonly Stopwatch Watch = new Stopwatch();

        public static string QualityName { get { return Auto ? "auto (now " + Levels[autoLevel].name + ")" : Levels[Chosen].name; } }

        public static void Toggle()
        {
            Enabled = !Enabled;
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid muzzle smoke " + (Enabled ? "on" : "off"));
        }

        // auto -> high -> medium -> low -> auto
        public static void CycleQuality()
        {
            if (Auto) { Auto = false; Chosen = 0; }
            else if (Chosen < Levels.Length - 1) Chosen++;
            else { Auto = true; Chosen = AutoBest; }
            autoLevel = Chosen;
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke quality: " + QualityName);
        }

        private static int LevelFor(float distance)
        {
            int level = Auto ? autoLevel : Chosen;
            if (distance > 300f) level = Mathf.Max(level, Levels.Length - 1);
            else if (distance > 150f) level = Mathf.Max(level, 2);
            return level;
        }

        public static void OnShot(CannonScript cannon)
        {
            if (!Enabled || cannon == null) return;
            try
            {
                float heatBefore = BarrelHeat.HeatOf(cannon);
                BarrelHeat.OnShot(cannon);
                float hotBore = 1f + 0.4f * Mathf.Min(heatBefore, 1f);
                int id = cannon.GetInstanceID();
                float now = Time.time, last;
                if (LastShot.TryGetValue(id, out last) && now - last < MinInterval) return;

                ComputeShader cs = VfxBundleTest.Compute("fluid");
                Material template = VfxBundleTest.Material("smokevolume");
                if (cs == null || template == null || !SystemInfo.supportsComputeShaders) return;
                Camera cam = Camera.main;
                Vector3 tip = cannon.TipPosition;
                float distance = cam != null ? (cam.transform.position - tip).magnitude : 0f;
                if (distance > MaxCameraDistance) return;

                Vector3 ex = cannon.transform.forward.normalized;

                // A young cloud right here takes the shot as one more blast.
                foreach (Cloud c in Active)
                {
                    if (c.age > MergeSeconds || c.evicted) continue;
                    Vector3 local = Quaternion.Inverse(c.rotation) * (tip - c.origin);
                    float margin = 1.2f * c.scale;
                    bool inside = local.x > margin && local.y > margin && local.z > margin
                                  && local.x < c.extent.x - margin && local.y < c.extent.y - margin && local.z < c.extent.z - margin;
                    if (inside && Vector3.Dot(ex, c.rotation * Vector3.right) > 0.8f)
                    {
                        Shot added = MakeShot(tip, c.age, ex, c.scale, cannon);
                        int burst = 0;
                        foreach (Shot earlier in c.shots) if (c.age - earlier.start < 0.5f) burst++;
                        CannonProfile profile = CannonProfile.Of(cannon, c.scale);
                        added.push = profile.push / (1f + 0.25f * burst);
                        added.amount = CaliberAmount(c.scale) * profile.amount * Mathf.Sqrt(1f / (1f + 0.25f * burst)) * hotBore;
                        added.size = profile.size; added.shock = profile.shock; added.brake = profile.brake;
                        c.shots.Add(added);
                        EmitMuzzleBlast(cannon, tip, ex, c.scale, added.amount);
                        MuzzleFlash.Spawn(tip + ex * (1.2f * c.scale), c.scale, c.timeScale);
                        c.end = Mathf.Min(c.age + Lifetime * c.timeScale, MaxLifetime);
                        LastShot[id] = now;
                        return;
                    }
                }

                int shotClouds = 0, fading = 0;
                Cloud oldest = null;
                foreach (Cloud existing in Active)
                {
                    if (existing.isOverflow) continue;
                    if (existing.evicted) { fading++; continue; }
                    shotClouds++;
                    if (oldest == null || existing.age > oldest.age) oldest = existing;
                }
                if (shotClouds >= MaxActive)
                {
                    // The limit is reached: the oldest cloud vanishes (quickly) and the new one is made. Not when too many are already vanishing.
                    if (oldest == null || fading >= MaxFading) return;
                    EvictCloud(oldest);
                }
                LastShot[id] = now;

                int level = LevelFor(distance);
                QualityLevel q = Levels[level];
                float caliber = CaliberMillimeters(cannon);
                Sizing size = SizeFor(caliber, level);
                float scale = size.scale, cell = size.cell;

                // The box's axes: x along the barrel, y as close to "up" as it can be, z across.
                Vector3 ey = Vector3.up - Vector3.Dot(Vector3.up, ex) * ex;
                if (ey.sqrMagnitude < 1e-4f) ey = Vector3.forward - Vector3.Dot(Vector3.forward, ex) * ex;
                ey.Normalize();
                Vector3 ez = Vector3.Cross(ex, ey);
                Quaternion rot = Quaternion.LookRotation(ez, ey);

                Vector3 extent = size.extent;
                // The muzzle sits a third of the way along the box, so the front of the vehicle (the turret and the hull below the gun)
                // is inside it, not only the gas ahead.
                // The ground has to be inside the box (its dust, the solid under the smoke): in a small box the muzzle is put higher than the usual
                // 30% when the ground is further below it than that.
                RaycastHit below;
                float heightFraction = MuzzleHeightFraction;
                if (FindGround(tip, cannon.PartScript, out below)) heightFraction = Mathf.Clamp((Mathf.Max(tip.y - below.point.y, 0f) + 0.6f) / extent.y, MuzzleHeightFraction, 0.6f);
                Vector3 muzzleLocal = new Vector3(extent.x * 0.32f, extent.y * heightFraction, extent.z * 0.5f);
                Vector3 origin = tip - rot * muzzleLocal;

                Stack<FluidSim> pool;
                FluidSim sim = Acquire(cs, size.dims, cell, q.half);
                sim.Cell = cell;
                sim.Clear();
                sim.Params.cycles = q.cycles;
                sim.Params.edgeMeters = Mathf.Clamp(0.9f * scale, 0.4f, 2.5f);
                sim.CoarseSweeps = 10;

                Cloud cloud = new Cloud { level = level, sim = sim, origin = origin, rotation = rot, extent = extent, scale = scale, timeScale = size.timeScale, end = Lifetime * size.timeScale };
                Shot first = MakeShot(tip, 0f, ex, scale, cannon);
                CannonProfile firstProfile = CannonProfile.Of(cannon, scale);
                first.amount = CaliberAmount(scale) * firstProfile.amount * hotBore;
                first.push = firstProfile.push;
                first.size = firstProfile.size; first.shock = firstProfile.shock; first.brake = firstProfile.brake;
                LastProfileText = firstProfile.text;
                cloud.shots.Add(first);
                EmitMuzzleBlast(cannon, tip, ex, scale, first.amount);
                MuzzleFlash.Spawn(tip + ex * (1.2f * scale), scale, size.timeScale);
                cloud.cannonPart = cannon.PartScript;
                RaycastHit ground;
                if (FindGround(tip + ex * 1.5f * scale, cannon.PartScript, out ground))
                {
                    cloud.hasGround = true;
                    cloud.groundPoint = ground.point;
                    cloud.groundNormal = ground.normal.sqrMagnitude > 0.5f ? ground.normal.normalized : Vector3.up;
                }
                cloud.material = new Material(template) { name = "ArmorFragments fluid smoke" };
                cloud.material.SetFloat("_UseDepth", DepthTextureAvailable() ? 1f : 0f);
                cloud.material.SetFloat("_Extinction", 8f);
                cloud.material.SetFloat("_Erosion", 0.55f);
                cloud.material.SetFloat("_NoiseScale", 1.1f / scale);
                cloud.material.SetFloat("_UsePhi", 1f);
                cloud.material.SetColor("_DustColor", DustColor(cloud));
                cloud.material.SetVector("_GridExtent", new Vector4(extent.x, extent.y, extent.z, 0f));

                if (boxMesh == null) boxMesh = MakeBox();
                cloud.box = new GameObject("ArmorFragments fluid smoke");
                cloud.box.AddComponent<MeshFilter>().sharedMesh = boxMesh;
                MeshRenderer renderer = cloud.box.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = cloud.material;
                renderer.shadowCastingMode = SmokeShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                cloud.boxRenderer = renderer;
                renderer.receiveShadows = false;
                VfxBundleTest.SetLayer(cloud.box, ImpactCamera.EffectsLayer);
                Place(cloud);

                Active.Add(cloud);
                EnsureDriver();

                if (!loggedFirst)
                {
                    loggedFirst = true;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] first fluid smoke: caliber " + caliber.ToString("F0") + " mm, quality " + q.name + ", box "
                        + extent.x.ToString("F1") + " x " + extent.y.ToString("F1") + " x " + extent.z.ToString("F1") + " m, cells of "
                        + (cell * 100f).ToString("F1") + " cm, " + size.dims.x + " x " + size.dims.y + " x " + size.dims.z + " = " + (size.dims.x * size.dims.y * size.dims.z)
                        + " cells, lives " + cloud.end.ToString("F1") + " s");
                }
            }
            catch (System.Exception e)
            {
                Enabled = false;
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] fluid smoke failed and is now off: " + e);
            }
        }

        private static void EmitMuzzleBlast(CannonScript cannon, Vector3 tip, Vector3 forward, float scale, float amount)
        {
            try
            {
                Vector3 vehicle = cannon.PartScript != null && cannon.PartScript.Aircraft != null ? cannon.PartScript.Aircraft.Velocity : Vector3.zero;
                bool brake = true;
                if (cannon.PartScript != null)
                    foreach (PartModifierData modifier in cannon.PartScript.Part.Modifiers)
                    {
                        CannonData data = modifier as CannonData;
                        if (data != null) { brake = data.MuzzleBrake; break; }
                    }
                ImpactEffects.MuzzleBlast(tip + forward * (0.2f * scale), forward, vehicle, scale, amount, MuzzleSparksStrength, brake);
            }
            catch (System.Exception) { }
        }

        // Puts the box, and the matrix that takes world points into the box's own space, where the cloud is.
        private static void Place(Cloud c)
        {
            c.box.transform.SetPositionAndRotation(c.origin + c.rotation * (c.extent * 0.5f), c.rotation);
            c.box.transform.localScale = c.extent;
            c.material.SetMatrix("_WorldToGrid", Matrix4x4.TRS(c.origin, c.rotation, Vector3.one).inverse);
        }

        // The cloud's opacity: it fades out over its last FadeSeconds; one that was sent away for a newer cloud fades from where it was to nothing in EvictSeconds.
        private static float FadeOf(Cloud c)
        {
            if (c.evicted) return c.evictFade * Mathf.Clamp01((c.evictEnd - c.age) / EvictSeconds);
            return Mathf.Clamp01((c.end - c.age) / (FadeSeconds * c.timeScale));
        }

        private static void EvictCloud(Cloud c)
        {
            if (c.evicted) return;
            c.evictFade = FadeOf(c);
            c.evicted = true;
            c.evictEnd = c.age + EvictSeconds;
            c.end = Mathf.Min(c.end, c.evictEnd);
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke: the limit of " + (ArmorFragmentsPlugin.SmokeMaxClouds != null && ArmorFragmentsPlugin.SmokeMaxClouds.Value > 0 ? ArmorFragmentsPlugin.SmokeMaxClouds.Value.ToString() : "clouds")
                + " was reached: the oldest cloud (" + c.age.ToString("F1") + " s old) fades out in " + EvictSeconds.ToString("F1") + " s");
        }

        // Called every frame while a cloud lives.
        private static void Tick()
        {
            float unscaled = Time.unscaledDeltaTime;
            Monitor(unscaled);
            BarrelHeat.Tick(Time.deltaTime);

            float frame = Mathf.Min(Time.deltaTime, 0.03f);
            ReadBlastSettings();
            MuzzleFlash.Tick(Time.deltaTime, Active.Count > 0 ? Active[0].timeScale : 1f);
            WindQueryManager wind = WindQueryManager.Instance;
            Camera cam = Camera.main;
            Watch.Restart();
            try { UpdateOverflow(frame); }
            catch (System.Exception e) { ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] smoke overflow failed and is off: " + e); if (ArmorFragmentsPlugin.SmokeOverflow != null) ArmorFragmentsPlugin.SmokeOverflow.Value = false; }
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Cloud c = Active[i];
                try
                {
                    if (c.age >= c.end || c.box == null) { Retire(c); Active.RemoveAt(i); continue; }
                    if (frame > 0f)
                    {
                        // Gravity in the box's axes; the blast slows fast, then the smoke drifts.
                        c.sim.Params.buoyDir = Quaternion.Inverse(c.rotation) * Vector3.up;
                        c.sim.Params.drag = c.age < BlastSeconds * c.timeScale ? BlastBrake / c.timeScale : LateBrake;
                        // Heat that makes the smoke rise and the weak, endless swirling of the air (see the settings in section Smoke).
                        if (ArmorFragmentsPlugin.SmokeBuoyancy != null) c.sim.Params.buoyancy = ArmorFragmentsPlugin.SmokeBuoyancy.Value;
                        float loss = CaliberLoss(c.scale);
                        c.sim.Params.cool = 0.3f * loss;
                        c.sim.Params.dissipate = 0.04f * loss;
                        if (ArmorFragmentsPlugin.SmokeDustWeight != null) c.sim.Params.dustWeight = ArmorFragmentsPlugin.SmokeDustWeight.Value;
                        c.sim.Params.detail = 0.6f;
                        UpdateWind(c, wind, frame);
                        RefreshGround(c);
                        // The solids: every frame, or every second frame when the cloud is past its blast and nothing near it moves.
                        bool skipSolids = FastSolids && c.sim.HasSolids && c.age > 1.6f * c.timeScale && c.lastPeakSpeed < 0.5f && ((c.solidFrame++ & 1) == 1);
                        if (!skipSolids)
                        {
                            c.sim.SetObstacles(Obstacles(c), PlaneBuffer);
                            c.lastPeakSpeed = peakSpeed;
                        }

                        // A cloud past the middle of its life changes slowly: it is stepped every other frame, twice as long.
                        float step = frame;
                        bool run = true;
                        if (c.age > 0.45f * c.end)
                        {
                            c.accumulated += frame;
                            c.frameCounter++;
                            run = (c.frameCounter & 1) == 0;
                            step = c.accumulated;
                            if (run) c.accumulated = 0f;
                        }
                        if (run)
                        {
                            bool violent = RecentShot(c);
                            int substeps = violent ? Mathf.Clamp(Mathf.CeilToInt(step / 0.006f), 1, 3) : 1;
                            float dt = step / substeps;
                            for (int sub = 0; sub < substeps; sub++)
                            {
                                List<FluidSim.Blob> blobs = Blobs(c, c.age + dt * sub);
                                Wash.Build(blobs, c.origin, c.rotation, c.extent, c.drift, c.crafts, dt);
                                if (blobs.Count > 0) c.sim.AddBlobs(blobs);
                                c.sim.Step(dt);
                            }
                        }
                        c.age += frame;

                        // Once the shot is over the box drifts with the wind (the smoke inside it moves with the box).
                        if (wind != null && c.drift.sqrMagnitude > 1e-6f)
                        {
                            c.origin += c.drift * frame;
                            Place(c);
                        }
                    }

                    if (diagClouds < 5 && c.diagFrames == 3)
                    {
                        diagClouds++;
                        Cloud cd = c;
                        c.sim.CountSolid(solid => ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke surroundings: " + cd.overlapsSeen + " collider(s) in the box, "
                            + cd.partsSeen + " of vehicle parts, " + cd.shapesSent + " solid shape(s) sent (ground " + (cd.hasGround ? "yes" : "no") + "), "
                            + solid + " of " + (cd.sim != null ? cd.sim.Dims.x * cd.sim.Dims.y * cd.sim.Dims.z : 0) + " cells are solid"));
                    }
                    if (checkClouds < 18 && (c.diagFrames == 8 || c.diagFrames == 16 || c.diagFrames == 32) && c.sim != null)
                    {
                        checkClouds++;
                        Cloud cc = c;
                        float splitX = cc.extent.x * 0.32f + 2.5f * cc.scale;
                        c.sim.Inspect(splitX, r => ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke check, " + cc.age.ToString("F2") + " s after the shot: fastest air "
                            + r.fastest.ToString("F1") + " m/s at cell " + r.cell.x + "," + r.cell.y + "," + r.cell.z + " of " + cc.sim.Dims.x + "x" + cc.sim.Dims.y + "x" + cc.sim.Dims.z + " (" + r.solidAround + " of its 6 neighbours are solid), smoke " + r.mass.ToString("F0") + " (" + (r.mass > 0 ? 100.0 * r.ahead / r.mass : 0).ToString("F0") + "% of it beyond the muzzle end), the fastest part moved at " + cc.shapePeak.ToString("F1") + " m/s"
                            + (r.bad > 0 ? ", " + r.bad + " cell(s) that are not numbers" : "") + ", the wind over the box peaked at " + cc.windPeak.ToString("F1") + " m/s, quality " + Levels[cc.level].name + ", box " + cc.extent.x.ToString("F1") + " m"));
                    }
                    c.diagFrames++;
                    Material m = c.material;
                    m.SetTexture("_Grid", c.sim.Scalars);
                    m.SetTexture("_PhiA", c.sim.PhiA);
                    m.SetTexture("_PhiB", c.sim.PhiB);
                    m.SetTexture("_Solid", c.sim.Solid);
                    bool fast = FastDrawing;
                    if (fast)
                    {
                        c.sim.UpdateOccupancy();
                        m.SetTexture("_Occ", c.sim.Occupancy);
                        Vector3Int od = c.sim.OccupancyDims;
                        m.SetVector("_OccDim", new Vector4(od.x, od.y, od.z, 0f));
                    }
                    m.SetFloat("_UseOcc", fast ? 1f : 0f);
                    float haze = HeatHazeStrength;
                    m.SetFloat("_Distort", haze > 0.01f && OpaqueTextureAvailable() ? haze : 0f);
                    m.SetFloat("_SoftContact", ArmorFragmentsPlugin.SmokeSoftContact != null ? ArmorFragmentsPlugin.SmokeSoftContact.Value : 0.35f);
                    m.SetFloat("_Cubic", ArmorFragmentsPlugin.SmokeCubicGrid == null || ArmorFragmentsPlugin.SmokeCubicGrid.Value ? 1f : 0f);
                    m.SetFloat("_FogOn", ArmorFragmentsPlugin.SmokeFog == null || ArmorFragmentsPlugin.SmokeFog.Value ? 1f : 0f);
                    m.SetFloat("_MultiScatter", 0.6f);
                    m.SetFloat("_NoiseRotate", 1f);       // the detail noise: three turned octaves, so its tile does not repeat in a line
                    m.SetFloat("_ThinSoft", 1f);          // thin smoke is carved less, so a thin haze does not show the noise
                    m.SetFloat("_LightEvery", fast ? 2f : 1f);
                    m.SetFloat("_ShowSolid", ShowSolid ? 1f : 0f);
                    // The flash of the latest shot lights the smoke, and the shadow the smoke casts on the scene.
                    if (c.shots.Count > 0)
                    {
                        Shot latest = c.shots[c.shots.Count - 1];
                        float flashLevel = MuzzleFlash.Enabled ? MuzzleFlash.Level(c.age - latest.start, c.timeScale) * MuzzleFlash.Intensity : 0f;
                        m.SetVector("_FlashPos", new Vector4(latest.flashWorld.x, latest.flashWorld.y, latest.flashWorld.z, 4f * flashLevel));
                    }
                    else m.SetVector("_FlashPos", Vector4.zero);
                    m.SetColor("_FlashColor", new Color(1f, 0.62f, 0.3f, 1f));
                    m.SetFloat("_FlashRange", 3f * c.scale);
                    bool shadows = SmokeShadows;
                    m.SetFloat("_ShadowStrength", shadows ? ShadowStrength : 0f);
                    if (c.boxRenderer != null)
                    {
                        UnityEngine.Rendering.ShadowCastingMode wanted = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                        if (c.boxRenderer.shadowCastingMode != wanted) c.boxRenderer.shadowCastingMode = wanted;
                    }
                    Vector2 w = c.sim.PhiWeights;
                    m.SetVector("_PhiWeights", new Vector4(w.x, w.y, 0f, 0f));
                    m.SetFloat("_Fade", FadeOf(c));
                    // Fewer ray steps for far clouds.
                    float distance = cam != null ? (cam.transform.position - c.box.transform.position).magnitude : 0f;
                    m.SetFloat("_Steps", Levels[c.level].steps * (distance > 120f ? 0.75f : 1f));
                    float stepCells = ArmorFragmentsPlugin.SmokeRayStepCells != null ? ArmorFragmentsPlugin.SmokeRayStepCells.Value : 1.5f;
                    m.SetFloat("_StepMeters", stepCells * c.sim.Cell);
                    m.SetFloat("_StepMax", ArmorFragmentsPlugin.SmokeRayStepMax != null ? ArmorFragmentsPlugin.SmokeRayStepMax.Value : 64);
                }
                catch (System.Exception e)
                {
                    Enabled = false;
                    ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] fluid smoke failed and is now off: " + e);
                    Retire(c);
                    Active.RemoveAt(i);
                }
            }
            SortBoxes(cam);
            Watch.Stop();
            if (Active.Count > 0) { cpuMsSum += (float)Watch.Elapsed.TotalMilliseconds; cpuMsCount++; }
        }

        // Overlapping boxes (a cloud and its overflow) are drawn from the farthest to the nearest: the order Unity picks by itself
        // is by the distance of each box's middle, which is wrong for a small box inside a big one.
        private static readonly List<KeyValuePair<float, Cloud>> SortList = new List<KeyValuePair<float, Cloud>>();
        private static void SortBoxes(Camera cam)
        {
            if (cam == null || Active.Count < 2) return;
            SortList.Clear();
            foreach (Cloud c in Active)
            {
                if (c.boxRenderer == null) continue;
                // the distance to the nearest point of the box, so that a big box that holds a small one is drawn first
                Vector3 local = Quaternion.Inverse(c.rotation) * (cam.transform.position - c.origin);
                Vector3 nearest = new Vector3(Mathf.Clamp(local.x, 0f, c.extent.x), Mathf.Clamp(local.y, 0f, c.extent.y), Mathf.Clamp(local.z, 0f, c.extent.z));
                SortList.Add(new KeyValuePair<float, Cloud>((local - nearest).sqrMagnitude + 1e-3f * (c.extent.x + c.extent.y + c.extent.z), c));
            }
            SortList.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (int i = 0; i < SortList.Count; i++) SortList[i].Value.boxRenderer.sortingOrder = i;
        }

        // The scene's colour texture (made after the solid things are drawn), which the heat haze looks through. If the render pipeline does
        // not make it, it is switched on while the haze is wanted and put back at the end.
        private static bool opaqueChecked, opaqueAvailable, opaqueWasOff;
        internal static bool OpaqueTextureAvailable()
        {
            if (opaqueChecked) return opaqueAvailable;
            opaqueChecked = true;
            try
            {
                UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
                if (asset != null)
                {
                    if (!asset.supportsCameraOpaqueTexture)
                    {
                        asset.supportsCameraOpaqueTexture = true;
                        opaqueWasOff = true;
                        ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] the render pipeline does not make a colour texture; switched it on for the heat haze (put back when the mod unloads)");
                    }
                    opaqueAvailable = true;
                }
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] could not get the scene's colour texture for the heat haze: " + e.Message);
            }
            return opaqueAvailable;
        }

        private static bool RecentShot(Cloud c)
        {
            foreach (Shot s in c.shots) if (c.age - s.start < 0.3f) return true;
            return false;
        }

        // The sources, in the box's axes, for every shot of the cloud (see SmokeBlast). The muzzle stays where it was in the world; the box may
        // drift away from it.
        private static readonly SmokeBlast.Ground BlastGround = new SmokeBlast.Ground();

        private static List<FluidSim.Blob> Blobs(Cloud c, float now)
        {
            List<FluidSim.Blob> list = new List<FluidSim.Blob>();
            float s = c.scale, T = c.timeScale;
            Quaternion inverse = Quaternion.Inverse(c.rotation);
            foreach (Shot shot in c.shots)
            {
                float t = now - shot.start;
                if (t < 0f) continue;
                Vector3 muzzle = inverse * (shot.muzzleWorld - c.origin);
                BlastGround.present = shot.hasGround;
                if (shot.hasGround)
                {
                    BlastGround.normal = inverse * shot.groundNormal;
                    BlastGround.height = shot.height;
                    BlastGround.foot = inverse * (shot.footWorld - c.origin);
                    BlastGround.count = shot.dustCount;
                    BlastGround.dustScale = shot.hasColor ? 1f : UnknownGroundDust;
                    for (int i = 0; i < shot.dustCount; i++)
                    {
                        BlastGround.points[i] = inverse * (shot.dustWorld[i] - c.origin);
                        BlastGround.amounts[i] = shot.dustAmount[i];
                    }
                }
                SmokeBlast.Build(list, t, s * shot.size, T, muzzle, BlastGround, shot.amount, shot.push, shot.shock, shot.brake);
            }
            return list;
        }

        // The wind of the game over the box, as the air the gas is pulled toward. It is asked on a 4 x 4 x 4 lattice (so it can differ from
        // one end of the box to the other: gusts, turbulence, thermals, the lee of a hill) and given to the solver in the box's space,
        // minus the velocity the box itself drifts at. The drift starts when the shot is over and follows most of the mean wind.
        private const float CalmTurbulence = 0.12f;      // m/s2 of swirling in dead calm air
        private static void UpdateWind(Cloud c, WindQueryManager wind, float frame)
        {
            if (wind == null) { c.sim.Params.turbulence = CalmTurbulence; c.sim.NoWind(); return; }
            if (!windPositions.IsCreated)
            {
                windPositions = new NativeArray<float3>(64, Allocator.Persistent);
                windResults = new NativeArray<float3>(64, Allocator.Persistent);
            }
            for (int i = 0; i < 64; i++)
            {
                Vector3 fraction = new Vector3(((i & 3) + 0.5f) / 4f, (((i >> 2) & 3) + 0.5f) / 4f, ((i >> 4) + 0.5f) / 4f);
                Vector3 world = c.origin + c.rotation * Vector3.Scale(c.extent, fraction);
                windPositions[i] = world;
            }
            wind.QueryWindVelocity(windPositions, windResults);

            Vector3 mean = Vector3.zero;
            for (int i = 0; i < 64; i++) mean += new Vector3(windResults[i].x, windResults[i].y, windResults[i].z);
            mean /= 64f;

            // The turbulence of the air follows the weather: a calm day swirls a little, a windy or gusty one a lot. The wind speed and how much
            // it differs from one end of the box to the other (gusts, thermals, the lee of a hill) both count.
            float gust = 0f;
            for (int i = 0; i < 64; i++) gust += (new Vector3(windResults[i].x, windResults[i].y, windResults[i].z) - mean).sqrMagnitude;
            gust = Mathf.Sqrt(gust / 64f);
            float weather = Mathf.Clamp(CalmTurbulence + 0.05f * mean.magnitude + 0.5f * gust, CalmTurbulence, 0.9f);
            c.sim.Params.turbulence = Mathf.Lerp(c.sim.Params.turbulence, weather, 1f - Mathf.Exp(-frame / 1.5f));

            float ramp = Mathf.SmoothStep(0f, 1f, (c.age - 1.0f * c.timeScale) / (0.8f * c.timeScale));
            Vector3 target = mean * (DriftShare * ramp);
            c.drift = Vector3.Lerp(c.drift, target, 1f - Mathf.Exp(-frame / 0.4f));

            Quaternion toGrid = Quaternion.Inverse(c.rotation);
            for (int i = 0; i < 64; i++)
                AmbientBuffer[i] = toGrid * (new Vector3(windResults[i].x, windResults[i].y, windResults[i].z) - c.drift);
            float peak = 0f;
            for (int i = 0; i < 64; i++) peak = Mathf.Max(peak, AmbientBuffer[i].magnitude);
            c.windPeak = Mathf.Max(c.windPeak, peak);
            c.sim.SetWind(AmbientBuffer);
        }

        // Frame time with and without clouds. In the automatic mode, a slow frame while clouds run lowers the quality of the NEXT clouds;
        // a long stretch of fast frames raises it again. Every ten seconds of activity a line goes to the log.
        private static void Monitor(float seconds)
        {
            float ms = seconds * 1000f;
            if (ms <= 0f || ms > 500f) return;
            frameEma = frameEma <= 0f ? ms : Mathf.Lerp(frameEma, ms, 0.05f);
            if (Active.Count == 0)
            {
                baselineEma = baselineEma <= 0f ? ms : Mathf.Lerp(baselineEma, ms, 0.02f);
                slowTimer = 0f;
                return;
            }

            if (Auto)
            {
                // Slow only counts when the frame time without clouds is known (it is measured whenever no cloud runs) and the clouds are
                // clearly what made it longer: more than 3 ms or a fifth above it.
                bool slow = baselineEma > 0f && frameEma > baselineEma + Mathf.Max(3f, 0.2f * baselineEma);
                bool fine = baselineEma <= 0f || frameEma < baselineEma + 1.5f;
                slowTimer = slow ? slowTimer + seconds : 0f;
                goodTimer = fine ? goodTimer + seconds : 0f;
                if (slowTimer > 1f && autoLevel < Levels.Length - 1)
                {
                    autoLevel++;
                    slowTimer = 0f; goodTimer = 0f;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke: frames are slow (" + frameEma.ToString("F1") + " ms, normally " + baselineEma.ToString("F1")
                                                     + " ms): quality of new clouds is now " + Levels[autoLevel].name);
                }
                else if (goodTimer > 12f && autoLevel > Chosen)
                {
                    autoLevel--;
                    goodTimer = 0f;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke: frames are fast again: quality of new clouds is now " + Levels[autoLevel].name);
                }
            }

            logTimer += seconds;
            if (logTimer > 10f)
            {
                logTimer = 0f;
                ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke: " + Active.Count + " cloud(s), frame " + frameEma.ToString("F1") + " ms (without clouds "
                    + baselineEma.ToString("F1") + " ms), the simulations cost " + (cpuMsCount > 0 ? (cpuMsSum / cpuMsCount).ToString("F2") : "0") + " ms of CPU time per frame, quality "
                    + QualityName);
                cpuMsSum = 0f; cpuMsCount = 0;
            }
        }

        // A finished simulation waits for the next shot of the same size; only a few are kept, so memory does not pile up.
        private const int MaxPooled = 5;
        // 16-bit fields (Smoke/HalfPrecision, on by default; only the ultra, high and medium levels use them): the speed, smoke, heat, noise coordinates
        // and pressure take half the memory and about 40% of the time of a step, and the clouds look the same (the difference is that of
        // turbulence from a 0.1% change). A graphics card that cannot write to those formats gets 32 bits.
        public static bool HalfPrecision { get { return ArmorFragmentsPlugin.SmokeHalfPrecision == null || ArmorFragmentsPlugin.SmokeHalfPrecision.Value; } }
        private static int halfChecked;       // 0 not asked, 1 supported, 2 not

        private static bool HalfSupported()
        {
            if (halfChecked == 0)
            {
                halfChecked = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                              && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RHalf) && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RHalf) ? 1 : 2;
                if (halfChecked == 2) ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke: this graphics card cannot write to 16-bit textures, so ultra, high and medium use 32 bits");
            }
            return halfChecked == 1;
        }

        private static Vector3Int PoolKey(Vector3Int dims, bool half) { return half ? new Vector3Int(dims.x, dims.y, dims.z + 100000) : dims; }

        // A simulation of this size: one from the pool if there is one of the same kind, else a new one.
        private static FluidSim Acquire(ComputeShader cs, Vector3Int dims, float cell, bool wantHalf)
        {
            bool half = wantHalf && HalfPrecision && HalfSupported();
            Stack<FluidSim> pool;
            if (Pools.TryGetValue(PoolKey(dims, half), out pool) && pool.Count > 0) return pool.Pop();
            FluidSim.HalfFields = half; FluidSim.HalfPhi = half; FluidSim.HalfPressure = half;
            try { return new FluidSim(cs, dims.x, dims.y, dims.z, cell); }
            finally { FluidSim.HalfFields = false; FluidSim.HalfPhi = false; FluidSim.HalfPressure = false; }
        }

        private static void ReturnToPool(FluidSim sim)
        {
            Stack<FluidSim> pool;
            Vector3Int key = PoolKey(sim.Dims, sim.IsHalf);
            if (!Pools.TryGetValue(key, out pool)) Pools[key] = pool = new Stack<FluidSim>();
            pool.Push(sim);
            int total = 0;
            foreach (Stack<FluidSim> p in Pools.Values) total += p.Count;
            if (total > MaxPooled)
            {
                // Throw away one of another size, or an extra of this one.
                foreach (KeyValuePair<Vector3Int, Stack<FluidSim>> pair in Pools)
                    if (pair.Value.Count > 0 && (pair.Key != sim.Dims || pair.Value.Count > 1)) { pair.Value.Pop().Dispose(); break; }
            }
        }

        private static void Retire(Cloud c)
        {
            foreach (Cloud other in Active) if (other.parent == c) other.parent = null;
            if (c.box != null) Object.Destroy(c.box);
            if (c.material != null) Object.Destroy(c.material);
            if (c.sim != null) ReturnToPool(c.sim);
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
            BarrelHeat.Shutdown();
            try
            {
                UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
                if (opaqueWasOff && asset != null) asset.supportsCameraOpaqueTexture = false;
            }
            catch (System.Exception) { }
            opaqueChecked = false; opaqueAvailable = false; opaqueWasOff = false;
            ShutdownTerrain();
            MuzzleFlash.Shutdown();
            Wash.Shutdown();
            GroundColor.Shutdown();
            foreach (Stack<FluidSim> pool in Pools.Values) while (pool.Count > 0) pool.Pop().Dispose();
            Pools.Clear();
            HullPlanes.Clear();
            if (windPositions.IsCreated) windPositions.Dispose();
            if (windResults.IsCreated) windResults.Dispose();
            LastShot.Clear();
            if (driver != null) Object.Destroy(driver.gameObject);
            driver = null;
            loggedFirst = false;
            frameEma = baselineEma = slowTimer = goodTimer = logTimer = cpuMsSum = 0f;
            cpuMsCount = 0;
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

        // ---- the world around the cloud -----------------------------------------------------------------------------------------

        private static readonly Collider[] Overlaps = new Collider[768];
        private static readonly List<FluidSim.Shape> ShapeBuffer = new List<FluidSim.Shape>(32);
        private static readonly List<KeyValuePair<float, int>> Candidates = new List<KeyValuePair<float, int>>(256);
        private static readonly List<Vector4> PlaneBuffer = new List<Vector4>(1024);
        private static readonly Dictionary<Mesh, Vector4[]> HullPlanes = new Dictionary<Mesh, Vector4[]>();      // null value = not a usable convex hull
        private const int MaxPlanesPerShape = 64;
        private const int MaxShapes = 256;

        // The solid things inside the box: the parts of vehicles (the shooter's own too, except its cannon) and the ground. Each is a box,
        // an ellipsoid or a half-space in the box's own space (see Fluid.compute).
        private static List<FluidSim.Shape> Obstacles(Cloud c)
        {
            ShapeBuffer.Clear();
            PlaneBuffer.Clear();
            peakSpeed = 0f;
            Matrix4x4 gridToWorld = Matrix4x4.TRS(c.origin, c.rotation, Vector3.one);
            Vector3 center = c.origin + c.rotation * (c.extent * 0.5f);
            Quaternion toGrid = Quaternion.Inverse(c.rotation);

            if (c.hasGround && !c.useHeight)
            {
                float height = Vector3.Dot(center - c.groundPoint, c.groundNormal);
                if (height < c.extent.magnitude * 0.5f)
                {
                    Matrix4x4 plane = Matrix4x4.TRS(c.groundPoint, Quaternion.FromToRotation(Vector3.up, c.groundNormal), Vector3.one);
                    // The ground stands still in the world, so it slides under the box at the wind's speed.
                    ShapeBuffer.Add(FluidSim.Shape.Make(plane.inverse * gridToWorld, Vector3.one, 2, toGrid * (-c.drift), Vector3.zero, Vector3.zero));
                }
            }

            int count = Physics.OverlapBoxNonAlloc(center, c.extent * 0.5f, Overlaps, c.rotation, ~0, QueryTriggerInteraction.Ignore);
            c.overlapsSeen = count; c.partsSeen = 0;
            c.crafts.Clear();

            // A vehicle has more colliders than the solver takes, so the ones nearest the muzzle go first (the gas is thickest there).
            Vector3 near = c.shots.Count > 0 ? c.shots[0].muzzleWorld : center;
            Candidates.Clear();
            for (int i = 0; i < count; i++)
            {
                Collider col = Overlaps[i];
                if (col == null) continue;
                PartScript part = col.GetComponentInParent<PartScript>();
                if (part == null)
                {
                    // Things standing on the ground (walls, buildings, rocks): solid like the parts, a little less important than them.
                    if (!WorldSolids || !IsWorldShape(col) || col.GetComponentInParent<CannonProjectileScript>() != null) continue;
                    Bounds wb = col.bounds;
                    float worldSize = wb.size.magnitude;
                    if (worldSize < c.sim.Cell) continue;
                    Candidates.Add(new KeyValuePair<float, int>(Mathf.Sqrt(wb.SqrDistance(near)) - 0.5f * worldSize + 6f, i));
                    continue;
                }
                c.partsSeen++;
                AircraftScript owner = part.Aircraft;
                if (owner != null)
                {
                    Bounds seen;
                    if (c.crafts.TryGetValue(owner, out seen)) { seen.Encapsulate(col.bounds); c.crafts[owner] = seen; }
                    else c.crafts[owner] = col.bounds;
                }
                if (part == c.cannonPart) continue;     // only vehicles; the cannon that fired does not block itself
                if (col.GetComponentInParent<CannonProjectileScript>() != null) continue;
                // The parts that matter most come first: big ones and ones near the muzzle (the distance from the surface of the part's box,
                // less half its size). Specks smaller than a cell of the box do not matter to the gas at all.
                Bounds bb = col.bounds;
                float size = bb.size.magnitude;
                if (size < c.sim.Cell) continue;
                Candidates.Add(new KeyValuePair<float, int>(Mathf.Sqrt(bb.SqrDistance(near)) - 0.5f * size, i));
            }
            Candidates.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int k = 0; k < Candidates.Count && ShapeBuffer.Count < MaxShapes; k++)
            {
                Collider col = Overlaps[Candidates[k].Value];
                FluidSim.Shape shape;
                if (ShapeOf(col, col.GetComponentInParent<PartScript>(), c, gridToWorld, toGrid, out shape)) ShapeBuffer.Add(shape);
            }
            c.shapesSent = ShapeBuffer.Count;
            c.shapePeak = Mathf.Max(c.shapePeak, peakSpeed);
            return ShapeBuffer;
        }

        private static bool ShapeOf(Collider col, PartScript part, Cloud c, Matrix4x4 gridToWorld, Quaternion toGrid, out FluidSim.Shape shape)
        {
            shape = new FluidSim.Shape();
            Vector3 half; int kind;
            Matrix4x4 ltw = col.transform.localToWorldMatrix;
            Matrix4x4 m;
            BoxCollider box = col as BoxCollider;
            SphereCollider sphere = col as SphereCollider;
            CapsuleCollider capsule = col as CapsuleCollider;
            MeshCollider mesh = col as MeshCollider;
            if (box != null)
            {
                m = ltw * Matrix4x4.TRS(box.center, Quaternion.identity, Vector3.one);
                half = box.size * 0.5f; kind = 0;
            }
            else if (sphere != null)
            {
                m = ltw * Matrix4x4.TRS(sphere.center, Quaternion.identity, Vector3.one);
                half = Vector3.one * sphere.radius; kind = 1;
            }
            else if (capsule != null)
            {
                m = ltw * Matrix4x4.TRS(capsule.center, Quaternion.identity, Vector3.one);
                float r = capsule.radius, h = Mathf.Max(capsule.height * 0.5f, r);
                half = capsule.direction == 0 ? new Vector3(h, r, r) : (capsule.direction == 1 ? new Vector3(r, h, r) : new Vector3(r, r, h));
                kind = 0;
            }
            else if (mesh != null && mesh.sharedMesh != null)
            {
                Bounds b = mesh.sharedMesh.bounds;
                m = ltw * Matrix4x4.TRS(b.center, Quaternion.identity, Vector3.one);
                half = b.extents; kind = 0;
                // The real convex shape the physics uses, not the box around it: a sloped hull is much smaller than its box.
                Vector4[] planes = ConvexPlanes(mesh.sharedMesh);
                if (planes != null && PlaneBuffer.Count + planes.Length <= 16384)
                {
                    Vector3 hullCenterWorld = m.GetColumn(3);
                    Vector3 hullVelocity = -c.drift, hullSpin = Vector3.zero;
                    Assets.Scripts.Craft.IRigidBody hullBody = part != null && part.Body != null ? part.Body.RigidBody : null;
                    if (hullBody != null && !hullBody.IsDead) { hullVelocity += SafeVelocity(part, hullBody.GetPointVelocity(hullCenterWorld)); hullSpin = SafeSpin(part, hullBody.angularVelocity, col.bounds.extents.magnitude); }
                    int first = PlaneBuffer.Count;
                    // The planes are measured from the middle of the mesh's box, which is where the shape's own space starts.
                    for (int i = 0; i < planes.Length; i++)
                        PlaneBuffer.Add(new Vector4(planes[i].x, planes[i].y, planes[i].z, planes[i].w + Vector3.Dot(new Vector3(planes[i].x, planes[i].y, planes[i].z), b.center)));
                    shape = FluidSim.Shape.Convex(m.inverse * gridToWorld, half, first, planes.Length, toGrid * hullVelocity, toGrid * hullSpin, toGrid * (hullCenterWorld - c.origin));
                    return true;
                }
            }
            else
            {
                Bounds b = col.bounds;
                m = Matrix4x4.TRS(b.center, Quaternion.identity, Vector3.one);
                half = b.extents; kind = 0;
            }

            // How the part moves: its velocity at the middle of the shape and its spin, in the box's space, relative to the box (which drifts
            // with the wind). The tank driving toward the gas pushes it; one standing still makes the wind blow around it.
            Vector3 centerWorld = m.GetColumn(3);
            Vector3 velocity = -c.drift, spin = Vector3.zero;
            Assets.Scripts.Craft.IRigidBody body = part != null && part.Body != null ? part.Body.RigidBody : null;
            if (body != null && !body.IsDead)
            {
                velocity += SafeVelocity(part, body.GetPointVelocity(centerWorld));
                spin = SafeSpin(part, body.angularVelocity, col.bounds.extents.magnitude);
            }
            shape = FluidSim.Shape.Make(m.inverse * gridToWorld, half, kind, toGrid * velocity, toGrid * spin, toGrid * (centerWorld - c.origin));
            return true;
        }

        // A part that reports an impossible speed (not a number, or far beyond anything a vehicle does) must not reach the solver: the gas
        // next to it would be thrown out of the box. Such a value counts as standing still, and the first few are written to the log.
        private const float MaxSolidSpeed = 60f;         // m/s
        private const float MaxSolidSpin = 40f;          // rad/s
        private static int rejectedVelocities;
        private static float peakSpeed;          // the fastest part speed accepted in the current pass over the shapes

        private static Vector3 SafeVelocity(PartScript part, Vector3 v)
        {
            if (!float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && v.sqrMagnitude <= MaxSolidSpeed * MaxSolidSpeed) { peakSpeed = Mathf.Max(peakSpeed, v.magnitude); return v; }
            Reject(part, "speed", v);
            return Vector3.zero;
        }

        // The spin of a part moves every point of it by spin x distance from its centre: a big part turning at 40 rad/s has points
        // moving at hundreds of metres a second. So what is limited is the speed of the part's surface, not only the spin.
        private const float MaxSurfaceSpeed = 30f;       // m/s
        private static int slowedSpins;

        private static Vector3 SafeSpin(PartScript part, Vector3 w, float radius)
        {
            if (float.IsNaN(w.x) || float.IsNaN(w.y) || float.IsNaN(w.z) || w.sqrMagnitude > MaxSolidSpin * MaxSolidSpin)
            {
                Reject(part, "spin", w);
                return Vector3.zero;
            }
            float surface = w.magnitude * Mathf.Max(radius, 0.01f);
            if (surface <= MaxSurfaceSpeed) { peakSpeed = Mathf.Max(peakSpeed, surface); return w; }
            if (++slowedSpins <= 6)
                ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke: part '" + (part != null && part.Part != null ? part.Part.Name : "?") + "' turns at " + w.magnitude.ToString("F1")
                                                 + " rad/s and is " + radius.ToString("F1") + " m across (its surface moves at " + surface.ToString("F0") + " m/s): the smoke sees it slowed down");
            peakSpeed = Mathf.Max(peakSpeed, MaxSurfaceSpeed);
            return w * (MaxSurfaceSpeed / surface);
        }

        private static void Reject(PartScript part, string what, Vector3 value)
        {
            if (++rejectedVelocities > 6) return;
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] fluid smoke: part '" + (part != null && part.Part != null ? part.Part.Name : "?") + "' reports an impossible " + what + " ("
                                             + value.ToString("F1") + "); the smoke treats it as standing still");
        }

        // The planes (outward normal, offset) that bound a convex mesh, in the mesh's own space, found once per mesh. Null when the mesh is not
        // readable or is not convex (then the box around it stands in).
        private static Vector4[] ConvexPlanes(Mesh mesh)
        {
            Vector4[] cached;
            if (HullPlanes.TryGetValue(mesh, out cached)) return cached;
            Vector4[] result = null;
            try
            {
                if (mesh.isReadable)
                {
                    Vector3[] v = mesh.vertices;
                    int[] t = mesh.triangles;
                    Vector3 middle = Vector3.zero;
                    for (int i = 0; i < v.Length; i++) middle += v[i];
                    if (v.Length > 3) middle /= v.Length;
                    List<Vector4> list = new List<Vector4>();
                    bool convex = v.Length >= 4;
                    for (int i = 0; i + 2 < t.Length && convex; i += 3)
                    {
                        Vector3 a = v[t[i]], b = v[t[i + 1]], c2 = v[t[i + 2]];
                        Vector3 n = Vector3.Cross(b - a, c2 - a);
                        if (n.sqrMagnitude < 1e-12f) continue;
                        n.Normalize();
                        float d = -Vector3.Dot(n, a);
                        if (Vector3.Dot(n, middle) + d > 0f) { n = -n; d = -d; }      // the middle of a convex solid is on the inside
                        bool duplicate = false;
                        for (int k = 0; k < list.Count && !duplicate; k++)
                            duplicate = Vector3.Dot(n, new Vector3(list[k].x, list[k].y, list[k].z)) > 0.9995f && Mathf.Abs(d - list[k].w) < 1e-3f;
                        if (!duplicate) list.Add(new Vector4(n.x, n.y, n.z, d));
                        if (list.Count > MaxPlanesPerShape) convex = false;
                    }
                    // Convex means every vertex lies behind every plane; a mesh that fails this is not what the physics uses as it is.
                    for (int k = 0; k < list.Count && convex; k++)
                        for (int i = 0; i < v.Length; i++)
                            if (list[k].x * v[i].x + list[k].y * v[i].y + list[k].z * v[i].z + list[k].w > 2e-3f) { convex = false; break; }
                    if (convex && list.Count >= 4) result = list.ToArray();
                }
            }
            catch (System.Exception) { result = null; }
            HullPlanes[mesh] = result;
            return result;
        }

        // The first thing below `from` that is not part of a vehicle: the ground (the terrain, a runway, a building roof...).
        private static bool FindGround(Vector3 from, PartScript own, out RaycastHit result)
        {
            result = default(RaycastHit);
            RaycastHit[] hits = Physics.RaycastAll(from + Vector3.up * 1f, Vector3.down, 40f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.GetComponentInParent<CannonProjectileScript>() != null) continue;
                if (hit.collider.GetComponentInParent<PartScript>() != null) continue;
                result = hit;
                return true;
            }
            return false;
        }

        // One shot: its muzzle in the box's space, and where its blast meets the ground (if the ground is close).
        private static Shot MakeShot(Vector3 tip, float start, Vector3 forward, float scale, CannonScript cannon)
        {
            Shot shot = new Shot { muzzleWorld = tip, start = start, flashWorld = tip + forward * (1.2f * scale) };
            RaycastHit hit;
            // The ground under the muzzle: where the blast starts to run along the ground, and how low the barrel is.
            if (FindGround(tip, cannon.PartScript, out hit))
            {
                Vector3 normal = hit.normal.sqrMagnitude > 0.5f ? hit.normal.normalized : Vector3.up;
                shot.hasGround = true;
                shot.groundNormal = normal;
                shot.footWorld = hit.point;
                shot.height = Mathf.Max(Vector3.Dot(tip - hit.point, normal), 0f);
                Color ground;
                if (SmokeGroundDust && GroundColor.TryGet(hit, out ground)) { shot.hasColor = true; shot.groundColor = ground; }
            }
            // Dust is lifted where the line of fire passes close above the ground: a few spots along it.
            for (int i = 0; i < 4; i++)
            {
                Vector3 point = tip + forward * ((2.4f + 2.2f * i) * scale);
                RaycastHit spot;
                if (!FindGround(point, cannon.PartScript, out spot)) continue;
                Vector3 n = spot.normal.sqrMagnitude > 0.5f ? spot.normal.normalized : Vector3.up;
                float height = Vector3.Dot(point - spot.point, n);
                float amount = Mathf.Clamp01(1f - height / (6f * scale)) * (1f - 0.12f * i);
                if (amount <= 0.02f) continue;
                shot.dustWorld[shot.dustCount] = spot.point;
                shot.dustAmount[shot.dustCount] = amount;
                shot.dustCount++;
                if (!shot.hasColor && SmokeGroundDust)
                {
                    Color ground;
                    if (GroundColor.TryGet(spot, out ground)) { shot.hasColor = true; shot.groundColor = ground; }
                }
                if (!shot.hasGround) { shot.hasGround = true; shot.groundNormal = n; shot.footWorld = spot.point; shot.height = Mathf.Max(height, 3f * scale); }
            }
            return shot;
        }

        // Ground whose colour cannot be read (a runway, a road, a building) lifts little dust, and what there is is pale grey.
        private const float UnknownGroundDust = 0.3f;

        // The colour of the dust: the ground's own colour, a little paler (a light grey when the ground cannot be read).
        private static Color DustColor(Cloud c)
        {
            foreach (Shot shot in c.shots)
                if (shot.hasColor) return GroundColor.DustFrom(shot.groundColor);
            return new Color(0.66f, 0.64f, 0.61f, 1f);
        }

        // The calibre of a cannon in millimetres (75 when the game does not say).
        internal static float CaliberMillimeters(CannonScript cannon)
        {
            if (cannon.PartScript != null)
            {
                foreach (PartModifierData modifier in cannon.PartScript.Part.Modifiers)
                {
                    CannonData data = modifier as CannonData;
                    if (data != null) return data.CaliberInMilimeters;
                }
            }
            return 75f;
        }

        // Whether the scene's depth texture exists, so the smoke can stop at solid things. If the render pipeline does not make it and the
        // camera is allowed, ask the main camera for it.
        private static bool depthChecked, depthAvailable;
        internal static bool DepthTextureAvailable()
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
