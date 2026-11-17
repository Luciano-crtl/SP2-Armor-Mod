using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ArmorFragments.Sim;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // The damage maps of one vehicle: ONE atlas of normal maps and ONE atlas of hole masks (made when the first hole
    // appears), with a tile (two: front and back) for each of its armor plates. A plate that is damaged gets its UV0 pointing at its tiles, and a material:
    //  - the game's own part material (a clone the theme keeps up to date, so the paint is the game's) with the normal
    //    atlas as its normal map: the damage is added to the paint by the shader;
    //  - once it has a hole, a URP Lit material (alpha clipping, the paint's color, the same normal atlas and the hole
    //    atlas): the game's part shader has no alpha test, so a see-through hole needs this one.
    // The damage is computed by a worker thread (DamageWorker) and shown a rectangle at a time (Graphics.CopyTexture).
    internal sealed class VehicleMaps
    {
        public readonly AircraftScript Aircraft;
        public readonly ThemeScript Theme;
        public int AtlasSize;
        public Texture2D Normal;
        public Texture2D Alpha;
        public Texture2D Height;        // the bump map (made when the relief setting asks for it)
        public Texture2D DamageAtlas;   // the impact camera's damage view (made when that is switched on)
        public float Density;           // pixels per meter of the tiles
        public bool PaintMode;          // marks change the plate's color too (the hole atlas then carries the plate's whole color, baked when it is made)
        public bool Gloss;              // and its metallic/smoothness (the bump atlas then carries them in its red and alpha channels, baked when it is made)
        public Material Clone;       // the game's part material with the normal atlas
        public float LastUsed;
        public bool Disposed;
        public bool Touched;            // a plate of this vehicle has been used: the atlases it will need are made, one a frame (WarmUp)
        private bool heightPending;     // the relief setting wants the bump map and it is not made yet
        public readonly Dictionary<PartScript, PlateVisual> Plates = new Dictionary<PartScript, PlateVisual>();

        private const int SentinelSize = PlateCpu.Align;   // a flat corner of the atlas, for vertices that belong to no face (the sides)

        private VehicleMaps(AircraftScript aircraft, ThemeScript theme)
        {
            Aircraft = aircraft;
            Theme = theme;
        }

        public static string LastFailure = "";

        // Lays out the tiles of every armor plate of the aircraft and makes the atlases. Null if there is nothing to do.
        public static VehicleMaps Create(AircraftScript aircraft)
        {
            ThemeScript theme = aircraft.Theme;
            if (theme == null) { LastFailure = "no theme"; return null; }

            List<PlateInfo> infos = new List<PlateInfo>();
            foreach (PartData data in aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null || TagCodec.Parse(data.Name).Armor == null) continue;
                PlateInfo info = PlateDamageMaps.InfoOf(part);
                if (info != null) infos.Add(info);
            }
            if (infos.Count == 0) { LastFailure = "no armor plate with a readable mesh"; return null; }

            // A thick block also gets tiles for its sides (each a plate of its own, so marks can go on them too).
            List<PlateInfo> all = new List<PlateInfo>();
            foreach (PlateInfo info in infos)
            {
                all.Add(info);
                for (int axis = 1; axis <= 2; axis++)
                    if (info.Sides[axis - 1] != null) all.Add(info.ForAxis(axis));
            }

            // The atlas is as big as all the plates would need at the setting's density (at most the biggest the setting allows: 2048 x 2048 unless it says
            // more), but the tiles are NOT handed out now: a plate gets its own (as dense as the setting says, less if the atlas is filling up) the first time
            // it is marked, so the detail of a plate does not depend on how many plates the vehicle has.
            float density = Mathf.Max(ArmorFragmentsPlugin.DamageMapsDensity.Value, 16f);
            int maxAtlas = Mathf.Clamp(Mathf.ClosestPowerOfTwo(ArmorFragmentsPlugin.DamageMapsMaxAtlas.Value), 1024, 4096);
            long area = 0;
            foreach (PlateInfo info in all)
            {
                int w, h;
                TileSize(info.Geometry, density, out w, out h);
                area += 2L * w * h;
            }
            int size = 512;
            while (size < maxAtlas && (long)size * size < (area * 13L) / 10L + 4096) size *= 2;

            VehicleMaps maps = new VehicleMaps(aircraft, theme) { AtlasSize = size, LastUsed = Time.time, Density = density, PaintMode = ArmorFragmentsPlugin.DamageMapsPaintDamage.Value };
            maps.Gloss = maps.PaintMode && ArmorFragmentsPlugin.DamageMapsSteelGloss.Value;
            maps.heightPending = maps.Gloss;   // the bump atlas is made (in a frame of its own) as soon as the vehicle is used
            Dictionary<PartScript, Vector3> paints = new Dictionary<PartScript, Vector3>();
            Dictionary<PartScript, Vector2> surfaces = new Dictionary<PartScript, Vector2>();
            for (int i = 0; i < all.Count; i++)
            {
                PlateInfo info = all[i];
                Vector3 paint;
                Vector2 surface;
                if (!paints.TryGetValue(info.Part, out paint))
                {
                    Color color; float metallic, smoothness;
                    ReadPaint(theme, info.Part, out color, out metallic, out smoothness);
                    paints[info.Part] = paint = new Vector3(color.r, color.g, color.b);
                    surfaces[info.Part] = new Vector2(Mathf.Clamp01(metallic), Mathf.Clamp01(smoothness));
                }
                surface = surfaces[info.Part];
                PlateCpu cpu = new PlateCpu   // no tiles yet (Width = 0): AllocateTiles gives them
                {
                    Paint = paint,
                    PaintMetallic = surface.x,
                    PaintSmooth = surface.y,
                    PaintMode = maps.PaintMode,
                    Thickness = info.ThicknessM,
                };
                PlateVisual visual = new PlateVisual { Info = info, Cpu = cpu, Owner = maps };
                int side = info.Geometry.Axis;
                if (side == 0)
                {
                    maps.Plates[info.Part] = visual;
                }
                else
                {
                    PlateVisual main = maps.Plates[info.Part];   // the plate's own comes first in the list
                    visual.Main = main;
                    main.Sides[side - 1] = visual;
                }
            }

            long atlasAt = System.Diagnostics.Stopwatch.GetTimestamp();
            maps.Normal = new Texture2D(size, size, TextureFormat.RGBA32, PlateCpu.MipLevels, true) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments damage normals" };
            FillTexture(maps.Normal, 128, 128, 255, 255);   // flat normals

            ArmorFragmentsPlugin.Log.LogInfo("[DMAP] atlas " + size + "x" + size + " for " + infos.Count + " armor plate(s) of '" +
                                             aircraft.name + "', tiles given out as plates are marked (" + density.ToString("F0") + " px/m wanted), up to " + (4L * size * size * 4 / (1024 * 1024)) +
                                             " MB of video memory once all its atlases exist (the normal atlas took " +
                                             PerfMonitor.Since(atlasAt).ToString("F1") + " ms)");
            return maps;
        }

        // Fills a whole texture with one color without making a second copy of it: one row is built and copied into the texture's own
        // memory row by row (the old way allocated, filled and uploaded 16 MB arrays, which cost tens of milliseconds in one frame).
        private static void FillTexture(Texture2D texture, byte r, byte g, byte b, byte a)
        {
            // The raw data holds every mip level one after the other; one color all the way through is the same pattern, end to end.
            int width = texture.width;
            byte[] row = new byte[width * 4];
            FillPattern(row, r, g, b, a);
            Unity.Collections.NativeArray<byte> raw = texture.GetRawTextureData<byte>();
            int total = raw.Length;
            for (int at = 0; at < total; at += row.Length)
            {
                int n = Math.Min(row.Length, total - at);
                if (n == row.Length) Unity.Collections.NativeSliceExtensions.Slice(raw, at, n).CopyFrom(row);
                else
                {
                    byte[] rest = new byte[n];
                    Buffer.BlockCopy(row, 0, rest, 0, n);
                    Unity.Collections.NativeSliceExtensions.Slice(raw, at, n).CopyFrom(rest);
                }
            }
            texture.Apply(false, false);
        }

        // Fills an array with a repeating pattern, by copying what is already there (fast for big arrays).
        private static void FillPattern(byte[] data, params byte[] pattern)
        {
            for (int i = 0; i < pattern.Length && i < data.Length; i++) data[i] = pattern[i];
            int filled = pattern.Length;
            while (filled < data.Length)
            {
                int copy = Math.Min(filled, data.Length - filled);
                Buffer.BlockCopy(data, 0, data, filled, copy);
                filled += copy;
            }
        }

        // ---- The atlas's tiles, given out as plates are marked ----------------------------------------------------

        public const int MaxTileSide = 1024;   // a plate's tile is never more than this many pixels on a side (a long plate is less dense, not huge)
        public const float MinDensity = 48f;   // the least dense a tile may get when the atlas is filling up
        private sealed class Shelf { public int Y, Height, X; }
        private readonly List<Shelf> shelves = new List<Shelf>();   // rows of tiles; a tile goes in the row it wastes least
        private int shelfBottom = SentinelSize;                      // where the next new row starts
        private long usedPixels;                                      // the area of the tiles given out

        // The size of one tile (front or back) of a plate at a density: multiples of Align, so it lines up with every mip level.
        private static void TileSize(PlateGeometry g, float density, out int w, out int h)
        {
            float own = g.Axis == 0 ? density : density * 0.5f;   // the sides of thick blocks are small and seen at a slant: half the density
            float limit = (MaxTileSide - 2 * PlateCpu.Border - PlateCpu.Align) / Mathf.Max(g.SizeU, g.SizeV, 0.01f);
            own = Mathf.Min(own, limit);
            w = (Mathf.CeilToInt(g.SizeU * own) + 2 * PlateCpu.Border + PlateCpu.Align - 1) / PlateCpu.Align * PlateCpu.Align;
            h = (Mathf.CeilToInt(g.SizeV * own) + 2 * PlateCpu.Border + PlateCpu.Align - 1) / PlateCpu.Align * PlateCpu.Align;
        }

        // Shelf packing, one tile at a time.
        private bool TryAllocateTile(int w, int h, out int x, out int y)
        {
            x = 0; y = 0;
            if (w > AtlasSize || h > AtlasSize) return false;
            Shelf best = null;
            int bestWaste = int.MaxValue;
            foreach (Shelf shelf in shelves)
            {
                if (shelf.Height < h || shelf.X + w > AtlasSize) continue;
                if (shelf.Height - h < bestWaste) { best = shelf; bestWaste = shelf.Height - h; }
            }
            if (best == null)
            {
                if (shelfBottom + h > AtlasSize) return false;
                best = new Shelf { Y = shelfBottom, Height = h, X = 0 };
                shelfBottom += h;
                shelves.Add(best);
            }
            x = best.X; y = best.Y;
            best.X += w;
            usedPixels += (long)w * h;
            return true;
        }

        // Gives one plate (or one side) its two tiles at this density. False if the atlas has no room for them.
        private bool TryAllocateOne(PlateVisual plate, float density)
        {
            PlateGeometry g = plate.Info.Geometry;
            int w, h;
            TileSize(g, density, out w, out h);
            int x0, y0, x1, y1;
            if (!TryAllocateTile(w, h, out x0, out y0) || !TryAllocateTile(w, h, out x1, out y1)) return false;

            PlateCpu c = plate.Cpu;
            c.Width = w; c.Height = h;
            c.PxU = (w - 2 * PlateCpu.Border) / g.SizeU;
            c.PxV = (h - 2 * PlateCpu.Border) / g.SizeV;
            c.TileX[0] = x0; c.TileY[0] = y0;
            c.TileX[1] = x1; c.TileY[1] = y1;
            c.Get(0); c.Get(1);          // both faces exist, so the first job can hand over the whole of each tile (its paint, its flat normals)
            plate.NeedsFull = true;
            return true;
        }

        // The tiles of a plate and of its sides, the first time the plate is marked. The plate's own is as dense as the setting says and goes less
        // dense, a step at a time, while the atlas has no room; a side that finds no room stays without marks. False: not even the plate's own fits.
        public bool AllocateTiles(PlateVisual main)
        {
            if (main.Cpu.Width > 0) return true;
            float density = Mathf.Clamp(ArmorFragmentsPlugin.DamageMapsDensity.Value, 16f, 768f);   // finer than that and a few plates would fill the atlas

            // What this block may take of the atlas (its faces and its sides together): a sixteenth of it, and no more than a quarter of what is
            // left, so the plates that come later still get a fair share (the first ones were taking most of it and the rest got 40 pixels per
            // meter or nothing). The density is what makes the block's tiles fit that area (a side has a quarter of the area at half the density).
            long atlasArea = (long)AtlasSize * AtlasSize;
            long left = (long)(atlasArea * 0.85) - usedPixels;
            long budget = Math.Min(atlasArea / 16, Math.Max(left / 4, 20000));
            float unit = 2f * main.Info.Geometry.SizeU * main.Info.Geometry.SizeV;    // pixels per (px/m)^2 of its two faces
            foreach (PlateVisual side in main.Sides)
                if (side != null) unit += 0.5f * side.Info.Geometry.SizeU * side.Info.Geometry.SizeV;
            if (unit > 0.0001f) density = Mathf.Min(density, Mathf.Sqrt(budget / unit));
            density = Mathf.Max(density, MinDensity);

            float got = 0f;
            for (float d = density; d >= MinDensity * 0.7f; d *= 0.8f)
            {
                if (TryAllocateOne(main, d)) { got = d; break; }
            }
            if (got <= 0f) return false;
            foreach (PlateVisual side in main.Sides)
            {
                if (side == null) continue;
                for (float d = got; d >= MinDensity * 0.7f; d *= 0.8f)
                    if (TryAllocateOne(side, d)) break;
            }
            ArmorFragmentsPlugin.Log.LogInfo("[DMAP] tiles for plate '" + main.Info.Part.Part.Name + "': " + main.Cpu.PxU.ToString("F0") + " x " + main.Cpu.PxV.ToString("F0") +
                                             " px/m (" + main.Cpu.Width + " x " + main.Cpu.Height + " a face), " + (100L * usedPixels / atlasArea) + "% of the atlas is given out");
            return true;
        }

        // The plate, ready to be stamped: its UV0 and material set. Null if it cannot be shown (merged into its group's mesh...).
        // The same for one of the plates of a thick block: axis 0 = its own faces, 1 and 2 = its sides (null if it has none).
        public PlateVisual PlateFor(PartScript part, int axis)
        {
            PlateVisual main = PlateFor(part);
            if (main == null || axis == 0) return main;
            PlateVisual side = main.Sides[axis - 1];
            return side != null && side.Cpu.Width > 0 ? side : null;   // a side that found no room in the atlas has no marks
        }

        // Every plate with tiles: the blocks' own and their sides.
        public IEnumerable<PlateVisual> AllPlates()
        {
            foreach (PlateVisual plate in Plates.Values)
            {
                yield return plate;
                foreach (PlateVisual side in plate.Sides)
                    if (side != null) yield return side;
            }
        }

        public PlateVisual PlateFor(PartScript part)
        {
            PlateVisual plate;
            if (Disposed || !Plates.TryGetValue(part, out plate) || plate.Failed) return null;
            LastUsed = Time.time;
            if (!plate.Applied && !plate.TryApply()) return null;
            Touched = true;
            return plate;
        }

        // The plate's paint, as the game has it (the primary color of its first material), or a gray if it cannot be read.
        public static void ReadPaint(ThemeScript theme, PartScript part, out Color color, out float metallic, out float smoothness)
        {
            color = new Color(0.5f, 0.5f, 0.5f, 1f);
            metallic = 0f;
            smoothness = 0.4f;
            try
            {
                PartMaterial paint = theme.Theme.GetMaterial(part.Part.MaterialIds[0]);
                if (paint != null && paint.ColorData != null && paint.ColorData.Length > 0)
                {
                    color = paint.PrimaryColor;
                    color.a = 1f;
                    metallic = paint.PrimaryColorMetallic;
                    smoothness = paint.PrimaryColorSmoothness;
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] the plate's paint could not be read: " + e.Message);
            }
        }

        // Paint mode: the hole atlas carries each plate's whole color, so every tile starts as that plate's paint (and the flat corner the
        // sides of thin plates point to, as bare gray). Written straight into the texture's memory; the mip levels are made after.
        private void BakePaint()
        {
            BakeTiles(Alpha, c => new[] { (byte)Mathf.RoundToInt(c.Paint.x * 255f), (byte)Mathf.RoundToInt(c.Paint.y * 255f), (byte)Mathf.RoundToInt(c.Paint.z * 255f), (byte)255 },
                      new byte[] { 150, 152, 158, 255 });
        }

        // The bump atlas as the metallic/smoothness map: each tile starts as its plate's paint (metallic in red, smoothness in alpha, flat height in green),
        // and the flat corner as bare steel.
        private void BakeSurface()
        {
            BakeTiles(Height, c => new[] { (byte)Mathf.RoundToInt(c.PaintMetallic * 255f), (byte)128, (byte)128, (byte)Mathf.RoundToInt(c.PaintSmooth * 255f) },
                      new byte[] { 190, 128, 128, 110 });
        }

        // Writes one pixel pattern over every plate's two tiles (and another over the flat corner), straight into the texture's memory; the mip levels are made after.
        private void BakeTiles(Texture2D target, Func<PlateCpu, byte[]> patternOf, byte[] corner)
        {
            Unity.Collections.NativeArray<byte> raw = target.GetRawTextureData<byte>();
            foreach (PlateVisual plate in AllPlates())
            {
                PlateCpu c = plate.Cpu;
                if (c.Width <= 0) continue;   // no tiles yet
                byte[] row = new byte[c.Width * 4];
                FillPattern(row, patternOf(c));
                for (int face = 0; face < 2; face++)
                    for (int y = 0; y < c.Height; y++)
                        Unity.Collections.NativeSliceExtensions.Slice(raw, ((c.TileY[face] + y) * AtlasSize + c.TileX[face]) * 4, c.Width * 4).CopyFrom(row);
            }
            byte[] flat = new byte[PlateCpu.Align * 4];
            FillPattern(flat, corner);
            for (int y = 0; y < PlateCpu.Align; y++)
                Unity.Collections.NativeSliceExtensions.Slice(raw, y * AtlasSize * 4, PlateCpu.Align * 4).CopyFrom(flat);
            target.Apply(true, false);
        }

        // The hole atlas: white everywhere (the color comes from the material) with the mask in its alpha, 255 = solid.
        // It is RGBA, not Alpha8: an Alpha8 texture reads as black (0,0,0,a) on Direct3D, which painted the plate black.
        // Made when the first hole shows up, so vehicles that never get one do not pay for it.
        public void EnsureAlpha()
        {
            if (Alpha != null) return;
            Alpha = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, PlateCpu.MipLevels, false) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments damage holes" };
            FillTexture(Alpha, 255, 255, 255, 255);   // no holes
            if (PaintMode) BakePaint();
        }

        // The bump map: the green channel is the height, 0.5 = flat. Made when the relief setting asks for it.
        public void EnsureHeight()
        {
            if (Height != null) return;
            Height = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, PlateCpu.MipLevels, true) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments damage heights" };
            FillTexture(Height, 128, 128, 128, 255);
            if (Gloss) BakeSurface();
            if (heightPending)
            {
                heightPending = false;
                RefreshRelief();   // the materials that were set up without it take it now
            }
        }

        // Makes ONE atlas that is wanted and not there yet (each is 16 MB and costs ~15 ms to make, so they are spread over frames instead of
        // all landing in the frame of the first impact). Called every frame; true = it made one (the caller makes no more this frame).
        public bool WarmUp()
        {
            if (Disposed || !Touched) return false;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            string what = null;
            if (heightPending && Height == null) { EnsureHeight(); what = "bump map"; }
            else if (Alpha == null && (PaintMode || !ArmorFragmentsPlugin.DamageMapsGeometry.Value)) { EnsureAlpha(); what = PaintMode ? "paint map" : "hole map"; }
            else if (DamageAtlas == null && ArmorFragmentsPlugin.ImpactCameraDamageView.Value) { EnsureDamage(); what = "damage view"; }
            if (what == null) return false;
            ArmorFragmentsPlugin.Log.LogInfo("[PERF] made the " + what + " atlas of '" + Aircraft.name + "' in its own frame: " + PerfMonitor.Since(started).ToString("F1") + " ms");
            return true;
        }

        // The damage view: transparent everywhere until a plate is marked.
        public void EnsureDamage()
        {
            if (DamageAtlas != null) return;
            DamageAtlas = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, PlateCpu.MipLevels, false) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments damage view" };
            FillTexture(DamageAtlas, 0, 0, 0, 0);
        }

        // How far the shader's parallax moves the surface for the largest height the bump map can say, in UV units (the shader
        // takes its amplitude in UV, and the tiles hold Density pixels per meter of a plate).
        public float ParallaxAmplitude
        {
            get { return PlateStamps.HeightRange * Density / Mathf.Max(AtlasSize, 1); }
        }

        // Sets a material (the vehicle's clone of the part material, or a holed plate's Lit one) to show the relief the way the
        // setting says: the normal map, the bump map (parallax), or both.
        public void ConfigureRelief(Material material)
        {
            int mode = Mathf.Clamp(ArmorFragmentsPlugin.DamageMapsRelief.Value, 0, 2);
            bool normal = mode != 1, bump = mode != 0;

            material.SetTexture("_BumpMap", normal ? Normal : null);
            material.SetFloat("_BumpScale", ArmorFragmentsPlugin.DamageMapsNormalStrength.Value);
            SetKeyword(material, "_NORMALMAP", normal);

            if (bump && Height == null)
            {
                heightPending = true;   // made in a frame of its own (WarmUp); the materials get it then
                bump = false;
            }
            material.SetTexture("_ParallaxMap", bump ? Height : null);
            material.SetFloat("_Parallax", ParallaxAmplitude);
            SetKeyword(material, "_PARALLAXMAP", bump);
            if (bump) ReportParallax(material);
        }

        // A holed/marked plate's Lit material reads the bump atlas as its metallic/smoothness map (steel shiny and metal, scorched paint dull).
        // Until the atlas exists the material keeps the paint's own values.
        public void ConfigureGloss(Material material)
        {
            if (!Gloss) return;
            if (Height == null) { heightPending = true; return; }
            material.SetTexture("_MetallicGlossMap", Height);
            material.SetFloat("_Smoothness", 1f);   // the shader multiplies the map's alpha by it
            SetKeyword(material, "_METALLICSPECGLOSSMAP", true);
            ReportGloss(material);
        }

        private static bool glossReported;

        // Once: does the Lit shader of this build have the keyword for the metallic map? (If its variant was stripped the map is accepted and ignored.)
        private static void ReportGloss(Material material)
        {
            if (glossReported) return;
            glossReported = true;
            try
            {
                Shader shader = material.shader;
                bool declared = false;
                foreach (UnityEngine.Rendering.LocalKeyword keyword in shader.keywordSpace.keywords)
                    if (keyword.name == "_METALLICSPECGLOSSMAP") declared = true;
                ArmorFragmentsPlugin.Log.LogInfo("[GLOSS] shader '" + shader.name + "': _MetallicGlossMap slot " + material.HasProperty("_MetallicGlossMap") +
                    ", keyword _METALLICSPECGLOSSMAP declared " + declared + ", on " + material.IsKeywordEnabled("_METALLICSPECGLOSSMAP") +
                    ". If 'declared' is false the steel is not shiny (its variant is not in the build).");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogInfo("[GLOSS] could not inspect the shader: " + e.Message);
            }
        }

        private static readonly HashSet<string> ParallaxReported = new HashSet<string>();

        // Once per shader: does it have the bump map's slot, the amplitude, and the keyword that switches the parallax on? (If the shader has
        // no such keyword, or its compiled variants were stripped from the build, the bump map is accepted and then ignored.)
        private static void ReportParallax(Material material)
        {
            try
            {
                Shader shader = material.shader;
                if (shader == null || !ParallaxReported.Add(shader.name)) return;
                bool declared = false;
                foreach (UnityEngine.Rendering.LocalKeyword keyword in shader.keywordSpace.keywords)
                    if (keyword.name == "_PARALLAXMAP") declared = true;
                ArmorFragmentsPlugin.Log.LogInfo("[RELIEF] shader '" + shader.name + "': _ParallaxMap slot " + material.HasProperty("_ParallaxMap") +
                    ", _Parallax " + material.HasProperty("_Parallax") + ", keyword _PARALLAXMAP declared " + declared +
                    ", on " + material.IsKeywordEnabled("_PARALLAXMAP") + ", amplitude " + material.GetFloat("_Parallax").ToString("F4") +
                    ", bump atlas " + (material.GetTexture("_ParallaxMap") != null) + ". If 'declared' is false the shader has no parallax, and only the normal map or the real geometry shows relief.");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogInfo("[RELIEF] could not inspect the shader: " + e.Message);
            }
        }

        private static void SetKeyword(Material material, string keyword, bool on)
        {
            if (on) material.EnableKeyword(keyword);
            else material.DisableKeyword(keyword);
        }

        // The relief setting changed: every material of the vehicle is set again.
        public void RefreshRelief()
        {
            if (Disposed) return;
            if (Clone != null) ConfigureRelief(Clone);
            foreach (PlateVisual plate in Plates.Values)
                if (plate.Lit != null) { ConfigureRelief(plate.Lit); ConfigureGloss(plate.Lit); }
        }

        // A rectangle computed by the worker goes into the atlas. Returns the milliseconds it took.
        public double Deliver(Delivery d)
        {
            if (Disposed) return 0;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            Texture2D target;
            switch (d.Kind)
            {
                case AtlasKind.Hole: EnsureAlpha(); target = Alpha; break;
                case AtlasKind.Height: EnsureHeight(); target = Height; break;
                case AtlasKind.Damage: EnsureDamage(); target = DamageAtlas; break;
                default: target = Normal; break;
            }
            const int bytesPerPixel = 4;
            int bucket = 16;
            while (bucket < Mathf.Max(d.W, d.H)) bucket *= 2;
            Texture2D staging = Staging(d.Kind, bucket);
            // Level 0, then the smaller levels the worker made (when the rectangle is aligned for them).
            int levels = d.Mips != null && d.X % PlateCpu.Align == 0 && d.Y % PlateCpu.Align == 0 ? PlateCpu.MipLevels : 1;
            int w = d.W, h = d.H;
            for (int level = 0; level < levels; level++)
            {
                byte[] source = level == 0 ? d.Bytes : d.Mips[level - 1];
                int stride = bucket >> level;
                byte[] padded = Padded(stride, d.Kind, stride * stride * bytesPerPixel);
                for (int row = 0; row < h; row++)
                    Buffer.BlockCopy(source, row * w * bytesPerPixel, padded, row * stride * bytesPerPixel, w * bytesPerPixel);
                staging.SetPixelData(padded, level);
                w >>= 1; h >>= 1;
            }
            staging.Apply(false, false);
            w = d.W; h = d.H;
            for (int level = 0; level < levels; level++)
            {
                Graphics.CopyTexture(staging, 0, level, 0, 0, w, h, target, 0, level, d.X >> level, d.Y >> level);
                w >>= 1; h >>= 1;
            }

            // Real geometry has real holes (but paint is drawn by the Lit shader). The switch to Lit waits until every rectangle of the block's
            // jobs is in the atlas (PlateDamageMaps.Tick): done now, the Lit material would read the atlas's blank, white, shiny parts for a few frames.
            if (d.Kind == AtlasKind.Hole && d.Plate != null && (PaintMode || !d.Plate.UsesGeometry)) PlateDamageMaps.WantLit(d.Plate.Main ?? d.Plate);
            return watch.Elapsed.TotalMilliseconds;
        }

        private static readonly Dictionary<int, Texture2D> StagingTextures = new Dictionary<int, Texture2D>();
        private static readonly Dictionary<int, byte[]> PaddedBuffers = new Dictionary<int, byte[]>();

        private static Texture2D Staging(AtlasKind kind, int bucket)
        {
            int key = bucket * 4 + (int)kind;
            Texture2D texture;
            if (StagingTextures.TryGetValue(key, out texture) && texture != null) return texture;
            // Same format as the atlas it feeds (CopyTexture needs it): normals and heights are linear data, the hole mask and the
            // damage view's colors are not.
            texture = new Texture2D(bucket, bucket, TextureFormat.RGBA32, PlateCpu.MipLevels, kind == AtlasKind.Normal || kind == AtlasKind.Height);
            texture.name = "ArmorFragments damage staging";
            StagingTextures[key] = texture;
            return texture;
        }

        // The plugin is being unloaded: the staging textures are shared by every vehicle, so they are freed here, not in Dispose.
        public static void ReleaseStaging()
        {
            foreach (Texture2D texture in StagingTextures.Values)
                if (texture != null) UnityEngine.Object.Destroy(texture);
            StagingTextures.Clear();
            PaddedBuffers.Clear();
        }

        // The staging texture takes a whole texture's worth of bytes, of exactly its size (one buffer per size, reused).
        private static byte[] Padded(int bucket, AtlasKind kind, int length)
        {
            int key = bucket * 4 + (int)kind;
            byte[] buffer;
            if (!PaddedBuffers.TryGetValue(key, out buffer) || buffer.Length != length)
            {
                buffer = new byte[length];
                PaddedBuffers[key] = buffer;
            }
            return buffer;
        }

        // Puts every plate back as it was and frees the atlases. (restore = false when the aircraft is already gone.)
        public void Dispose(bool restore)
        {
            if (Disposed) return;
            Disposed = true;
            foreach (PlateVisual plate in Plates.Values) plate.Release(restore);
            Plates.Clear();
            if (Clone != null)
            {
                if (restore && Theme != null) Theme.ReleaseDefaultPartMaterialInstance(Clone);
                else UnityEngine.Object.Destroy(Clone);
                Clone = null;
            }
            if (Normal != null) UnityEngine.Object.Destroy(Normal);
            if (Alpha != null) UnityEngine.Object.Destroy(Alpha);
            if (Height != null) UnityEngine.Object.Destroy(Height);
            if (DamageAtlas != null) UnityEngine.Object.Destroy(DamageAtlas);
            Normal = null;
            Alpha = null;
            Height = null;
            DamageAtlas = null;
        }

        // The game's part material with the normal atlas (one for the whole vehicle).
        public Material PartClone()
        {
            if (Clone != null) return Clone;
            Material material = Theme.RequestDefaultPartMaterialInstance();
            if (material == null) return null;
            ConfigureRelief(material);
            Clone = material;
            return Clone;
        }

        // The atlas position of the flat sentinel (the middle of its corner), as a UV.
        public Vector2 SentinelUv()
        {
            float c = (SentinelSize * 0.5f) / AtlasSize;
            return new Vector2(c, c);
        }
    }

    // One plate of a vehicle: how it was before the maps touched it, and what it shows now.
    internal sealed class PlateVisual
    {
        public PlateInfo Info;
        public PlateCpu Cpu;
        public VehicleMaps Owner;
        public PlateVisual Main;                                   // a side of a thick block: the block's own plate (which owns the mesh and the material)
        public readonly PlateVisual[] Sides = new PlateVisual[2];  // the block's sides, if it has them
        public bool Applied;
        public bool Failed;
        public bool HasHole;
        public Material Lit;
        public bool NeedsFull;   // its tiles were just given out: the next job hands over the whole of them
        public bool TakeFull() { bool wanted = NeedsFull; NeedsFull = false; return wanted; }
        public System.Collections.Generic.List<Collider> Neighbors;   // the other armor plates' colliders that reach this plate (PlateClearance)
        public ClearGrid[] ClearKnown;                                // what the main thread has measured of the room around it, per face
        public bool[][] ClearDone;

        private Material[] originalMaterials;
        private bool originalExclude;
        private List<Vector2> originalUv0;
        private List<Vector4> originalTangents;
        private bool wroteMesh;

        // ---- Real geometry (experimental, see PlateMesher) ----
        public PlateMeshSource Source;   // the mesh as it was, read once on the main thread; Ok says whether it can be rewritten
        public int PendingJobs;          // jobs for this plate the worker has not finished (it builds the mesh only after the last one)
        public int GeometryApplied;      // how many times its mesh has been rewritten (for the log)

        private sealed class SavedMesh
        {
            public Vector3[] Vertices, Normals;
            public int[] Triangles;
            public readonly List<Vector2> Uv0 = new List<Vector2>();
            public readonly List<Vector4> Uv1 = new List<Vector4>();
            public readonly List<Vector4> Uv2 = new List<Vector4>();
            public readonly List<Vector4> Tangents = new List<Vector4>();
            public IndexFormat Format;
            public int Uv1Dimension;
            public int Uv2Dimension;
        }
        private SavedMesh saved;

        // The plate is drawn with real geometry (setting on and the plate can be rewritten): its holes are missing cells of the
        // mesh, and its relief is the mesh's own.
        public bool UsesGeometry
        {
            get { return ArmorFragmentsPlugin.DamageMapsGeometry.Value && Source != null && Source.Ok; }
        }

        // Main thread. Can this plate's mesh be rewritten as real geometry? (Reads the mesh the first time, and logs why not.)
        public bool CanRemesh()
        {
            if (!Applied) return false;
            if (Main != null) return Main.CanRemesh();   // the block's mesh is one: a side's marks are cut into it too (by the block's own plate)
            if (Source == null)
            {
                Source = PlateMeshSource.Read(Info);
                if (!Source.Ok)
                    ArmorFragmentsPlugin.Log.LogInfo("[GEOM] plate '" + Info.Part.Part.Name + "' stays flat: " + Source.Why);
            }
            return Source.Ok;
        }

        // The rewritten mesh as last put in, with its flat and its pushed vertices, so the blend between the two can change without a
        // new build: 0 = the mesh is flat and the maps (normal / bump) show the relief, 1 = the geometry shows it and the maps are off.
        private MeshData geometry;
        private Vector3[] blendVertices, blendNormals;
        public float Blend;
        public float BlendTime;          // when the blend last changed (unscaled time)

        public bool HasGeometry
        {
            get { return geometry != null; }
        }

        // Triangles and vertices of the plate's rewritten mesh now (0 if it has none): what the cameras have to draw for it.
        public int GeometryTriangles
        {
            get { return geometry != null ? geometry.Triangles.Length / 3 : 0; }
        }

        public int GeometryVertices
        {
            get { return geometry != null ? geometry.Vertices.Length : 0; }
        }

        // The biggest push of the geometry in the mesh, meters (how much a view could see of it).
        public float MaxHeight
        {
            get { return geometry != null ? geometry.MaxHeight : 0f; }
        }

        // Puts a rewritten mesh into the game's own mesh object (so nothing that refers to it is disturbed), at the given blend.
        // Returns the milliseconds it took.
        public double ApplyGeometry(MeshData data, float blend)
        {
            Mesh mesh = Info.Mesh;
            if (mesh == null || Owner.Disposed || !Applied || Source == null || !Source.Ok) return 0;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            bool firstTime = saved == null;
            if (saved == null) saved = Save(mesh);
            double savedMs = watch.Elapsed.TotalMilliseconds;
            geometry = data;
            blendVertices = (Vector3[])data.BaseVertices.Clone();
            blendNormals = (Vector3[])data.BaseNormals.Clone();
            Blend = Mathf.Clamp01(blend);
            MixMoved();
            double mixedMs = watch.Elapsed.TotalMilliseconds;

            mesh.Clear();
            mesh.indexFormat = data.Vertices.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(blendVertices);
            mesh.SetNormals(blendNormals);
            mesh.SetUVs(0, data.Uv0);
            if (data.Uv1 != null) SetUv(mesh, 1, data.Uv1, Source.Uv1Dimension);
            if (data.Uv2 != null) SetUv(mesh, 2, data.Uv2, Source.Uv2Dimension);
            mesh.SetTriangles(data.Triangles, 0, true);
            double setMs = watch.Elapsed.TotalMilliseconds;
            mesh.RecalculateTangents();
            double tangentsMs = watch.Elapsed.TotalMilliseconds;
            ApplyMapStrength();
            GeometryApplied++;
            double total = watch.Elapsed.TotalMilliseconds;
            if (total > 20.0)
                ArmorFragmentsPlugin.Log.LogInfo("[PERF] putting a plate's new mesh in took " + total.ToString("F1") + " ms (" + data.Vertices.Length + " vertices" +
                    (firstTime ? ", the first time" : "") + "): saving the old mesh " + savedMs.ToString("F1") + ", mixing " + (mixedMs - savedMs).ToString("F1") +
                    ", setting it " + (setMs - mixedMs).ToString("F1") + ", tangents " + (tangentsMs - setMs).ToString("F1") + ", the rest " + (total - tangentsMs).ToString("F1") + " ms");
            return total;
        }

        // The vertices the push moves are set between their flat place and their pushed one.
        private void MixMoved()
        {
            int[] moved = geometry.Moved;
            for (int k = 0; k < moved.Length; k++)
            {
                int i = moved[k];
                blendVertices[i] = Vector3.Lerp(geometry.BaseVertices[i], geometry.Vertices[i], Blend);
                blendNormals[i] = Vector3.Lerp(geometry.BaseNormals[i], geometry.Normals[i], Blend).normalized;
            }
        }

        // The transition: moves the geometry's blend (and, the other way, the maps' strength). Cheap: only the moved vertices are
        // recomputed, and the arrays go back into the mesh as they are.
        public void SetBlend(float blend, float now)
        {
            Mesh mesh = Info.Mesh;
            if (geometry == null || mesh == null || Owner.Disposed) return;

            Blend = Mathf.Clamp01(blend);
            BlendTime = now;
            MixMoved();
            mesh.SetVertices(blendVertices);
            mesh.SetNormals(blendNormals);
            if (Blend < 0.01f || Blend > 0.99f) mesh.RecalculateBounds();
            ApplyMapStrength();
        }

        // The normal map and the parallax fade out as the geometry fades in (a property block: the vehicle's material is shared).
        private void ApplyMapStrength()
        {
            Renderer renderer = Info.Map.Renderer;
            if (renderer == null) return;
            float keep = 1f - Mathf.Clamp01(Blend);
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            block.SetFloat("_Parallax", OwnParallax() * keep);   // always: this plate's tiles are not the vehicle's nominal density
            if (Blend > 0.001f) block.SetFloat("_BumpScale", ArmorFragmentsPlugin.DamageMapsNormalStrength.Value * keep);
            renderer.SetPropertyBlock(block);
        }

        // How far the shader's parallax moves the surface for the biggest height the bump map can say, in UV units, FOR THIS PLATE: the tiles are
        // given out one plate at a time and each has the density it got (the sides of a block half of its own), while one renderer has one value,
        // so the sparsest tile of the block decides (a tile that is too dense for it only shows a little less relief; one that is too sparse
        // would show far too much, smeared).
        private float OwnParallax()
        {
            PlateVisual block = Main != null ? Main : this;
            float least = block.Cpu.Width > 0 ? Mathf.Min(block.Cpu.PxU, block.Cpu.PxV) : float.MaxValue;
            foreach (PlateVisual side in block.Sides)
                if (side != null && side.Cpu.Width > 0) least = Mathf.Min(least, Mathf.Min(side.Cpu.PxU, side.Cpu.PxV));
            if (least == float.MaxValue) return Owner.ParallaxAmplitude;
            return PlateStamps.HeightRange * least / Mathf.Max(Owner.AtlasSize, 1);
        }

        // The mesh as the game made it (and then as the maps left it: with the normal map's UV0), so it can be put back.
        private SavedMesh Save(Mesh mesh)
        {
            SavedMesh copy = new SavedMesh
            {
                Vertices = mesh.vertices, Normals = mesh.normals, Triangles = mesh.GetTriangles(0), Format = mesh.indexFormat,
                Uv1Dimension = Source != null ? Source.Uv1Dimension : 0,
                Uv2Dimension = Source != null ? Source.Uv2Dimension : 0,
            };
            mesh.GetUVs(0, copy.Uv0);
            mesh.GetTangents(copy.Tangents);
            if (copy.Uv1Dimension > 0) mesh.GetUVs(1, copy.Uv1);
            if (copy.Uv2Dimension > 0) mesh.GetUVs(2, copy.Uv2);
            return copy;
        }

        // A UV channel (1 or 2) back into a mesh with the number of components it had.
        private static void SetUv(Mesh mesh, int channel, Vector4[] values, int dimension)
        {
            if (dimension == 2)
            {
                Vector2[] two = new Vector2[values.Length];
                for (int i = 0; i < two.Length; i++) two[i] = values[i];
                mesh.SetUVs(channel, two);
            }
            else if (dimension == 3)
            {
                Vector3[] three = new Vector3[values.Length];
                for (int i = 0; i < three.Length; i++) three[i] = values[i];
                mesh.SetUVs(channel, three);
            }
            else
            {
                mesh.SetUVs(channel, values);
            }
        }

        // The game's own geometry back (the maps keep working on it).
        public void RestoreGeometry()
        {
            geometry = null;
            blendVertices = null;
            blendNormals = null;
            Blend = 0f;
            if (saved == null) return;
            SavedMesh copy = saved;
            saved = null;

            Mesh mesh = Info.Mesh;
            if (mesh != null)
            {
                mesh.Clear();
                mesh.indexFormat = copy.Format;
                mesh.SetVertices(copy.Vertices);
                mesh.SetNormals(copy.Normals);
                mesh.SetUVs(0, copy.Uv0);
                if (copy.Uv1Dimension > 0 && copy.Uv1.Count == copy.Vertices.Length) SetUv(mesh, 1, copy.Uv1.ToArray(), copy.Uv1Dimension);
                if (copy.Uv2Dimension > 0 && copy.Uv2.Count == copy.Vertices.Length) SetUv(mesh, 2, copy.Uv2.ToArray(), copy.Uv2Dimension);
                mesh.SetTriangles(copy.Triangles, 0, true);
                if (copy.Tangents.Count == copy.Vertices.Length) mesh.SetTangents(copy.Tangents);
                else mesh.RecalculateTangents();
            }
            Renderer renderer = Info.Map != null ? Info.Map.Renderer : null;
            if (renderer != null) renderer.SetPropertyBlock(null);
        }

        // UV0 into the plate's tiles, tangents, and the vehicle's material.
        public bool TryApply()
        {
            System.Diagnostics.Stopwatch setupWatch = System.Diagnostics.Stopwatch.StartNew();
            PartMaterialScript.RendererMaterialMap map = Info.Map;
            Renderer renderer = map.Renderer;
            if (renderer == null || !renderer.enabled)
            {
                Failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] plate '" + Info.Part.Part.Name +
                    "' is merged into its group's mesh: turn on 'Keep armor plates out of the mesh merge' and start the flight again");
                return false;
            }

            Material clone = Owner.PartClone();
            double cloneMs = setupWatch.Elapsed.TotalMilliseconds;
            if (clone == null)
            {
                Failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] no part material could be made for '" + Info.Part.Part.Name + "'");
                return false;
            }

            if (!Owner.AllocateTiles(this))
            {
                Failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] no room is left in the vehicle's atlas for plate '" + Info.Part.Part.Name + "': it gets no marks (raise MaxAtlasSize or lower PixelsPerMeter)");
                return false;
            }

            Mesh mesh = Info.Mesh;
            originalMaterials = map.OriginalMaterials;
            originalExclude = map.ExcludeFromMeshCombine;
            originalUv0 = new List<Vector2>();
            originalTangents = new List<Vector4>();
            mesh.GetUVs(0, originalUv0);
            mesh.GetTangents(originalTangents);

            WriteUv(mesh);
            mesh.RecalculateTangents();
            wroteMesh = true;
            double meshMs = setupWatch.Elapsed.TotalMilliseconds;

            SetMaterial(clone);
            map.ExcludeFromMeshCombine = true;
            Applied = true;
            foreach (PlateVisual side in Sides) if (side != null) side.Applied = true;
            ApplyMapStrength();   // this plate's own parallax amplitude
            double setupMs = setupWatch.Elapsed.TotalMilliseconds;
            if (setupMs > 8.0)
                ArmorFragmentsPlugin.Log.LogInfo("[PERF] first use of plate '" + Info.Part.Part.Name + "' took " + setupMs.ToString("F1") + " ms (" + mesh.vertexCount +
                    " vertices): the vehicle's material " + cloneMs.ToString("F1") + " ms, the mesh " + (meshMs - cloneMs).ToString("F1") + " ms, setting the material " +
                    (setupMs - meshMs).ToString("F1") + " ms");
            return true;
        }

        private void WriteUv(Mesh mesh)
        {
            PlateGeometry geo = Info.Geometry;
            int count = mesh.vertexCount;
            List<Vector3> vertices = new List<Vector3>(count);
            List<Vector3> normals = new List<Vector3>(count);
            mesh.GetVertices(vertices);
            mesh.GetNormals(normals);
            bool hasNormals = normals.Count == count;

            // The vertices that belong to no face of the block (a sloped or rounded part of its surface) must not read the atlas's shared gray corner: that
            // is bare steel, and the block turned shiny steel where nothing had hit it. In paint mode they read a pixel in the flat border of the block's
            // own tile instead, which is filled with the block's paint (and its base shine) like the rest of the tile.
            Vector2 sentinel = Owner.PaintMode
                ? new Vector2((Cpu.TileX[0] + 1f) / Owner.AtlasSize, (Cpu.TileY[0] + 1f) / Owner.AtlasSize)
                : Owner.SentinelUv();
            float atlas = Owner.AtlasSize;
            int innerW = Cpu.Width - 2 * PlateCpu.Border, innerH = Cpu.Height - 2 * PlateCpu.Border;

            List<Vector2> uvs = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = Info.ToPart.MultiplyPoint3x4(vertices[i]);
                Vector3 n = hasNormals ? Info.ToPart.MultiplyVector(normals[i]).normalized : geo.N;

                // The plate this vertex belongs to: the block's own (its two big faces) or one of its sides, whichever the normal lines up with best.
                PlateVisual owner = null;
                float best = 0.5f, facing = 0f;
                for (int k = -1; k < 2; k++)
                {
                    PlateVisual candidate = k < 0 ? this : Sides[k];
                    if (candidate == null || candidate.Cpu.Width <= 0) continue;
                    float f = Vector3.Dot(n, candidate.Info.Geometry.N);
                    if (Mathf.Abs(f) >= best && (owner == null || Mathf.Abs(f) > best + 1e-4f)) { owner = candidate; best = Mathf.Abs(f); facing = f; }
                }
                if (owner == null) { uvs.Add(sentinel); continue; }

                PlateGeometry g = owner.Info.Geometry;
                PlateCpu c = owner.Cpu;
                int face = facing > 0f ? 0 : 1;
                Vector2 plane = g.ToPlane(p);
                float u = Mathf.Clamp01(plane.x / g.SizeU);
                float v = Mathf.Clamp01(plane.y / g.SizeV);
                uvs.Add(new Vector2((c.TileX[face] + PlateCpu.Border + u * (c.Width - 2 * PlateCpu.Border)) / atlas,
                                    (c.TileY[face] + PlateCpu.Border + v * (c.Height - 2 * PlateCpu.Border)) / atlas));
            }
            mesh.SetUVs(0, uvs);
        }

        private void SetMaterial(Material material)
        {
            PartMaterialScript.RendererMaterialMap map = Info.Map;
            Material[] materials = new Material[map.OriginalMaterials.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = material;
            map.OriginalMaterials = materials;
            map.SetRendererMaterial(materials);
        }

        // The plate has a hole: from now on it is drawn with the Lit shader, which can cut it out.
        public void EnsureLit()
        {
            if (Main != null) { Main.EnsureLit(); return; }   // a side is drawn with the block's own material
            if (HasHole || !Applied || Owner.Disposed) return;
            HasHole = true;

            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null || !lit.isSupported)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] the URP Lit shader is not available: no see-through holes");
                return;
            }
            Owner.EnsureAlpha();

            Color color;
            float metallic, smoothness;
            VehicleMaps.ReadPaint(Owner.Theme, Info.Part, out color, out metallic, out smoothness);
            if (Owner.PaintMode) color = Color.white;   // the atlas carries the whole color

            Material material = new Material(lit);
            material.name = "ArmorFragments plate (Lit)";
            material.SetTexture("_BaseMap", Owner.Alpha);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            Owner.ConfigureRelief(material);
            Owner.ConfigureGloss(material);
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cutoff", 0.5f);
            material.EnableKeyword("_ALPHATEST_ON");
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Cull", ArmorFragmentsPlugin.DamageMapsHoleBothSides.Value ? 0f : 2f);
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = (int)RenderQueue.AlphaTest;
            Lit = material;
            SetMaterial(material);
            CopyDecalLayer();
        }

        // The game's part shader gets the decals (emblems, text) that project onto a part from the mesh: the decal target's id is stored in the
        // mesh's UV1.y, as the bits of the rendering-layer mask the decal projectors are set to (DecalTargetScript.InitializeRenderer). The Lit shader
        // reads the renderer's own rendering-layer mask instead, so a plate switched to it lost its emblems. The mask is copied to the renderer.
        private uint originalLayerMask;
        private bool changedLayerMask;

        private void CopyDecalLayer()
        {
            try
            {
                Renderer renderer = Info.Map.Renderer;
                Mesh mesh = Info.Mesh;
                if (renderer == null || mesh == null || changedLayerMask) return;
                List<Vector3> uv1 = new List<Vector3>();
                mesh.GetUVs(1, uv1);
                if (uv1.Count == 0) return;
                uint mask = unchecked((uint)BitConverter.SingleToInt32Bits(uv1[0].y));
                if (mask == 0) return;
                originalLayerMask = renderer.renderingLayerMask;
                renderer.renderingLayerMask = mask;
                changedLayerMask = true;
                ArmorFragmentsPlugin.Log.LogInfo("[DMAP] plate '" + Info.Part.Part.Name + "': its decals (emblems) are kept: the renderer's rendering layer 0x" +
                                                 originalLayerMask.ToString("X") + " -> 0x" + mask.ToString("X"));
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] could not copy the decal layer of a plate: " + e.Message);
            }
        }

        // Back to the game's own look (restore) and free what was made.
        public void Release(bool restore)
        {
            if (Main != null) return;   // a side owns nothing: the block's own plate restores the mesh and the material
            try
            {
                if (restore && Applied)
                {
                    RestoreGeometry();
                    Info.Map.OriginalMaterials = originalMaterials;
                    Info.Map.ExcludeFromMeshCombine = originalExclude;
                    if (Info.Map.Renderer != null) Info.Map.SetRendererMaterial(originalMaterials);
                    if (wroteMesh && Info.Mesh != null)
                    {
                        Info.Mesh.SetUVs(0, originalUv0);
                        Info.Mesh.SetTangents(originalTangents);
                    }
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] a plate could not be restored: " + e.Message);
            }
            if (changedLayerMask)
            {
                try { if (Info.Map.Renderer != null) Info.Map.Renderer.renderingLayerMask = originalLayerMask; } catch (Exception) { }
                changedLayerMask = false;
            }
            if (Lit != null) UnityEngine.Object.Destroy(Lit);
            Lit = null;
            Applied = false;
        }
    }

    // The worker thread: plays the impacts onto the plates' CPU maps and works out the rectangles to deliver. A shell's impact
    // is simulated (ImpactSim, on every thread that is free) when the settings say so; everything else, and any simulation that
    // is too slow or disagrees with the energy model, gets the quick analytic marks (PlateStamps). It touches no Unity object
    // (only plain arrays), so it cannot hitch the game; the main thread takes what it makes.
    internal static class DamageWorker
    {
        public sealed class Job
        {
            public VehicleMaps Owner;
            public PlateVisual Plate;
            public PlateImpact[] Impacts;
            public float DeliverAt;

            // The simulation's settings, taken when the impact happened (the worker never reads the settings).
            public int SimQuality = -1;        // 0 low, 1 medium, 2 high; -1 = no simulation (the quick marks)
            public bool AutoQuality;
            public int TimeLimitMs;
            public float ResistanceJPerM3;
            public int Threads;                // 0 = automatic

            // Which extra atlases this job also fills (the settings, taken when the impact happened), and whether it must redo
            // every marked pixel of the plate (a stream that was just switched on catching up).
            public bool Height;
            public bool Damage;
            public bool Everything;
            public List<ClearPatch> ClearPatches;   // the room around the plate that was measured for this job (the worker merges it first)
            public bool SimCracks, SimSmooth;  // the marks' cracks and the smooth edge of a hole
            public bool UseGpu;                // the player's wish (the card is used only if it was found and passed its test)

            // Real geometry (experimental): after the last queued job for the plate the worker also rewrites its mesh.
            public bool Geometry;
            public float GridMeters;
            public float FineMeters;           // the finer cell size around holes and sharp features (0 = none)
            public int MaxVertices;
        }

        private static readonly BlockingCollection<Job> Jobs = new BlockingCollection<Job>(new ConcurrentQueue<Job>());
        public static readonly ConcurrentQueue<Delivery> Done = new ConcurrentQueue<Delivery>();
        public static readonly ConcurrentQueue<MeshData> Meshes = new ConcurrentQueue<MeshData>();   // rewritten plate meshes, for the main thread
        public static readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();   // warnings, for the main thread to log
        public static readonly ConcurrentQueue<string> Infos = new ConcurrentQueue<string>();      // information, the same
        private static Thread thread;
        public static volatile int LastMs10;   // the last job, in tenths of a millisecond

        // The simulation's numbers, for the panel.
        private static int pendingSims;
        public static int PendingSims { get { return Volatile.Read(ref pendingSims); } }
        public static volatile int LastSimMs;
        public static volatile int LastSimQuality = -1;
        public static volatile int LastSimThreads;
        public static volatile bool LastSimGpu;     // the last simulation ran on the graphics card

        // True once the worker thread has been made (then only it may touch the graphics card's solver).
        public static bool HasThread
        {
            get { return thread != null; }
        }
        public static volatile int SimsDone;
        public static volatile int Fallbacks;
        public static volatile int TunedQuality = ImpactSim.MaxQuality;   // the best quality the time limit lets the simulation use now

        private static SimPool pool;
        private static int poolWorkers = -1;
        private static volatile int allowedWorkers = int.MaxValue;
        private static int goodRuns;

        // Set by Shutdown (the plugin is being unloaded): no new job is taken, a simulation that is running stops at its next step.
        private static volatile bool stopping;
        private static readonly object PoolGate = new object();

        public static int Pending
        {
            get { return Jobs.Count; }
        }

        public static int WorkersNow
        {
            get { SimPool p = pool; return p == null ? 0 : Math.Min(p.Workers, p.MaxWorkers) + 1; }
        }

        // The game's frame rate dropped: the simulation lends some threads back (or takes them again).
        public static void SetAllowedWorkers(int workers)
        {
            allowedWorkers = Math.Max(0, workers);
            SimPool p = pool;
            if (p != null) p.MaxWorkers = Math.Min(p.Workers, allowedWorkers);
        }

        public static int ConfiguredWorkers(int threads)
        {
            int total = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount - 2);   // automatic: all but two cores
            return Math.Max(0, total - 1);                                                     // the worker thread itself works too
        }

        public static void Enqueue(Job job)
        {
            if (stopping) return;   // unloading: nobody will take it
            if (thread == null)
            {
                thread = new Thread(Run) { IsBackground = true, Name = "ArmorFragments damage maps", Priority = System.Threading.ThreadPriority.BelowNormal };
                thread.Start();
            }
            if (job.SimQuality >= 0) Interlocked.Increment(ref pendingSims);
            if (job.Plate != null) Interlocked.Increment(ref job.Plate.PendingJobs);
            try
            {
                Jobs.Add(job);
            }
            catch (InvalidOperationException)
            {
                // Shutdown closed the queue between the check above and here.
                if (job.SimQuality >= 0) Interlocked.Decrement(ref pendingSims);
                if (job.Plate != null) Interlocked.Decrement(ref job.Plate.PendingJobs);
            }
        }

        // The plugin is being unloaded: stop the worker thread and the simulation's threads without waiting for them (a simulation can
        // take seconds, and this is called from the game's main thread). The queue is closed (no new job; the thread ends when it has
        // looked at what is left, which it now skips), a simulation in progress stops at its next step, and the pool's threads leave.
        // Nothing here touches a Unity object.
        public static void Shutdown()
        {
            stopping = true;
            try { Jobs.CompleteAdding(); }
            catch (ObjectDisposedException) { }

            lock (PoolGate)
            {
                if (pool != null) pool.Dispose();
                pool = null;
                poolWorkers = -1;
            }

            Delivery done;
            while (Done.TryDequeue(out done)) { }
            MeshData rewritten;
            while (Meshes.TryDequeue(out rewritten)) { }
            string text;
            while (Messages.TryDequeue(out text)) { }
            while (Infos.TryDequeue(out text)) { }
        }

        private static void Run()
        {
            try
            {
                Work();
            }
            finally
            {
                GpuStatus.ReleaseSolver();   // this thread is the only one that used the card: it lets it go
            }
        }

        private static void Work()
        {
            foreach (Job job in Jobs.GetConsumingEnumerable())
            {
                if (stopping)
                {
                    if (job.SimQuality >= 0) Interlocked.Decrement(ref pendingSims);
                    if (job.Plate != null) Interlocked.Decrement(ref job.Plate.PendingJobs);
                    continue;   // drain what is left without working on it
                }

                try
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    if (job.ClearPatches != null)
                        foreach (ClearPatch patch in job.ClearPatches) job.Plate.Cpu.MergeClear(patch);
                    foreach (PlateImpact impact in job.Impacts)
                    {
                        if (stopping) break;
                        if (job.SimQuality >= 0 && SimBridge.Wants(impact) && TrySimulate(job, impact)) continue;
                        if (stopping) break;   // the simulation was cut short by Shutdown
                        PlateStamps.Apply(job.Plate.Cpu, impact);
                    }
                    if (stopping) continue;    // nobody is left to take what this job makes

                    List<Delivery> output = new List<Delivery>();
                    PlateStamps.Flush(job.Plate.Cpu, job.Plate, job.Owner, job.DeliverAt, output, job.Height, job.Damage, job.Everything, job.Owner.PaintMode, job.Owner.Gloss);
                    foreach (Delivery d in output) Done.Enqueue(d);

                    // Real geometry: the plate's mesh is rewritten once the last job queued for it is done (the later jobs would
                    // only make this one's work stale).
                    PlateVisual block = job.Plate.Main != null ? job.Plate.Main : job.Plate;   // the mesh is the block's: its sides are cut into it too
                    int family = Volatile.Read(ref block.PendingJobs);
                    foreach (PlateVisual side in block.Sides) if (side != null) family += Volatile.Read(ref side.PendingJobs);
                    if (job.Geometry && block.Source != null && block.Source.Ok && family <= 1)
                    {
                        MeshData mesh = PlateMesher.Build(block.Cpu, block.Info.Geometry, block.Source, job.Owner,
                                                          job.GridMeters, job.FineMeters, job.MaxVertices);
                        foreach (PlateVisual side in block.Sides)
                        {
                            if (side == null || side.Cpu.Width <= 0 || (side.Cpu.Faces[0] == null && side.Cpu.Faces[1] == null)) continue;
                            PlateMeshSource second = PlateMeshSource.Derive(side.Info.Geometry, side.Info.ToPart, block.Source, mesh);
                            if (!second.Ok) continue;
                            MeshData extra = PlateMesher.Build(side.Cpu, side.Info.Geometry, second, job.Owner, job.GridMeters, job.FineMeters, job.MaxVertices);
                            mesh = PlateMesher.Combine(mesh, extra);
                        }
                        mesh.Plate = block;
                        mesh.DeliverAt = job.DeliverAt;
                        Meshes.Enqueue(mesh);
                    }
                    LastMs10 = (int)(watch.Elapsed.TotalMilliseconds * 10.0);
                }
                catch (Exception e)
                {
                    Messages.Enqueue("[DMAP] the worker failed on an impact: " + e);
                }
                finally
                {
                    if (job.SimQuality >= 0) Interlocked.Decrement(ref pendingSims);
                    if (job.Plate != null) Interlocked.Decrement(ref job.Plate.PendingJobs);
                }
            }
        }

        // Plays one shell impact with the simulation and lays the result into the plate. False = the quick marks must be used.
        private static bool TrySimulate(Job job, PlateImpact impact)
        {
            try
            {
                int quality = job.AutoQuality ? Math.Min(job.SimQuality, TunedQuality) : job.SimQuality;
                SimPool simPool = null;   // the CPU's threads are only made if the CPU has to do the work
                ImpactSimInput input = SimBridge.InputFor(impact, quality, job.TimeLimitMs, job.ResistanceJPerM3);
                input.Cancelled = StopRequested;

                // The quality is the finest grid the simulation may use. A thick plate or a big shell on a fine grid would run past the
                // time limit (and give nothing), so the grid is the finest one whose predicted time fits the budget. The graphics card is
                // used when the player wants it and it was found and tested; if it fails at any point the CPU takes over for good.
                ClSolver gpu = job.UseGpu ? GpuStatus.Solver : null;
                string grid;
                long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                ImpactSimResult result = null;
                if (gpu != null)
                {
                    try
                    {
                        input.Columns = ChooseColumns(impact, input, quality, true, MapDensity(job), out grid);
                        result = ImpactSim.Run(input, simPool, gpu);
                    }
                    catch (Exception e)
                    {
                        GpuStatus.Fail(e.GetType().Name + ": " + e.Message);
                        Messages.Enqueue("[GPU] the graphics card failed, the CPU does the impact simulation from now on: " + e.Message);
                        gpu = null;
                        result = null;
                    }
                }
                if (result == null)
                {
                    simPool = EnsurePool(job.Threads);
                    input.Columns = ChooseColumns(impact, input, quality, false, MapDensity(job), out grid);
                    result = ImpactSim.Run(input, simPool);
                }
                else
                {
                    grid = GridNote;
                }
                if (stopping) return false;   // cut short by Shutdown: not a slow run, nothing to tune or report
                bool onGpu = gpu != null;
                LearnCost(impact, input, result, onGpu);

                LastSimMs = (int)result.Ms;
                LastSimQuality = quality;
                LastSimGpu = onGpu;
                LastSimThreads = onGpu ? 0 : Math.Min(simPool.Workers, simPool.MaxWorkers) + 1;
                Tune(job, result);

                string what = "[SIMFX] " + impact.Kind + ", quality " + ImpactSim.QualityName(quality) + grid + ", " + result.Particles +
                              " particles, " + result.Steps + " steps, " + result.Ms.ToString("F0") + " ms on " +
                              (onGpu ? "the graphics card" : LastSimThreads + " thread(s)");
                if (result.TimedOut)
                {
                    Fallbacks++;
                    Infos.Enqueue(what + ": too slow (limit " + job.TimeLimitMs + " ms), the quick marks are used");
                    return false;
                }
                if (!SimBridge.Agrees(impact.Kind, result))
                {
                    Fallbacks++;
                    Infos.Enqueue(what + ": it disagrees with the energy model (" + (result.Perforated ? "it got through" : "it did not") +
                                  "), the quick marks are used");
                    return false;
                }

                long appliedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                SimBridge.Apply(job.Plate.Cpu, impact, result, job.SimCracks, job.SimSmooth);
                Infos.Enqueue("[PERF] where the time of that simulation went: " + result.Breakdown + "; laying the result into the plate " +
                              PerfMonitor.Since(appliedAt).ToString("F1") + " ms; the whole job " + PerfMonitor.Since(startedAt).ToString("F1") + " ms");
                SimsDone++;
                Infos.Enqueue(what + ", dent " + (result.DepthM * 1000f).ToString("F0") + " mm" +
                              (result.HoleRadiusM > 0f ? ", hole " + (result.HoleRadiusM * 2000f).ToString("F0") + " mm" : ""));
                return true;
            }
            catch (Exception e)
            {
                Fallbacks++;
                Messages.Enqueue("[SIMFX] the simulation failed, the quick marks are used: " + e.Message);
                return false;
            }
        }

        // ---- Choosing the grid ---------------------------------------------------------------------
        //
        // A run costs about  c x columns^3 x layers  milliseconds: the lattice has columns^2 x layers particles and the shell takes
        // a number of steps that grows with the columns (a step is a fixed fraction of a cell). 'c' depends on the machine and on the
        // outcome (a shell that goes through takes more steps than one that is stopped), so it is learned from the runs themselves,
        // one value per kind of impact. The first guesses come from the first real runs in the game (Mono, 14 threads).
        private const double BudgetShare = 0.45;                // of the time limit: a mark that comes later than this is too late to matter
        private static readonly int[] Ladder = { 128, 112, 96, 80, 64, 56, 48, 40, 32 };
        private static readonly double[] CostPerUnit = { 2.0e-3, 1.4e-3, 2.1e-3 };   // ms per (columns^3 x layers): ricochet, blocked, penetrated
        private static readonly double[] CostPerUnitGpu = { 4.0e-5, 4.0e-5, 4.0e-5 };    // the same on the graphics card (measured: about 3e-5)
        private static string GridNote = "";

        private static int CostClass(DamageKind kind)
        {
            return kind == DamageKind.Penetrated ? 2 : kind == DamageKind.Blocked ? 1 : 0;
        }

        private static double Units(ImpactSimInput input, int columns)
        {
            return (double)columns * columns * columns * ImpactSim.LayersFor(input.CaliberM, input.ThicknessM, columns, input.AngleDeg);
        }

        // The finest grid (not above the quality's own) whose predicted time fits the budget; the coarsest one if none does.
        // The plate's map, in pixels per meter (the finer of its two directions): what the marks can show.
        private static float MapDensity(Job job)
        {
            PlateCpu cpu = job.Plate != null ? job.Plate.Cpu : null;
            return cpu != null ? Math.Max(cpu.PxU, cpu.PxV) : 0f;
        }

        private static int ChooseColumns(PlateImpact impact, ImpactSimInput input, int quality, bool gpu, float pixelsPerMeter, out string note)
        {
            int wanted = ImpactSim.ColumnsOfQuality(quality);
            note = "";
            GridNote = "";

            // No finer than the map can show: the quality is the most the simulation may use, not what it always uses. The first grid of the
            // ladder that is at least as fine as the map needs (it is only ever lowered, and never under the ladder's 32).
            string mapNote = "";
            int useful = ImpactSim.UsefulColumns(input.CaliberM, input.AngleDeg, pixelsPerMeter);
            if (useful < wanted)
            {
                int enough = Ladder[0];
                for (int i = Ladder.Length - 1; i >= 0; i--)
                {
                    if (Ladder[i] >= useful) { enough = Ladder[i]; break; }
                }
                if (enough < wanted)
                {
                    mapNote = " (the map shows " + pixelsPerMeter.ToString("F0") + " px/m, which a " + enough + "x" + enough + " grid already out-resolves)";
                    wanted = enough;
                }
            }
            note = mapNote;
            GridNote = mapNote;
            if (input.TimeLimitMs <= 0) return wanted;   // no limit: nothing to fit

            double budget = input.TimeLimitMs * BudgetShare;
            double cost = (gpu ? CostPerUnitGpu : CostPerUnit)[CostClass(impact.Kind)];
            int chosen = Ladder[Ladder.Length - 1];
            foreach (int columns in Ladder)
            {
                if (columns > wanted) continue;
                chosen = columns;
                if (Units(input, columns) * cost <= budget) break;
            }
            if (chosen < wanted)
                note = mapNote + " (a " + chosen + "x" + chosen + " grid: " + wanted + "x" + wanted + " would take about " +
                       (Units(input, wanted) * cost / 1000.0).ToString("F1") + " s)";
            GridNote = note;
            return chosen;
        }

        // What the run really cost, per unit. A run that was stopped by the time limit only says it cost AT LEAST this much.
        private static void LearnCost(PlateImpact impact, ImpactSimInput input, ImpactSimResult result, bool gpu)
        {
            int index = CostClass(impact.Kind);
            double observed = result.Ms / Math.Max(Units(input, input.Columns), 1.0);
            double[] table = gpu ? CostPerUnitGpu : CostPerUnit;
            double known = table[index];
            table[index] = result.TimedOut ? Math.Max(known, observed * 1.2) : Math.Max(0.5 * known + 0.5 * observed, gpu ? 5e-6 : 2e-4);
        }

        // The simulation gives itself the best quality the time limit allows: a run that took most of the limit lowers it,
        // a few quick ones raise it again (up to the setting).
        private static void Tune(Job job, ImpactSimResult result)
        {
            if (!job.AutoQuality || job.TimeLimitMs <= 0) return;
            if (result.TimedOut || result.Ms > 0.7 * job.TimeLimitMs)
            {
                if (TunedQuality > 0)
                {
                    TunedQuality--;
                    Infos.Enqueue("[SIMFX] it was slow: the best quality it will use for now is " + ImpactSim.QualityName(TunedQuality));
                }
                goodRuns = 0;
            }
            else if (result.Ms < 0.2 * job.TimeLimitMs)
            {
                if (++goodRuns >= 3 && TunedQuality < ImpactSim.MaxQuality) { TunedQuality++; goodRuns = 0; }
            }
            else goodRuns = 0;
        }

        private static bool StopRequested()
        {
            return stopping;
        }

        private static SimPool EnsurePool(int threads)
        {
            int workers = ConfiguredWorkers(threads);
            lock (PoolGate)
            {
                if (pool == null || poolWorkers != workers)
                {
                    if (pool != null) pool.Dispose();
                    pool = new SimPool(workers);
                    poolWorkers = workers;
                    Infos.Enqueue("[SIMFX] simulation threads: " + (workers + 1) + " (" + Environment.ProcessorCount + " cores)");
                }
                pool.MaxWorkers = Math.Min(workers, allowedWorkers);
                // Shutdown ran while this job was starting: the pool just made must not outlive it.
                if (stopping) pool.Dispose();
                return pool;
            }
        }
    }
}
