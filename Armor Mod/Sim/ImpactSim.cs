using System;
using System.Collections.Generic;

namespace ArmorFragments.Sim
{
    // What the simulation is told about one impact. Everything comes from the energy model's own numbers.
    internal struct ImpactSimInput
    {
        public float CaliberM;         // the shell's diameter
        public float ShellMassKg;
        public float SpeedMS;          // at the plate
        public float AngleDeg;         // from the plate's normal (0 = head-on)
        public float DirU, DirV;       // the way the shell is going along the plate (any length; 0,0 = along u)
        public float ThicknessM;       // the plate's thickness
        public float PlateHardness;    // percent, 100 = reference: how late the metal gives way
        public float PlateBrittleness; // 0..1: how early it cracks
        public float ResistanceJPerM3; // the energy it takes to push a cubic meter of plate out of the way
        public bool Ricochet;          // the model says the shell bounces: it only skims
        public float KeptEnergy;       // ricochet: the share of its energy the shell keeps
        public float DeflectionDeg;    // a shell that gets through is turned toward the plate's normal by this much (the energy model's own number)
        public float EnergyScale;      // 1 = the shell's real energy (a lab knob)
        public int Quality;            // 0 low, 1 medium, 2 high
        public int Columns;            // the grid's columns per side; 0 = the quality's own (40, 64 or 96). The game picks a smaller one when the quality's would take too long
        public int Seed;
        public ShellProfile Profile;   // the shape of the shell's nose (unset = the old hemisphere); it changes how the metal is pushed aside, never what it costs
        public int TimeLimitMs;        // give up after this long (0 = no limit)
        public Action<float> Progress; // told how far along it is (0..1) as it goes (the lab's progress bar); null = nobody asks
        public Func<bool> Cancelled;   // asked once a step: true = stop now (the result then says TimedOut). Null = never
    }

    internal sealed class ImpactSimResult
    {
        public int N;                  // columns per side
        public float Cell;             // meters per column
        public float Size;             // meters, the whole patch
        public float[] Front;          // height of the front surface over its old place (meters, + = out)
        public float[] Back;           // the same for the back surface (+ = out, the way a shell comes out)
        public byte[] Hole;            // 0 where there is no material, 255 elsewhere
        public float[] Solid;          // per column, 0..1: how much of the column's material is left (1 = all). A hole is where it falls under one half
        public float[] CrackFront;     // per column, 0..1: how torn the front surface is there (broken links across it)
        public float[] CrackBack;      // the same for the back surface
        public bool TimedOut;          // it took too long and was stopped: nothing else in the result is valid
        public bool Perforated;        // the shell got through
        public float DepthM;           // the deepest dent (the whole thickness if it is through)
        public float HoleRadiusM;      // of the hole, if any
        public float EnergyLeft;       // share of the shell's energy that was not spent
        public int Particles;
        public int Steps;
        public int Broken;             // links that tore
        public int Charged;            // particles the shell displaced (each cost it ResistanceJPerM3 x its volume)
        public double Ms;
        public string Breakdown;       // where the time went, for the [PERF] log (null when not measured)
    }

    // A SIMPLIFIED IMPACT SIMULATION, MADE TO LOOK RIGHT, NOT TO BE ENGINEERING-ACCURATE.
    //
    // A small patch of the plate is a 3D lattice of particles joined by links (6 along the axes and 12 along the
    // diagonals of its faces). The shell is a rigid capsule that moves through it. Position-based dynamics, with
    // the particles taken color by color so that each color can be worked out on all threads at once without any
    // two particles that are linked changing at the same time (so the result does not depend on the thread count).
    //
    //  - The shell pushes particles out of its way. Every particle it displaces costs it ResistanceJPerM3 x the
    //    particle's volume (once): the same price per volume the energy model charges (ArmorResistance), so a shell
    //    that has the energy for the plug goes through and one that has not stops, as the model says.
    //  - A link that stretches past the yield strain stays stretched (plastic flow: lips, bulges). Past the break
    //    strain it breaks (cracks, petals, a torn exit). A brittle plate breaks early, a ductile one flows.
    //  - After the shell has gone (or stopped) the lattice settles; the surface heights of the front and back and the
    //    places where nothing is left (the hole) are read from where the particles ended up.
    internal sealed class ImpactSim
    {
        private const int Links = 18;
        private static readonly int[] Ox = { 1, -1, 0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1, 0, 0, 0, 0 };
        private static readonly int[] Oy = { 0, 0, 1, -1, 0, 0, 1, -1, 1, -1, 0, 0, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] Oz = { 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1 };

        // ---- Grid ---------------------------------------------------------------------------------
        private int n, k, count;
        private float h, hz, t, size;
        private float[] px, py, pz, qx, qy, qz, vx, vy, vz;
        private float[] ox, oy, oz;    // where each particle started (the shell is charged by what it sweeps through)
        private float[] rest;          // count * Links; 0 or less = no link (or broken)
        private float[] rest0;         // the rest lengths the links started with (plastic flow lengthens them)
        private int[] nbr;             // count * Links
        private byte[] fixedFlag, charged;
        private int[][] colors;
        private float[] chunkCharged;

        // ---- Shell --------------------------------------------------------------------------------
        private float cx, cy, cz;      // the center of the capsule's nose sphere
        private float ax, ay, az;      // its direction
        private float tx0, ty0, tz0;   // where its tip started
        private float ax0, ay0, az0;   // the direction it started with (the tunnel it digs is along this)
        private float tipS;            // how far its tip has gone along that line
        private float radius, bodyLength;
        private float noseLen, noseP;  // the nose's profile (see ShellProfile): its length in meters (0 = the old hemisphere) and its exponent

        // ---- Material -----------------------------------------------------------------------------
        private float yieldStrain, breakStrain, ductility, plasticRate;
        private float particleRadius;
        private float deepestTip;      // the lowest the shell's tip has been
        private float plateBrittle;    // 0..1, the plate's brittleness (what the cracks are grown with)
        private int simSeed;
        private bool isRicochet;
        private bool backOpen;         // the shell's tip has reached the back of the plate: only then may the back layer tear
        private float breakReach2;     // links only break close to the shell (squared); far away the plate bends but does not tear
        private float[] baseTop, baseBottom;
        private int[] baseCover;

        // The graphics card, when it does the particles' work (the shell's logic stays here either way).
        private ClSolver gpu;
        private int gpuCharged;
        private readonly float[] gpuParams = new float[ClSolver.ParamCount];

