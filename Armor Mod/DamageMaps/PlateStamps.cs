using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArmorFragments
{
    internal enum DamageKind
    {
        Ricochet,    // a groove along the way the shell bounced
        Blocked,     // a dent with a raised rim
        Penetrated,  // a hole in, a bigger torn one out the back
        Pit,         // a fragment stopped in the plate
        SmallHole,   // a fragment went through
        Burst,       // a high-explosive shell went off on the plate: a wide, shallow dish
        Flex,        // a heavy blow somewhere nearby bent this plate a little: a broad, shallow dish (Caliber = its radius, Depth = how deep)
    }

    // What one stamp needs, in plate coordinates (meters).
    internal struct PlateImpact
    {
        public DamageKind Kind;
        public int Axis;           // which of the block's plates: 0 = its own big faces, 1 and 2 = its sides (see PlateGeometry.Side)
        public bool Front;         // which face was hit (the other one is where a shell comes out)
        public float U, V;         // where
        public float DirU, DirV;   // the shell's direction along the plate (for a ricochet: the way it bounced off)
        public float Angle;        // degrees from the normal (0 = head-on)
        public float Depth;        // meters: only for Flex (how deep the dish is)
        public float Caliber;      // meters (a fragment: its diameter)
        public float Strength;     // 0..1: how hard the hit was compared with what the plate could take
        public float Thickness;    // meters
        public int Seed;

        // What the simulation needs to play the impact (a shell's impact only; fragments do not use it).
        public float ShellMass;        // kg
        public float Speed;            // m/s at the plate
        public float PlateHardness;    // percent
        public float PlateBrittleness; // 0..1
        public float KeptEnergy;       // ricochet: the share of its energy the shell keeps
        public float Deflection;       // degrees: how far the plate turns a shell that goes through it (the energy model's number)
        public ShellProfile Profile;   // the shape of the shell's nose (how the metal is pushed aside; whether it tears into petals or shears out a plug)
    }

    // The atlases a vehicle's plates are drawn into (one rectangle at a time).
    internal enum AtlasKind
    {
        Normal,   // the normal map the shader reads
        Hole,     // white with the hole mask in the alpha (the Lit material of a holed plate)
        Height,   // the bump (height) map: the green channel, 0.5 = flat; read by the shader's parallax
        Damage,   // dark red whose opacity says how damaged each pixel is (the impact camera's damage view)
    }

    // A piece of the atlas to be overwritten (a small rectangle of one atlas).
    internal sealed class Delivery
    {
        public VehicleMaps Owner;
        public PlateVisual Plate;
        public AtlasKind Kind;
        public int X, Y, W, H;
        public byte[][] Mips;      // the same rectangle at half, quarter... size (levels 1..MipLevels-1), or null if it is not aligned for them
        public byte[] Bytes;       // W*H*4, RGBA: normals; (holes) white with the mask in the alpha; (height) the height in green; (damage) dark red and its opacity
        public float DeliverAt;    // Time.time from which it may be shown (the shell's ray has to arrive first)
    }

    // How much room a plate's surface has, out of it and into it, before it would go through another armor plate (see PlateClearance, which measures
    // it on the main thread). One value per cell of a coarse grid over the plate's (u, v) area, one grid per face. Free = nothing in the way.
    internal sealed class ClearGrid
    {
        public const float Free = 10f;
        public int W, H;
        public float Cell;
        public float[] Out, In;   // meters: rise allowed (0 = buried in another plate) and dent depth allowed

        public static ClearGrid Make(float sizeU, float sizeV)
        {
            float cell = Mathf.Max(0.03f, Mathf.Max(sizeU, sizeV) / 80f);
            return Create(Mathf.Max(1, Mathf.CeilToInt(sizeU / cell)), Mathf.Max(1, Mathf.CeilToInt(sizeV / cell)), cell);
        }

        public static ClearGrid Create(int w, int h, float cell)
        {
            ClearGrid grid = new ClearGrid { W = w, H = h, Cell = cell, Out = new float[w * h], In = new float[w * h] };
            for (int i = 0; i < grid.Out.Length; i++) { grid.Out[i] = Free; grid.In[i] = Free; }
            return grid;
        }

        // The smallest of the four cells around (u, v): steps rather than a blend, so a buried cell keeps its whole neighborhood in check.
        public float MinOut(float u, float v) { return Min4(Out, u, v); }
        public float MinIn(float u, float v) { return Min4(In, u, v); }

        private float Min4(float[] a, float u, float v)
        {
            int x0 = Mathf.FloorToInt(u / Cell - 0.5f), y0 = Mathf.FloorToInt(v / Cell - 0.5f);
            float best = Free;
            for (int dy = 0; dy <= 1; dy++)
            {
                for (int dx = 0; dx <= 1; dx++)
                {
                    int x = Mathf.Clamp(x0 + dx, 0, W - 1), y = Mathf.Clamp(y0 + dy, 0, H - 1);
                    float value = a[y * W + x];
                    if (value < best) best = value;
                }
            }
            return best;
        }
    }

    // A rectangle of cells of a ClearGrid that was just measured, on its way to the worker.
    internal sealed class ClearPatch
    {
        public int Face, FullW, FullH, X0, Y0, W, H;
        public float Cell;
        public float[] Out, In;
    }

    // The damage of one plate, kept on the CPU: for each face, a height field (meters, outward from the face) and a
    // hole mask. Stamps add to them; the normals the shader reads are worked out from the heights, so impacts that
    // overlap combine. Only the worker thread touches these arrays.
    internal sealed class PlateCpu
    {
        public const int Border = 2;   // pixels around the plate's area that stay flat (so filtering never bleeds)
        public const int MipLevels = 5;   // the atlases keep this many levels (1, 1/2, 1/4, 1/8, 1/16): marks stay steady from far away
        public const int Align = 16;      // tiles and the rectangles handed over are multiples of this, so every level lines up

        public sealed class Face
        {
            public float[] Height;
            public byte[] Mask;
            public byte[] Soot;   // 0..255: how scorched the paint is (the blast and the heat of a hit)
            public byte[] Bare;   // 0..255: how much of the paint has flaked off, showing the steel
            public byte[] Shine;  // 0..255: how much of that steel was rubbed bright (a scrape, the rim of a hole): it is smoother and brighter than torn metal
            public int X0 = int.MaxValue, Y0 = int.MaxValue, X1 = -1, Y1 = -1;   // what changed since the last flush
            public bool HoleDirty;
        }

        public bool PaintMode;          // marks also change the color (the plate is drawn from the hole atlas, which carries its whole color)
        public Vector3 Paint = new Vector3(0.5f, 0.5f, 0.5f);   // the plate's paint (sRGB, 0..1): what the colors of its marks start from
        public float PaintMetallic, PaintSmooth = 0.4f;         // the paint's own metallic and smoothness (what the steel and the soot are told apart from)
        public float Thickness;         // the plate's own thickness (meters, before any dent thinned it): what the depth limit is a share of
        public int Width, Height;       // the tile's size in pixels, the same for both faces
        public float PxU, PxV;          // pixels per meter inside the border
        public readonly int[] TileX = new int[2];   // where each face's tile starts in the atlas
        public readonly int[] TileY = new int[2];
        public readonly Face[] Faces = new Face[2];
        public readonly ClearGrid[] Clear = new ClearGrid[2];   // the room around the plate (null = nothing overlaps it): the heights stay inside it

        // The worker's copy of what the main thread measured.
        public void MergeClear(ClearPatch patch)
        {
            if (patch == null || patch.Face < 0 || patch.Face > 1) return;
            ClearGrid grid = Clear[patch.Face];
            if (grid == null || grid.W != patch.FullW || grid.H != patch.FullH) Clear[patch.Face] = grid = ClearGrid.Create(patch.FullW, patch.FullH, patch.Cell);
            for (int j = 0; j < patch.H; j++)
            {
                for (int i = 0; i < patch.W; i++)
                {
                    int at = (patch.Y0 + j) * grid.W + patch.X0 + i;
                    if (at < 0 || at >= grid.Out.Length) continue;
                    grid.Out[at] = patch.Out[j * patch.W + i];
                    grid.In[at] = patch.In[j * patch.W + i];
                }
            }
        }

        public Face Get(int index)
        {
            Face face = Faces[index];
            if (face == null)
            {
                face = new Face { Height = new float[Width * Height], Mask = new byte[Width * Height], Soot = new byte[Width * Height], Bare = new byte[Width * Height], Shine = new byte[Width * Height] };
                for (int i = 0; i < face.Mask.Length; i++) face.Mask[i] = 255;
                Faces[index] = face;
            }
            return face;
        }
    }

    // The shapes of damage, in meters on the plate, drawn into a PlateCpu. Pure arithmetic: it runs on a worker thread.
    internal static class PlateStamps
    {
        public static void Apply(PlateCpu cpu, PlateImpact impact, bool flex = true)
        {
            Vector2 dir = new Vector2(impact.DirU, impact.DirV);
            if (dir.sqrMagnitude < 0.0001f) dir = new Vector2(1f, 0f);
            dir.Normalize();

            float angle = Mathf.Clamp(impact.Angle, 0f, 85f) * Mathf.Deg2Rad;
            float elong = Mathf.Clamp(1f / Mathf.Max(Mathf.Cos(angle), 0.25f), 1f, 4f);
            float strength = Mathf.Clamp01(impact.Strength);
            float caliber = Mathf.Max(impact.Caliber, 0.002f);
            int entry = impact.Front ? 0 : 1;
            int exit = 1 - entry;
            float minPixel = 1.6f / Mathf.Min(cpu.PxU, cpu.PxV);   // nothing smaller than a pixel or two shows

            switch (impact.Kind)
            {
                case DamageKind.Blocked:
                {
                    float radius = Mathf.Max(caliber * 0.5f * (0.85f + 0.5f * strength), minPixel);
                    Bowl(cpu, cpu.Get(entry), impact.U, impact.V, radius, caliber * (0.08f + 0.32f * strength), elong, dir, 0.22f);
                    break;
                }
                case DamageKind.Penetrated:
                {
                    float hole = Mathf.Max(caliber * 0.5f, minPixel);
                    Bowl(cpu, cpu.Get(entry), impact.U, impact.V, hole * 1.7f, caliber * 0.10f, elong, dir, 0.25f);
                    Hole(cpu, cpu.Get(entry), impact.U, impact.V, hole, elong, dir, 0.10f, impact.Seed);

                    // The shell comes out the back, further along its way by thickness x tan(angle), and tears the metal.
                    float lateral = Mathf.Min(impact.Thickness * Mathf.Tan(angle), 0.6f);
                    float exitU = impact.U + dir.x * lateral;
                    float exitV = impact.V + dir.y * lateral;
                    Hole(cpu, cpu.Get(exit), exitU, exitV, hole * 1.15f, 1f + 0.6f * (elong - 1f), dir, 0.30f, impact.Seed + 1);
                    Bulge(cpu, cpu.Get(exit), exitU, exitV, hole * 2.4f, caliber * (0.10f + 0.15f * strength), impact.Seed + 2);
                    break;
                }
                case DamageKind.Ricochet:
                {
                    float length = Mathf.Clamp(caliber * (1.5f + 8f * (impact.Angle / 90f) * (impact.Angle / 90f)), 0.02f, 1f);
                    Streak(cpu, cpu.Get(entry), impact.U, impact.V, impact.U + dir.x * length, impact.V + dir.y * length,
                           Mathf.Max(caliber * 0.5f, minPixel), caliber * (0.05f + 0.12f * strength));
                    break;
                }
                case DamageKind.Burst:
                {
                    // A shell that goes off on the plate dishes it over a wide area (the blast, not a point): wide, shallow, with a low rim.
                    float radius = Mathf.Max(caliber * (0.9f + 0.7f * strength), minPixel);
                    Bowl(cpu, cpu.Get(entry), impact.U, impact.V, radius, caliber * (0.03f + 0.07f * strength), elong, dir, 0.18f);
                    break;
                }
                case DamageKind.Pit:
                {
                    float radius = Mathf.Max(caliber * 0.65f, minPixel);
                    Bowl(cpu, cpu.Get(entry), impact.U, impact.V, radius, caliber * (0.15f + 0.35f * strength), 1f, dir, 0.3f);
                    break;
                }
                case DamageKind.SmallHole:
                {
                    float radius = Mathf.Max(caliber * 0.5f, minPixel);
                    Bowl(cpu, cpu.Get(entry), impact.U, impact.V, radius * 1.6f, caliber * 0.12f, 1f, dir, 0.25f);
                    Hole(cpu, cpu.Get(entry), impact.U, impact.V, radius, 1f, dir, 0.15f, impact.Seed);
                    Hole(cpu, cpu.Get(exit), impact.U, impact.V, radius * 1.1f, 1f, dir, 0.25f, impact.Seed + 1);
                    break;
                }
            }
            AddOblique(cpu, impact);
            if (flex) AddFlex(cpu, impact);
            LimitDepth(cpu, impact.Thickness, impact.Caliber);
            PaintImpact(cpu, impact);
        }

        // ---- A very slanted hit skids before it stops: a groove along its way ----

        // Past about 65 degrees the shell does not dig in where it touched: it skids along the plate, scooping a groove, and stops (or is stopped)
        // further on, so the mark is a long oval (up to about five calibers at 85 degrees) and not a dent with a rim. The groove runs from a little
        // behind the point of contact to well past it. Returns false for a hit that is not slanted enough.
        public static bool ObliqueSpan(PlateImpact impact, out float u0, out float v0, out float u1, out float v1, out float t)
        {
            u0 = v0 = u1 = v1 = 0f; t = 0f;
            if (impact.Kind != DamageKind.Blocked && impact.Kind != DamageKind.Penetrated) return false;
            t = Mathf.Clamp01((impact.Angle - 65f) / 20f);
            if (t <= 0f) return false;
            Vector2 dir = new Vector2(impact.DirU, impact.DirV);
            if (dir.sqrMagnitude < 0.0001f) return false;
            dir.Normalize();
            float length = Mathf.Max(impact.Caliber, 0.002f) * (0.6f + 4.5f * t * t);
            u0 = impact.U - dir.x * length * 0.3f; v0 = impact.V - dir.y * length * 0.3f;
            u1 = impact.U + dir.x * length * 0.7f; v1 = impact.V + dir.y * length * 0.7f;
            return true;
        }

        public static void AddOblique(PlateCpu c, PlateImpact impact)
        {
            float u0, v0, u1, v1, t;
            if (!ObliqueSpan(impact, out u0, out v0, out u1, out v1, out t)) return;
            float caliber = Mathf.Max(impact.Caliber, 0.002f);
            float strength = Mathf.Clamp01(impact.Strength);
            Streak(c, c.Get(impact.Front ? 0 : 1), u0, v0, u1, v1, Mathf.Max(caliber * 0.5f, 1.6f / Mathf.Min(c.PxU, c.PxV)), caliber * (0.02f + 0.05f * strength) * t);
        }

        // ---- Flexing: a heavy blow bends the whole plate a little ----

        public static float FlexScale = 1f;   // 0 = off (the setting); read by the worker, written by the main thread

        // The dish a blow makes in the plate round the hit: wide (a few calibers, at most half the plate), and as deep as the blow is hard (the square
        // of its strength, so only the heavy ones show), never more than half the plate's thickness; a plate that is punched through bends a good deal less.
        public static void FlexOf(PlateImpact impact, float sizeU, float sizeV, out float radius, out float depth)
        {
            float caliber = Mathf.Max(impact.Caliber, 0.002f);
            float strength = Mathf.Clamp01(impact.Strength);
            float scale = impact.Kind == DamageKind.Penetrated ? 0.4f : 1f;
            radius = Mathf.Clamp(caliber * (4f + 4f * strength), 0.12f, Mathf.Max(0.12f, 0.5f * Mathf.Min(sizeU, sizeV)));
            depth = Mathf.Min(0.5f * impact.Thickness, caliber * 0.10f * strength * strength * scale);
        }

        // The struck face is pushed in over a broad area, and the other one out by about as much (the plate is bent, not dented). Used for the plate
        // that was hit (blocked or penetrated) and for the plates round it (kind Flex, which says its own radius and depth).
        public static void AddFlex(PlateCpu c, PlateImpact impact)
        {
            float radius, depth;
            if (impact.Kind == DamageKind.Flex) { radius = impact.Caliber; depth = impact.Depth; }
            else if (impact.Kind == DamageKind.Blocked || impact.Kind == DamageKind.Penetrated)
            {
                float sizeU = (c.Width - 2 * PlateCpu.Border) / c.PxU, sizeV = (c.Height - 2 * PlateCpu.Border) / c.PxV;
                FlexOf(impact, sizeU, sizeV, out radius, out depth);
            }
            else return;
            depth *= FlexScale;
            if (depth < 0.0002f || radius <= 0f) return;

            int entry = impact.Front ? 0 : 1;
            Dish(c, c.Get(entry), impact.U, impact.V, radius, -depth);
            Dish(c, c.Get(1 - entry), impact.U, impact.V, radius, depth * 0.6f);
        }

        // A smooth dish: 'height' at the middle, falling to nothing at the radius, with no rim.
        private static void Dish(PlateCpu c, PlateCpu.Face f, float cu, float cv, float radius, float height)
        {
            int x0, y0, x1, y1;
            if (!Region(c, cu, cv, radius, out x0, out y0, out x1, out y1)) return;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    float dx = (x + 0.5f - PlateCpu.Border) / c.PxU - cu;
                    float dy = (y + 0.5f - PlateCpu.Border) / c.PxV - cv;
                    float rho = Mathf.Sqrt(dx * dx + dy * dy) / radius;
                    if (rho >= 1f) continue;
                    float bowl = 1f - rho * rho;
                    f.Height[y * c.Width + x] += height * bowl * bowl;
                }
            }
            Dirty(f, x0, y0, x1, y1, false);
        }

        // ---- Paint: scorching and flaked-off paint (the look of a mark, apart from its shape) ----

        // A hit scorches the paint around it and knocks it off where the metal is struck, showing bare steel (brighter, with uneven edges); a
        // blast scorches a wide area; a ricochet leaves a streak of bare metal. Added to the plate's soot and bare-steel fields, which
        // become color when the maps are handed over (PixelColor). Used both for the quick marks and for the simulation's.
        public static void PaintImpact(PlateCpu cpu, PlateImpact impact)
        {
            if (!cpu.PaintMode) return;
            Vector2 dir = new Vector2(impact.DirU, impact.DirV);
            if (dir.sqrMagnitude < 0.0001f) dir = new Vector2(1f, 0f);
            dir.Normalize();
            float caliber = Mathf.Max(impact.Caliber, 0.002f);
            float strength = Mathf.Clamp01(impact.Strength);
            int entry = impact.Front ? 0 : 1, exit = 1 - entry;
            float angle = Mathf.Clamp(impact.Angle, 0f, 85f) * Mathf.Deg2Rad;
            // A slanted hit digs and scorches an oval along the shell's way (the same stretch as the dent: Apply's 'elong').
            float elong = Mathf.Clamp(1f / Mathf.Max(Mathf.Cos(angle), 0.25f), 1f, 4f);

            switch (impact.Kind)
            {
                case DamageKind.Blocked:
                    Splash(cpu, cpu.Get(entry), impact.U, impact.V, caliber * 1.6f, 0.30f + 0.40f * strength, caliber * (0.55f + 0.30f * strength), 1f, impact.Seed, elong, dir);
                    FollowShape(cpu, cpu.Get(entry), caliber, impact.Seed);
                    ObliqueScrape(cpu, cpu.Get(entry), impact, caliber, strength);
                    break;
                case DamageKind.Penetrated:
                {
                    Splash(cpu, cpu.Get(entry), impact.U, impact.V, caliber * 1.8f, 0.40f + 0.30f * strength, caliber * 0.90f, 1f, impact.Seed, elong, dir);
                    // The way out is the hotter side: the shell and the torn metal leave it black, and the paint flakes round the hole.
                    float lateral = Mathf.Min(impact.Thickness * Mathf.Tan(angle), 0.6f);
                    float elongOut = 1f + 0.6f * (elong - 1f);
                    Splash(cpu, cpu.Get(exit), impact.U + dir.x * lateral, impact.V + dir.y * lateral, caliber * 1.5f, 0.65f + 0.30f * strength, caliber * 0.80f, 1f, impact.Seed + 1, elongOut, dir);
                    FollowShape(cpu, cpu.Get(entry), caliber, impact.Seed);
                    FollowShape(cpu, cpu.Get(exit), caliber, impact.Seed + 1);
                    ObliqueScrape(cpu, cpu.Get(entry), impact, caliber, strength);
                    break;
                }
                case DamageKind.Ricochet:
                {
                    // The shell skids along the plate: a wide band of paint rubbed off to bright metal, in fine lines along the way, brightest and
                    // widest where it touched and thinning out as it lifted away; scorched and chipped where it hit, and a dark smear where it left.
                    float length = Mathf.Clamp(caliber * (1.5f + 8f * (impact.Angle / 90f) * (impact.Angle / 90f)), 0.02f, 1f);
                    float endU = impact.U + dir.x * length, endV = impact.V + dir.y * length;
                    Scrape(cpu, cpu.Get(entry), impact.U, impact.V, endU, endV, caliber, strength, impact.Seed);
                    Splash(cpu, cpu.Get(entry), impact.U, impact.V, caliber * 1.1f, 0.30f + 0.30f * strength, caliber * 0.8f, 1f, impact.Seed + 2);
                    Splash(cpu, cpu.Get(entry), endU, endV, caliber * 0.8f, 0.22f, 0f, 0f, impact.Seed + 3);
                    break;
                }
                case DamageKind.Burst:
                    // The blast scorches a wide area and blows some of the paint off in its middle.
                    Splash(cpu, cpu.Get(entry), impact.U, impact.V, caliber * 2.4f, 0.50f + 0.30f * strength, caliber * 1.2f, 0.55f, impact.Seed);
                    break;
                case DamageKind.Pit:
                    Splash(cpu, cpu.Get(entry), impact.U, impact.V, caliber * 1.0f, 0f, caliber * 1.1f, 1f, impact.Seed);
                    break;
                case DamageKind.SmallHole:
                    Splash(cpu, cpu.Get(entry), impact.U, impact.V, caliber * 1.0f, 0.15f, caliber * 1.3f, 1f, impact.Seed);
                    Splash(cpu, cpu.Get(exit), impact.U, impact.V, caliber * 1.0f, 0.25f, caliber * 1.3f, 1f, impact.Seed + 1);
                    break;
            }
        }

        // Soot (soft, dark, patchy) of radius 'radius' and flaked paint (a ragged-edged patch of bare steel) of radius 'chipRadius'.
        // 'elong' stretches the patch along 'dir' (a slanted hit), the soot reaching a little further ahead.
        private static void Splash(PlateCpu c, PlateCpu.Face f, float cu, float cv, float radius, float soot, float chipRadius, float chipAmount, int seed,
                                   float elong = 1f, Vector2 dir = default(Vector2))
        {
            if (dir.sqrMagnitude < 0.0001f) { dir = new Vector2(1f, 0f); elong = 1f; }
            elong = Mathf.Max(elong, 1f);
            float extent = Mathf.Max(radius * 2f, chipRadius) * 1.1f * elong;
            int x0, y0, x1, y1;
            if (!Region(c, cu, cv, extent, out x0, out y0, out x1, out y1)) return;

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int at = y * c.Width + x;
                    float dx = (x + 0.5f - PlateCpu.Border) / c.PxU - cu;
                    float dy = (y + 0.5f - PlateCpu.Border) / c.PxV - cv;
                    float along = dx * dir.x + dy * dir.y, across = -dx * dir.y + dy * dir.x;
                    float dist = Mathf.Sqrt((along / elong) * (along / elong) + across * across);
                    float ahead = along - (elong - 1f) * radius * 0.25f;   // the scorch trails a little forward of the dent
                    float sootDist = Mathf.Sqrt((ahead / elong) * (ahead / elong) + across * across);

                    if (soot > 0f)
                    {
                        float rho = sootDist / radius;
                        if (rho < 2f)
                        {
                            float s = soot * Mathf.Exp(-rho * rho * 1.2f) * (0.7f + 0.6f * ValueNoise(x * 0.45f, y * 0.45f, seed));
                            int value = f.Soot[at] + Mathf.RoundToInt(Mathf.Clamp01(s) * 255f);
                            f.Soot[at] = (byte)Mathf.Min(value, 255);
                        }
                    }
                    if (chipAmount > 0f && dist < chipRadius * 1.5f)
                    {
                        // The flakes: bare in the middle, patchy toward the edge. The edge has lobes (it is not a circle) and the flakes are a few
                        // millimeters to a centimeter whatever the map's detail, so some bits of paint hold on inside and some flakes sit outside.
                        float angleAround = Mathf.Atan2(across, along / elong);
                        float lobe = 1f + 0.30f * Noise(angleAround, seed + 5);
                        float flake = Mathf.Max(chipRadius * 0.28f, 2.5f / Mathf.Min(c.PxU, c.PxV));
                        float mu = (x + 0.5f - PlateCpu.Border) / c.PxU, mv = (y + 0.5f - PlateCpu.Border) / c.PxV;
                        float grain = 0.65f * ValueNoise(mu / flake, mv / flake, seed + 11) + 0.35f * ValueNoise(2f * mu / flake + 7f, 2f * mv / flake + 3f, seed + 17);
                        float cover = 1.25f - dist / (chipRadius * lobe);
                        float chip = Mathf.Clamp01((cover - 0.55f * grain) * 2.4f);
                        chip = chip * chipAmount;
                        int value = Mathf.RoundToInt(chip * 255f);
                        if (value > f.Bare[at]) f.Bare[at] = (byte)value;
                        int shiny = Mathf.RoundToInt(chip * 70f);   // torn metal: a little shiny
                        if (shiny > f.Shine[at]) f.Shine[at] = (byte)shiny;
                    }
                }
            }
            Dirty(f, x0, y0, x1, y1, true);
        }

        // A skid: the paint rubbed off along the line from (u0, v0) to (u1, v1) in a band that is widest at the start and thins out, with fine
        // lines along the way (noise stretched in the direction of travel) and a ragged edge. What is rubbed bare is also rubbed bright (Shine).
        private static void Scrape(PlateCpu c, PlateCpu.Face f, float u0, float v0, float u1, float v1, float caliber, float strength, int seed)
        {
            float width = caliber * (0.55f + 0.25f * strength);   // half the band's width where it starts
            float dxs = u1 - u0, dys = v1 - v0;
            float length = Mathf.Max(Mathf.Sqrt(dxs * dxs + dys * dys), 0.0001f);
            float sx = dxs / length, sy = dys / length;
            float margin = width * 1.8f;
            int x0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (Mathf.Min(u0, u1) - margin) * c.PxU));
            int x1 = Mathf.Min(c.Width - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (Mathf.Max(u0, u1) + margin) * c.PxU));
            int y0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (Mathf.Min(v0, v1) - margin) * c.PxV));
            int y1 = Mathf.Min(c.Height - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (Mathf.Max(v0, v1) + margin) * c.PxV));
            if (x1 <= x0 || y1 <= y0) return;

            float pixel = 1f / Mathf.Min(c.PxU, c.PxV);
            float lineWidth = Mathf.Max(width * 0.10f, 1.6f * pixel);   // lines finer than a pixel would only be noise
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    float px = (x + 0.5f - PlateCpu.Border) / c.PxU - u0;
                    float py = (y + 0.5f - PlateCpu.Border) / c.PxV - v0;
                    float s = px * sx + py * sy;                  // along the way
                    float a = Mathf.Abs(-px * sy + py * sx);      // across it
                    if (s < -0.6f * width || s > length + 0.3f * width) continue;

                    float t = Mathf.Clamp01(s / length);
                    float half = width * (1f - 0.55f * t) * (0.8f + 0.4f * ValueNoise(s / (width * 1.5f), 7.3f, seed + 21));   // wobbling edge
                    if (a > half * 1.25f) continue;

                    float lines = ValueNoise(s / (width * 2.5f), a / lineWidth, seed + 5);
                    float body = Mathf.Clamp01((1f - a / half) * 2.2f + 0.15f);
                    float amount = Mathf.Clamp01(body * (0.45f + 0.9f * lines)) * (1f - 0.5f * t);
                    if (s < 0f) amount *= Mathf.Clamp01(1f + s / (0.6f * width));   // taper in front of where it touched

                    int at = y * c.Width + x;
                    int bare = Mathf.RoundToInt(amount * 255f);
                    if (bare > f.Bare[at]) f.Bare[at] = (byte)bare;
                    int shine = Mathf.RoundToInt(amount * 235f);
                    if (shine > f.Shine[at]) f.Shine[at] = (byte)shine;
                }
            }
            Dirty(f, x0, y0, x1, y1, true);
        }

        // How shiny and how metal one pixel is: the paint's own values, changed toward bright steel where it flaked (more where the metal was
        // rubbed bright than where it was torn), and toward dull where it is scorched. Metallic in 0..255, smoothness in 0..255 (what the Lit
        // shader reads from the red and alpha channels of the map).
        public static void PixelSurface(PlateCpu c, PlateCpu.Face f, int index, out byte metallic, out byte smoothness)
        {
            float bare = f != null && f.Bare != null ? f.Bare[index] / 255f : 0f;
            float soot = f != null && f.Soot != null ? f.Soot[index] / 255f : 0f;
            float shine = f != null && f.Shine != null ? f.Shine[index] / 255f : 0f;
            float m = c.PaintMetallic, s = c.PaintSmooth;
            if (bare > 0f || soot > 0f)
            {
                // Not fully metal: the Lit shader gives a metal almost no color of its own (it shows what it reflects), and the game's dark
                // surroundings made the bare steel look black. A low value keeps the steel's gray and still adds a shine.
                m = Mathf.Lerp(m, 0.3f, bare);
                s = Mathf.Lerp(s, 0.28f + 0.55f * shine, bare);
                float k = Mathf.Clamp01(soot) * 0.92f * (1f - 0.65f * bare);   // the same as the color: soot clings less to steel
                m *= 1f - k;
                s = Mathf.Lerp(s, 0.06f, k);
            }
            metallic = (byte)Mathf.Clamp(Mathf.RoundToInt(m * 255f), 0, 255);
            smoothness = (byte)Mathf.Clamp(Mathf.RoundToInt(s * 255f), 0, 255);
        }

        private static void ObliqueScrape(PlateCpu c, PlateCpu.Face f, PlateImpact impact, float caliber, float strength)
        {
            float u0, v0, u1, v1, t;
            if (!ObliqueSpan(impact, out u0, out v0, out u1, out v1, out t)) return;
            Scrape(c, f, u0, v0, u1, v1, caliber, strength * 0.7f, impact.Seed + 9);
        }

        // The bare metal follows what the metal actually did: where the plate was dented the paint is gone (the deeper, the more), the raised rim
        // loses its paint, and a ring round every hole is bright rubbed steel. Read from the height field and the hole mask, so it has the shape of
        // the hole and the dent whatever made them (the stamps, or the simulation), oval for a slanted hit.
        private static void FollowShape(PlateCpu c, PlateCpu.Face f, float caliber, int seed)
        {
            if (f == null || f.X1 < 0) return;
            int x0 = Mathf.Max(PlateCpu.Border, f.X0), y0 = Mathf.Max(PlateCpu.Border, f.Y0);
            int x1 = Mathf.Min(c.Width - PlateCpu.Border, f.X1), y1 = Mathf.Min(c.Height - PlateCpu.Border, f.Y1);
            if (x1 <= x0 || y1 <= y0) return;

            float depthRef = Mathf.Max(0.12f * caliber, 0.002f);
            float riseRef = 0.05f * caliber + 0.0005f;
            float flake = Mathf.Max(0.10f * caliber, 2.5f / Mathf.Min(c.PxU, c.PxV));
            int reach = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(0.06f * caliber * Mathf.Min(c.PxU, c.PxV), 1.5f)), 1, 8);   // the width of the bright ring, in pixels

            bool anyHole = false;
            for (int y = y0; y < y1 && !anyHole; y++)
                for (int x = x0; x < x1; x++)
                    if (f.Mask[y * c.Width + x] < 128) { anyHole = true; break; }

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int at = y * c.Width + x;
                    if (f.Mask[at] < 128) continue;   // a hole: there is nothing there to paint

                    float h = f.Height[at];
                    float bare = 0f, shine = 0f;
                    float dent = Mathf.Clamp01(-h / depthRef);
                    float rise = Mathf.Clamp01(h / riseRef);
                    float cover = Mathf.Max(dent * 1.1f, rise * 0.8f);
                    if (cover > 0.05f)
                    {
                        // Ragged: flakes of a size in meters (not pixels) decide where the paint is gone, so the edge is torn, not a smooth curve.
                        float mu = (x + 0.5f - PlateCpu.Border) / c.PxU, mv = (y + 0.5f - PlateCpu.Border) / c.PxV;
                        float grain = 0.65f * ValueNoise(mu / flake, mv / flake, seed + 31) + 0.35f * ValueNoise(2f * mu / flake + 7f, 2f * mv / flake + 3f, seed + 37);
                        bare = Mathf.Clamp01((cover - 0.45f * grain) * 2.6f) * 0.95f;
                    }

                    if (anyHole)
                    {
                        bool near = false;
                        for (int dy = -reach; dy <= reach && !near; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= c.Height) continue;
                            for (int dx = -reach; dx <= reach; dx++)
                            {
                                int xx = x + dx;
                                if (xx < 0 || xx >= c.Width) continue;
                                if (f.Mask[yy * c.Width + xx] < 128) { near = true; break; }
                            }
                        }
                        if (near) { bare = 1f; shine = 0.85f; }
                    }
                    if (bare <= 0f) continue;

                    byte value = (byte)Mathf.RoundToInt(bare * 255f);
                    if (value > f.Bare[at]) f.Bare[at] = value;
                    byte glow = (byte)Mathf.RoundToInt(Mathf.Max(shine, bare * 0.3f) * 255f);
                    if (glow > f.Shine[at]) f.Shine[at] = glow;
                }
            }
            Dirty(f, x0, y0, x1, y1, true);
        }

        // Smooth noise in 0..1 on a lattice of one unit (the same for the same seed).
        private static float ValueNoise(float x, float y, int seed)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float tx = x - ix, ty = y - iy;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float a = Hash(ix * 374761393 + iy * 668265263 + seed * 1442695041);
            float b = Hash((ix + 1) * 374761393 + iy * 668265263 + seed * 1442695041);
            float cc = Hash(ix * 374761393 + (iy + 1) * 668265263 + seed * 1442695041);
            float d = Hash((ix + 1) * 374761393 + (iy + 1) * 668265263 + seed * 1442695041);
            return (a * (1f - tx) + b * tx) * (1f - ty) + (cc * (1f - tx) + d * tx) * ty;
        }

        // The color of one pixel of a face, as the plate's own paint changed by its marks: paint, flaked toward steel, scorched toward soot.
        public static void PixelColor(PlateCpu c, PlateCpu.Face f, int index, out byte r, out byte g, out byte b)
        {
            float bare = f != null && f.Bare != null ? f.Bare[index] / 255f : 0f;
            float soot = f != null && f.Soot != null ? f.Soot[index] / 255f : 0f;
            if (bare <= 0f && soot <= 0f)
            {
                r = (byte)Mathf.RoundToInt(c.Paint.x * 255f); g = (byte)Mathf.RoundToInt(c.Paint.y * 255f); b = (byte)Mathf.RoundToInt(c.Paint.z * 255f);
                return;
            }
            float shine = f != null && f.Shine != null ? f.Shine[index] / 255f : 0f;
            float v = (0.92f + 0.16f * Hash(index)) * (1f + 0.3f * shine);   // bare steel is never quite even; rubbed bright it is lighter
            float rr = Mathf.Lerp(c.Paint.x, 0.66f * v, bare), gg = Mathf.Lerp(c.Paint.y, 0.67f * v, bare), bb = Mathf.Lerp(c.Paint.z, 0.71f * v, bare);
            float s = Mathf.Clamp01(soot) * 0.92f * (1f - 0.65f * bare);   // soot clings less to bare steel
            rr = rr * (1f - s) + 0.07f * s; gg = gg * (1f - s) + 0.062f * s; bb = bb * (1f - s) + 0.055f * s;
            r = (byte)Mathf.Clamp(Mathf.RoundToInt(rr * 255f), 0, 255);
            g = (byte)Mathf.Clamp(Mathf.RoundToInt(gg * 255f), 0, 255);
            b = (byte)Mathf.Clamp(Mathf.RoundToInt(bb * 255f), 0, 255);
        }

        // Marks add up (a shot on an old dent digs a new one on top), but a plate cannot be dented deeper than it is thick: the heights are held
        // between -0.6 of the thickness (the energy model never takes more than 60% of a plate away for a dent either) and +1.5 (metal pushed out
        // the back, or bent into petals, can stand well proud of a thin plate). Only what changed since the last handing over is looked at, so
        // it costs next to nothing.
        public static void LimitDepth(PlateCpu c, float thickness, float caliber = 0f)
        {
            thickness = Mathf.Max(thickness, c.Thickness);   // a thinned plate (the impact carries its thickness left) still has its own depth
            if (thickness < 0.0005f) return;
            // A thin sheet (a 1 mm plate) is bent and torn by a shell, not dented 0.6 mm deep: what a hit may push is also a share of its caliber.
            float give = Mathf.Min(0.3f * caliber, 0.04f);
            float low = -Mathf.Max(0.6f * thickness, give), high = Mathf.Max(1.5f * thickness, give);
            for (int index = 0; index < 2; index++)
            {
                PlateCpu.Face f = c.Faces[index];
                if (f == null || f.X1 < 0) continue;
                int x1 = Mathf.Min(f.X1, c.Width), y1 = Mathf.Min(f.Y1, c.Height);
                ClearGrid room = c.Clear[index];
                for (int y = Mathf.Max(f.Y0, 0); y < y1; y++)
                {
                    int row = y * c.Width;
                    for (int x = Mathf.Max(f.X0, 0); x < x1; x++)
                    {
                        float lo = low, hi = high;
                        if (room != null)
                        {
                            // Another plate is in the way: the surface may not rise into the one in front of it, nor a dent run into the one behind.
                            float u = (x + 0.5f - PlateCpu.Border) / c.PxU, v = (y + 0.5f - PlateCpu.Border) / c.PxV;
                            float up = room.MinOut(u, v), down = room.MinIn(u, v);
                            if (up < ClearGrid.Free) hi = Mathf.Min(hi, Mathf.Max(up - 0.0015f, 0f));
                            if (down < ClearGrid.Free) lo = Mathf.Max(lo, -Mathf.Max(down * 0.85f, 0f));
                        }
                        float h = f.Height[row + x];
                        if (h < lo) f.Height[row + x] = lo;
                        else if (h > hi) f.Height[row + x] = hi;
                    }
                }
            }
        }

        // ---- Shapes -------------------------------------------------------------------------------

        // A bowl with a raised rim. 'elong' stretches it along 'dir' (a slanted hit digs a longer dent).
        private static void Bowl(PlateCpu c, PlateCpu.Face f, float cu, float cv, float radius, float depth,
                                 float elong, Vector2 dir, float rim)
        {
            float extent = radius * Mathf.Max(elong, 1f) * 1.8f;
            int x0, y0, x1, y1;
            if (!Region(c, cu, cv, extent, out x0, out y0, out x1, out y1)) return;

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    float dx = (x + 0.5f - PlateCpu.Border) / c.PxU - cu;
                    float dy = (y + 0.5f - PlateCpu.Border) / c.PxV - cv;
                    float along = dx * dir.x + dy * dir.y;
                    float across = -dx * dir.y + dy * dir.x;
                    float rho = Mathf.Sqrt((along / elong) * (along / elong) + across * across) / radius;

                    float h = 0f;
                    if (rho < 1f)
                    {
                        float bowl = 1f - rho * rho;
                        h = -depth * bowl * bowl;
                    }
                    float ring = (rho - 1.18f) / 0.16f;
                    h += rim * depth * Mathf.Exp(-ring * ring);
                    f.Height[y * c.Width + x] += h;
                }
            }
            Dirty(f, x0, y0, x1, y1, false);
        }

        // Metal pushed out the back where the shell tore through: a bump with an uneven edge.
        private static void Bulge(PlateCpu c, PlateCpu.Face f, float cu, float cv, float radius, float height, int seed)
        {
            float extent = radius * 1.5f;
            int x0, y0, x1, y1;
            if (!Region(c, cu, cv, extent, out x0, out y0, out x1, out y1)) return;

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    float dx = (x + 0.5f - PlateCpu.Border) / c.PxU - cu;
                    float dy = (y + 0.5f - PlateCpu.Border) / c.PxV - cv;
                    float angle = Mathf.Atan2(dy, dx);
                    float rho = Mathf.Sqrt(dx * dx + dy * dy) / (radius * (1f + 0.25f * Noise(angle, seed)));
                    if (rho >= 1f) continue;
                    float bump = 1f - rho * rho;
                    f.Height[y * c.Width + x] += height * bump * bump;
                }
            }
            Dirty(f, x0, y0, x1, y1, false);
        }

        // A hole through the plate: the mask goes to 0 (see-through). 'irregular' makes the edge uneven (0 = round).
        private static void Hole(PlateCpu c, PlateCpu.Face f, float cu, float cv, float radius, float elong, Vector2 dir,
                                 float irregular, int seed)
        {
            float extent = radius * Mathf.Max(elong, 1f) * (1f + irregular) * 1.3f;
            int x0, y0, x1, y1;
            if (!Region(c, cu, cv, extent, out x0, out y0, out x1, out y1)) return;

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    float dx = (x + 0.5f - PlateCpu.Border) / c.PxU - cu;
                    float dy = (y + 0.5f - PlateCpu.Border) / c.PxV - cv;
                    float along = dx * dir.x + dy * dir.y;
                    float across = -dx * dir.y + dy * dir.x;
                    float edge = radius * (1f + irregular * Noise(Mathf.Atan2(across, along), seed));
                    if ((along / elong) * (along / elong) + across * across < edge * edge)
                        f.Mask[y * c.Width + x] = 0;
                }
            }
            Dirty(f, x0, y0, x1, y1, true);
        }

        // A groove from (u0, v0) to (u1, v1): deepest where the shell touched, fading out as it left, with a ridge on each side.
        private static void Streak(PlateCpu c, PlateCpu.Face f, float u0, float v0, float u1, float v1, float width, float depth)
        {
            float minU = Mathf.Min(u0, u1), maxU = Mathf.Max(u0, u1), minV = Mathf.Min(v0, v1), maxV = Mathf.Max(v0, v1);
            float margin = width * 2.5f;
            int x0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (minU - margin) * c.PxU));
            int x1 = Mathf.Min(c.Width - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (maxU + margin) * c.PxU));
            int y0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (minV - margin) * c.PxV));
            int y1 = Mathf.Min(c.Height - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (maxV + margin) * c.PxV));
            if (x1 <= x0 || y1 <= y0) return;

            float dxs = u1 - u0, dys = v1 - v0;
            float length = Mathf.Max(Mathf.Sqrt(dxs * dxs + dys * dys), 0.0001f);
            float sx = dxs / length, sy = dys / length;

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    float px = (x + 0.5f - PlateCpu.Border) / c.PxU - u0;
                    float py = (y + 0.5f - PlateCpu.Border) / c.PxV - v0;
                    float t = Mathf.Clamp01((px * sx + py * sy) / length);
                    float qx = px - sx * t * length, qy = py - sy * t * length;
                    float dist = Mathf.Sqrt(qx * qx + qy * qy);

                    float along = depth * (1f - 0.8f * t);
                    float groove = dist / (0.5f * width);
                    float ridge = (dist - width) / (0.4f * width);
                    f.Height[y * c.Width + x] += -along * Mathf.Exp(-groove * groove) + 0.25f * along * Mathf.Exp(-ridge * ridge);
                }
            }
            Dirty(f, x0, y0, x1, y1, false);
        }

        // ---- Helpers ------------------------------------------------------------------------------

        // The pixels (inside the border) that a stamp of this extent around (cu, cv) can touch.
        private static bool Region(PlateCpu c, float cu, float cv, float extent, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (cu - extent) * c.PxU));
            x1 = Mathf.Min(c.Width - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (cu + extent) * c.PxU));
            y0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (cv - extent) * c.PxV));
            y1 = Mathf.Min(c.Height - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (cv + extent) * c.PxV));
            return x1 > x0 && y1 > y0;
        }

        internal static void Dirty(PlateCpu.Face f, int x0, int y0, int x1, int y1, bool hole)
        {
            if (x0 < f.X0) f.X0 = x0;
            if (y0 < f.Y0) f.Y0 = y0;
            if (x1 > f.X1) f.X1 = x1;
            if (y1 > f.Y1) f.Y1 = y1;
            if (hole) f.HoleDirty = true;
        }

        // A smooth value in -1..1 that depends on the angle and the seed: what makes edges uneven (and the same each time).
        private static float Noise(float angle, int seed)
        {
            float p1 = Hash(seed * 7 + 1) * 6.2831853f;
            float p2 = Hash(seed * 13 + 3) * 6.2831853f;
            float p3 = Hash(seed * 29 + 5) * 6.2831853f;
            return 0.5f * Mathf.Sin(3f * angle + p1) + 0.3f * Mathf.Sin(5f * angle + p2) + 0.2f * Mathf.Sin(8f * angle + p3);
        }

        private static float Hash(int value)
        {
            unchecked
            {
                uint x = (uint)value * 2654435761u;
                x ^= x >> 15; x *= 2246822519u; x ^= x >> 13;
                return (x & 0xFFFFFF) / 16777216f;
            }
        }

        // ---- Handing over what changed ------------------------------------------------------------

        // Largest height (meters) the bump map can say, each way: the green channel runs from -HeightRange to +HeightRange.
        public const float HeightRange = 0.06f;

        // The rectangles that changed since the last time, as bytes for the atlases: normals (and, if a hole changed, the mask;
        // and, if asked, the bump map and the damage view). 'everything' marks the whole of every face that has any damage as changed
        // (what a stream that has just been switched on needs, to catch up).
        public static void Flush(PlateCpu c, PlateVisual plate, VehicleMaps owner, float deliverAt, List<Delivery> output,
                                 bool height = false, bool damage = false, bool everything = false, bool paint = false, bool gloss = false)
        {
            for (int index = 0; index < 2; index++)
            {
                PlateCpu.Face f = c.Faces[index];
                if (f != null && everything)
                {
                    f.X0 = 0; f.Y0 = 0; f.X1 = c.Width; f.Y1 = c.Height;
                    f.HoleDirty = f.HoleDirty || paint || HasHole(f);   // the hole atlas only exists if there is a hole (or paint) to put in it
                }
                if (f == null || f.X1 < 0) continue;

                // One more pixel around: the normal of a pixel depends on its neighbors.
                int x0 = Mathf.Max(f.X0 - 1, 0), y0 = Mathf.Max(f.Y0 - 1, 0);
                int x1 = Mathf.Min(f.X1 + 1, c.Width), y1 = Mathf.Min(f.Y1 + 1, c.Height);
                // Aligned to the mip levels (tiles are multiples of Align wide and high in the game; elsewhere the rectangle just stays as it is).
                if (c.Width % PlateCpu.Align == 0 && c.Height % PlateCpu.Align == 0)
                {
                    x0 -= x0 % PlateCpu.Align; y0 -= y0 % PlateCpu.Align;
                    x1 = Mathf.Min((x1 + PlateCpu.Align - 1) / PlateCpu.Align * PlateCpu.Align, c.Width);
                    y1 = Mathf.Min((y1 + PlateCpu.Align - 1) / PlateCpu.Align * PlateCpu.Align, c.Height);
                }
                int w = x1 - x0, h = y1 - y0;

                byte[] normals = new byte[w * h * 4];
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        float left = f.Height[y * c.Width + Mathf.Max(x - 1, 0)];
                        float right = f.Height[y * c.Width + Mathf.Min(x + 1, c.Width - 1)];
                        float down = f.Height[Mathf.Max(y - 1, 0) * c.Width + x];
                        float up = f.Height[Mathf.Min(y + 1, c.Height - 1) * c.Width + x];
                        // The slope in meters per meter: a pixel is 1/PxU meters wide.
                        float nx = -(right - left) * 0.5f * c.PxU;
                        float ny = -(up - down) * 0.5f * c.PxV;
                        float inverse = 1f / Mathf.Sqrt(nx * nx + ny * ny + 1f);
                        int at = ((y - y0) * w + (x - x0)) * 4;
                        normals[at] = Encode(nx * inverse);
                        normals[at + 1] = Encode(ny * inverse);
                        normals[at + 2] = Encode(inverse);
                        normals[at + 3] = 255;
                    }
                }
                AddDelivery(output, new Delivery
                {
                    Owner = owner, Plate = plate, Kind = AtlasKind.Normal,
                    X = c.TileX[index] + x0, Y = c.TileY[index] + y0, W = w, H = h, Bytes = normals, DeliverAt = deliverAt,
                });

                if (height)
                {
                    byte[] bump = new byte[w * h * 4];
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            int at = ((y - y0) * w + (x - x0)) * 4;
                            float level = Mathf.Clamp01(0.5f + f.Height[y * c.Width + x] / HeightRange);
                            bump[at + 1] = (byte)Mathf.RoundToInt(level * 255f); bump[at + 2] = 128;
                            // The map is also the Lit shader's metallic/smoothness map (red and alpha) when the steel gloss is on.
                            if (gloss) PixelSurface(c, f, y * c.Width + x, out bump[at], out bump[at + 3]);
                            else { bump[at] = 128; bump[at + 3] = 255; }
                        }
                    }
                    AddDelivery(output, new Delivery
                    {
                        Owner = owner, Plate = plate, Kind = AtlasKind.Height,
                        X = c.TileX[index] + x0, Y = c.TileY[index] + y0, W = w, H = h, Bytes = bump, DeliverAt = deliverAt,
                    });
                }

                if (damage)
                {
                    byte[] view = new byte[w * h * 4];
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            int at = ((y - y0) * w + (x - x0)) * 4;
                            // How damaged this pixel is, 0..1: how far it was pushed in or out (a few millimeters already show;
                            // thirty or so is as bad as it gets), and a hole is the worst there is.
                            float depth = Mathf.Abs(f.Height[y * c.Width + x]);
                            float level = f.Mask[y * c.Width + x] < 128 ? 1f : 1f - Mathf.Exp(-depth / 0.008f);
                            if (level < 0.01f) continue;   // untouched: fully transparent (the array starts as zeros)
                            // Little damage: a lighter red, see-through. A lot: dark red, nearly solid.
                            view[at] = (byte)Mathf.RoundToInt(Mathf.Lerp(235f, 70f, level));
                            view[at + 1] = (byte)Mathf.RoundToInt(Mathf.Lerp(45f, 0f, level));
                            view[at + 2] = (byte)Mathf.RoundToInt(Mathf.Lerp(35f, 0f, level));
                            view[at + 3] = (byte)Mathf.RoundToInt(255f * (0.25f + 0.70f * level));
                        }
                    }
                    AddDelivery(output, new Delivery
                    {
                        Owner = owner, Plate = plate, Kind = AtlasKind.Damage,
                        X = c.TileX[index] + x0, Y = c.TileY[index] + y0, W = w, H = h, Bytes = view, DeliverAt = deliverAt,
                    });
                }

                if (f.HoleDirty)
                {
                    byte[] mask = new byte[w * h * 4];
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            int at = ((y - y0) * w + (x - x0)) * 4;
                            if (paint) PixelColor(c, f, y * c.Width + x, out mask[at], out mask[at + 1], out mask[at + 2]);
                            else { mask[at] = 255; mask[at + 1] = 255; mask[at + 2] = 255; }
                            mask[at + 3] = f.Mask[y * c.Width + x];
                        }
                    }
                    AddDelivery(output, new Delivery
                    {
                        Owner = owner, Plate = plate, Kind = AtlasKind.Hole,
                        X = c.TileX[index] + x0, Y = c.TileY[index] + y0, W = w, H = h, Bytes = mask, DeliverAt = deliverAt,
                    });
                }

                f.X0 = int.MaxValue; f.Y0 = int.MaxValue; f.X1 = -1; f.Y1 = -1;
                f.HoleDirty = false;
            }
        }

        private static void AddDelivery(List<Delivery> output, Delivery d)
        {
            d.Mips = BuildMips(d.Bytes, d.W, d.H, d.Kind == AtlasKind.Normal);
            output.Add(d);
        }

        // The rectangle at half size, quarter size... (a box average of each 2 x 2; normals are averaged and made unit length again, which
        // is what a flatter, further-away surface looks like). Null if it does not halve evenly all the way down.
        private static byte[][] BuildMips(byte[] source, int w, int h, bool normals)
        {
            int levels = PlateCpu.MipLevels - 1;
            if (w % PlateCpu.Align != 0 || h % PlateCpu.Align != 0) return null;
            byte[][] result = new byte[levels][];
            byte[] from = source;
            for (int level = 0; level < levels; level++)
            {
                int nw = w >> 1, nh = h >> 1;
                byte[] to = new byte[nw * nh * 4];
                for (int y = 0; y < nh; y++)
                {
                    for (int x = 0; x < nw; x++)
                    {
                        int a = ((2 * y) * w + 2 * x) * 4, b = a + 4, c = a + w * 4, d = c + 4;
                        int at = (y * nw + x) * 4;
                        if (normals)
                        {
                            float nx = (from[a] + from[b] + from[c] + from[d]) * 0.25f / 255f * 2f - 1f;
                            float ny = (from[a + 1] + from[b + 1] + from[c + 1] + from[d + 1]) * 0.25f / 255f * 2f - 1f;
                            float nz = (from[a + 2] + from[b + 2] + from[c + 2] + from[d + 2]) * 0.25f / 255f * 2f - 1f;
                            float length = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                            if (length < 0.0001f) { nx = 0f; ny = 0f; nz = 1f; length = 1f; }
                            to[at] = Encode(nx / length); to[at + 1] = Encode(ny / length); to[at + 2] = Encode(nz / length); to[at + 3] = 255;
                        }
                        else
                        {
                            for (int k = 0; k < 4; k++) to[at + k] = (byte)((from[a + k] + from[b + k] + from[c + k] + from[d + k] + 2) >> 2);
                        }
                    }
                }
                result[level] = to;
                from = to; w = nw; h = nh;
            }
            return result;
        }

        private static bool HasHole(PlateCpu.Face f)
        {
            for (int i = 0; i < f.Mask.Length; i++)
                if (f.Mask[i] < 128) return true;
            return false;
        }

        private static byte Encode(float component)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt((component * 0.5f + 0.5f) * 255f), 0, 255);
        }
    }
}
