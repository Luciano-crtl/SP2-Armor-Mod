using System;
using ArmorFragments.Sim;
using UnityEngine;

namespace ArmorFragments
{
    // Between the impact simulation and the plate's maps: what the simulation is told, and how its result (a coarse grid of
    // heights and holes around the impact) is laid into the plate's tiles.
    internal static class SimBridge
    {
        // Does the impact get a simulation (a shell's impact, with what the simulation needs)?
        public static bool Wants(PlateImpact impact)
        {
            if (impact.Kind != DamageKind.Blocked && impact.Kind != DamageKind.Penetrated && impact.Kind != DamageKind.Ricochet) return false;
            return impact.ShellMass > 0f && impact.Speed > 1f && impact.Caliber > 0f;
        }

        public static ImpactSimInput InputFor(PlateImpact impact, int quality, int timeLimitMs, float resistanceJPerM3)
        {
            return new ImpactSimInput
            {
                CaliberM = impact.Caliber,
                ShellMassKg = impact.ShellMass,
                SpeedMS = impact.Speed,
                AngleDeg = impact.Angle,
                DirU = impact.DirU,
                DirV = impact.DirV,
                ThicknessM = impact.Thickness,
                PlateHardness = impact.PlateHardness,
                PlateBrittleness = impact.PlateBrittleness,
                ResistanceJPerM3 = resistanceJPerM3,
                Ricochet = impact.Kind == DamageKind.Ricochet,
                KeptEnergy = impact.KeptEnergy,
                DeflectionDeg = impact.Kind == DamageKind.Penetrated ? impact.Deflection : 0f,
                EnergyScale = 1f,
                Quality = quality,
                Seed = impact.Seed,
                Profile = impact.Profile,
                TimeLimitMs = timeLimitMs,
            };
        }

        // The simulation must agree with the energy model about the outcome (the model decides the game: a shell the model
        // lets through must make a hole, one it stops must not). If not, the quick marks are used instead.
        public static bool Agrees(DamageKind kind, ImpactSimResult result)
        {
            bool hole = result.Perforated || result.HoleRadiusM > 0f;
            if (kind == DamageKind.Penetrated) return hole;
            if (kind == DamageKind.Blocked) return !hole;
            return true;
        }