        // On the card, the plate is allowed to stop settling as soon as no particle moves faster than SettleCalm x the shell's speed (after at
        // least a few steps), instead of always taking all 24. (A switch only so a test can compare the two.)
        public static bool EarlySettle = true;
        public static int IterationsOverride = 0;   // a test can ask for another number of relaxation iterations a step (0 = the normal one)
        public static float SettleCalm = 0.02f;

        public static ImpactSimResult Run(ImpactSimInput input, SimPool pool)
        {
            return Run(input, pool, null);
        }

        // With a solver the particles are worked out on the graphics card; without one, on the pool's threads. Any failure of the card
        // is an exception (the caller goes back to the CPU).
        public static ImpactSimResult Run(ImpactSimInput input, SimPool pool, ClSolver gpu)
        {
            ImpactSim sim = new ImpactSim();
            sim.gpu = gpu;
            return sim.Execute(input, pool);
        }

        // The columns per side of a quality's own grid.
        public static int ColumnsOfQuality(int quality)
        {
            return quality <= 0 ? 40 : quality == 1 ? 64 : quality == 2 ? 96 : 128;
        }

        public const int MaxQuality = 3;

        public static string QualityName(int quality)
        {
            return quality <= 0 ? "low" : quality == 1 ? "medium" : quality == 2 ? "high" : "ultra";
        }

        // The columns that are worth having for a map with so many pixels a meter. The map shows nothing finer than its pixels, so a cell
        // under half a pixel is work that nobody sees: the grid needs at most (patch width x pixels per meter x 2) columns. (At 120 px/m,
        // which a vehicle with many plates gets, that is 84 columns for a 50 mm shell: the 96 columns of "high" already out-resolve it.)
        // Returns int.MaxValue when the density is not known. Never under 32.
        public static int UsefulColumns(float caliberM, float angleDeg, float pixelsPerMeter)
        {
            if (pixelsPerMeter <= 0f) return int.MaxValue;
            float width = PatchSize(caliberM, angleDeg);
            return Math.Max(32, (int)Math.Ceiling(width * pixelsPerMeter * 2f));
        }

        // How wide (meters) the patch of plate is that is simulated: so many calibres, never under 10 cm. Measured (the damage reaches 1.7 to
        // 2.7 calibres from the impact head-on, and 3.4 to 4 or more at 60 degrees and beyond, where it ran into the patch's fixed border and
        // a ricochet's groove was cut short at 40% of the patch), 7 calibres is about the least that holds a head-on hit, and a slanted one
        // gets a wider patch: 7 up to 40 degrees, 11 at 80 (the cells are then a little coarser, which the ultra qualities make up for).
        public static float PatchCalibers = 7f;

        // Radial cracks (see GrowCracks): how many a fully brittle plate gets (+ 1), the plastic stretch, as a share of what the plate can
        // take before it tears, that a crack needs to run through a place (for a plate with no brittleness; the brittle ones need less by
        // CrackSoft), and the chance, per step of a crack, that it branches.
        public static bool GrowCracksOn = true;
        public static float CrackCount = 7f;
        public static float CrackThreshold = 0.06f;
        public static float CrackSoft = 0.35f;
        public static float CrackBranch = 0.045f;

        public static float PatchSize(float caliberM, float angleDeg)
        {
            float r = Math.Max(caliberM * 0.5f, 0.002f);
            float slant = Math.Min(Math.Max((angleDeg - 40f) / 40f, 0f), 1f);
            return Math.Max(r * 2f * (PatchCalibers + 4f * slant), 0.1f);
        }

        // How many layers deep the lattice is for a shell of this caliber and a plate of this thickness, on a grid of 'columns'.
        // (The cost of a run grows with columns^2 x layers x the steps it takes, and the steps grow with the columns too.)
        public static int LayersFor(float caliberM, float thicknessM, int columns, float angleDeg = 0f)
        {
            float r = Math.Max(caliberM * 0.5f, 0.002f);
            float thickness = Math.Max(thicknessM, 0.002f);
            float cell = PatchSize(caliberM, angleDeg) / columns;
            return Math.Max(2, Math.Min(14, (int)MathF.Round(thickness / cell)));
        }

        private ImpactSimResult Execute(ImpactSimInput input, SimPool pool)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            // ---- The patch --------------------------------------------------------------------
            radius = Math.Max(input.CaliberM * 0.5f, 0.002f);
            t = Math.Max(input.ThicknessM, 0.002f);
            n = input.Columns >= 16 ? input.Columns : ColumnsOfQuality(input.Quality);
            size = PatchSize(input.CaliberM, input.AngleDeg);
            h = size / n;
            k = LayersFor(input.CaliberM, input.ThicknessM, n, input.AngleDeg);
            hz = t / k;
            count = n * n * k;
            particleRadius = 0.35f * Math.Min(h, hz);
            bodyLength = radius * 2.5f;
            noseLen = input.Profile.IsSet ? Math.Min(input.Profile.NoseLength * radius, 0.9f * (radius + bodyLength)) : 0f;
            noseP = Math.Max(input.Profile.Fullness, 1f);

            float hardness = Math.Max(input.PlateHardness, 10f) / 100f;
            float brittle = Math.Min(Math.Max(input.PlateBrittleness, 0f), 1f);
            plateBrittle = brittle;
            simSeed = input.Seed;
            isRicochet = input.Ricochet;
            yieldStrain = 0.02f * MathF.Sqrt(hardness);
            float give = Math.Min(Math.Max(1f / MathF.Sqrt(hardness), 0.6f), 1.3f);   // a harder plate gives a little less
            breakStrain = (1.2f + (0.12f - 1.2f) * brittle) * give;   // how much a link can be stretched at once
            ductility = (1.5f + (0.10f - 1.5f) * brittle) * give;     // how much it can be lengthened by flowing, in all
            plasticRate = 0.25f;

            Allocate();
            Build(input.Seed);
            Surfaces(out baseTop, out baseBottom, out baseCover);   // the plate before the shot (its lattice is not perfectly smooth)
            double setupMs = watch.Elapsed.TotalMilliseconds;

