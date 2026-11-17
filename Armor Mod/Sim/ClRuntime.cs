using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ArmorFragments.Sim
{
    // OpenCL through P/Invoke, for running the impact simulation on the graphics card. No Unity in here (the lab app can use it too), and
    // nothing is assumed to exist: OpenCL.dll comes with the graphics driver (NVIDIA, AMD and Intel all ship it), and every call may fail.
    // The caller must be a thread of its own, never the game's main thread: a driver call can take a long time.
    internal static class ClNative
    {
        private const string Lib = "OpenCL.dll";

        [DllImport(Lib)] public static extern int clGetPlatformIDs(uint n, IntPtr[] platforms, out uint count);
        [DllImport(Lib)] public static extern int clGetPlatformInfo(IntPtr platform, uint param, UIntPtr size, byte[] value, out UIntPtr ret);
        [DllImport(Lib)] public static extern int clGetDeviceIDs(IntPtr platform, ulong type, uint n, IntPtr[] devices, out uint count);
        [DllImport(Lib)] public static extern int clGetDeviceInfo(IntPtr device, uint param, UIntPtr size, byte[] value, out UIntPtr ret);
        [DllImport(Lib)] public static extern IntPtr clCreateContext(IntPtr props, uint n, IntPtr[] devices, IntPtr notify, IntPtr user, out int err);
        [DllImport(Lib)] public static extern IntPtr clCreateCommandQueue(IntPtr ctx, IntPtr device, ulong props, out int err);
        [DllImport(Lib)] public static extern IntPtr clCreateProgramWithSource(IntPtr ctx, uint count, string[] strings, UIntPtr[] lengths, out int err);
        [DllImport(Lib)] public static extern int clBuildProgram(IntPtr program, uint n, IntPtr[] devices, string options, IntPtr notify, IntPtr user);
        [DllImport(Lib)] public static extern int clGetProgramBuildInfo(IntPtr program, IntPtr device, uint param, UIntPtr size, byte[] value, out UIntPtr ret);
        [DllImport(Lib)] public static extern IntPtr clCreateKernel(IntPtr program, string name, out int err);
        [DllImport(Lib)] public static extern IntPtr clCreateBuffer(IntPtr ctx, ulong flags, UIntPtr size, IntPtr host, out int err);
        [DllImport(Lib)] public static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, ref IntPtr value);
        [DllImport(Lib)] public static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, ref int value);
        [DllImport(Lib)] public static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, IntPtr nullValue);   // a __local argument: only its size
        [DllImport(Lib)] public static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, ref float value);
        [DllImport(Lib)] public static extern int clEnqueueNDRangeKernel(IntPtr queue, IntPtr kernel, uint dim, UIntPtr[] offset, UIntPtr[] global, UIntPtr[] local, uint nWait, IntPtr[] wait, IntPtr evt);
        [DllImport(Lib)] public static extern int clEnqueueWriteBuffer(IntPtr queue, IntPtr buffer, uint blocking, UIntPtr offset, UIntPtr size, float[] data, uint nWait, IntPtr[] wait, IntPtr evt);
        [DllImport(Lib)] public static extern int clEnqueueReadBuffer(IntPtr queue, IntPtr buffer, uint blocking, UIntPtr offset, UIntPtr size, float[] data, uint nWait, IntPtr[] wait, IntPtr evt);
        [DllImport(Lib)] public static extern int clEnqueueWriteBuffer(IntPtr queue, IntPtr buffer, uint blocking, UIntPtr offset, UIntPtr size, byte[] data, uint nWait, IntPtr[] wait, IntPtr evt);
        [DllImport(Lib)] public static extern int clEnqueueWriteBuffer(IntPtr queue, IntPtr buffer, uint blocking, UIntPtr offset, UIntPtr size, int[] data, uint nWait, IntPtr[] wait, IntPtr evt);
        [DllImport(Lib)] public static extern int clEnqueueReadBuffer(IntPtr queue, IntPtr buffer, uint blocking, UIntPtr offset, UIntPtr size, int[] data, uint nWait, IntPtr[] wait, IntPtr evt);
        [DllImport(Lib)] public static extern int clFinish(IntPtr queue);
        [DllImport(Lib)] public static extern int clReleaseMemObject(IntPtr mem);
        [DllImport(Lib)] public static extern int clReleaseKernel(IntPtr kernel);
        [DllImport(Lib)] public static extern int clReleaseProgram(IntPtr program);
        [DllImport(Lib)] public static extern int clReleaseCommandQueue(IntPtr queue);
        [DllImport(Lib)] public static extern int clReleaseContext(IntPtr ctx);

        public static string Text(byte[] bytes, UIntPtr size)
        {
            int n = (int)size;
            return n <= 1 ? "" : Encoding.UTF8.GetString(bytes, 0, Math.Min(n, bytes.Length) - 1);
        }

        public static string PlatformString(IntPtr platform, uint param)
        {
            byte[] buffer = new byte[512];
            UIntPtr size;
            clGetPlatformInfo(platform, param, (UIntPtr)buffer.Length, buffer, out size);
            return Text(buffer, size);
        }

        public static string DeviceString(IntPtr device, uint param)
        {
            byte[] buffer = new byte[512];
            UIntPtr size;
            clGetDeviceInfo(device, param, (UIntPtr)buffer.Length, buffer, out size);
            return Text(buffer, size);
        }

        public static long DeviceNumber(IntPtr device, uint param)
        {
            byte[] buffer = new byte[8];
            UIntPtr size;
            if (clGetDeviceInfo(device, param, (UIntPtr)8, buffer, out size) != 0) return 0;
            return (int)size == 4 ? BitConverter.ToInt32(buffer, 0) : BitConverter.ToInt64(buffer, 0);
        }
    }

    // What the probe found out about the graphics card.
    internal sealed class ClProbeResult
    {
        public bool Available;            // a GPU was found and a kernel could be compiled and run
        public string Error = "";         // why not, if not
        public string Platform = "", Device = "", Version = "", DeviceList = "";
        public int ComputeUnits, ClockMhz;
        public long MemoryMb;
        public bool Discrete;
        public double BuildMs;            // compiling the kernel from its text
        public double DispatchMicroseconds;
        public double ToyMs;              // the toy relaxation (see Probe)
        public int ToyParticles, ToySteps;

        public string Summary()
        {
            if (!Available) return "not available: " + Error;
            return Device + " (" + Platform + ", " + Version + "), " + ComputeUnits + " compute units at " + ClockMhz + " MHz, " + MemoryMb +
                   " MB" + (Discrete ? ", discrete" : ", shares memory with the CPU") + "; kernel built in " + BuildMs.ToString("F0") +
                   " ms; a dispatch costs " + DispatchMicroseconds.ToString("F1") + " us; toy relaxation of " + ToyParticles + " particles x " +
                   ToySteps + " steps in " + ToyMs.ToString("F0") + " ms";
        }
    }

    internal static class ClProbe
    {
        // A toy of the impact simulation's shape (a lattice of particles joined by 18 links, relaxed color by color): enough to tell
        // that kernels compile and run on this card, and roughly how fast.
        private const string Source = @"
__kernel void relax(__global float4* p, int n, int k, float h, float hz, int color)
{
    int gid = get_global_id(0);
    if (gid >= n * n * k) return;
    int i = gid % n, j = (gid / n) % n, l = gid / (n * n);
    if (((i & 1) | ((j & 1) << 1) | ((l & 1) << 2)) != color) return;
    if (i == 0 || j == 0 || l == 0 || i == n - 1 || j == n - 1 || l == k - 1) return;

    float4 me = p[gid];
    float3 delta = (float3)(0.0f, 0.0f, 0.0f);
    int links = 0;
    for (int dz = -1; dz <= 1; dz++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++)
    {
        int order = abs(dx) + abs(dy) + abs(dz);
        if (order == 0 || order > 2) continue;
        int nb = gid + dx + dy * n + dz * n * n;
        float3 d = p[nb].xyz - me.xyz;
        float len = length(d);
        float rest = sqrt((float)(dx * dx) * h * h + (float)(dy * dy) * h * h + (float)(dz * dz) * hz * hz);
        delta += d * ((len - rest) / fmax(len, 1e-6f));
        links++;
    }
    me.xyz += delta * (0.5f / links);
    p[gid] = me;
}

__kernel void nothing(__global float4* p) { }
";

        // Runs the whole probe. Never throws: a failure is the result's Error. Call it from a thread of its own.
        public static ClProbeResult Run()
        {
            ClProbeResult result = new ClProbeResult();
            IntPtr context = IntPtr.Zero, queue = IntPtr.Zero, program = IntPtr.Zero, relax = IntPtr.Zero, nothing = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero, tiny = IntPtr.Zero;
            try
            {
                uint platformCount;
                int status = ClNative.clGetPlatformIDs(0, null, out platformCount);
                if (status != 0 || platformCount == 0) { result.Error = "OpenCL reports no platform (status " + status + ")"; return result; }
                IntPtr[] platforms = new IntPtr[platformCount];
                ClNative.clGetPlatformIDs(platformCount, platforms, out platformCount);

                // The best GPU: a discrete one over one that shares the CPU's memory, then the most compute at the highest clock.
                IntPtr device = IntPtr.Zero;
                double best = -1.0;
                StringBuilder list = new StringBuilder();
                foreach (IntPtr platform in platforms)
                {
                    uint deviceCount;
                    if (ClNative.clGetDeviceIDs(platform, 0xFFFFFFFF, 0, null, out deviceCount) != 0 || deviceCount == 0) continue;
                    IntPtr[] devices = new IntPtr[deviceCount];
                    ClNative.clGetDeviceIDs(platform, 0xFFFFFFFF, deviceCount, devices, out deviceCount);
                    foreach (IntPtr candidate in devices)
                    {
                        long type = ClNative.DeviceNumber(candidate, 0x1000);
                        string name = ClNative.DeviceString(candidate, 0x102B);
                        if (list.Length > 0) list.Append("; ");
                        list.Append(name).Append(type == 4 ? " (GPU)" : type == 2 ? " (CPU)" : "");
                        if (type != 4) continue;

                        bool discrete = ClNative.DeviceNumber(candidate, 0x1035) == 0;   // CL_DEVICE_HOST_UNIFIED_MEMORY
                        double score = (discrete ? 1e9 : 0.0) + ClNative.DeviceNumber(candidate, 0x1002) * (double)ClNative.DeviceNumber(candidate, 0x100C);
                        if (score <= best) continue;
                        best = score;
                        device = candidate;
                        result.Platform = ClNative.PlatformString(platform, 0x0902);
                        result.Device = name;
                        result.Version = ClNative.DeviceString(candidate, 0x103D);
                        result.ComputeUnits = (int)ClNative.DeviceNumber(candidate, 0x1002);
                        result.ClockMhz = (int)ClNative.DeviceNumber(candidate, 0x100C);
                        result.MemoryMb = ClNative.DeviceNumber(candidate, 0x101F) / (1024 * 1024);
                        result.Discrete = discrete;
                    }
                }
                result.DeviceList = list.ToString();
                if (device == IntPtr.Zero) { result.Error = "no graphics card among the OpenCL devices (" + result.DeviceList + ")"; return result; }

                int err;
                context = ClNative.clCreateContext(IntPtr.Zero, 1, new[] { device }, IntPtr.Zero, IntPtr.Zero, out err);
                if (err != 0) { result.Error = "clCreateContext failed (" + err + ")"; return result; }
                queue = ClNative.clCreateCommandQueue(context, device, 0, out err);
                if (err != 0) { result.Error = "clCreateCommandQueue failed (" + err + ")"; return result; }

                Stopwatch build = Stopwatch.StartNew();
                program = ClNative.clCreateProgramWithSource(context, 1, new[] { Source }, null, out err);
                if (err != 0) { result.Error = "clCreateProgramWithSource failed (" + err + ")"; return result; }
                status = ClNative.clBuildProgram(program, 1, new[] { device }, "", IntPtr.Zero, IntPtr.Zero);
                result.BuildMs = build.Elapsed.TotalMilliseconds;
                if (status != 0)
                {
                    byte[] log = new byte[4096];
                    UIntPtr size;
                    ClNative.clGetProgramBuildInfo(program, device, 0x1183, (UIntPtr)log.Length, log, out size);
                    result.Error = "the kernel did not compile (" + status + "): " + ClNative.Text(log, size).Trim();
                    return result;
                }
                relax = ClNative.clCreateKernel(program, "relax", out err);
                if (err != 0) { result.Error = "clCreateKernel failed (" + err + ")"; return result; }
                nothing = ClNative.clCreateKernel(program, "nothing", out err);

                // What a dispatch costs (thousands of them make a simulation).
                tiny = ClNative.clCreateBuffer(context, 1, (UIntPtr)64, IntPtr.Zero, out err);
                ClNative.clSetKernelArg(nothing, 0, (UIntPtr)IntPtr.Size, ref tiny);
                UIntPtr[] one = { (UIntPtr)64 };
                ClNative.clEnqueueNDRangeKernel(queue, nothing, 1, null, one, null, 0, null, IntPtr.Zero);
                ClNative.clFinish(queue);
                Stopwatch dispatch = Stopwatch.StartNew();
                for (int i = 0; i < 1000; i++) ClNative.clEnqueueNDRangeKernel(queue, nothing, 1, null, one, null, 0, null, IntPtr.Zero);
                ClNative.clFinish(queue);
                result.DispatchMicroseconds = dispatch.Elapsed.TotalMilliseconds * 1000.0 / 1000.0;

                // The toy: 64 x 64 x 7 particles, 100 steps of 5 iterations over the 8 colors.
                int n = 64, k = 7, steps = 100;
                float h = 0.0035f, hz = 0.0035f;
                int count = n * n * k;
                float[] data = new float[count * 4];
                Random random = new Random(1);
                for (int l = 0; l < k; l++)
                    for (int j = 0; j < n; j++)
                        for (int i = 0; i < n; i++)
                        {
                            int at = ((l * n + j) * n + i) * 4;
                            data[at] = i * h + (float)(random.NextDouble() - 0.5) * h * 0.3f;
                            data[at + 1] = j * h + (float)(random.NextDouble() - 0.5) * h * 0.3f;
                            data[at + 2] = l * hz + (float)(random.NextDouble() - 0.5) * hz * 0.3f;
                        }
                buffer = ClNative.clCreateBuffer(context, 1, (UIntPtr)(data.Length * 4), IntPtr.Zero, out err);
                if (err != 0) { result.Error = "clCreateBuffer failed (" + err + ")"; return result; }
                if (ClNative.clEnqueueWriteBuffer(queue, buffer, 1, UIntPtr.Zero, (UIntPtr)(data.Length * 4), data, 0, null, IntPtr.Zero) != 0)
                {
                    result.Error = "clEnqueueWriteBuffer failed";
                    return result;
                }
                ClNative.clSetKernelArg(relax, 0, (UIntPtr)IntPtr.Size, ref buffer);
                ClNative.clSetKernelArg(relax, 1, (UIntPtr)4, ref n);
                ClNative.clSetKernelArg(relax, 2, (UIntPtr)4, ref k);
                ClNative.clSetKernelArg(relax, 3, (UIntPtr)4, ref h);
                ClNative.clSetKernelArg(relax, 4, (UIntPtr)4, ref hz);
                UIntPtr[] global = { (UIntPtr)(((count + 63) / 64) * 64) };
                UIntPtr[] local = { (UIntPtr)64 };

                Stopwatch toy = Stopwatch.StartNew();
                for (int step = 0; step < steps; step++)
                    for (int iteration = 0; iteration < 5; iteration++)
                        for (int color = 0; color < 8; color++)
                        {
                            ClNative.clSetKernelArg(relax, 5, (UIntPtr)4, ref color);
                            if (ClNative.clEnqueueNDRangeKernel(queue, relax, 1, null, global, local, 0, null, IntPtr.Zero) != 0)
                            {
                                result.Error = "a dispatch failed";
                                return result;
                            }
                        }
                ClNative.clFinish(queue);
                result.ToyMs = toy.Elapsed.TotalMilliseconds;
                result.ToyParticles = count;
                result.ToySteps = steps;

                float[] back = new float[data.Length];
                if (ClNative.clEnqueueReadBuffer(queue, buffer, 1, UIntPtr.Zero, (UIntPtr)(back.Length * 4), back, 0, null, IntPtr.Zero) != 0)
                {
                    result.Error = "reading the result back failed";
                    return result;
                }
                float sample = back[((k / 2 * n + n / 2) * n + n / 2) * 4 + 2];
                if (float.IsNaN(sample) || float.IsInfinity(sample)) { result.Error = "the toy gave a bad number"; return result; }

                result.Available = true;
                return result;
            }
            catch (DllNotFoundException)
            {
                result.Error = "OpenCL.dll is not installed (it comes with the graphics driver)";
                return result;
            }
            catch (EntryPointNotFoundException e)
            {
                result.Error = "OpenCL.dll is too old or incomplete: " + e.Message;
                return result;
            }
            catch (Exception e)
            {
                result.Error = e.GetType().Name + ": " + e.Message;
                return result;
            }
            finally
            {
                try
                {
                    if (buffer != IntPtr.Zero) ClNative.clReleaseMemObject(buffer);
                    if (tiny != IntPtr.Zero) ClNative.clReleaseMemObject(tiny);
                    if (relax != IntPtr.Zero) ClNative.clReleaseKernel(relax);
                    if (nothing != IntPtr.Zero) ClNative.clReleaseKernel(nothing);
                    if (program != IntPtr.Zero) ClNative.clReleaseProgram(program);
                    if (queue != IntPtr.Zero) ClNative.clReleaseCommandQueue(queue);
                    if (context != IntPtr.Zero) ClNative.clReleaseContext(context);
                }
                catch (Exception)
                {
                    // releasing is best effort
                }
            }
        }
    }
}