        // Lays the simulation's grid into the plate's face tiles: the front's heights on the face the shell hit, the back's on
        // the other face, and the holes through both. The grid is coarser than the tile's pixels, so it is sampled smoothly.
        //   cracks:      the torn links make thin grooves in the surfaces
        //   smoothEdges: the edge of a hole follows where the material runs out between the columns, a little ragged, not the columns' square steps
        public static void Apply(PlateCpu cpu, PlateImpact impact, ImpactSimResult sim, bool cracks = true, bool smoothEdges = true)
        {
            int entry = impact.Front ? 0 : 1;
            PlateCpu.Face faceIn = cpu.Get(entry), faceOut = cpu.Get(1 - entry);

            float half = sim.Size * 0.5f;
            int x0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (impact.U - half) * cpu.PxU));
            int x1 = Mathf.Min(cpu.Width - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (impact.U + half) * cpu.PxU));
            int y0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (impact.V - half) * cpu.PxV));
            int y1 = Mathf.Min(cpu.Height - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (impact.V + half) * cpu.PxV));
            if (x1 <= x0 || y1 <= y0) return;

            int n = sim.N;
            bool anyHole = false;
            bool grooves = cracks && sim.CrackFront != null && sim.CrackBack != null;
            bool smooth = smoothEdges && sim.Solid != null;
            float[] distance = smooth ? SignedDistance(sim) : null;   // to the hole's edge, meters, + on the metal (null = no hole)
            float pixel = 1f / Mathf.Max(cpu.PxU, cpu.PxV);
            float grooveDepth = Mathf.Clamp(0.04f * impact.Caliber, 0.0004f, 0.002f);   // meters at a full crack
            float ragged = smooth ? 0.10f + 0.20f * Mathf.Clamp01(impact.PlateBrittleness) : 0f;
            float rimWidth = Mathf.Max(impact.Caliber * 0.12f, pixel * 1.5f);

            // How the plate fails where a shell goes through: tearing into petals that fold outward, or shearing out a plug with a clean edge.
            bool through = impact.Kind == DamageKind.Penetrated && sim.HoleRadiusM > 0f;
            float plug = through ? PlugShare(impact) : 0.5f;
            float petalAmount = Mathf.Clamp01((0.5f - plug) * 2f), plugAmount = Mathf.Clamp01((plug - 0.5f) * 2f);
            ragged *= 1f + 0.8f * petalAmount - 0.8f * plugAmount;
            float edgeShift = through ? impact.Caliber * (0.05f * plugAmount - 0.06f * petalAmount) : 0f;   // plug: a hole of the full caliber; petals: smaller (they fold in)
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    float u = (x + 0.5f - PlateCpu.Border) / cpu.PxU;
                    float v = (y + 0.5f - PlateCpu.Border) / cpu.PxV;
                    float gx = (u - impact.U) / sim.Cell + (n - 1) * 0.5f;
                    float gy = (v - impact.V) / sim.Cell + (n - 1) * 0.5f;
                    if (gx < 0f || gy < 0f || gx > n - 1 || gy > n - 1) continue;

                    int i = Mathf.Min((int)gx, n - 2), j = Mathf.Min((int)gy, n - 2);
                    float fx = gx - i, fy = gy - j;
                    int idx = y * cpu.Width + x;
                    faceIn.Height[idx] += Sample(sim.Front, n, i, j, fx, fy);
                    faceOut.Height[idx] += Sample(sim.Back, n, i, j, fx, fy);
                    bool hole;
                    if (smooth)
                    {
                        // The edge is written as a distance: 128 = on the edge, 128 more or less for a pixel's width of metal or of hole. The
                        // pixels near the edge then say where it is to a fraction of a pixel (the geometry and the hole map both read them
                        // smoothly), and the ragged edge is the distance moved a little by the noise.
                        hole = false;
                        if (distance != null)
                        {
                            float d = Sample(distance, n, i, j, fx, fy) - ragged * Noise(x, y, impact.Seed) * sim.Cell - edgeShift;
                            // Round the edge of a hole the paint is gone and the steel shows (a bright rim, brightest at the edge).
                            if (cpu.PaintMode && d >= 0f && d < rimWidth)
                            {
                                byte rim = (byte)Mathf.RoundToInt(255f * (1f - d / rimWidth));
                                if (rim > faceIn.Bare[idx]) faceIn.Bare[idx] = rim;
                                if (rim > faceOut.Bare[idx]) faceOut.Bare[idx] = rim;
                                byte shine = (byte)(rim * 0.9f);   // the rim is rubbed bright where the shell cut through
                                if (shine > faceIn.Shine[idx]) faceIn.Shine[idx] = shine;
                                if (shine > faceOut.Shine[idx]) faceOut.Shine[idx] = shine;
                            }
                            float value = Mathf.Clamp(128f + d / pixel * 128f, 0f, 255f);
                            if (value < 255f)
                            {
                                byte b = (byte)value;
                                if (b < faceIn.Mask[idx]) faceIn.Mask[idx] = b;
                                if (b < faceOut.Mask[idx]) faceOut.Mask[idx] = b;
                            }
                            hole = value < 128f;
                        }
                    }
                    else
                    {
                        hole = SampleHole(sim.Hole, n, i, j, fx, fy) < 0.5f;
                        if (hole) { faceIn.Mask[idx] = 0; faceOut.Mask[idx] = 0; }
                    }
                    if (hole) anyHole = true;
                    else if (grooves)
                    {
                        faceIn.Height[idx] -= grooveDepth * Sample(sim.CrackFront, n, i, j, fx, fy);
                        faceOut.Height[idx] -= grooveDepth * Sample(sim.CrackBack, n, i, j, fx, fy);
                    }
                }
            }
            PlateStamps.Dirty(faceIn, x0, y0, x1, y1, anyHole);
            PlateStamps.Dirty(faceOut, x0, y0, x1, y1, anyHole);
            if (through && anyHole) ShapeExit(cpu, faceIn, faceOut, impact, sim, petalAmount, plugAmount);
            PlateStamps.AddOblique(cpu, impact);   // a very slanted shell skids: a groove along its way, whatever the simulation made
            PlateStamps.AddFlex(cpu, impact);
            PlateStamps.LimitDepth(cpu, impact.Thickness, impact.Caliber);
            PlateStamps.PaintImpact(cpu, impact);
        }

        // 0 = it tears into petals, 1 = it shears out a plug. A pointed nose, a thin ductile plate: petals. A flat nose, a thick or brittle
        // plate: a plug (the plate fails in a cylinder as wide as the shell).
        public static float PlugShare(PlateImpact impact)
        {
            float blunt = impact.Profile.Bluntness;
            float thick = Mathf.Clamp01(impact.Thickness / Mathf.Max(impact.Caliber, 0.001f));
            return Mathf.Clamp01(0.4f * blunt + 0.3f * thick + 0.3f * Mathf.Clamp01(impact.PlateBrittleness));
        }

        // The back of a hole the shell made: petals bent outward around it, or a clean shear lip. Worked out from the hole itself (its middle
        // and its radius, from the simulation's columns), so it follows whatever shape the simulation gave it.
        private static void ShapeExit(PlateCpu cpu, PlateCpu.Face faceIn, PlateCpu.Face faceOut, PlateImpact impact, ImpactSimResult sim,
                                      float petalAmount, float plugAmount)
        {
            if (petalAmount < 0.05f && plugAmount < 0.05f) return;
            int n = sim.N;
            double su = 0, sv = 0;
            int cells = 0;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    bool hole = sim.Solid != null ? sim.Solid[j * n + i] < 0.5f : sim.Hole[j * n + i] < 128;
                    if (!hole) continue;
                    su += impact.U + (i - (n - 1) * 0.5f) * sim.Cell;
                    sv += impact.V + (j - (n - 1) * 0.5f) * sim.Cell;
                    cells++;
                }
            if (cells < 3) return;
            float cu = (float)(su / cells), cv = (float)(sv / cells);
            float radius = Mathf.Sqrt(cells * sim.Cell * sim.Cell / Mathf.PI);

            float reach = radius * (petalAmount > plugAmount ? 1.9f : 1.4f);
            int x0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (cu - reach) * cpu.PxU));
            int x1 = Mathf.Min(cpu.Width - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (cu + reach) * cpu.PxU));
            int y0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (cv - reach) * cpu.PxV));
            int y1 = Mathf.Min(cpu.Height - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (cv + reach) * cpu.PxV));
            if (x1 <= x0 || y1 <= y0) return;

            int petals = 5 + Math.Abs(impact.Seed % 4);
            float phase = Hash(impact.Seed, 7, 3) * Mathf.PI;
            float lift = Mathf.Min(impact.Caliber * 0.32f * petalAmount, 1.2f * impact.Thickness);
            float lip = impact.Caliber * 0.06f * plugAmount;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int idx = y * cpu.Width + x;
                    if (faceOut.Mask[idx] < 128) continue;   // the hole itself
                    float dx = (x + 0.5f - PlateCpu.Border) / cpu.PxU - cu, dy = (y + 0.5f - PlateCpu.Border) / cpu.PxV - cv;
                    float rho = Mathf.Sqrt(dx * dx + dy * dy);
                    if (rho > reach) continue;

                    if (petalAmount > 0.05f)
                    {
                        // Petals: lobes around the hole, rising from its edge, highest a little way out, with a slit between each pair.
                        float lobe = 0.5f + 0.5f * Mathf.Cos(petals * (Mathf.Atan2(dy, dx) - phase));
                        lobe = Mathf.Pow(lobe, 0.8f);
                        float away = Mathf.Max(rho - radius, 0f) / (0.9f * radius);
                        float rise = away * Mathf.Exp(1f - away);
                        float height = lift * lobe * rise;
                        if (lobe < 0.08f) height -= 0.15f * lift * (1f - lobe / 0.08f) * Mathf.Min(away * 2f, 1f);   // the slit
                        faceOut.Height[idx] += height;
                    }
                    else
                    {
                        // A plug leaves a clean lip around the hole on the way out, and a faint one on the way in.
                        float ring = (rho - 1.08f * radius) / (0.12f * radius);
                        float bump = lip * Mathf.Exp(-ring * ring);
                        faceOut.Height[idx] += bump;
                        if (faceIn.Mask[idx] >= 128) faceIn.Height[idx] += 0.4f * bump;
                    }
                }
            }
            PlateStamps.Dirty(faceOut, x0, y0, x1, y1, false);
            if (plugAmount >= petalAmount) PlateStamps.Dirty(faceIn, x0, y0, x1, y1, false);
        }

        // The distance, in meters, from the middle of each of the simulation's columns to the edge of the hole (positive on the metal, negative
        // in the hole): half a column less than the distance to the nearest column of the other kind. Null if there is no hole. Looks 6 columns
        // out; beyond that it is that far.
        private static float[] SignedDistance(ImpactSimResult sim)
        {
            int n = sim.N;
            bool[] hole = new bool[n * n];
            bool any = false;
            for (int j = 1; j < n - 1; j++)
                for (int i = 1; i < n - 1; i++)
                    if (sim.Solid[j * n + i] < 0.5f) { hole[j * n + i] = true; any = true; }
            if (!any) return null;

            const int reach = 6;
            float[] distance = new float[n * n];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    bool mine = hole[j * n + i];
                    int best = reach * reach * 2 + 1;
                    for (int dj = -reach; dj <= reach; dj++)
                    {
                        int jj = j + dj;
                        if (jj < 0 || jj >= n) continue;
                        for (int di = -reach; di <= reach; di++)
                        {
                            int ii = i + di;
                            if (ii < 0 || ii >= n || hole[jj * n + ii] == mine) continue;
                            int d2 = di * di + dj * dj;
                            if (d2 < best) best = d2;
                        }
                    }
                    float cells = best > reach * reach * 2 ? reach : Mathf.Sqrt(best) - 0.5f;
                    distance[j * n + i] = (mine ? -cells : cells) * sim.Cell;
                }
            }
            return distance;
        }

        // Smooth noise in -1..1 on a lattice of 3 pixels (the same for the same seed): the ragged edge of torn metal.
        private static float Noise(float x, float y, int seed)
        {
            float gx = x / 3f, gy = y / 3f;
            int ix = Mathf.FloorToInt(gx), iy = Mathf.FloorToInt(gy);
            float tx = gx - ix, ty = gy - iy;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed), c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
            return (a * (1f - tx) + b * tx) * (1f - ty) + (c * (1f - tx) + d * tx) * ty;
        }

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 32767.5f - 1f;
            }
        }

        private static float Sample(float[] grid, int n, int i, int j, float fx, float fy)
        {
            float a = grid[j * n + i], b = grid[j * n + i + 1], c = grid[(j + 1) * n + i], d = grid[(j + 1) * n + i + 1];
            return (a * (1f - fx) + b * fx) * (1f - fy) + (c * (1f - fx) + d * fx) * fy;
        }

        private static float SampleHole(byte[] grid, int n, int i, int j, float fx, float fy)
        {
            float a = grid[j * n + i], b = grid[j * n + i + 1], c = grid[(j + 1) * n + i], d = grid[(j + 1) * n + i + 1];
            return ((a * (1f - fx) + b * fx) * (1f - fy) + (c * (1f - fx) + d * fx) * fy) / 255f;
        }
    }
}