            // ---- The shell --------------------------------------------------------------------
            float angle = Math.Min(Math.Max(input.AngleDeg, 0f), 85f) * (float)(Math.PI / 180.0);
            float du = input.DirU, dv = input.DirV;
            float dl = MathF.Sqrt(du * du + dv * dv);
            if (dl < 1e-4f) { du = 1f; dv = 0f; } else { du /= dl; dv /= dl; }
            ax = MathF.Sin(angle) * du;
            ay = MathF.Sin(angle) * dv;
            az = -MathF.Cos(angle);
            float deflection = input.Ricochet ? 0f : Math.Min(Math.Max(input.DeflectionDeg, 0f), 30f) * (float)(Math.PI / 180.0);

            // The tip starts a few cells above the point of impact on the front surface.
            float start = radius + 3f * h;
            cx = -ax * start;
            cy = -ay * start;
            cz = t * 0.5f - az * start;
            ax0 = ax; ay0 = ay; az0 = az;
            deepestTip = t * 0.5f;
            tx0 = cx + ax * radius; ty0 = cy + ay * radius; tz0 = cz + az * radius;

            float speed0 = Math.Max(input.SpeedMS, 1f);
            double energy0 = 0.5 * input.ShellMassKg * speed0 * speed0 * Math.Max(input.EnergyScale, 0.0001f);
            double energy = energy0;
            float cellVolume = h * h * hz;
            double costPerParticle = input.ResistanceJPerM3 * cellVolume;

            float dt = 0.4f * h / speed0;
            int iterations = IterationsOverride > 0 ? IterationsOverride : (input.Quality <= 0 ? 4 : 5);
            float depthLimit = Math.Min(0.45f * radius, 0.5f * t);
            bool bounced = false, skimming = false;
            float skimLeft = 0f;
            float sin = MathF.Sin(angle);
            float skimLength = Math.Min(Math.Max(radius * (2f + 8f * sin * sin), radius * 2f), size * 0.4f);
            breakReach2 = (3f * radius) * (3f * radius);
            float keptSpeedFactor = MathF.Sqrt(Math.Min(Math.Max(input.KeptEnergy, 0.02f), 1f));

            if (gpu != null) gpu.Begin(px, py, pz, ox, oy, oz, fixedFlag, n, k);

            bool shellActive = true;
            bool stopped = false;
            bool exited = false;
            int steps = 0, settle = 0, stoppedSteps = 0, shellSteps = 0;
            const int MaxActive = 600;
            const int SettleSteps = 24;

            // ---- The whole simulation on the card: the shell's logic too, queued in chunks of steps ------------------
            bool deviceShell = gpu != null && gpu.DeviceShell;
            if (deviceShell)
            {
                float[] state = gpuParams;
                FillParams(dt, 0.995f, false);
                state[ClSolver.PMass] = input.ShellMassKg; state[ClSolver.PSpeed0] = speed0; state[ClSolver.PEnergy0] = (float)energy0;
                state[ClSolver.PCost] = (float)costPerParticle; state[ClSolver.PThickness] = t; state[ClSolver.PHz] = hz;
                state[ClSolver.PDepthLimit] = depthLimit; state[ClSolver.PSkimLength] = skimLength;
                state[ClSolver.PRicochet] = input.Ricochet ? 1f : 0f; state[ClSolver.PKept] = input.KeptEnergy;
                state[ClSolver.PDu] = du; state[ClSolver.PDv] = dv;
                state[ClSolver.PEps] = EarlySettle ? SettleCalm * speed0 : 0f; state[ClSolver.PMinSettle] = 4f;
                state[ClSolver.PDeflect] = deflection;
                state[ClSolver.PEnergy] = (float)energy0; state[ClSolver.PShellActive] = 1f; state[ClSolver.PDeepest] = deepestTip;
                gpu.WriteParams(state);

                const int ChunkSteps = 16;
                while (true)
                {
                    if ((input.TimeLimitMs > 0 && watch.ElapsedMilliseconds > input.TimeLimitMs) ||
                        (input.Cancelled != null && input.Cancelled()))
                        return new ImpactSimResult { TimedOut = true, Steps = (int)state[ClSolver.PSteps], Particles = count, Ms = watch.Elapsed.TotalMilliseconds };
                    bool finished = gpu.RunSteps(ChunkSteps, iterations, state);
                    if (input.Progress != null)
                        input.Progress(ProgressOf(state[ClSolver.PShellActive] > 0.5f, state[ClSolver.PCz] + state[ClSolver.PAz] * state[ClSolver.PRadius], (int)state[ClSolver.PSettle]));
                    if (finished) break;
                }
                energy = state[ClSolver.PEnergy];
                stopped = state[ClSolver.PStopped] > 0.5f;
                exited = state[ClSolver.PExited] > 0.5f;
                deepestTip = state[ClSolver.PDeepest];
                steps = (int)state[ClSolver.PSteps];
                shellSteps = (int)state[ClSolver.PShellSteps];
                gpuCharged = (int)state[ClSolver.PLastCount];
            }

