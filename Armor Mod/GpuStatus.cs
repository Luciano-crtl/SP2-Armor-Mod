using System;
using System.Threading;
using ArmorFragments.Sim;

namespace ArmorFragments
{
    // The graphics card, for the impact simulation (through OpenCL). Found out once, in a thread of its own (a driver can take its time and
    // the game must never wait for it): the solver is made (its kernels compiled from text) and TESTED against the CPU on two small
    // impacts; only if it gives the same verdicts and nearly the same marks does the simulation use it. The setting UseGpu is the player's
    // wish; whenever the card cannot be used (no OpenCL, a failure, a result that does not match) the CPU does the work, as it always did.
    //
    // The solver is handed to the damage worker's thread and used only there (an OpenCL queue is not thread safe); that thread also lets it
    // go when it ends.
    internal static class GpuStatus
    {
        private static Thread thread;
        private static volatile ClSolver solver;
        private static volatile bool tested;       // the check is over (the solver is usable if it is not null)
        private static volatile string note = "";  // what to log, once, when the check is over
        private static volatile bool logged;
        private static volatile bool stopping;
        private static bool startedOnce;
        private static readonly object Gate = new object();

        // What the panel shows.
        public static volatile string Text = "not checked yet";

        // The card's solver if it can be used now (null = use the CPU). Only the damage worker's thread may use it.
        public static ClSolver Solver
        {
            get { return ArmorFragmentsPlugin.UseGpu.Value && !stopping ? solver : null; }
        }

        public static void Start()
        {
            startedOnce = true;
            if (thread != null) return;
            if (!ArmorFragmentsPlugin.UseGpu.Value)
            {
                Text = "off (the setting)";
                return;
            }

            Text = "checking...";
            thread = new Thread(Check) { IsBackground = true, Name = "ArmorFragments gpu check", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        private static void Check()
        {
            ClSolver made = null;
            try
            {
                string error;
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                made = ClSolver.Create(out error);
                if (made == null)
                {
                    Finish(null, "the graphics card cannot be used, the CPU does the impact simulation: " + error);
                    return;
                }

                string mismatch = Compare(made);
                if (mismatch != null)
                {
                    made.Dispose();
                    Finish(null, "the graphics card's results do not match the CPU's, so the CPU is used: " + mismatch);
                    return;
                }

                string description = made.DeviceName + " (" + made.ComputeUnits + " compute units at " + made.ClockMhz + " MHz, " + made.MemoryMb + " MB" +
                                     (made.Discrete ? ", discrete" : ", shares memory with the CPU") + "); kernels built in " + made.BuildMs.ToString("F0") +
                                     " ms, checked against the CPU in " + watch.ElapsedMilliseconds + " ms in all";
                if (stopping) { made.Dispose(); return; }
                solver = made;   // from here on only the damage worker's thread uses it
                made = null;
                Finish(solver, description);
            }
            catch (Exception e)
            {
                if (made != null) made.Dispose();
                Finish(null, "the graphics card check failed, the CPU does the impact simulation: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Finish(ClSolver usable, string message)
        {
            note = (usable != null ? "[GPU] the impact simulation runs on " : "[GPU] ") + message;
            Text = usable != null ? "OpenCL on " + usable.DeviceName + " (" + usable.ComputeUnits + " CUs, " + usable.MemoryMb + " MB)" : "CPU (" + message + ")";
            tested = true;
        }

        // Two small impacts, one that goes through and one that is stopped, on the CPU and on the card: the same verdict, and about the same
        // depth and hole.
        private static string Compare(ClSolver card)
        {
            SimPool inline = new SimPool(0);
            float[][] cases =
            {
                new[] { 0.020f, 0f },   // a 50 mm shell through 20 mm, head-on
                new[] { 0.060f, 0f },   // ... and stopped by 60 mm
                new[] { 0.020f, 40f },  // ... and through 20 mm at an angle
            };
            foreach (float[] c in cases)
            {
                ImpactSimInput input = new ImpactSimInput
                {
                    CaliberM = 0.05f, ShellMassKg = 1.67f, SpeedMS = 750f, AngleDeg = c[1], DirU = 1f, DirV = 0f, ThicknessM = c[0],
                    PlateHardness = 100f, PlateBrittleness = 0.5f, ResistanceJPerM3 = 600f * 1e7f, KeptEnergy = 0.3f, EnergyScale = 1f,
                    Quality = 0, Seed = 7,
                };
                ImpactSimResult cpu = ImpactSim.Run(input, inline);
                ImpactSimResult gpu = ImpactSim.Run(input, inline, card);
                string label = "a " + (c[0] * 1000f).ToString("F0") + " mm plate at " + c[1].ToString("F0") + " deg";
                if (cpu.Perforated != gpu.Perforated) return label + ": the CPU says " + (cpu.Perforated ? "through" : "stopped") + ", the card " + (gpu.Perforated ? "through" : "stopped");
                if (Math.Abs(cpu.DepthM - gpu.DepthM) > 0.15f * Math.Max(cpu.DepthM, 0.002f)) return label + ": depth " + cpu.DepthM + " against " + gpu.DepthM;
                if (Math.Abs(cpu.HoleRadiusM - gpu.HoleRadiusM) > 0.25f * Math.Max(cpu.HoleRadiusM, 0.002f)) return label + ": hole radius " + cpu.HoleRadiusM + " against " + gpu.HoleRadiusM;
            }
            return null;
        }

        // Every frame (main thread): the log line, once, when the check is over (the log is not written from other threads).
        public static void Tick()
        {
            if (!startedOnce) return;
            if (thread == null) { Start(); return; }   // the player switched the setting on after the game started

            if (logged || !tested) return;
            logged = true;
            ArmorFragmentsPlugin.Log.LogInfo(note);
        }

        // The card failed during a simulation (the worker's thread says so): never use it again in this session.
        public static void Fail(string reason)
        {
            ClSolver broken = solver;
            solver = null;
            Text = "CPU (the graphics card failed: " + reason + ")";
            if (broken != null) broken.Dispose();   // called on the worker's thread, the one that used it
        }

        // The damage worker's thread is ending (or never started): lets the card go. Safe to call twice.
        public static void ReleaseSolver()
        {
            lock (Gate)
            {
                ClSolver held = solver;
                solver = null;
                if (held != null) held.Dispose();
            }
        }

        // The plugin is being unloaded. If the worker thread exists it lets the card go when it ends; if it never started, nobody else is using it.
        public static void Shutdown()
        {
            stopping = true;
            if (!DamageWorker.HasThread) ReleaseSolver();
        }
    }
}
