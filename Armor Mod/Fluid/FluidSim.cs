using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments.Fluid
{
    // Drives the GPU smoke solver (Fluid.compute in the effect bundle). It has no dependency on the game, so the same file also runs
    // in the Unity project that builds the bundle, where an editor test renders it to pictures.
    //
    // One simulation is a box of nx x ny x nz cells of `cell` metres. The box has its own axes (the caller decides how they sit in the
    // world); gravity and wind are given in them. The boundaries are open: whatever reaches the edge leaves.
    public sealed class FluidSim : IDisposable
    {
        public struct Blob
        {
            public Vector3 center;     // metres from the box corner
            public float radius;
            public float rho;
            public float temp;
            public Vector3 vel;
            public float radial;
            public float noise;
            public float falloff;
            public float swirl;        // m/s of random swirling inside the ball
            public float swirlFreq;    // 1/m: how fine the swirling is
            public float dust;         // dust density put into the ball
            public float pull;         // 0: `vel` is added (an impulse); > 0: the air in the ball is pulled toward `vel` by this share per call (a steady flow)
            public const int Stride = 16 * 4;
        }

        // A solid thing, given by the matrix that takes a point of the box's space into the shape's own space.
        // kind 0: a box (|local| <= half); kind 1: an ellipsoid (|local / half.x| <= 1); kind 2: a half-space (local.y <= 0).
        // It may move: `vel` is its velocity at `center` and `omega` its spin, both in the box's space (relative to the box, which itself
        // drifts with the wind), so the gas next to it is pushed along with it.
        public struct Shape
        {
            public Matrix4x4 toLocal;
            public Vector4 halfKind;   // xyz = half sizes, w = kind
            public Vector4 vel;
            public Vector4 omega;
            public Vector4 center;
            public const int Stride = 32 * 4;

            public static Shape Make(Matrix4x4 toLocal, Vector3 half, int kind, Vector3 vel, Vector3 omega, Vector3 center)
            {
                return new Shape { toLocal = toLocal, halfKind = new Vector4(half.x, half.y, half.z, kind), vel = vel, omega = omega, center = center };
            }

            // A convex solid (kind 3): `half` is the half size of its box, and its `planeCount` planes start at `firstPlane` of the list given
            // to SetObstacles (a plane is the outward normal and an offset: inside when dot(normal, local) + offset <= 0).
            public static Shape Convex(Matrix4x4 toLocal, Vector3 half, int firstPlane, int planeCount, Vector3 vel, Vector3 omega, Vector3 center)
            {
                return new Shape { toLocal = toLocal, halfKind = new Vector4(half.x, half.y, half.z, 3),
                                   vel = vel, omega = new Vector4(omega.x, omega.y, omega.z, planeCount), center = new Vector4(center.x, center.y, center.z, firstPlane) };
            }
        }

        public struct Settings
        {
            public Vector3 buoyDir;        // "up" in the box's axes
            public float buoyancy;         // m/s^2 of lift per unit of temperature
            public float weight;           // m/s^2 of sinking per unit of density
            public float vorticity;        // vorticity confinement
            public float cool;             // per second: the temperature below 0.35 (the residual heat) decays as exp(-cool * t)
            public float coolHot;          // per second: the temperature above 0.35 (the flame) decays as exp(-coolHot * t)
            public float turbulence;       // m/s^2 of weak, endless swirling everywhere in the box (0 = still air)
            public float dustWeight;       // m/s^2 of sinking per unit of dust (0 = dust floats like the smoke)
            public float detail;           // m/s^2 of small eddies inside the smoke (0 = none)
            public float dissipate;        // per second: the smoke thins as exp(-dissipate * t)
            public float dissipateDust;    // per second: the same for the dust
            public float drag;             // per second
            public float edgeMeters;       // thickness of the absorbing edge
            public int cycles;             // multigrid cycles per step
            public float windFollow;       // per second: how fast the gas is pulled toward the ambient wind (0 = no wind)
            public static Settings Default
            {
                get
                {
                    return new Settings { buoyDir = Vector3.up, buoyancy = 0.6f, weight = 0f, vorticity = 1.2f, cool = 1.7f, coolHot = 1.7f, turbulence = 0f,
                                          dissipate = 0.04f, dissipateDust = 0.04f, drag = 0.15f, edgeMeters = 1.0f, cycles = 2, windFollow = 1.2f };
                }
            }
        }

        private sealed class Level
        {
            public Vector3Int dims;
            public float h;
            public RenderTexture p, rhs, res, solid;
        }

        public readonly Vector3Int Dims;
        private float cellSize;
        // metres; it may be changed between steps (a bigger box for a bigger gun) without reallocating. The pressure solver's coarse levels
        // take their size from it, so they follow it.
        public float Cell
        {
            get { return cellSize; }
            set
            {
                cellSize = value;
                float h = value;
                foreach (Level level in levels) { level.h = h; h *= 2f; }
            }
        }
        public Settings Params = Settings.Default;
        // For the half-precision experiment: 16-bit floats for the speed, smoke, heat and their helper textures / the texture coordinates of
        // the detail noise / the pressure of the multigrid. Read when a simulation is built.
        public static bool HalfFields, HalfPhi, HalfPressure;
        public readonly bool IsHalf;          // built with 16-bit fields (a simulation of one kind is never reused as the other)
        public int CoarseSweeps = 10;      // red-black sweeps on the smallest multigrid level

        private readonly ComputeShader cs;
        private readonly List<Level> levels = new List<Level>();
        private RenderTexture velA, velB, scalA, scalB, hat, tilde, vort, phiA, phiB, phiTmp, solidVel, solidPrev;
        public static int DisplacePasses = 5;        // how far (in cells) the smoke of a covered cell can be handed on in one frame
        public static bool DisplaceSmoke = true;     // the smoke a solid moves into is pushed aside, not deleted
        public static int MaskMode = 0;     // 1 = leave solid cells out of the advection read, 2 = and out of the limiter (experiments)
        private ComputeBuffer blobBuffer, shapeBuffer, planeBuffer;
        private int planeCapacity;
        private Texture3D windTexture;      // 4 x 4 x 4 ambient velocities over the box
        private readonly Color[] windColors = new Color[64];
        private bool windSet;
        private int shapeCapacity;
        private bool hadShapes;
        private int blobCapacity;
        private int seed = 1;
        private bool disposed;

        private readonly int kBuoyancy, kCurl, kVortForce, kDivergence, kSmooth, kResidual, kRestrict, kProlong, kSubGrad, kAdv, kMac, kDamp, kBlobs, kClear, kClearP, kInitPhi, kObstacles, kMaskRestrict, kWind, kDisplace, kShift, kShiftSolid, kCapPhi, kCapture, kCut, kOccupancy, kBfeccPre, kTestInit;

        // The texture to draw: R = density, G = temperature (0..1 above the air).
        public RenderTexture Scalars { get { return scalA; } }
        public RenderTexture Velocity { get { return velA; } }
        public RenderTexture Solid { get { return levels[0].solid; } }      // 1 where a solid fills the cell (it changes each time obstacles are set)
        public float Time { get; private set; }

        // Texture coordinates (metres from the box corner) carried by the flow, in two sets that restart in turn: the renderer carves
        // its detail noise with them. PhiWeights = how much each set counts now (they always add up to 1).
        public RenderTexture PhiA { get { return phiA; } }
        public RenderTexture PhiB { get { return phiB; } }
        public float PhiCycle = 0.6f;
        public Vector2 PhiWeights
        {
            get
            {
                float a = (Time / PhiCycle) % 1f, b = (Time / PhiCycle + 0.5f) % 1f;
                return new Vector2(1f - Mathf.Abs(2f * a - 1f), 1f - Mathf.Abs(2f * b - 1f));
            }
        }

        public FluidSim(ComputeShader shader, int nx, int ny, int nz, float cell)
        {
            cs = shader;
            Dims = new Vector3Int(nx, ny, nz);
            cellSize = cell;
            kBuoyancy = cs.FindKernel("Buoyancy"); kCurl = cs.FindKernel("Curl"); kVortForce = cs.FindKernel("VortForce");
            kDivergence = cs.FindKernel("Divergence"); kSmooth = cs.FindKernel("Smooth"); kResidual = cs.FindKernel("Residual");
            kRestrict = cs.FindKernel("Restrict"); kProlong = cs.FindKernel("ProlongAdd"); kSubGrad = cs.FindKernel("SubGrad");
            kAdv = cs.FindKernel("AdvBack"); kMac = cs.FindKernel("MacLimit"); kDamp = cs.FindKernel("Damp");
            kBlobs = cs.FindKernel("AddBlobs"); kClear = cs.FindKernel("ClearAll"); kClearP = cs.FindKernel("ClearP"); kInitPhi = cs.FindKernel("InitPhi"); kObstacles = cs.FindKernel("Obstacles"); kMaskRestrict = cs.FindKernel("MaskRestrict"); kDisplace = cs.FindKernel("Displace"); kWind = cs.FindKernel("ApplyWind");
            kShift = cs.FindKernel("Shift"); kShiftSolid = cs.FindKernel("ShiftSolid"); kCapPhi = cs.FindKernel("CapturePhi"); kCapture = cs.FindKernel("Capture"); kCut = cs.FindKernel("Cut"); kOccupancy = cs.FindKernel("Occupancy"); kBfeccPre = cs.FindKernel("BfeccPre"); kTestInit = cs.FindKernel("TestInit");

            IsHalf = HalfFields;
            velA = Make(nx, ny, nz, HalfFields ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat); velB = Make(nx, ny, nz, HalfFields ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat);
            RenderTextureFormat fieldFormat = HalfFields ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat;
            RenderTextureFormat phiFormat = HalfPhi ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat;
            scalA = Make(nx, ny, nz, fieldFormat); scalB = Make(nx, ny, nz, fieldFormat);
            hat = Make(nx, ny, nz, fieldFormat); tilde = Make(nx, ny, nz, fieldFormat);
            vort = Make(nx, ny, nz, fieldFormat);
            phiA = Make(nx, ny, nz, phiFormat); phiB = Make(nx, ny, nz, phiFormat);
            phiTmp = Make(nx, ny, nz, phiFormat);
            solidVel = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat);
            solidPrev = Make(nx, ny, nz, RenderTextureFormat.RFloat);

            // Multigrid levels: halve every dimension while they stay even and at least 8 cells.
            Vector3Int d = Dims;
            float h = cell;
            while (true)
            {
                Level L = new Level
                {
                    dims = d, h = h,
                    p = Make(d.x, d.y, d.z, HalfPressure ? RenderTextureFormat.RHalf : RenderTextureFormat.RFloat),
                    rhs = Make(d.x, d.y, d.z, HalfPressure ? RenderTextureFormat.RHalf : RenderTextureFormat.RFloat),
                    res = Make(d.x, d.y, d.z, HalfPressure ? RenderTextureFormat.RHalf : RenderTextureFormat.RFloat),
                    solid = Make(d.x, d.y, d.z, RenderTextureFormat.RFloat),
                };
                levels.Add(L);
                if ((d.x & 1) != 0 || (d.y & 1) != 0 || (d.z & 1) != 0 || Mathf.Min(d.x, Mathf.Min(d.y, d.z)) < 16) break;
                d = new Vector3Int(d.x / 2, d.y / 2, d.z / 2);
                h *= 2f;
            }
            Clear();
        }

        private static RenderTexture Make(int x, int y, int z, RenderTextureFormat format)
        {
            RenderTexture rt = new RenderTexture(x, y, 0, format)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = z,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave,    // survives scene changes in the editor
            };
            rt.Create();
            return rt;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (RenderTexture rt in new[] { velA, velB, scalA, scalB, hat, tilde, vort, phiA, phiB, phiTmp, solidVel, solidPrev, occupancy })
                if (rt != null) rt.Release();
            foreach (Level L in levels) { L.p.Release(); L.rhs.Release(); L.res.Release(); L.solid.Release(); }
            if (shapeBuffer != null) shapeBuffer.Release();
            if (planeBuffer != null) planeBuffer.Release();
            if (windTexture != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(windTexture); else UnityEngine.Object.DestroyImmediate(windTexture); }
            if (blobBuffer != null) blobBuffer.Release();
        }

        private void Dispatch(int kernel, Vector3Int dims)
        {
            cs.SetInts("Dim", dims.x, dims.y, dims.z);
            cs.Dispatch(kernel, (dims.x + 7) / 8, (dims.y + 7) / 8, (dims.z + 7) / 8);
        }

        private static void Swap(ref RenderTexture a, ref RenderTexture b) { RenderTexture t = a; a = b; b = t; }

        public void Clear()
        {
            cs.SetFloat("Hh", Cell);
            cs.SetTexture(kClear, "VelOut", velA);
            cs.SetTexture(kClear, "ScalOut", scalA);
            cs.SetTexture(kClear, "Vort", vort);
            Dispatch(kClear, Dims);
            cs.SetTexture(kClear, "VelOut", velB);
            cs.SetTexture(kClear, "ScalOut", scalB);
            Dispatch(kClear, Dims);
            foreach (Level L in levels) ClearPressure(L);
            ResetPhi(phiA); ResetPhi(phiB);
            hadShapes = true;
            SetObstacles(null);
            Time = 0f;
        }

        // The solid things in the box (vehicle parts, the ground). Call before stepping; null or empty clears them.
        public bool HasSolids { get { return hadShapes; } }

        // The ground as a height map over the box's floor (n x n samples, x fastest; the height in the box's space, below 0 where there is none).
        // It is used by the next SetObstacles. SetGround(null, ...) goes back to having no height map.
        private Texture2D groundTexture, noGround;
        private float groundSizeX, groundSizeZ; private Vector3 groundVelocity; private bool groundSet;
        public void SetGround(float[] heights, int n, float sizeX, float sizeZ, Vector3 velocity)
        {
            if (heights == null) { groundSet = false; return; }
            if (groundTexture == null || groundTexture.width != n)
            {
                if (groundTexture != null) UnityEngine.Object.Destroy(groundTexture);
                groundTexture = new Texture2D(n, n, TextureFormat.RFloat, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            }
            groundTexture.SetPixelData(heights, 0);
            groundTexture.Apply(false);
            groundSizeX = sizeX; groundSizeZ = sizeZ; groundVelocity = velocity; groundSet = true;
        }

        public void SetObstacles(IList<Shape> shapes, IList<Vector4> planes = null)
        {
            int count = shapes == null ? 0 : shapes.Count;
            if (count == 0 && !hadShapes && !groundSet) return;
            hadShapes = count > 0 || groundSet;
            if (count > 0)
            {
                if (shapeBuffer == null || shapeCapacity < count)
                {
                    if (shapeBuffer != null) shapeBuffer.Release();
                    shapeCapacity = Mathf.Max(16, count);
                    shapeBuffer = new ComputeBuffer(shapeCapacity, Shape.Stride);
                }
                Shape[] array = new Shape[count];
                shapes.CopyTo(array, 0);
                shapeBuffer.SetData(array);
                cs.SetBuffer(kObstacles, "Shapes", shapeBuffer);
            }
            else
            {
                if (shapeBuffer == null) { shapeCapacity = 16; shapeBuffer = new ComputeBuffer(shapeCapacity, Shape.Stride); }
                cs.SetBuffer(kObstacles, "Shapes", shapeBuffer);
            }
            int planeCount = planes == null ? 0 : planes.Count;
            if (planeBuffer == null || planeCapacity < planeCount)
            {
                if (planeBuffer != null) planeBuffer.Release();
                planeCapacity = Mathf.Max(64, planeCount);
                planeBuffer = new ComputeBuffer(planeCapacity, 16);
            }
            if (planeCount > 0)
            {
                Vector4[] planeArray = new Vector4[planeCount];
                planes.CopyTo(planeArray, 0);
                planeBuffer.SetData(planeArray);
            }
            cs.SetBuffer(kObstacles, "Planes", planeBuffer);
            cs.SetFloat("Hh", Cell);
            cs.SetInt("ShapeCount", count);
            if (!groundSet && noGround == null)
                noGround = new Texture2D(1, 1, TextureFormat.RFloat, false) { hideFlags = HideFlags.HideAndDontSave };
            cs.SetTexture(kObstacles, "GroundHeight", groundSet ? groundTexture : noGround);
            cs.SetVector("HeightInfo", new Vector4(groundSet ? 1f : 0f, groundSizeX, groundSizeZ, 0f));
            cs.SetVector("HeightVel", groundVelocity);
            // The map of last time is kept by swapping the two textures (the new map is written over the older one in full).
            if (DisplaceSmoke) { RenderTexture older = levels[0].solid; levels[0].solid = solidPrev; solidPrev = older; }
            cs.SetTexture(kObstacles, "SolidOut", levels[0].solid);
            cs.SetTexture(kObstacles, "SolidVelOut", solidVel);
            Dispatch(kObstacles, Dims);
            if (DisplaceSmoke && count > 0)
            {
                cs.SetTexture(kDisplace, "Solid", levels[0].solid); cs.SetTexture(kDisplace, "SolidPrev", solidPrev); cs.SetTexture(kDisplace, "SolidVel", solidVel);
                cs.SetTexture(kDisplace, "ScalIn", scalA); cs.SetTexture(kDisplace, "ScalOut", scalB);
                for (int pass = 0; pass < DisplacePasses; pass++)
                {
                    cs.SetFloat("DisplaceFinal", pass == DisplacePasses - 1 ? 1f : 0f);
                    cs.SetTexture(kDisplace, "ScalIn", scalA); cs.SetTexture(kDisplace, "ScalOut", scalB);
                    Dispatch(kDisplace, Dims);
                    Swap(ref scalA, ref scalB);
                }
            }
            for (int i = 1; i < levels.Count; i++)
            {
                cs.SetTexture(kMaskRestrict, "SolidFine", levels[i - 1].solid);
                cs.SetTexture(kMaskRestrict, "SolidOut", levels[i].solid);
                Dispatch(kMaskRestrict, levels[i].dims);
            }
        }

        // ---- a box that follows something, and boxes that hand smoke to each other ---------------------------------------------
        // The window of this simulation moves by n cells (in the box's own axes): every field keeps its place in the world, the cells that
        // come in are empty, what leaves is lost. Whole cells only, so nothing is resampled and nothing blurs.
        // For drawing: per block of 8 x 8 x 8 cells, the most smoke in it or next to it (x). Call after the step; the drawing skips empty blocks.
        private RenderTexture occupancy;
        public RenderTexture Occupancy { get { return occupancy; } }
        public Vector3Int OccupancyDims { get { return new Vector3Int((Dims.x + 7) / 8, (Dims.y + 7) / 8, (Dims.z + 7) / 8); } }

        public void UpdateOccupancy()
        {
            Vector3Int o = OccupancyDims;
            if (occupancy == null) occupancy = Make(o.x, o.y, o.z, RenderTextureFormat.ARGBHalf);
            cs.SetInts("OccDim", o.x, o.y, o.z);
            cs.SetInts("Dim", Dims.x, Dims.y, Dims.z);
            cs.SetTexture(kOccupancy, "Src", scalA);
            cs.SetTexture(kOccupancy, "Dst", occupancy);
            cs.Dispatch(kOccupancy, (o.x + 3) / 4, (o.y + 3) / 4, (o.z + 3) / 4);
        }

        public void ShiftBy(Vector3Int n)
        {
            if (n == Vector3Int.zero) return;
            cs.SetFloat("Hh", Cell);
            cs.SetInts("ShiftOff", n.x, n.y, n.z);
            ShiftField(ref velA, ref velB, false);
            ShiftField(ref scalA, ref scalB, false);
            ShiftField(ref phiA, ref phiTmp, true);
            ShiftField(ref phiB, ref phiTmp, true);
            // Last step's map of solids (the next SetObstacles compares the new one with it) moves with the window too.
            cs.SetInts("ShiftOff", n.x, n.y, n.z);
            cs.SetTexture(kShiftSolid, "SolidFine", levels[0].solid);
            cs.SetTexture(kShiftSolid, "SolidOut", solidPrev);
            Dispatch(kShiftSolid, Dims);
            RenderTexture moved = solidPrev; solidPrev = levels[0].solid; levels[0].solid = moved;
        }

        private void ShiftField(ref RenderTexture field, ref RenderTexture scratch, bool phi)
        {
            cs.SetFloat("ShiftPhi", phi ? 1f : 0f);
            cs.SetTexture(kShift, "Src", field);
            cs.SetTexture(kShift, "Dst", scratch);
            Dispatch(kShift, Dims);
            Swap(ref field, ref scratch);
        }

        // This box takes the smoke that another box (`src`) has inside it, and the other box loses it. The matrices take a point of this box's
        // space into the other's and back; `velRot` turns velocity vectors from the other's axes into this one's. Smoke is only taken where it
        // is at least `margin` metres from this box's faces (near them the edge damping would eat it).
        public void CaptureFrom(FluidSim src, Matrix4x4 destToSrc, Matrix4x4 srcToDest, Matrix4x4 velRot, float margin, Vector4? plane = null)
        {
            cs.SetVector("CapPlane", plane.HasValue ? plane.Value : new Vector4(0f, 0f, 0f, 1f));
            Vector3 destExtent = new Vector3(Dims.x, Dims.y, Dims.z) * Cell;
            Vector3 srcExtent = new Vector3(src.Dims.x, src.Dims.y, src.Dims.z) * src.Cell;
            cs.SetFloat("Hh", Cell);
            cs.SetMatrix("CapDestToSrc", destToSrc);
            cs.SetMatrix("CapSrcToDest", srcToDest);
            cs.SetMatrix("CapVelRot", velRot);
            cs.SetVector("CapSrcExtent", srcExtent);
            cs.SetVector("CapDestExtent", destExtent);
            cs.SetFloat("CapMargin", margin);

            // The texture coordinates of the detail noise, both sets (they read this box's smoke as it was before the capture).
            cs.SetTexture(kCapPhi, "Solid", levels[0].solid);
            cs.SetTexture(kCapPhi, "ScalIn", scalA);
            cs.SetTexture(kCapPhi, "CapScal", src.scalA);
            cs.SetTexture(kCapPhi, "CapPhi", src.phiA); cs.SetTexture(kCapPhi, "CapPhiOwn", phiA); cs.SetTexture(kCapPhi, "CapPhiOut", phiTmp);
            Dispatch(kCapPhi, Dims);
            Swap(ref phiA, ref phiTmp);
            cs.SetTexture(kCapPhi, "CapPhi", src.phiB); cs.SetTexture(kCapPhi, "CapPhiOwn", phiB); cs.SetTexture(kCapPhi, "CapPhiOut", phiTmp);
            Dispatch(kCapPhi, Dims);
            Swap(ref phiB, ref phiTmp);

            cs.SetTexture(kCapture, "Solid", levels[0].solid);
            cs.SetTexture(kCapture, "VelIn", velA); cs.SetTexture(kCapture, "ScalIn", scalA);
            cs.SetTexture(kCapture, "VelOut", velB); cs.SetTexture(kCapture, "ScalOut", scalB);
            cs.SetTexture(kCapture, "CapVel", src.velA); cs.SetTexture(kCapture, "CapScal", src.scalA);
            Dispatch(kCapture, Dims);
            Swap(ref velA, ref velB); Swap(ref scalA, ref scalB);

            // The other box lets go of what was taken.
            cs.SetFloat("Hh", src.Cell);
            cs.SetTexture(kCut, "ScalIn", src.scalA); cs.SetTexture(kCut, "ScalOut", src.scalB);
            Dispatch(kCut, src.Dims);
            Swap(ref src.scalA, ref src.scalB);
        }

        // The ambient wind over the box: 64 velocities (m/s) on a 4 x 4 x 4 lattice, x fastest, in the box's space. Call every frame (the wind
        // changes); the gas is pulled toward it by Params.windFollow.
        public void SetWind(Vector3[] ambient)
        {
            if (windTexture == null)
            {
                windTexture = new Texture3D(4, 4, 4, TextureFormat.RGBAFloat, false)
                {
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
                };
            }
            for (int i = 0; i < 64; i++) windColors[i] = new Color(ambient[i].x, ambient[i].y, ambient[i].z, 0f);
            windTexture.SetPixels(windColors);
            windTexture.Apply(false);
            windSet = true;
        }

        public void NoWind() { windSet = false; }

        // Diagnostics: the fastest air, the smoke left, the cells with a value that is not a number, how much of the smoke is ahead of
        // `splitX` metres, and where the fastest air is (its cell, and how many of its six neighbours are solid). The answer arrives a few
        // frames later, in `done`.
        public struct Inspection { public float fastest; public double mass, ahead; public int bad; public Vector3Int cell; public int solidAround; public Vector3 velocity; }

        public void Inspect(float splitX, Action<Inspection> done)
        {
            Vector3Int d = Dims;
            RenderTexture scalarTexture = scalA, velocityTexture = velA, solidTexture = levels[0].solid;
            Inspection result = new Inspection { fastest = -1f, mass = -1 };
            AsyncGPUReadback.Request(scalarTexture, 0, TextureFormat.RGBAFloat, rs =>
            {
                // The data of a readback is only valid inside its own callback, so the numbers are taken out of it before the next one.
                try
                {
                    if (rs.hasError) { done(result); return; }
                    result.mass = 0;
                    for (int z = 0; z < d.z; z++)
                    {
                        Unity.Collections.NativeArray<Vector4> ls = rs.GetData<Vector4>(z);
                        for (int i = 0; i < ls.Length; i++)
                        {
                            float density = ls[i].x;
                            if (float.IsNaN(density) || float.IsInfinity(density)) { result.bad++; continue; }
                            result.mass += density;
                            if (((i % d.x) + 0.5f) * Cell > splitX) result.ahead += density;
                        }
                    }
                }
                catch (Exception e) { UnityEngine.Debug.LogError("[FluidSim] inspecting the smoke failed: " + e.Message); done(result); return; }
                AsyncGPUReadback.Request(velocityTexture, 0, TextureFormat.RGBAFloat, rv =>
                {
                    try
                    {
                        if (rv.hasError) { done(result); return; }
                        result.fastest = 0f;
                        for (int z = 0; z < d.z; z++)
                        {
                            Unity.Collections.NativeArray<Vector4> lv = rv.GetData<Vector4>(z);
                            for (int i = 0; i < lv.Length; i++)
                            {
                                Vector3 v = new Vector3(lv[i].x, lv[i].y, lv[i].z);
                                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) { result.bad++; continue; }
                                float m = v.magnitude;
                                if (m > result.fastest) { result.fastest = m; result.velocity = v; result.cell = new Vector3Int(i % d.x, i / d.x, z); }
                            }
                        }
                    }
                    catch (Exception e) { UnityEngine.Debug.LogError("[FluidSim] inspecting the air failed: " + e.Message); done(result); return; }
                    AsyncGPUReadback.Request(solidTexture, 0, 0, d.x, 0, d.y, 0, d.z, rsol =>
                    {
                        try
                        {
                            if (!rsol.hasError)
                            {
                                Vector3Int c = result.cell;
                                int around = 0;
                                int[][] offsets = { new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 } };
                                foreach (int[] o in offsets)
                                {
                                    int x = c.x + o[0], y = c.y + o[1], z = c.z + o[2];
                                    if (x < 0 || y < 0 || z < 0 || x >= d.x || y >= d.y || z >= d.z) continue;
                                    if (rsol.GetData<float>(z)[y * d.x + x] > 0.5f) around++;
                                }
                                result.solidAround = around;
                            }
                        }
                        catch (Exception e) { UnityEngine.Debug.LogError("[FluidSim] inspecting the solids failed: " + e.Message); }
                        done(result);
                    });
                });
            });
        }

        // Diagnostics: how many cells of the box are solid right now (the answer arrives a few frames later, in `done`).
        public void CountSolid(Action<int> done)
        {
            Vector3Int d = Dims;
            AsyncGPUReadback.Request(levels[0].solid, 0, 0, d.x, 0, d.y, 0, d.z, request =>
            {
                if (request.hasError) { done(-1); return; }
                int count = 0;
                for (int z = 0; z < d.z; z++)
                {
                    Unity.Collections.NativeArray<float> layer = request.GetData<float>(z);
                    for (int i = 0; i < layer.Length; i++) if (layer[i] > 0.5f) count++;
                }
                done(count);
            });
        }

        private void ResetPhi(RenderTexture target)
        {
            cs.SetFloat("Hh", Cell);
            cs.SetTexture(kInitPhi, "Dst", target);
            Dispatch(kInitPhi, Dims);
        }

        // ---- sources -----------------------------------------------------------------------------------------------------------
        public void AddBlobs(IList<Blob> blobs)
        {
            if (blobs == null || blobs.Count == 0) return;
            if (blobBuffer == null || blobCapacity < blobs.Count)
            {
                if (blobBuffer != null) blobBuffer.Release();
                blobCapacity = Mathf.Max(16, blobs.Count);
                blobBuffer = new ComputeBuffer(blobCapacity, Blob.Stride);
            }
            Blob[] array = new Blob[blobs.Count];
            blobs.CopyTo(array, 0);
            blobBuffer.SetData(array);
            seed += 17;
            cs.SetFloat("Hh", Cell);
            cs.SetInt("BlobCount", array.Length);
            cs.SetInt("Seed", seed);
            cs.SetBuffer(kBlobs, "Blobs", blobBuffer);
            cs.SetTexture(kBlobs, "Solid", levels[0].solid);
            cs.SetTexture(kBlobs, "VelIn", velA); cs.SetTexture(kBlobs, "ScalIn", scalA);
            cs.SetTexture(kBlobs, "VelOut", velB); cs.SetTexture(kBlobs, "ScalOut", scalB);
            Dispatch(kBlobs, Dims);
            Swap(ref velA, ref velB); Swap(ref scalA, ref scalB);
        }

        // ---- one step ----------------------------------------------------------------------------------------------------------
        // For measuring only: the parts of a step that are left out (1 forces, 2 pressure projection, 4 only the V-cycles of it, 8 speed advection,
        // 16 smoke advection, 32 texture-coordinate advection, 64 damping). 0 = the whole step.
        public int SkipParts;

        // Advection of the speed and the smoke: false = MacCormack (the default), true = BFECC (three back-traces and a limiter instead of two).
        public bool UseBfecc;

        public void Step(float dt)
        {
            Settings s = Params;
            cs.SetFloat("Hh", Cell);
            cs.SetFloat("Dt", dt);

            // Forces.
            if ((SkipParts & 1) == 0)
            {
            cs.SetVector("BuoyDir", s.buoyDir);
            cs.SetFloat("Buoy", s.buoyancy);
            cs.SetFloat("Weight", s.weight);
            cs.SetFloat("DustWeight", s.dustWeight);
            cs.SetTexture(kBuoyancy, "VelIn", velA); cs.SetTexture(kBuoyancy, "ScalIn", scalA); cs.SetTexture(kBuoyancy, "VelOut", velB);
            Dispatch(kBuoyancy, Dims);
            Swap(ref velA, ref velB);

            if (windSet && s.windFollow > 0f)
            {
                cs.SetFloat("WindK", 1f - Mathf.Exp(-s.windFollow * dt));
                cs.SetTexture(kWind, "WindIn", windTexture);
                cs.SetTexture(kWind, "VelIn", velA); cs.SetTexture(kWind, "VelOut", velB);
                Dispatch(kWind, Dims);
                Swap(ref velA, ref velB);
            }

            cs.SetTexture(kCurl, "VelIn", velA); cs.SetTexture(kCurl, "Vort", vort);
            Dispatch(kCurl, Dims);
            cs.SetFloat("VortEps", s.vorticity);
            cs.SetTexture(kVortForce, "VelIn", velA); cs.SetTexture(kVortForce, "VortIn", vort); cs.SetTexture(kVortForce, "VelOut", velB);
            Dispatch(kVortForce, Dims);
            Swap(ref velA, ref velB);
            }

            if ((SkipParts & 2) == 0) Project(s.cycles);

            // Advection: velocity, then the smoke, both with the projected velocity.
            float dtdx = dt / Cell;
            cs.SetFloat("DtDx", dtdx);
            cs.SetTexture(kAdv, "Solid", levels[0].solid); cs.SetTexture(kMac, "Solid", levels[0].solid);
            cs.SetFloat("MaskAdv", 0f); cs.SetFloat("MaskLim", 0f);      // the speed: a solid holds the speed of the solid itself, which the gas next to it takes
            if ((SkipParts & 8) == 0) Advect(velA, velB);
            cs.SetFloat("MaskAdv", (MaskMode & 1) != 0 ? 1f : 0f); cs.SetFloat("MaskLim", (MaskMode & 2) != 0 ? 1f : 0f);      // the smoke and the texture coordinates: only the fluid cells count
            if ((SkipParts & 16) == 0) Advect(scalA, scalB);
            if ((SkipParts & 32) == 0) { AdvectPlain(ref phiA); AdvectPlain(ref phiB); }
            if ((SkipParts & 8) == 0) Swap(ref velA, ref velB);
            if ((SkipParts & 16) == 0) Swap(ref scalA, ref scalB);

            // Damping.
            cs.SetFloat("EdgeCells", s.edgeMeters / Cell);
            cs.SetFloat("KeepRate", Mathf.Min(1f, dt * 10f));
            cs.SetFloat("Cool", Mathf.Exp(-s.cool * dt));
            cs.SetFloat("CoolHot", Mathf.Exp(-s.coolHot * dt));
            cs.SetFloat("TurbAmp", s.turbulence);
            cs.SetFloat("TurbFreq", 0.35f);
            cs.SetFloat("TurbTime", Time);
            cs.SetFloat("DetailAmp", s.detail);
            cs.SetFloat("DetailFreq", 1.4f);
            cs.SetFloat("Diss", Mathf.Exp(-s.dissipate * dt));
            cs.SetFloat("DissDust", Mathf.Exp(-s.dissipateDust * dt));
            cs.SetFloat("Drag", Mathf.Exp(-s.drag * dt));
            cs.SetTexture(kDamp, "Solid", levels[0].solid); cs.SetTexture(kDamp, "SolidVel", solidVel);
            cs.SetTexture(kDamp, "VelIn", velA); cs.SetTexture(kDamp, "ScalIn", scalA);
            cs.SetTexture(kDamp, "VelOut", velB); cs.SetTexture(kDamp, "ScalOut", scalB);
            if ((SkipParts & 64) == 0) { Dispatch(kDamp, Dims); Swap(ref velA, ref velB); Swap(ref scalA, ref scalB); }

            float before = Time;
            Time += dt;
            if (Mathf.FloorToInt(Time / PhiCycle) != Mathf.FloorToInt(before / PhiCycle)) ResetPhi(phiA);
            if (Mathf.FloorToInt(Time / PhiCycle + 0.5f) != Mathf.FloorToInt(before / PhiCycle + 0.5f)) ResetPhi(phiB);
        }

        // MacCormack: trace back (hat), trace forward from that (tilde), then correct and limit. The result goes to `dst`; the field
        // and the velocity that moves it are read from `src` and velA.
        private void Advect(RenderTexture src, RenderTexture dst)
        {
            cs.SetTexture(kAdv, "VelIn", velA);
            cs.SetTexture(kAdv, "Src", src); cs.SetTexture(kAdv, "Dst", hat); cs.SetFloat("TraceSign", 1f);
            Dispatch(kAdv, Dims);
            cs.SetTexture(kAdv, "Src", hat); cs.SetTexture(kAdv, "Dst", tilde); cs.SetFloat("TraceSign", -1f);
            Dispatch(kAdv, Dims);
            if (UseBfecc)
            {
                // BFECC: the error (src - tilde) / 2 is added to the field before the last advection, whose result is then kept within the
                // range of the values it read (MacLimit with the original field as 'tilde' adds no correction of its own).
                cs.SetTexture(kBfeccPre, "Src", src); cs.SetTexture(kBfeccPre, "TildeIn", tilde); cs.SetTexture(kBfeccPre, "Dst", hat);
                Dispatch(kBfeccPre, Dims);
                cs.SetTexture(kAdv, "Src", hat); cs.SetTexture(kAdv, "Dst", tilde); cs.SetFloat("TraceSign", 1f);
                Dispatch(kAdv, Dims);
                cs.SetTexture(kMac, "VelIn", velA); cs.SetTexture(kMac, "Src", src);
                cs.SetTexture(kMac, "HatIn", tilde); cs.SetTexture(kMac, "TildeIn", src); cs.SetTexture(kMac, "Dst", dst);
                Dispatch(kMac, Dims);
                return;
            }
            cs.SetTexture(kMac, "VelIn", velA); cs.SetTexture(kMac, "Src", src);
            cs.SetTexture(kMac, "HatIn", hat); cs.SetTexture(kMac, "TildeIn", tilde); cs.SetTexture(kMac, "Dst", dst);
            Dispatch(kMac, Dims);
        }

        // For the study of the advection schemes: a slotted sphere turned about the z axis through the middle of the box by a solid-body speed of
        // `omega` radians per step (cells of 1, steps of 1), advected `steps` times by 0 = semi-Lagrangian, 1 = MacCormack, 2 = BFECC. The field is
        // left in Scalars (x); the speed field is not changed by the advection.
        public void AdvectTest(int scheme, int steps, float omega)
        {
            cs.SetFloat("Hh", 1f); cs.SetFloat("DtDx", 1f); cs.SetFloat("TestOmega", omega);
            cs.SetTexture(kTestInit, "VelOut", velA); cs.SetTexture(kTestInit, "ScalOut", scalA);
            Dispatch(kTestInit, Dims);
            cs.SetTexture(kAdv, "Solid", levels[0].solid); cs.SetTexture(kMac, "Solid", levels[0].solid);
            cs.SetFloat("MaskAdv", 0f); cs.SetFloat("MaskLim", 0f);
            bool saved = UseBfecc;
            for (int i = 0; i < steps; i++)
            {
                if (scheme == 0)
                {
                    cs.SetTexture(kAdv, "VelIn", velA);
                    cs.SetTexture(kAdv, "Src", scalA); cs.SetTexture(kAdv, "Dst", scalB); cs.SetFloat("TraceSign", 1f);
                    Dispatch(kAdv, Dims);
                }
                else
                {
                    UseBfecc = scheme == 2;
                    Advect(scalA, scalB);
                }
                Swap(ref scalA, ref scalB);
            }
            UseBfecc = saved;
        }

        // Plain semi-Lagrangian: the texture coordinates only need to follow the flow.
        private void AdvectPlain(ref RenderTexture field)
        {
            cs.SetTexture(kAdv, "VelIn", velA);
            cs.SetTexture(kAdv, "Src", field); cs.SetTexture(kAdv, "Dst", phiTmp); cs.SetFloat("TraceSign", 1f);
            Dispatch(kAdv, Dims);
            Swap(ref field, ref phiTmp);
        }

        // ---- pressure projection --------------------------------------------------------------------------------------------
        private void Project(int cycles)
        {
            Level L0 = levels[0];
            cs.SetTexture(kDivergence, "Solid", L0.solid); cs.SetTexture(kDivergence, "SolidVel", solidVel);
            cs.SetTexture(kDivergence, "VelIn", velA); cs.SetTexture(kDivergence, "RhsOut", L0.rhs);
            Dispatch(kDivergence, Dims);
            if ((SkipParts & 4) == 0) for (int i = 0; i < cycles; i++) VCycle(0);
            cs.SetTexture(kSubGrad, "Solid", L0.solid); cs.SetTexture(kSubGrad, "SolidVel", solidVel);
            cs.SetTexture(kSubGrad, "VelIn", velA); cs.SetTexture(kSubGrad, "PIn", L0.p); cs.SetTexture(kSubGrad, "VelOut", velB);
            Dispatch(kSubGrad, Dims);
            Swap(ref velA, ref velB);
        }

        private void Sweeps(Level L, int count)
        {
            cs.SetFloat("H2", L.h * L.h);
            cs.SetTexture(kSmooth, "P", L.p); cs.SetTexture(kSmooth, "Rhs", L.rhs); cs.SetTexture(kSmooth, "Solid", L.solid);
            for (int i = 0; i < count; i++)
                for (int color = 0; color < 2; color++)
                {
                    cs.SetInt("RbColor", color);
                    Dispatch(kSmooth, L.dims);
                }
        }

        private void VCycle(int index)
        {
            Level L = levels[index];
            if (index == levels.Count - 1) { Sweeps(L, CoarseSweeps); return; }
            Sweeps(L, 2);

            cs.SetFloat("H2", L.h * L.h);
            cs.SetTexture(kResidual, "P", L.p); cs.SetTexture(kResidual, "Rhs", L.rhs); cs.SetTexture(kResidual, "Res", L.res); cs.SetTexture(kResidual, "Solid", L.solid);
            Dispatch(kResidual, L.dims);

            Level C = levels[index + 1];
            cs.SetTexture(kRestrict, "ResIn", L.res); cs.SetTexture(kRestrict, "RhsOut", C.rhs);
            Dispatch(kRestrict, C.dims);
            ClearPressure(C);

            VCycle(index + 1);

            cs.SetTexture(kProlong, "P", L.p); cs.SetTexture(kProlong, "PIn", C.p);
            Dispatch(kProlong, L.dims);
            Sweeps(L, 2);
        }

        private void ClearPressure(Level L)
        {
            cs.SetTexture(kClearP, "P", L.p);
            Dispatch(kClearP, L.dims);
        }
    }
}