            while (!deviceShell && steps < MaxActive + SettleSteps)
            {
                if ((input.TimeLimitMs > 0 && watch.ElapsedMilliseconds > input.TimeLimitMs) ||
                    (input.Cancelled != null && input.Cancelled()))
                    return new ImpactSimResult { TimedOut = true, Steps = steps, Particles = count, Ms = watch.Elapsed.TotalMilliseconds };

                // Where the shell is this step.
                float speed = 0f;
                if (shellActive)
                {
                    if (energy <= 0.0 || (energy < energy0 * 0.0004)) { stopped = true; energy = 0.0; }
                    else
                    {
                        speed = (float)Math.Sqrt(2.0 * energy / input.ShellMassKg);
                        speed = Math.Min(speed, speed0 * 1.0001f);
                    }
                    cx += ax * speed * dt;
                    cy += ay * speed * dt;
                    cz += az * speed * dt;

                    float tipZ = cz + az * radius;
                    tipS = (cx + ax * radius - tx0) * ax0 + (cy + ay * radius - ty0) * ay0 + (tipZ - tz0) * az0;
                    // The plate turns the shell toward its own normal as it goes through (the energy model's deflection, spread over the thickness).
                    if (deflection > 0f && !bounced && !skimming)
                    {
                        float progress = Math.Min(Math.Max((t * 0.5f - tipZ) / t, 0f), 1f);
                        float theta = Math.Max(angle - deflection * progress, 0f);
                        float sn = MathF.Sin(theta);
                        ax = sn * du; ay = sn * dv; az = -MathF.Cos(theta);
                    }
                    // A shell that ricochets digs in to a shallow depth, skims along the plate for a while (the groove), then leaves.
                    if (input.Ricochet && !bounced && !skimming && (t * 0.5f - tipZ) >= depthLimit)
                    {
                        skimming = true;
                        float lateral = MathF.Sqrt(ax * ax + ay * ay);
                        if (lateral < 1e-4f) { ax = du; ay = dv; lateral = 1f; }
                        ax /= lateral; ay /= lateral; az = 0f;
                        skimLeft = skimLength;
                    }
                    if (skimming)
                    {
                        skimLeft -= speed * dt;
                        if (skimLeft <= 0f)
                        {
                            skimming = false;
                            bounced = true;
                            az = 0.5f;                     // it turns up and leaves
                            float norm = MathF.Sqrt(ax * ax + ay * ay + az * az);
                            ax /= norm; ay /= norm; az /= norm;
                            energy *= input.KeptEnergy;
                        }
                    }
                    if (tipZ <= -t * 0.5f + hz) backOpen = true;
                    if (tipZ < deepestTip) deepestTip = tipZ;
                    if (az < 0f && tipZ < -t * 0.5f - radius * 1.2f) { exited = true; shellActive = false; }
                    if (az > 0f && bounced && tipZ > t * 0.5f + radius * 1.5f) { shellActive = false; }
                    if (stopped)
                    {
                        // It stays where it is, in the way, for a moment while the plate takes up the blow; then it is gone.
                        shellActive = ++stoppedSteps <= 8;
                    }
                    if (steps >= MaxActive) shellActive = false;
                }
                else
                {
                    settle++;
                    if (settle > SettleSteps) break;
                }

                if (input.Progress != null) input.Progress(ProgressOf(shellActive, cz + az * radius, settle));
                bool hasShell = shellActive;
                if (hasShell) shellSteps++;
                float damp = shellActive ? 0.995f : 0.85f;

                float chargedNow = 0f;
                if (gpu != null)
                {
                    // ---- The same work, on the graphics card ---------------------------------------
                    FillParams(dt, damp, hasShell);
                    int cumulative = gpu.Step(gpuParams, hasShell, iterations);
                    chargedNow = cumulative - gpuCharged;
                    gpuCharged = cumulative;
                }
                else
                {
                    // ---- Predict, and push the particles out of the shell's way ------------------
                    int chunks = (count + 2047) / 2048;
                    if (chunkCharged == null || chunkCharged.Length < chunks) chunkCharged = new float[chunks];
                    Array.Clear(chunkCharged, 0, chunkCharged.Length);
                    float dtLocal = dt;
                    pool.For(count, 2048, (s, e, c) => PredictAndPush(s, e, c, dtLocal, hasShell));

                    // ---- Tearing: the links the push has stretched past what the metal can take ------
                    pool.For(count, 2048, (s, e, c) => Tear(s, e));

                    // ---- Links ---------------------------------------------------------------------
                    for (int it = 0; it < iterations; it++)
                    {
                        for (int color = 0; color < 8; color++)
                        {
                            int[] list = colors[color];
                            pool.For(list.Length, 1024, (s, e, c) => Relax(list, s, e));
                        }
                    }
                    if (hasShell) pool.For(count, 2048, (s, e, c) => Push(s, e, c));

                    // ---- Plastic flow, then the new velocities ---------------------------------------
                    pool.For(count, 2048, (s, e, c) => Flow(s, e));
                    float dampLocal = damp;
                    pool.For(count, 2048, (s, e, c) => Finish(s, e, dtLocal, dampLocal));

                    for (int c = 0; c < chunks; c++) chargedNow += chunkCharged[c];
                }

                if (shellActive) energy -= chargedNow * costPerParticle;
                steps++;
            }

            int gpuBroken = 0;
            if (gpu != null) gpu.End(px, py, pz, out gpuBroken);   // the particles come back to be read as they are now
            int[] gpuCracks = null;
            float[] gpuStretch = null;
            if (gpu != null)
            {
                gpuCracks = new int[2 * n * n];
                gpuStretch = new float[2 * n * n];
                gpu.ReadCracks(n, k, gpuCracks, gpuStretch);
            }

            double loopMs = watch.Elapsed.TotalMilliseconds;
            ImpactSimResult result = Sample(exited || (energy > 0 && !stopped && az < 0f && exited), input.EnergyScale);
            double sampleMs = watch.Elapsed.TotalMilliseconds - loopMs;
            FillCracks(result, gpuCracks, gpuStretch);
            result.EnergyLeft = (float)Math.Max(energy / energy0, 0.0);
            result.Particles = count;
            result.Steps = steps;
            int chargedCount = 0;
            int brokenLinks = 0;
            if (gpu != null)
            {
                chargedCount = gpuCharged;
                result.Broken = gpuBroken;
            }
            else
            {
                for (int c = 0; c < count; c++) if (charged[c] != 0) chargedCount++;
                for (int slot = 0; slot < rest.Length; slot++) if (rest[slot] < 0f) brokenLinks++;
                result.Broken = brokenLinks / 2;
            }
            result.Charged = chargedCount;
            result.Ms = watch.Elapsed.TotalMilliseconds;

            double perTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string breakdown = "lattice " + setupMs.ToString("F1") + " ms; " + shellSteps + " steps with the shell + " + (steps - shellSteps) + " settling";
            if (gpu != null)
            {
                breakdown += "; card: start " + (gpu.TicksBegin * perTick).ToString("F1") + " ms, numbers upload (waits for the step before) " +
                    (gpu.TicksParamWrite * perTick).ToString("F1") + " ms, queueing " + (gpu.TicksDispatch * perTick).ToString("F1") +
                    " ms, wait for the card (" + gpu.SyncedSteps + " waits" + (gpu.DeviceShell ? ", shell on the card" : "") + ") " + (gpu.TicksCounterRead * perTick).ToString("F1") +
                    " ms, finish " + (gpu.TicksFinish * perTick).ToString("F1") + " ms, read back " + (gpu.TicksReadback * perTick).ToString("F1") + " ms";
            }
            else
            {
                breakdown += "; stepping " + (loopMs - setupMs).ToString("F1") + " ms";
            }
            breakdown += "; reading the surfaces " + sampleMs.ToString("F1") + " ms";
            result.Breakdown = breakdown;
            return result;
        }

