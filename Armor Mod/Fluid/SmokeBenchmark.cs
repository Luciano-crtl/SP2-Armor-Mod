using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using ArmorFragments.Fluid;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ArmorFragments
{
    // Measures what each part of the cannon smoke costs on this computer. Two halves:
    //  1. The solver alone: each stage runs many times and the card is waited for (a tiny readback), so the time is the card's own.
    //  2. A real cloud: the next shot's smoke is frozen and its parts (drawing, shadow, haze, solids, steps...) are switched one by one. Every
    //     frame the card is drained, so a frame that is short of processor time cannot hide what the card does.
    // The report goes to BepInEx/smoke-benchmark.txt and to the log ([SMOKEBENCH]).
    internal static class SmokeBenchmark
    {
        // What the smoke's own Tick reads while a cloud is frozen.
        public static bool Frozen;
        public static bool StepOn, SolidsOn, DrawOn, ShadowOn, HazeOn, CubicOn, LongRays, ShadowMapOn = true;
        public static string Status = "not run";

        // Processor time of the parts of the smoke's Tick, in ms (added up while the benchmark counts).
        public static readonly string[] CpuNames = { "wind and ground", "finding solids", "sending solids", "steps", "occupancy", "rest of drawing" };
        public static readonly double[] CpuMs = new double[6];
        private static long cpuMark;
        public static void CpuStart() { cpuMark = Stopwatch.GetTimestamp(); }
        public static void CpuLap(int part)
        {
            long now = Stopwatch.GetTimestamp();
            CpuMs[part] += (now - cpuMark) * 1000.0 / Stopwatch.Frequency;
            cpuMark = now;
        }

        private sealed class Runner : MonoBehaviour { }
        private static Runner runner;
        private static StringBuilder text;
        private static bool changedRate;
        private static int oldVSync, oldRate;

        public static bool Running { get { return runner != null; } }

        public static void Start()
        {
            if (Running) { Stop("cancelled"); return; }
            if (!FluidSmoke.Enabled) { Status = "turn the smoke on first"; return; }
            GameObject go = new GameObject("ArmorFragments smoke benchmark");
            UnityEngine.Object.DontDestroyOnLoad(go);
            runner = go.AddComponent<Runner>();
            runner.StartCoroutine(Guard(Run()));
        }

        public static void Stop(string status)
        {
            Frozen = false;
            ShadowMapOn = true;
            if (changedRate) { QualitySettings.vSyncCount = oldVSync; Application.targetFrameRate = oldRate; changedRate = false; }
            if (bench != null) { bench.Dispose(); bench = null; }
            if (runner != null) UnityEngine.Object.Destroy(runner.gameObject);
            runner = null;
            Status = status;
        }

        // Nothing that goes wrong may leave the smoke frozen. (Every step of Run is made here, so its errors land here.)
        private static IEnumerator Guard(IEnumerator inner)
        {
            while (true)
            {
                bool more;
                try { more = inner.MoveNext(); }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogError("[SMOKEBENCH] " + e);
                    Stop("failed (see the log)");
                    yield break;
                }
                if (!more) break;
                yield return inner.Current;
            }
        }

        private static void Line(string line)
        {
            text.AppendLine(line);
            ArmorFragmentsPlugin.Log.LogInfo("[SMOKEBENCH] " + line);
        }

        private static string Ms(double v) { return v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms"; }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0.0;
            values.Sort();
            return values[values.Count / 2];
        }

        private static FluidSim bench;

        // Waits until the card has done everything given to it.
        private static void Drain(RenderTexture texture)
        {
            AsyncGPUReadback.Request(texture, 0, 0, 1, 0, 1, 0, 1, null).WaitForCompletion();
        }

        private static double TimeOf(FluidSim sim, Action action, int times)
        {
            Drain(sim.Scalars);
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < times; i++) action();
            Drain(sim.Scalars);
            return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        // One action's time: what 8 more of it add to 2 of it (the cost of the waiting itself drops out).
        private static double Cost(FluidSim sim, Action action)
        {
            double few = TimeOf(sim, action, 2), many = TimeOf(sim, action, 10);
            return Math.Max(0.0, (many - few) / 8.0);
        }

        private static IEnumerator Run()
        {
            text = new StringBuilder();
            Line("Armor Fragments " + ArmorFragmentsPlugin.Version + " smoke benchmark");
            Line("graphics: " + SystemInfo.graphicsDeviceName + ", " + SystemInfo.graphicsDeviceVersion + ", " + SystemInfo.graphicsMemorySize
                + " MB; processor: " + SystemInfo.processorType + " (" + SystemInfo.processorCount + " threads)");
            UniversalRenderPipelineAsset urp = UniversalRenderPipeline.asset;
            Line("screen " + Screen.width + " x " + Screen.height + (urp != null ? ", render scale " + urp.renderScale.ToString("F2") + ", MSAA " + urp.msaaSampleCount
                + ", sun shadow map " + urp.mainLightShadowmapResolution + ", shadow distance " + urp.shadowDistance.ToString("F0") : ""));
            Line("settings: quality " + FluidSmoke.QualityName + ", box size " + ArmorFragmentsPlugin.SmokeBoxSize.Value + ", ray step " + ArmorFragmentsPlugin.SmokeRayStepCells.Value
                + " cells, at most " + ArmorFragmentsPlugin.SmokeRayStepMax.Value + " steps, 16 bits " + ArmorFragmentsPlugin.SmokeHalfPrecision.Value);

            if (!SystemInfo.supportsAsyncGPUReadback) { Line("this card cannot be timed (no readback)"); Stop("not possible here"); yield break; }

            oldVSync = QualitySettings.vSyncCount; oldRate = Application.targetFrameRate; changedRate = true;
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;

            Line("");
            Line("-- The solver alone, a 75 mm gun's box (graphics card time) --");
            string[] parts = { "forces", "pressure", "moving the air", "moving the smoke", "moving the detail", "damping and swirls" };
            int[] masks = { 1, 6, 8, 16, 32, 64 };
            for (int level = FluidSmoke.Levels.Length - 1; level >= 0; level--)
            {
                Status = "measuring the solver: " + FluidSmoke.Levels[level].name;
                yield return null;
                bench = FluidSmoke.MakeForBenchmark(75f, level);
                if (bench == null) { Line("the effects could not be loaded"); Stop("failed (see the log)"); yield break; }
                FluidSim sim = bench;
                List<FluidSim.Blob> blobs = new List<FluidSim.Blob>();
                Vector3 size = new Vector3(sim.Dims.x, sim.Dims.y, sim.Dims.z) * sim.Cell;
                for (int b = 0; b < 24; b++)
                    blobs.Add(new FluidSim.Blob { center = new Vector3(size.x * (0.25f + 0.02f * b), size.y * 0.4f, size.z * 0.5f), radius = size.y * 0.12f, rho = 0.5f, temp = 0.2f, vel = new Vector3(4f, 0f, 0f), falloff = 1f, swirl = 1f, swirlFreq = 1f });
                List<FluidSim.Shape> shapes = new List<FluidSim.Shape>();
                for (int b = 0; b < 128; b++)
                {
                    Vector3 at = new Vector3(size.x * (0.1f + 0.2f * ((b % 8) / 8f)), size.y * (0.05f + 0.25f * ((b / 8 % 4) / 4f)), size.z * (0.4f + 0.2f * ((b / 32) / 4f)));
                    shapes.Add(FluidSim.Shape.Make(Matrix4x4.Translate(-at), Vector3.one * 0.3f, 0, Vector3.zero, Vector3.zero, at));
                }
                sim.AddBlobs(blobs);
                for (int i = 0; i < 12; i++) { sim.Step(0.02f); if ((i & 3) == 3) yield return null; }

                List<double> full = new List<double>(), solids = new List<double>(), occ = new List<double>(), sources = new List<double>();
                List<double>[] each = new List<double>[parts.Length];
                for (int p = 0; p < parts.Length; p++) each[p] = new List<double>();
                for (int round = 0; round < 5; round++)
                {
                    sim.SkipParts = 0;
                    full.Add(Cost(sim, () => sim.Step(0.02f)));
                    yield return null;
                    for (int p = 0; p < parts.Length; p++)
                    {
                        sim.SkipParts = 127 & ~masks[p];
                        each[p].Add(Cost(sim, () => sim.Step(0.02f)));
                    }
                    sim.SkipParts = 0;
                    yield return null;
                    solids.Add(Cost(sim, () => sim.SetObstacles(shapes)));
                    occ.Add(Cost(sim, () => sim.UpdateOccupancy()));
                    sources.Add(Cost(sim, () => sim.AddBlobs(blobs)));
                    yield return null;
                }
                StringBuilder row = new StringBuilder();
                for (int p = 0; p < parts.Length; p++) row.Append(p > 0 ? ", " : "").Append(parts[p]).Append(' ').Append(Ms(Median(each[p])));
                Line(FluidSmoke.Levels[level].name + " (" + sim.Dims.x + "x" + sim.Dims.y + "x" + sim.Dims.z + " = " + (sim.Dims.x * sim.Dims.y * sim.Dims.z / 1000) + "k cells, "
                    + (sim.IsHalf ? "16" : "32") + " bits): one step " + Ms(Median(full)) + " [" + row + "]; 128 solids " + Ms(Median(solids)) + ", 24 sources " + Ms(Median(sources))
                    + ", empty-block map " + Ms(Median(occ)));
                bench.Dispose(); bench = null;
                yield return null;
            }

            // ---- A real cloud -------------------------------------------------------------------
            Line("");
            Status = "fire a cannon once, then don't touch the camera";
            float waitUntil = Time.unscaledTime + 60f;
            while (FluidSmoke.YoungestShotAge() > 1f)
            {
                if (Time.unscaledTime > waitUntil) { Line("no shot was fired"); Finish(); yield break; }
                yield return null;
            }
            float ready = Time.unscaledTime + 30f;
            while (FluidSmoke.YoungestShotAge() < 2.2f && Time.unscaledTime < ready) yield return null;      // time for the boxes it flows into
            FluidSmoke.LiveText(Line);
            RenderTexture fence = FluidSmoke.AnyScalars();
            if (fence == null) { Line("the cloud was gone before it could be measured"); Finish(); yield break; }
            Frozen = true;

            // The parts of the drawing are measured with the shadow off, so that the shadow's cost does not hide in them.
            string[] names =
            {
                "no smoke at all", "drawn", "drawn, shadow walked in every pixel (as in 0.13.0)", "drawn, no shadow",
                "drawn, no shadow, no heat haze", "drawn, no shadow, no cubic grid", "drawn, no shadow, ray steps not lengthened", "no smoke at all (again)",
                "solids only", "steps only", "steps and solids", "everything", "no smoke at all (last)"
            };
            //            draw  shadow haze  cubic long  solids step  map
            bool[][] on =
            {
                new[] { false, false, false, false, false, false, false, true },
                new[] { true, true, true, true, true, false, false, true },
                new[] { true, true, true, true, true, false, false, false },
                new[] { true, false, true, true, true, false, false, true },
                new[] { true, false, false, true, true, false, false, true },
                new[] { true, false, true, false, true, false, false, true },
                new[] { true, false, true, true, false, false, false, true },
                new[] { false, false, false, false, false, false, false, true },
                new[] { false, false, false, false, false, true, false, true },
                new[] { false, false, false, false, false, false, true, true },
                new[] { false, false, false, false, false, true, true, true },
                new[] { true, true, true, true, true, true, true, true },
                new[] { false, false, false, false, false, false, false, true },
            };
            int[] calmRows = { 0, 7, 12 };
            const int everything = 11;
            double[] frames = new double[names.Length];
            double[] cpu = null;
            for (int k = 0; k < names.Length; k++)
            {
                Status = "measuring the cloud: " + (k + 1) + " of " + names.Length + " (keep the camera still)";
                DrawOn = on[k][0]; ShadowOn = on[k][1]; HazeOn = on[k][2]; CubicOn = on[k][3]; LongRays = on[k][4]; SolidsOn = on[k][5]; StepOn = on[k][6]; ShadowMapOn = on[k][7];
                List<double> times = new List<double>();
                for (int f = 0; f < 70; f++)
                {
                    if (f == 10) Array.Clear(CpuMs, 0, CpuMs.Length);
                    yield return new WaitForEndOfFrame();
                    if (!FluidSmoke.Enabled || FluidSmoke.AnyScalars() == null) { Line("the cloud was lost while it was measured"); Finish(); yield break; }
                    Drain(FluidSmoke.AnyScalars());
                    if (f >= 10) times.Add(Time.unscaledDeltaTime * 1000.0);
                }
                frames[k] = Median(times);
                if (k == everything) { cpu = new double[CpuMs.Length]; for (int i = 0; i < cpu.Length; i++) cpu[i] = CpuMs[i] / 60.0; }
            }
            Frozen = false;
            ShadowMapOn = true;

            Line("-- The real cloud (whole frames, the card waited for every frame; the difference to no smoke) --");
            double calm = (frames[calmRows[0]] + frames[calmRows[1]] + frames[calmRows[2]]) / 3.0;
            for (int k = 0; k < names.Length; k++) Line(names[k] + ": frame " + Ms(frames[k]) + (Array.IndexOf(calmRows, k) >= 0 ? "" : ", +" + Ms(frames[k] - calm)));
            StringBuilder c = new StringBuilder();
            for (int i = 0; i < cpu.Length; i++) c.Append(i > 0 ? ", " : "").Append(CpuNames[i]).Append(' ').Append(Ms(cpu[i]));
            Line("processor time of the smoke in a frame: " + c);
            double spread = Math.Max(frames[calmRows[0]], Math.Max(frames[calmRows[1]], frames[calmRows[2]])) - Math.Min(frames[calmRows[0]], Math.Min(frames[calmRows[1]], frames[calmRows[2]]));
            Line("noise: the frames without smoke differed by " + Ms(spread) + "; differences smaller than that mean nothing");
            Finish();
        }

        private static void Finish()
        {
            string path = Path.Combine(Paths.BepInExRootPath, "smoke-benchmark.txt");
            try
            {
                File.WriteAllText(path, text.ToString());
                Stop("done: BepInEx/smoke-benchmark.txt");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[SMOKEBENCH] the report could not be written: " + e.Message);
                Stop("done (report in the log only)");
            }
        }
    }
}
