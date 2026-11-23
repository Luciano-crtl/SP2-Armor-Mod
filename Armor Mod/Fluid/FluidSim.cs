// RELEASE-STRIP-BEGIN
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
            public const int Stride = 15 * 4;
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
        }

        public struct Settings
        {
            public Vector3 buoyDir;        // "up" in the box's axes
            public float buoyancy;         // m/s^2 of lift per unit of temperature
            public float weight;           // m/s^2 of sinking per unit of density
            public float vorticity;        // vorticity confinement
            public float cool;             // per second: the excess temperature decays as exp(-cool * t)
            public float dissipate;        // per second: the density thins as exp(-dissipate * t)
            public float drag;             // per second
            public float edgeMeters;       // thickness of the absorbing edge
            public int cycles;             // multigrid cycles per step
            public float windFollow;       // per second: how fast the gas is pulled toward the ambient wind (0 = no wind)
            public static Settings Default
            {
                get
                {
                    return new Settings { buoyDir = Vector3.up, buoyancy = 0.6f, weight = 0f, vorticity = 1.2f, cool = 1.7f,
                                          dissipate = 0.04f, drag = 0.15f, edgeMeters = 1.0f, cycles = 2, windFollow = 1.2f };
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
        public float Cell;        // metres; it may be changed between steps (a bigger box for a bigger gun) without reallocating
        public Settings Params = Settings.Default;
        public int CoarseSweeps = 10;      // red-black sweeps on the smallest multigrid level

        private readonly ComputeShader cs;
        private readonly List<Level> levels = new List<Level>();
        private RenderTexture velA, velB, scalA, scalB, hat, tilde, vort, phiA, phiB, phiTmp, solidVel;
        private ComputeBuffer blobBuffer, shapeBuffer;
        private Texture3D windTexture;      // 4 x 4 x 4 ambient velocities over the box
        private readonly Color[] windColors = new Color[64];
        private bool windSet;
        private int shapeCapacity;
        private bool hadShapes;
        private int blobCapacity;
        private int seed = 1;
        private bool disposed;

        private readonly int kBuoyancy, kCurl, kVortForce, kDivergence, kSmooth, kResidual, kRestrict, kProlong, kSubGrad, kAdv, kMac, kDamp, kBlobs, kClear, kClearP, kInitPhi, kObstacles, kMaskRestrict, kWind;

        // The texture to draw: R = density, G = temperature (0..1 above the air).
        public RenderTexture Scalars { get { return scalA; } }
        public RenderTexture Velocity { get { return velA; } }
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
            Cell = cell;
            kBuoyancy = cs.FindKernel("Buoyancy"); kCurl = cs.FindKernel("Curl"); kVortForce = cs.FindKernel("VortForce");
            kDivergence = cs.FindKernel("Divergence"); kSmooth = cs.FindKernel("Smooth"); kResidual = cs.FindKernel("Residual");
            kRestrict = cs.FindKernel("Restrict"); kProlong = cs.FindKernel("ProlongAdd"); kSubGrad = cs.FindKernel("SubGrad");
            kAdv = cs.FindKernel("AdvBack"); kMac = cs.FindKernel("MacLimit"); kDamp = cs.FindKernel("Damp");
            kBlobs = cs.FindKernel("AddBlobs"); kClear = cs.FindKernel("ClearAll"); kClearP = cs.FindKernel("ClearP"); kInitPhi = cs.FindKernel("InitPhi"); kObstacles = cs.FindKernel("Obstacles"); kMaskRestrict = cs.FindKernel("MaskRestrict"); kWind = cs.FindKernel("ApplyWind");

            velA = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat); velB = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat);
            scalA = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat); scalB = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat);
            hat = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat); tilde = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat);
            vort = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat);
            phiA = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat); phiB = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat);
            phiTmp = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat);
            solidVel = Make(nx, ny, nz, RenderTextureFormat.ARGBFloat);

            // Multigrid levels: halve every dimension while they stay even and at least 8 cells.
            Vector3Int d = Dims;
            float h = cell;
            while (true)
            {
                Level L = new Level
                {
                    dims = d, h = h,
                    p = Make(d.x, d.y, d.z, RenderTextureFormat.RFloat),
                    rhs = Make(d.x, d.y, d.z, RenderTextureFormat.RFloat),
                    res = Make(d.x, d.y, d.z, RenderTextureFormat.RFloat),
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
            foreach (RenderTexture rt in new[] { velA, velB, scalA, scalB, hat, tilde, vort, phiA, phiB, phiTmp, solidVel })
                if (rt != null) rt.Release();
            foreach (Level L in levels) { L.p.Release(); L.rhs.Release(); L.res.Release(); L.solid.Release(); }
            if (shapeBuffer != null) shapeBuffer.Release();
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
        public void SetObstacles(IList<Shape> shapes)
        {
            int count = shapes == null ? 0 : shapes.Count;
            if (count == 0 && !hadShapes) return;
            hadShapes = count > 0;
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
            cs.SetFloat("Hh", Cell);
            cs.SetInt("ShapeCount", count);
            cs.SetTexture(kObstacles, "SolidOut", levels[0].solid);
            cs.SetTexture(kObstacles, "SolidVelOut", solidVel);
            Dispatch(kObstacles, Dims);
            for (int i = 1; i < levels.Count; i++)
            {
                cs.SetTexture(kMaskRestrict, "SolidFine", levels[i - 1].solid);
                cs.SetTexture(kMaskRestrict, "SolidOut", levels[i].solid);
                Dispatch(kMaskRestrict, levels[i].dims);
            }
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
        public void Step(float dt)
        {
            Settings s = Params;
            cs.SetFloat("Hh", Cell);
            cs.SetFloat("Dt", dt);

            // Forces.
            cs.SetVector("BuoyDir", s.buoyDir);
            cs.SetFloat("Buoy", s.buoyancy);
            cs.SetFloat("Weight", s.weight);
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

            Project(s.cycles);

            // Advection: velocity, then the smoke, both with the projected velocity.
            float dtdx = dt / Cell;
            cs.SetFloat("DtDx", dtdx);
            Advect(velA, velB);
            Advect(scalA, scalB);
            AdvectPlain(ref phiA);
            AdvectPlain(ref phiB);
            Swap(ref velA, ref velB);
            Swap(ref scalA, ref scalB);

            // Damping.
            cs.SetFloat("EdgeCells", s.edgeMeters / Cell);
            cs.SetFloat("KeepRate", Mathf.Min(1f, dt * 10f));
            cs.SetFloat("Cool", Mathf.Exp(-s.cool * dt));
            cs.SetFloat("Diss", Mathf.Exp(-s.dissipate * dt));
            cs.SetFloat("Drag", Mathf.Exp(-s.drag * dt));
            cs.SetTexture(kDamp, "Solid", levels[0].solid); cs.SetTexture(kDamp, "SolidVel", solidVel);
            cs.SetTexture(kDamp, "VelIn", velA); cs.SetTexture(kDamp, "ScalIn", scalA);
            cs.SetTexture(kDamp, "VelOut", velB); cs.SetTexture(kDamp, "ScalOut", scalB);
            Dispatch(kDamp, Dims);
            Swap(ref velA, ref velB); Swap(ref scalA, ref scalB);

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
            cs.SetTexture(kMac, "VelIn", velA); cs.SetTexture(kMac, "Src", src);
            cs.SetTexture(kMac, "HatIn", hat); cs.SetTexture(kMac, "TildeIn", tilde); cs.SetTexture(kMac, "Dst", dst);
            Dispatch(kMac, Dims);
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
            for (int i = 0; i < cycles; i++) VCycle(0);
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
// RELEASE-STRIP-END