        // How far along the simulation is, 0..1: while the shell is in the plate, how far through it the tip has gone (85% of the way); then
        // how many of the settling steps have been done. Never goes back (a shell that stops jumps ahead to the settling).
        private float lastProgress;

        private float ProgressOf(bool shellActive, float tipZ, int settle)
        {
            float p = shellActive
                ? 0.85f * Math.Min(Math.Max((t * 0.5f - tipZ) / (t + 2.4f * radius), 0f), 1f)
                : 0.85f + 0.15f * Math.Min(settle / 24f, 1f);
            if (p > lastProgress) lastProgress = p;
            return lastProgress;
        }

        // The shell's state and the material's numbers, for the graphics card's kernels (a step's worth).
        private void FillParams(float dt, float damp, bool hasShell)
        {
            float[] g = gpuParams;
            g[ClSolver.PCx] = cx; g[ClSolver.PCy] = cy; g[ClSolver.PCz] = cz;
            g[ClSolver.PAx] = ax; g[ClSolver.PAy] = ay; g[ClSolver.PAz] = az;
            g[ClSolver.PTipS] = tipS;
            g[ClSolver.PTx0] = tx0; g[ClSolver.PTy0] = ty0; g[ClSolver.PTz0] = tz0;
            g[ClSolver.PAx0] = ax0; g[ClSolver.PAy0] = ay0; g[ClSolver.PAz0] = az0;
            g[ClSolver.PRadius] = radius; g[ClSolver.PBody] = bodyLength; g[ClSolver.PParticle] = particleRadius;
            g[ClSolver.PNoseLen] = noseLen; g[ClSolver.PNoseP] = noseP;
            g[ClSolver.PH] = h; g[ClSolver.PDt] = dt; g[ClSolver.PDamp] = damp;
            g[ClSolver.PHasShell] = hasShell ? 1f : 0f; g[ClSolver.PBackOpen] = backOpen ? 1f : 0f;
            g[ClSolver.PBreak] = breakStrain; g[ClSolver.PYield] = yieldStrain; g[ClSolver.PRate] = plasticRate;
            g[ClSolver.PDuctility] = ductility; g[ClSolver.PReach2] = breakReach2;
        }

        // ---- Setup --------------------------------------------------------------------------------

        // The card keeps the links, the velocities and the rest of the particles' state itself: only where the particles start (and, at the
        // end, where they are) is needed here. So with a card the big arrays (the links alone are 3 x 18 numbers a particle, tens of
        // megabytes, and made the garbage collector stall) are not made at all.
        private void Allocate()
        {
            px = new float[count]; py = new float[count]; pz = new float[count];
            ox = new float[count]; oy = new float[count]; oz = new float[count];
            fixedFlag = new byte[count];
            if (gpu != null) return;
            qx = new float[count]; qy = new float[count]; qz = new float[count];
            vx = new float[count]; vy = new float[count]; vz = new float[count];
            rest = new float[count * Links];
            rest0 = new float[count * Links];
            nbr = new int[count * Links];
            charged = new byte[count];
        }

        private void Build(int seed)
        {
            uint state = (uint)(seed * 747796405 + 2891336453);
            Func<float> noise = () =>
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                return ((state & 0xFFFFFF) / 16777216f) * 2f - 1f;
            };

            for (int kk = 0; kk < k; kk++)
            {
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int idx = (kk * n + j) * n + i;
                        bool edge = i == 0 || j == 0 || i == n - 1 || j == n - 1;
                        float jitter = edge ? 0f : 0.12f;
                        px[idx] = (i - (n - 1) * 0.5f) * h + noise() * h * jitter;
                        py[idx] = (j - (n - 1) * 0.5f) * h + noise() * h * jitter;
                        pz[idx] = -t * 0.5f + (kk + 0.5f) * hz; noise();   // (no jitter across the thickness: the surface heights would be noisy)
                        if (gpu == null) { qx[idx] = px[idx]; qy[idx] = py[idx]; qz[idx] = pz[idx]; }
                        ox[idx] = px[idx]; oy[idx] = py[idx]; oz[idx] = pz[idx];
                        fixedFlag[idx] = (byte)(edge ? 1 : 0);
                    }
                }
            }

            if (gpu != null) return;   // the card makes the links and the colors itself

            int[] colorCounts = new int[8];
            for (int idx = 0; idx < count; idx++)
            {
                int i = idx % n, j = (idx / n) % n, kk = idx / (n * n);
                colorCounts[(i & 1) | ((j & 1) << 1) | ((kk & 1) << 2)]++;
            }
            colors = new int[8][];
            for (int c = 0; c < 8; c++) colors[c] = new int[colorCounts[c]];
            int[] fill = new int[8];

            for (int idx = 0; idx < count; idx++)
            {
                int i = idx % n, j = (idx / n) % n, kk = idx / (n * n);
                int color = (i & 1) | ((j & 1) << 1) | ((kk & 1) << 2);
                colors[color][fill[color]++] = idx;

                for (int o = 0; o < Links; o++)
                {
                    int ni = i + Ox[o], nj = j + Oy[o], nk = kk + Oz[o];
                    int slot = idx * Links + o;
                    if (ni < 0 || nj < 0 || nk < 0 || ni >= n || nj >= n || nk >= k) { nbr[slot] = -1; rest[slot] = 0f; continue; }
                    int other = (nk * n + nj) * n + ni;
                    nbr[slot] = other;
                    float dx = px[other] - px[idx], dy = py[other] - py[idx], dz = pz[other] - pz[idx];
                    rest[slot] = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    rest0[slot] = rest[slot];
                }
            }
        }

        // ---- The steps (each runs on whatever threads are free) -----------------------------------

        private void PredictAndPush(int start, int end, int chunk, float dt, bool hasShell)
        {
            float newly = 0f;
            for (int idx = start; idx < end; idx++)
            {
                if (fixedFlag[idx] != 0) continue;
                qx[idx] = px[idx] + vx[idx] * dt;
                qy[idx] = py[idx] + vy[idx] * dt;
                qz[idx] = pz[idx] + vz[idx] * dt;
                if (hasShell)
                {
                    PushOne(idx);
                    newly += Sweep(idx);
                }
            }
            chunkCharged[chunk] += newly;
        }

        private void Push(int start, int end, int chunk)
        {
            for (int idx = start; idx < end; idx++)
                if (fixedFlag[idx] == 0) PushOne(idx);
        }

        // The shell pays for the metal it digs through: a particle whose place the shell's nose has swept past is
        // charged once (ResistanceJPerM3 x its volume), wherever the push has taken it since. So the price is the
        // volume of the tunnel, the same as the energy model charges for a plug.
        private float Sweep(int idx)
        {
            if (charged[idx] != 0) return 0f;
            float rx = ox[idx] - tx0, ry = oy[idx] - ty0, rz = oz[idx] - tz0;
            float s = rx * ax0 + ry * ay0 + rz * az0;                 // along the line the shell digs
            float lx = rx - ax0 * s, ly = ry - ay0 * s, lz = rz - az0 * s;
            float lateral2 = lx * lx + ly * ly + lz * lz;
            if (lateral2 >= radius * radius) return 0f;
            // The nose is a sphere of the shell's radius: it has reached this point when its center is level with it.
            float reached = tipS - radius + MathF.Sqrt(radius * radius - lateral2);
            if (s > reached) return 0f;
            charged[idx] = 1;
            return 1f;
        }

        // The particle is inside the shell: it goes to the surface of the capsule.
        private void PushOne(int idx)
        {
            if (noseLen > 0f) { PushShaped(idx); return; }
            float x = qx[idx], y = qy[idx], z = qz[idx];

            // The nearest point of the capsule's axis, from the back of its body to the center of its nose.
            float bx = cx - ax * bodyLength, by = cy - ay * bodyLength, bz = cz - az * bodyLength;
            float along = (x - bx) * ax + (y - by) * ay + (z - bz) * az;
            along = Math.Min(Math.Max(along, 0f), bodyLength);
            float nx = bx + ax * along, ny = by + ay * along, nz = bz + az * along;

            float dx = x - nx, dy = y - ny, dz = z - nz;
            float d2 = dx * dx + dy * dy + dz * dz;
            float reach = radius + particleRadius;
            if (d2 >= reach * reach) return;

            float d = MathF.Sqrt(d2);
            float ux, uy, uz;
            if (d > 1e-6f) { ux = dx / d; uy = dy / d; uz = dz / d; }
            else { ux = -ay; uy = ax; uz = 0f; if (ux * ux + uy * uy < 1e-6f) ux = 1f; }

            float move = Math.Min(reach - d, 1.2f * h);
            qx[idx] = x + ux * move;
            qy[idx] = y + uy * move;
            qz[idx] = z + uz * move;
        }

        // The same for a shell with a nose profile (ShellProfile): a body of revolution, the radius at each distance behind the tip given by the
        // profile and the metal pushed out along the surface's normal (sideways and forward on the nose). The same arithmetic is in the kernel.
        private void PushShaped(int idx)
        {
            float x = qx[idx], y = qy[idx], z = qz[idx];
            float rx = x - (cx + ax * radius), ry = y - (cy + ay * radius), rz = z - (cz + az * radius);
            float s = rx * ax + ry * ay + rz * az;     // along the way it moves: behind the tip is negative
            float a = -s;                              // how far behind the tip
            float total = radius + bodyLength;
            if (a < -particleRadius || a > total + particleRadius) return;

            float lx = rx - ax * s, ly = ry - ay * s, lz = rz - az * s;
            float d = MathF.Sqrt(lx * lx + ly * ly + lz * lz);
            float ac = Math.Min(Math.Max(a, 0f), total);
            float r, slope;
            if (ac >= noseLen) { r = radius; slope = 0f; }
            else
            {
                float u = (noseLen - ac) / noseLen;
                float inner = Math.Max(1f - MathF.Pow(u, noseP), 1e-6f);
                r = radius * MathF.Pow(inner, 1f / noseP);
                slope = Math.Min(radius * MathF.Pow(inner, 1f / noseP - 1f) * MathF.Pow(u, noseP - 1f) / noseLen, 8f);
            }
            if (d >= r + particleRadius) return;

            float norm = MathF.Sqrt(1f + slope * slope);
            float ux, uy, uz;
            if (d > 1e-6f) { ux = lx / d; uy = ly / d; uz = lz / d; }
            else { ux = -ay; uy = ax; uz = 0f; if (ux * ux + uy * uy < 1e-6f) ux = 1f; }
            ux = (ux + ax * slope) / norm; uy = (uy + ay * slope) / norm; uz = (uz + az * slope) / norm;

            float move = Math.Min((r + particleRadius - d) / norm, 1.2f * h);
            qx[idx] = x + ux * move;
            qy[idx] = y + uy * move;
            qz[idx] = z + uz * move;
        }

        // One color of the links: each particle of the color moves toward what its links ask (their average).
        // The neighbors are all of other colors, so nothing else changes at the same time.
        private void Relax(int[] list, int start, int end)
        {
            for (int n0 = start; n0 < end; n0++)
            {
                int idx = list[n0];
                if (fixedFlag[idx] != 0) continue;

                float sx = 0f, sy = 0f, sz = 0f;
                int used = 0;
                float x = qx[idx], y = qy[idx], z = qz[idx];
                int baseSlot = idx * Links;
                for (int o = 0; o < Links; o++)
                {
                    float r = rest[baseSlot + o];
                    if (r <= 0f) continue;
                    int j = nbr[baseSlot + o];
                    float dx = qx[j] - x, dy = qy[j] - y, dz = qz[j] - z;
                    float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (len < 1e-9f) continue;
                    float c = (len - r) / len;
                    sx += c * dx; sy += c * dy; sz += c * dz;
                    used++;
                }
                if (used == 0) continue;
                float f = 1f / used;
                qx[idx] = x + sx * f;
                qy[idx] = y + sy * f;
                qz[idx] = z + sz * f;
            }
        }

        // A link stretched past the break strain by the push breaks (a brittle plate breaks early, a ductile one rather
        // flows). It is looked at right after the push, before the links pull the particles back: afterwards the whole
        // neighborhood has followed the pushed particles and nothing looks stretched any more. Both ends of a link see
        // the same numbers, so they decide the same.
        private void Tear(int start, int end)
        {
            for (int idx = start; idx < end; idx++)
            {
                int baseSlot = idx * Links;
                float x = qx[idx], y = qy[idx], z = qz[idx];
                for (int o = 0; o < Links; o++)
                {
                    float r = rest[baseSlot + o];
                    if (r <= 0f) continue;
                    int j = nbr[baseSlot + o];
                    float dx = qx[j] - x, dy = qy[j] - y, dz = qz[j] - z;
                    float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if ((len - r) / r > breakStrain && CanBreak(idx, j, o, x, y, z))
                        rest[baseSlot + o] = -1f;
                }
            }
        }

        // A link stretched or squeezed past the yield strain stays that way (plastic flow: the lips and bulges).
        private void Flow(int start, int end)
        {
            for (int idx = start; idx < end; idx++)
            {
                int baseSlot = idx * Links;
                float x = qx[idx], y = qy[idx], z = qz[idx];
                for (int o = 0; o < Links; o++)
                {
                    float r = rest[baseSlot + o];
                    if (r <= 0f) continue;
                    int j = nbr[baseSlot + o];
                    float dx = qx[j] - x, dy = qy[j] - y, dz = qz[j] - z;
                    float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    float strain = (len - r) / r;
                    if (strain > yieldStrain || strain < -yieldStrain)
                    {
                        float flowed = r + plasticRate * (len - r);
                        // Metal that has flowed too far is torn: that is what ductility is.
                        if (flowed > rest0[baseSlot + o] * (1f + ductility) && CanBreak(idx, j, o, x, y, z)) flowed = -1f;
                        rest[baseSlot + o] = flowed;
                    }
                }
            }
        }

        // A link may only break near the shell, and the back layer only once the shell has come through to it: elsewhere the
        // plate bends and stays bent but does not tear (so a shell that is stopped does not make a hole in the back).
        private bool CanBreak(int idx, int j, int o, float x, float y, float z)
        {
            float mx = 0.5f * (x + qx[j]) - cx, my = 0.5f * (y + qy[j]) - cy, mz = 0.5f * (z + qz[j]) - cz;
            if (mx * mx + my * my + mz * mz > breakReach2) return false;
            if (!backOpen)
            {
                int layer = idx / (n * n);
                if (layer == 0 || layer + Oz[o] == 0) return false;
            }
            return true;
        }

        private void Finish(int start, int end, float dt, float damp)
        {
            float inverse = 1f / dt;
            for (int idx = start; idx < end; idx++)
            {
                if (fixedFlag[idx] != 0) continue;
                vx[idx] = (qx[idx] - px[idx]) * inverse * damp;
                vy[idx] = (qy[idx] - py[idx]) * inverse * damp;
                vz[idx] = (qz[idx] - pz[idx]) * inverse * damp;
                px[idx] = qx[idx]; py[idx] = qy[idx]; pz[idx] = qz[idx];
            }
        }

        // ---- The result ---------------------------------------------------------------------------

        // The top and bottom of the particles in every column (and how many there are), read from where they are now.
        // Particles that were thrown far from the plate do not count.
        private void Surfaces(out float[] top, out float[] bottom, out int[] cover)
        {
            top = new float[n * n];
            bottom = new float[n * n];
            cover = new int[n * n];
            for (int i = 0; i < top.Length; i++) { top[i] = float.MinValue; bottom[i] = float.MaxValue; }

            float zMin = -t * 0.5f - radius * 0.6f, zMax = t * 0.5f + radius * 0.6f;
            for (int idx = 0; idx < count; idx++)
            {
                float z = pz[idx];
                if (z < zMin || z > zMax) continue;
                float gx = px[idx] / h + (n - 1) * 0.5f;
                float gy = py[idx] / h + (n - 1) * 0.5f;
                int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
                for (int dj = 0; dj <= 1; dj++)
                {
                    for (int di = 0; di <= 1; di++)
                    {
                        int cxi = ix + di, cyi = iy + dj;
                        if (cxi < 0 || cyi < 0 || cxi >= n || cyi >= n) continue;
                        int cell = cyi * n + cxi;
                        cover[cell]++;
                        float upper = z + hz * 0.5f, lower = z - hz * 0.5f;
                        if (upper > top[cell]) top[cell] = upper;
                        if (lower < bottom[cell]) bottom[cell] = lower;
                    }
                }
            }
        }

        // How torn each column's front and back surface is: the links across the plate that have broken, out of 8, three or more being a
        // full crack. From the card's counts, or (on the CPU) from the links themselves.
        private void FillCracks(ImpactSimResult result, int[] gpuCounts, float[] gpuStretch)
        {
            float[] front = new float[n * n], back = new float[n * n];
            float[] stretchFront = new float[n * n], stretchBack = new float[n * n];
            for (int cell = 0; cell < n * n; cell++)
            {
                int brokenFront = 0, brokenBack = 0;
                if (gpuCounts != null)
                {
                    brokenFront = gpuCounts[cell];
                    brokenBack = gpuCounts[n * n + cell];
                    stretchFront[cell] = gpuStretch[cell];
                    stretchBack[cell] = gpuStretch[n * n + cell];
                }
                else
                {
                    int top = (k - 1) * n * n + cell, bottom = cell;
                    for (int o = 0; o < Links; o++)
                    {
                        if (Oz[o] != 0) continue;
                        float rt = rest[top * Links + o], rb = rest[bottom * Links + o];
                        if (rt < 0f) brokenFront++; else if (rt > 0f) stretchFront[cell] = Math.Max(stretchFront[cell], rt / rest0[top * Links + o] - 1f);
                        if (rb < 0f) brokenBack++; else if (rb > 0f) stretchBack[cell] = Math.Max(stretchBack[cell], rb / rest0[bottom * Links + o] - 1f);
                    }
                }
                front[cell] = Math.Min(1f, brokenFront / 3f);
                back[cell] = Math.Min(1f, brokenBack / 3f);
            }
            if (GrowCracksOn && !isRicochet)
            {
                GrowCracks(front, stretchFront, result.Solid, 1);
                GrowCracks(back, stretchBack, result.Solid, 2);
            }
            result.CrackFront = front;
            result.CrackBack = back;
        }

        // Radial cracks, grown from the edge of the broken region (the hole, or the crater's torn floor) outward, and only through metal that
        // has been lengthened by flowing enough (the stretch map: what each link has taken, rest / rest0 - 1, against what the plate can take
        // before it tears: 'ductility'). Why they are grown this way: torn links alone only ever make a disc (the stretch of the links
        // around a hole is the same all around it and stops at the yield strain, so no rule on a neighbour's torn links or on the strain
        // makes a line; measured). A brittle plate gets more of them and they run through less stretched metal, so farther; a ductile one
        // gets one or none (it stretches and the lips turn up instead). Each crack wanders a little and may branch. The directions and
        // the wandering come from the shot's seed, the same for the same shot.
        private void GrowCracks(float[] crack, float[] stretch, float[] solid, int face)
        {
            double sumX = 0, sumY = 0;
            int count = 0;
            bool[] inside = new bool[n * n];
            for (int j = 1; j < n - 1; j++)
            {
                for (int i = 1; i < n - 1; i++)
                {
                    int cell = j * n + i;
                    if (solid[cell] < 0.5f || crack[cell] > 0.3f) { inside[cell] = true; sumX += i; sumY += j; count++; }
                }
            }
            if (count < 6) return;
            float centerX = (float)(sumX / count), centerY = (float)(sumY / count);

            uint state = unchecked((uint)(simSeed * 747796405 + face * 2891336453u + 12345u));
            Func<float> random = () =>
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                return (state & 0xFFFFFF) / 16777216f;
            };

            float needed = CrackThreshold + CrackSoft * (1f - plateBrittle);   // the share of its ductility the metal must have flowed through
            int cracks = (int)Math.Round(1f + CrackCount * plateBrittle * plateBrittle);
            float first = random() * 2f * (float)Math.PI;
            Stack<float[]> walks = new Stack<float[]>();   // x, y, angle, steps left, depth, how hard the metal is for this one to tear (x needed)
            for (int a = 0; a < cracks; a++)
            {
                float angle = first + (a + (random() - 0.5f) * 0.7f) * 2f * (float)Math.PI / cracks;
                // out from the middle to the edge of the broken region
                float x = centerX, y = centerY;
                int guard = 0;
                while (guard++ < n * 2)
                {
                    int ci = (int)Math.Round(x), cj = (int)Math.Round(y);
                    if (ci < 1 || cj < 1 || ci > n - 2 || cj > n - 2) break;
                    if (!inside[cj * n + ci]) break;
                    x += (float)Math.Cos(angle) * 0.5f;
                    y += (float)Math.Sin(angle) * 0.5f;
                }
                walks.Push(new[] { x, y, angle, n * 0.6f, 0f, 0.7f + 0.8f * random() });   // each crack has its own luck: they stop at different places
            }

            int budget = 4000;
            while (walks.Count > 0 && budget > 0)
            {
                float[] w = walks.Pop();
                float wx = w[0], wy = w[1], wa = w[2], left = w[3];
                int depth = (int)w[4];
                float hardness = w[5];
                while (left > 0f && budget-- > 0)
                {
                    int ci = (int)Math.Round(wx), cj = (int)Math.Round(wy);
                    if (ci < 1 || cj < 1 || ci > n - 2 || cj > n - 2) break;
                    float share = stretch[cj * n + ci] / Math.Max(ductility, 1e-4f);
                    if (share < needed * hardness) break;   // the metal here has not flowed enough to tear: the crack stops
                    float strength = Math.Min(1f, 0.5f + share);

                    // the point, spread over the four columns around it
                    int fi = (int)Math.Floor(wx), fj = (int)Math.Floor(wy);
                    float tx = wx - fi, ty = wy - fj;
                    for (int dj = 0; dj <= 1; dj++)
                    {
                        for (int di = 0; di <= 1; di++)
                        {
                            int ii = fi + di, jj = fj + dj;
                            if (ii < 1 || jj < 1 || ii > n - 2 || jj > n - 2) continue;
                            float weight = (di == 0 ? 1f - tx : tx) * (dj == 0 ? 1f - ty : ty);
                            int cell = jj * n + ii;
                            crack[cell] = Math.Max(crack[cell], Math.Min(1f, strength * (0.35f + 1.3f * weight)));
                        }
                    }

                    wa += (random() - 0.5f) * 0.35f;
                    if (depth < 2 && random() < CrackBranch)
                        walks.Push(new[] { wx, wy, wa + (random() < 0.5f ? -1f : 1f) * (0.45f + 0.3f * random()), left * 0.55f, depth + 1f, hardness * 1.2f });
                    wx += (float)Math.Cos(wa) * 0.7f;
                    wy += (float)Math.Sin(wa) * 0.7f;
                    left -= 1f;
                }
            }
        }

        // The change of the front and back surfaces, per column, and the columns where nothing is left (the hole).
        private ImpactSimResult Sample(bool perforated, float energyScale)
        {
            float[] top, bottom;
            int[] cover;
            Surfaces(out top, out bottom, out cover);

            ImpactSimResult result = new ImpactSimResult
            {
                N = n, Cell = h, Size = size,
                Front = new float[n * n], Back = new float[n * n], Hole = new byte[n * n], Solid = new float[n * n],
                Perforated = perforated,
            };
            int holeCells = 0;
            float deepest = 0f;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int cell = j * n + i;
                    bool border = i == 0 || j == 0 || i == n - 1 || j == n - 1;
                    // How much of the column's material is left, against what it started with: a smooth number, so the edge of a hole falls
                    // between the columns, not on them (a shell that was stopped leaves a crater, never a hole).
                    float solid = 1f;
                    if (perforated && !border && baseCover[cell] > 0) solid = Math.Min(1f, cover[cell] / (float)baseCover[cell]);
                    result.Solid[cell] = solid;
                    if (perforated && !border && solid < 0.5f)
                    {
                        result.Hole[cell] = 0;
                        holeCells++;
                        deepest = t;
                        continue;
                    }
                    if (cover[cell] == 0 && !border)
                    {
                        if (perforated)
                        {
                            result.Hole[cell] = 0;
                            holeCells++;
                            deepest = t;
                            continue;
                        }
                        // A shell that was stopped leaves a crater, not a hole, however empty the column looks: the dent
                        // is as deep as the tip got.
                        result.Hole[cell] = 255;
                        result.Front[cell] = -Math.Min(t, t * 0.5f - deepestTip + hz);
                        deepest = Math.Max(deepest, -result.Front[cell]);
                        continue;
                    }
                    result.Hole[cell] = 255;
                    if (cover[cell] == 0) continue;
                    if (border || baseCover[cell] == 0) continue;
                    result.Front[cell] = top[cell] - baseTop[cell];
                    result.Back[cell] = baseBottom[cell] - bottom[cell];
                    float dent = -result.Front[cell];
                    if (dent > deepest) deepest = dent;
                }
            }
            result.DepthM = Math.Min(deepest, t);
            result.HoleRadiusM = holeCells > 0 ? MathF.Sqrt(holeCells * h * h / (float)Math.PI) : 0f;
            return result;
        }
    }
}
