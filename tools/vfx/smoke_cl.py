"""The smoke solver of smoke_sim.py rewritten for the GPU with OpenCL (pyopencl).

The same method (stable fluids: MacCormack advection, vorticity confinement, buoyancy, pressure projection) with one
difference: the pressure is solved with a geometric multigrid instead of an FFT, which maps well onto a GPU.

Buffers are flat float32 arrays indexed (z * ny + y) * nx + x, y is up, the floor is y = 0.
Run `python smoke_cl.py preview <folder> [scale]` for a few pictures.
"""
import sys
import time
import numpy as np
import pyopencl as cl

KERNELS = r"""
#define I(x,y,z) (((z) * ny + (y)) * nx + (x))

inline float rnd(int x, int y, int z, int seed)
{
    uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791) ^ (uint)(seed * 2654435761u);
    h ^= h >> 13; h *= 1274126177u; h ^= h >> 16; h *= 2246822519u; h ^= h >> 13;
    return (h & 0xFFFFFF) / (float)0x1000000;
}

inline float samp(__global const float* f, float x, float y, float z, int nx, int ny, int nz)
{
    x = clamp(x, 0.0f, nx - 1.0f); y = clamp(y, 0.0f, ny - 1.0f); z = clamp(z, 0.0f, nz - 1.0f);
    int x0 = (int)floor(x), y0 = (int)floor(y), z0 = (int)floor(z);
    int x1 = min(x0 + 1, nx - 1), y1 = min(y0 + 1, ny - 1), z1 = min(z0 + 1, nz - 1);
    float fx = x - x0, fy = y - y0, fz = z - z0;
    float a = mix(mix(f[I(x0,y0,z0)], f[I(x1,y0,z0)], fx), mix(f[I(x0,y1,z0)], f[I(x1,y1,z0)], fx), fy);
    float b = mix(mix(f[I(x0,y0,z1)], f[I(x1,y0,z1)], fx), mix(f[I(x0,y1,z1)], f[I(x1,y1,z1)], fx), fy);
    return mix(a, b, fz);
}

// Central differences inside, one-sided at the walls (the same as torch.gradient).
inline float gx(__global const float* f, int x, int y, int z, int nx, int ny, int nz, float h)
{
    if (x == 0) return (f[I(1,y,z)] - f[I(0,y,z)]) / h;
    if (x == nx - 1) return (f[I(nx-1,y,z)] - f[I(nx-2,y,z)]) / h;
    return (f[I(x+1,y,z)] - f[I(x-1,y,z)]) / (2.0f * h);
}
inline float gy(__global const float* f, int x, int y, int z, int nx, int ny, int nz, float h)
{
    if (y == 0) return (f[I(x,1,z)] - f[I(x,0,z)]) / h;
    if (y == ny - 1) return (f[I(x,ny-1,z)] - f[I(x,ny-2,z)]) / h;
    return (f[I(x,y+1,z)] - f[I(x,y-1,z)]) / (2.0f * h);
}
inline float gz(__global const float* f, int x, int y, int z, int nx, int ny, int nz, float h)
{
    if (z == 0) return (f[I(x,y,1)] - f[I(x,y,0)]) / h;
    if (z == nz - 1) return (f[I(x,y,nz-1)] - f[I(x,y,nz-2)]) / h;
    return (f[I(x,y,z+1)] - f[I(x,y,z-1)]) / (2.0f * h);
}

__kernel void buoyancy(__global float* uy, __global const float* temp, __global const float* rho,
                       int nx, int ny, int nz, float buoy, float weight, float dt)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int i = I(x,y,z);
    uy[i] += (buoy * temp[i] - weight * rho[i]) * dt;
}

__kernel void vorticity(__global const float* ux, __global const float* uy, __global const float* uz,
                        __global float* wx, __global float* wy, __global float* wz, __global float* wm,
                        int nx, int ny, int nz, float h)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int i = I(x,y,z);
    float ax = gy(uz,x,y,z,nx,ny,nz,h) - gz(uy,x,y,z,nx,ny,nz,h);
    float ay = gz(ux,x,y,z,nx,ny,nz,h) - gx(uz,x,y,z,nx,ny,nz,h);
    float az = gx(uy,x,y,z,nx,ny,nz,h) - gy(ux,x,y,z,nx,ny,nz,h);
    wx[i] = ax; wy[i] = ay; wz[i] = az;
    wm[i] = sqrt(ax*ax + ay*ay + az*az);
}

__kernel void vort_force(__global float* ux, __global float* uy, __global float* uz,
                         __global const float* wx, __global const float* wy, __global const float* wz, __global const float* wm,
                         int nx, int ny, int nz, float h, float k)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int i = I(x,y,z);
    float a = gx(wm,x,y,z,nx,ny,nz,h), b = gy(wm,x,y,z,nx,ny,nz,h), c = gz(wm,x,y,z,nx,ny,nz,h);
    float l = sqrt(a*a + b*b + c*c) + 1e-6f;
    a /= l; b /= l; c /= l;
    ux[i] += k * (b * wz[i] - c * wy[i]);
    uy[i] += k * (c * wx[i] - a * wz[i]);
    uz[i] += k * (a * wy[i] - b * wx[i]);
}

__kernel void divergence(__global const float* ux, __global const float* uy, __global const float* uz, __global float* out,
                         int nx, int ny, int nz, float h)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    out[I(x,y,z)] = gx(ux,x,y,z,nx,ny,nz,h) + gy(uy,x,y,z,nx,ny,nz,h) + gz(uz,x,y,z,nx,ny,nz,h);
}

__kernel void sub_gradient(__global float* ux, __global float* uy, __global float* uz, __global const float* p,
                           int nx, int ny, int nz, float h)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int i = I(x,y,z);
    ux[i] -= gx(p,x,y,z,nx,ny,nz,h);
    float v = uy[i] - gy(p,x,y,z,nx,ny,nz,h);
    uy[i] = (y == 0) ? fmax(v, 0.0f) : v;       // free-slip floor
    uz[i] -= gz(p,x,y,z,nx,ny,nz,h);
}

// Partial sums, one per work group of 256: the host adds up the few partial sums.
__kernel void sum_partial(__global const float* f, __global float* partial, int count)
{
    __local float scratch[256];
    int g = get_global_id(0), l = get_local_id(0);
    scratch[l] = (g < count) ? f[g] : 0.0f;
    barrier(CLK_LOCAL_MEM_FENCE);
    for (int s = 128; s > 0; s >>= 1) {
        if (l < s) scratch[l] += scratch[l + s];
        barrier(CLK_LOCAL_MEM_FENCE);
    }
    if (l == 0) partial[get_group_id(0)] = scratch[0];
}

__kernel void add_const(__global float* f, int nx, int ny, int nz, float c)
{
    f[I(get_global_id(0), get_global_id(1), get_global_id(2))] += c;
}

// ---- multigrid for  laplacian(p) = rhs  with Neumann walls (a missing neighbour just does not count) -----------------------
__kernel void gs_sweep(__global float* p, __global const float* rhs, int nx, int ny, int nz, float h2, int color)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    if (((x + y + z) & 1) != color) return;
    float s = 0.0f; int n = 0;
    if (x > 0)      { s += p[I(x-1,y,z)]; n++; }
    if (x < nx - 1) { s += p[I(x+1,y,z)]; n++; }
    if (y > 0)      { s += p[I(x,y-1,z)]; n++; }
    if (y < ny - 1) { s += p[I(x,y+1,z)]; n++; }
    if (z > 0)      { s += p[I(x,y,z-1)]; n++; }
    if (z < nz - 1) { s += p[I(x,y,z+1)]; n++; }
    p[I(x,y,z)] = (s - rhs[I(x,y,z)] * h2) / (float)n;
}

__kernel void residual(__global const float* p, __global const float* rhs, __global float* res, int nx, int ny, int nz, float invh2)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    float c = p[I(x,y,z)];
    float s = 0.0f;
    if (x > 0)      s += p[I(x-1,y,z)] - c;
    if (x < nx - 1) s += p[I(x+1,y,z)] - c;
    if (y > 0)      s += p[I(x,y-1,z)] - c;
    if (y < ny - 1) s += p[I(x,y+1,z)] - c;
    if (z > 0)      s += p[I(x,y,z-1)] - c;
    if (z < nz - 1) s += p[I(x,y,z+1)] - c;
    res[I(x,y,z)] = rhs[I(x,y,z)] - s * invh2;
}

// One coarse cell per work item (coarse dimensions cnx, cny, cnz); the fine grid is twice as large.
__kernel void restrict_res(__global const float* fine, __global float* coarse, int cnx, int cny, int cnz)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int fnx = cnx * 2, fny = cny * 2;
    float s = 0.0f;
    for (int dz = 0; dz < 2; dz++) for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
        s += fine[((2*z+dz) * fny + (2*y+dy)) * fnx + (2*x+dx)];
    coarse[(z * cny + y) * cnx + x] = s * 0.125f;
}

__kernel void prolong_add(__global const float* coarse, __global float* fine, int fnx, int fny, int fnz)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int cnx = fnx / 2, cny = fny / 2, cnz = fnz / 2;
    float cx = (x + 0.5f) * 0.5f - 0.5f, cy = (y + 0.5f) * 0.5f - 0.5f, cz = (z + 0.5f) * 0.5f - 0.5f;
    int nx = cnx, ny = cny, nz = cnz;
    fine[(z * fny + y) * fnx + x] += samp(coarse, cx, cy, cz, nx, ny, nz);
}

// ---- advection -----------------------------------------------------------------------------------------------------------
__kernel void advect(__global const float* src, __global float* dst,
                     __global const float* ux, __global const float* uy, __global const float* uz,
                     int nx, int ny, int nz, float dtdx)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int i = I(x,y,z);
    dst[i] = samp(src, x - dtdx * ux[i], y - dtdx * uy[i], z - dtdx * uz[i], nx, ny, nz);
}

__kernel void advect_fwd(__global const float* src, __global float* dst,
                         __global const float* ux, __global const float* uy, __global const float* uz,
                         int nx, int ny, int nz, float dtdx)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int i = I(x,y,z);
    dst[i] = samp(src, x + dtdx * ux[i], y + dtdx * uy[i], z + dtdx * uz[i], nx, ny, nz);
}

// MacCormack: hat + (src - tilde) / 2, kept inside the range of the eight values the back-trace read.
__kernel void mac_limit(__global const float* src, __global const float* hat, __global const float* tilde, __global float* dst,
                        __global const float* ux, __global const float* uy, __global const float* uz,
                        int nx, int ny, int nz, float dtdx)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int i = I(x,y,z);
    float px = clamp(x - dtdx * ux[i], 0.0f, nx - 1.0f), py = clamp(y - dtdx * uy[i], 0.0f, ny - 1.0f), pz = clamp(z - dtdx * uz[i], 0.0f, nz - 1.0f);
    int x0 = (int)floor(px), y0 = (int)floor(py), z0 = (int)floor(pz);
    int x1 = min(x0 + 1, nx - 1), y1 = min(y0 + 1, ny - 1), z1 = min(z0 + 1, nz - 1);
    float lo = src[I(x0,y0,z0)], hi = lo, v;
    v = src[I(x1,y0,z0)]; lo = fmin(lo, v); hi = fmax(hi, v);
    v = src[I(x0,y1,z0)]; lo = fmin(lo, v); hi = fmax(hi, v);
    v = src[I(x1,y1,z0)]; lo = fmin(lo, v); hi = fmax(hi, v);
    v = src[I(x0,y0,z1)]; lo = fmin(lo, v); hi = fmax(hi, v);
    v = src[I(x1,y0,z1)]; lo = fmin(lo, v); hi = fmax(hi, v);
    v = src[I(x0,y1,z1)]; lo = fmin(lo, v); hi = fmax(hi, v);
    v = src[I(x1,y1,z1)]; lo = fmin(lo, v); hi = fmax(hi, v);
    float out = hat[i] + 0.5f * (src[i] - tilde[i]);
    dst[i] = clamp(out, lo, hi);
}

__kernel void damp(__global float* rho, __global float* temp, __global float* ux, __global float* uy, __global float* uz,
                   __global const float* sponge, int nx, int ny, int nz, float keep_rate, float cool, float diss, float drag)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int i = I(x,y,z);
    float keep = 1.0f - (1.0f - sponge[i]) * keep_rate;
    rho[i] = fmax(rho[i], 0.0f) * keep * diss;
    temp[i] = fmax(temp[i], 0.0f) * keep * cool;
    float k = keep * drag;
    ux[i] *= k; uz[i] *= k;
    float v = uy[i] * k;
    uy[i] = (y == 0) ? fmax(v, 0.0f) : v;
}

// ---- sources ---------------------------------------------------------------------------------------------------------------
__kernel void add_blob(__global float* rho, __global float* temp, __global float* ux, __global float* uy, __global float* uz,
                       __global const float* sx, __global const float* sy, __global const float* sz,
                       int nx, int ny, int nz, float h,
                       float cx, float cy, float cz, float radius, float rho_v, float temp_v,
                       float vx, float vy, float vz, float radial, float noise, float falloff, int use_swirl, int seed, float squash, float vdir)
{
    int x = get_global_id(0), y = get_global_id(1), z = get_global_id(2);
    int i = I(x,y,z);
    float px = (x + 0.5f) * h - cx, py = (y + 0.5f) * h - cy, pz = (z + 0.5f) * h - cz;
    float pys = py / squash;                       // a squashed source (a disc on the ground) has squash < 1
    float r = sqrt(px*px + pys*pys + pz*pz);
    float w = pow(clamp(1.0f - r / radius, 0.0f, 1.0f), falloff);
    if (w <= 0.0f) return;
    w = clamp(w * (1.0f + noise * (rnd(x,y,z,seed) - 0.5f) * 2.0f), 0.0f, 1.0f);
    rho[i] = fmax(rho[i], rho_v * w);
    temp[i] = fmax(temp[i], temp_v * w);
    float dy = py * vdir;                          // vdir < 1 makes the push more horizontal
    float inv = 1.0f / (sqrt(px*px + dy*dy + pz*pz) + 1e-3f);
    ux[i] += w * (vx + radial * px * inv);
    uy[i] += w * (vy + radial * dy * inv);
    uz[i] += w * (vz + radial * pz * inv);
    if (use_swirl) { ux[i] += w * sx[i]; uy[i] += w * sy[i]; uz[i] += w * sz[i]; }
}
"""


def resize3d_linear(a, shape):
    """Trilinear resize of a 3D array to `shape` (corners map to corners)."""
    out = a
    for axis in range(3):
        n_in, n_out = out.shape[axis], shape[axis]
        pos = np.linspace(0, n_in - 1, n_out)
        i0 = np.minimum(np.floor(pos).astype(int), n_in - 2)
        f = (pos - i0).astype(np.float32)
        lo = np.take(out, i0, axis=axis)
        hi = np.take(out, i0 + 1, axis=axis)
        wshape = [1, 1, 1]; wshape[axis] = n_out
        out = lo + (hi - lo) * f.reshape(wshape)
    return out.astype(np.float32)


class ClSmoke:
    def __init__(self, nx=128, ny=96, nz=128, dx=0.125, seed=1, device_index=0):
        self.nx, self.ny, self.nz, self.dx = nx, ny, nz, dx
        self.rng = np.random.default_rng(seed)
        platform = cl.get_platforms()[0]
        device = platform.get_devices()[device_index]
        self.ctx = cl.Context([device])
        self.q = cl.CommandQueue(self.ctx)
        self.prg = cl.Program(self.ctx, KERNELS).build()
        self._kernels = {}
        self.device_name = device.name
        n = nx * ny * nz
        self.n = n
        mf = cl.mem_flags

        def buf(host=None):
            if host is None:
                b = cl.Buffer(self.ctx, mf.READ_WRITE, size=n * 4)
                cl.enqueue_fill_buffer(self.q, b, np.float32(0), 0, n * 4)
                return b
            return cl.Buffer(self.ctx, mf.READ_WRITE | mf.COPY_HOST_PTR, hostbuf=np.ascontiguousarray(host, dtype=np.float32))

        self.rho, self.temp, self.ux, self.uy, self.uz = buf(), buf(), buf(), buf(), buf()
        self.new = [buf() for _ in range(5)]          # the advected copies of rho, temp, ux, uy, uz
        # Texture coordinates (in cells) carried by the flow, so the detail noise added at render time moves with the smoke.
        zz, yy, xx = np.meshgrid(np.arange(nz, dtype=np.float32), np.arange(ny, dtype=np.float32), np.arange(nx, dtype=np.float32), indexing="ij")
        # Two sets: the flow stretches these coordinates more and more, so each set is reset in turn (half a cycle apart) and the
        # renderer cross-fades between the noise they give, which keeps the detail from smearing into streaks.
        self.phi = [buf(xx.ravel()), buf(yy.ravel()), buf(zz.ravel()), buf(xx.ravel()), buf(yy.ravel()), buf(zz.ravel())]
        self.phi_init = [buf(xx.ravel()), buf(yy.ravel()), buf(zz.ravel())]
        self.phi_cycle = 0.6        # seconds between resets of one set
        self.hat = [buf() for _ in range(11)]
        self.tilde = [buf() for _ in range(5)]
        self.wx, self.wy, self.wz, self.wm = buf(), buf(), buf(), buf()
        self.div = buf()
        self.partial = cl.Buffer(self.ctx, mf.READ_WRITE, size=((n + 255) // 256) * 4)
        self.swirl = [buf(), buf(), buf()]
        self.time = 0.0
        self.seed_counter = 1

        # The absorbing edge: the smoke leaves the cube instead of piling up on its walls (the floor stays solid).
        def ramp(m, lo, hi, cells):
            i = np.arange(m, dtype=np.float32)
            w = np.ones(m, np.float32)
            if lo: w = np.minimum(w, np.clip(i / cells, 0, 1))
            if hi: w = np.minimum(w, np.clip((m - 1 - i) / cells, 0, 1))
            return w * w * (3 - 2 * w)
        edge = max(4, int(round(1.75 / dx)))          # 1.75 m of absorbing edge
        sponge = ramp(nz, True, True, edge)[:, None, None] * ramp(ny, False, True, edge)[None, :, None] * ramp(nx, True, True, edge)[None, None, :]
        self.sponge = buf(sponge.ravel())

        # Multigrid levels: halve every dimension while they all stay even and at least 8 cells.
        self.levels = []
        lx, ly, lz, h = nx, ny, nz, dx
        while True:
            self.levels.append({"dims": (lx, ly, lz), "h": h,
                                "p": buf() if not self.levels else cl.Buffer(self.ctx, mf.READ_WRITE, size=lx * ly * lz * 4),
                                "rhs": cl.Buffer(self.ctx, mf.READ_WRITE, size=lx * ly * lz * 4),
                                "res": cl.Buffer(self.ctx, mf.READ_WRITE, size=lx * ly * lz * 4)})
            if lx % 2 or ly % 2 or lz % 2 or min(lx, ly, lz) < 16:
                break
            lx, ly, lz, h = lx // 2, ly // 2, lz // 2, h * 2
        self.levels[0]["p"] = buf()
        for L in self.levels:
            L["rhs_np_size"] = L["dims"][0] * L["dims"][1] * L["dims"][2]

    # ---- helpers ---------------------------------------------------------------------------------------------------------
    def _kernel(self, name):
        k = self._kernels.get(name)
        if k is None:
            k = self._kernels[name] = cl.Kernel(self.prg, name)
        return k

    def _launch(self, name, dims, *args):
        self._kernel(name)(self.q, dims, None, *args)

    def _i(self, v):
        return np.int32(v)

    def _f(self, v):
        return np.float32(v)

    def fetch(self, b):
        out = np.empty(self.n, np.float32)
        cl.enqueue_copy(self.q, out, b)
        self.q.finish()
        return out.reshape(self.nz, self.ny, self.nx)

    def make_swirl(self, cells=9, amplitude=6.0, coarse=4):
        """A smooth, divergence-free random velocity field (the curl of a smoothed random vector potential), uploaded to the GPU.
        It is made on a grid `coarse` times coarser and interpolated, which is plenty for eddies of `cells` cells."""
        cz, cy, cx = max(8, self.nz // coarse), max(8, self.ny // coarse), max(8, self.nx // coarse)
        k = max(3, (cells // coarse) | 1)

        def smooth(a):
            for axis in range(3):
                pad = [(0, 0)] * 3
                pad[axis] = (k // 2 + 1, k // 2)
                c = np.cumsum(np.pad(a, pad, mode="edge"), axis=axis, dtype=np.float64)
                sl_hi = [slice(None)] * 3; sl_lo = [slice(None)] * 3
                sl_hi[axis] = slice(k, k + a.shape[axis]); sl_lo[axis] = slice(0, a.shape[axis])
                a = ((c[tuple(sl_hi)] - c[tuple(sl_lo)]) / k).astype(np.float32)
            return a
        pot = [smooth(smooth(self.rng.random((cz, cy, cx), dtype=np.float32) - 0.5)) for _ in range(3)]
        d = self.dx * self.nx / cx
        curl = [np.gradient(pot[2], d, axis=1) - np.gradient(pot[1], d, axis=0),
                np.gradient(pot[0], d, axis=0) - np.gradient(pot[2], d, axis=2),
                np.gradient(pot[1], d, axis=2) - np.gradient(pot[0], d, axis=1)]
        peak = max(float(np.abs(c).max()) for c in curl) + 1e-9
        for b, c in zip(self.swirl, curl):
            fine = resize3d_linear(c * (amplitude / peak), (self.nz, self.ny, self.nx))
            cl.enqueue_copy(self.q, b, np.ascontiguousarray(fine, dtype=np.float32))
        self.q.finish()

    # ---- sources ---------------------------------------------------------------------------------------------------------
    def add_blob(self, centre, radius, rho=0.0, temp=0.0, velocity=(0, 0, 0), radial=0.0, noise=0.35, falloff=2.0, swirl=False, squash=1.0, vdir=1.0):
        self.seed_counter += 1
        self._launch("add_blob", (self.nx, self.ny, self.nz), self.rho, self.temp, self.ux, self.uy, self.uz,
                     self.swirl[0], self.swirl[1], self.swirl[2], self._i(self.nx), self._i(self.ny), self._i(self.nz), self._f(self.dx),
                     self._f(centre[0]), self._f(centre[1]), self._f(centre[2]), self._f(radius), self._f(rho), self._f(temp),
                     self._f(velocity[0]), self._f(velocity[1]), self._f(velocity[2]), self._f(radial), self._f(noise), self._f(falloff),
                     self._i(1 if swirl else 0), self._i(self.seed_counter), self._f(squash), self._f(vdir))

    # ---- pressure projection (multigrid) -----------------------------------------------------------------------------------
    def _sweeps(self, level, count):
        L = self.levels[level]
        nx, ny, nz = L["dims"]
        h2 = self._f(L["h"] * L["h"])
        for _ in range(count):
            for color in (0, 1):
                self._launch("gs_sweep", (nx, ny, nz), L["p"], L["rhs"], self._i(nx), self._i(ny), self._i(nz), h2, self._i(color))

    def _vcycle(self, level):
        L = self.levels[level]
        nx, ny, nz = L["dims"]
        if level == len(self.levels) - 1:
            self._sweeps(level, 40)
            return
        self._sweeps(level, 2)
        self._launch("residual", (nx, ny, nz), L["p"], L["rhs"], L["res"], self._i(nx), self._i(ny), self._i(nz), self._f(1.0 / (L["h"] * L["h"])))
        C = self.levels[level + 1]
        cnx, cny, cnz = C["dims"]
        self._launch("restrict_res", (cnx, cny, cnz), L["res"], C["rhs"], self._i(cnx), self._i(cny), self._i(cnz))
        cl.enqueue_fill_buffer(self.q, C["p"], np.float32(0), 0, cnx * cny * cnz * 4)
        self._vcycle(level + 1)
        self._launch("prolong_add", (nx, ny, nz), C["p"], L["p"], self._i(nx), self._i(ny), self._i(nz))
        self._sweeps(level, 2)

    def _project(self, cycles=3):
        nx, ny, nz, h = self.nx, self.ny, self.nz, self._f(self.dx)
        L0 = self.levels[0]
        self._launch("divergence", (nx, ny, nz), self.ux, self.uy, self.uz, L0["rhs"], self._i(nx), self._i(ny), self._i(nz), h)
        # All walls are solid, so the right-hand side must have a zero mean for the equation to have a solution.
        groups = (self.n + 255) // 256
        self._kernel("sum_partial")(self.q, (groups * 256,), (256,), L0["rhs"], self.partial, self._i(self.n))
        part = np.empty(groups, np.float32)
        cl.enqueue_copy(self.q, part, self.partial)
        mean = float(part.astype(np.float64).sum()) / self.n
        self._launch("add_const", (nx, ny, nz), L0["rhs"], self._i(nx), self._i(ny), self._i(nz), self._f(-mean))
        for _ in range(cycles):
            self._vcycle(0)
        self._launch("sub_gradient", (nx, ny, nz), self.ux, self.uy, self.uz, L0["p"], self._i(nx), self._i(ny), self._i(nz), h)

    # ---- one step -----------------------------------------------------------------------------------------------------------
    def step(self, dt, buoyancy=3.0, weight=0.0, vorticity=0.6, cool=1.7, dissipate=0.06, drag=0.05, cycles=3):
        nx, ny, nz = self.nx, self.ny, self.nz
        dims = (nx, ny, nz)
        ni, nj, nk, h = self._i(nx), self._i(ny), self._i(nz), self._f(self.dx)

        self._launch("buoyancy", dims, self.uy, self.temp, self.rho, ni, nj, nk, self._f(buoyancy), self._f(weight), self._f(dt))
        self._launch("vorticity", dims, self.ux, self.uy, self.uz, self.wx, self.wy, self.wz, self.wm, ni, nj, nk, h)
        self._launch("vort_force", dims, self.ux, self.uy, self.uz, self.wx, self.wy, self.wz, self.wm, ni, nj, nk, h, self._f(vorticity * self.dx * dt))
        self._project(cycles)

        dtdx = self._f(dt / self.dx)
        fields = [self.rho, self.temp, self.ux, self.uy, self.uz]
        for k in range(5):
            self._launch("advect", dims, fields[k], self.hat[k], self.ux, self.uy, self.uz, ni, nj, nk, dtdx)
        for k in range(6):
            self._launch("advect", dims, self.phi[k], self.hat[5 + k], self.ux, self.uy, self.uz, ni, nj, nk, dtdx)
        for k in range(5):
            self._launch("advect_fwd", dims, self.hat[k], self.tilde[k], self.ux, self.uy, self.uz, ni, nj, nk, dtdx)
        for k in range(5):
            self._launch("mac_limit", dims, fields[k], self.hat[k], self.tilde[k], self.new[k], self.ux, self.uy, self.uz, ni, nj, nk, dtdx)
        # The new fields replace the old ones; the old buffers become the scratch space for the next step. The texture
        # coordinates take the plain semi-Lagrangian result.
        old = [self.rho, self.temp, self.ux, self.uy, self.uz]
        self.rho, self.temp, self.ux, self.uy, self.uz = self.new
        self.new = old
        for k in range(6):
            self.phi[k], self.hat[5 + k] = self.hat[5 + k], self.phi[k]
        t_old, t_new = self.time, self.time + dt
        P = self.phi_cycle
        if int(np.floor(t_new / P)) != int(np.floor(t_old / P)):          # set A starts over
            for k in range(3):
                cl.enqueue_copy(self.q, self.phi[k], self.phi_init[k])
        if int(np.floor(t_new / P + 0.5)) != int(np.floor(t_old / P + 0.5)):   # set B starts over, half a cycle later
            for k in range(3):
                cl.enqueue_copy(self.q, self.phi[3 + k], self.phi_init[k])

        self._launch("damp", dims, self.rho, self.temp, self.ux, self.uy, self.uz, self.sponge, ni, nj, nk,
                     self._f(min(1.0, dt * 10.0)), self._f(np.exp(-cool * dt)), self._f(np.exp(-dissipate * dt)), self._f(np.exp(-drag * dt)))
        self.time += dt

    def phi_weights(self, t=None):
        """How much each coordinate set counts right now: triangle waves half a cycle apart that always add up to 1."""
        t = self.time if t is None else t
        a = (t / self.phi_cycle) % 1.0
        b = (t / self.phi_cycle + 0.5) % 1.0
        return 1.0 - abs(2.0 * a - 1.0), 1.0 - abs(2.0 * b - 1.0)

    def finish(self):
        self.q.finish()


# The knobs of the blast, so a bake can be tuned from the command line (key=value).
PARAMS = {
    "fire_radius": 0.35, "fire_rho": 0.5, "fire_temp": 1.0, "fire_radial": 2.0, "fire_time": 0.10, "fire_height": 0.45,
    "dust_blobs": 9, "dust_disc": 1.6, "dust_blob_r": 0.5, "dust_up": 3.0, "dust_out": 4.0, "dust_squash": 1.0, "dust_vdir": 1.0,
    "dust_rho": 1.0, "dust_radial": 15.0, "dust_vy": 2.0, "dust_time": 0.35, "dust_r1": 2.2, "dust_r2": 3.6,
    "buoyancy": 0.1, "vorticity": 1.0, "cool": 1.7, "dissipate": 0.25, "drag": 0.15, "swirl": 9.0,
}


def run_ground_burst(sim, seconds=4.0, dt=0.016, callback=None, frame_times=None, progress=None):
    """A fireball of hot gas just above the ground and dust thrown off the ground around it."""
    P = PARAMS
    cx, cz = sim.nx * sim.dx * 0.5, sim.nz * sim.dx * 0.5
    sim.make_swirl(cells=max(3, int(round(1.1 / sim.dx))), amplitude=P["swirl"])   # about 1.1 m wide eddies
    dt_of = dt if callable(dt) else (lambda t: dt)
    pending = list(frame_times) if frame_times is not None else None
    total = len(frame_times) if frame_times is not None else 0
    s = 0
    while sim.time < seconds:
        t = sim.time
        if t < P["fire_time"]:
            sim.add_blob((cx, P["fire_height"], cz), P["fire_radius"], rho=P["fire_rho"], temp=P["fire_temp"],
                         radial=P["fire_radial"] * (1 - t / P["fire_time"]), noise=0.5, falloff=1.6, swirl=True)
        if t < P["dust_time"]:
            # The blast kicks the dust up in many separate eruptions at random places around the ground under it: each one a small
            # jet with its own upward and outward speed, so the cloud grows as a heap of billows instead of one smooth slab.
            fade = 1.0 - t / P["dust_time"]
            for _ in range(int(P["dust_blobs"])):
                ang = sim.rng.random() * 2 * np.pi
                rad = P["dust_disc"] * np.sqrt(sim.rng.random())
                px, pz = cx + rad * np.cos(ang), cz + rad * np.sin(ang)
                out = P["dust_out"] * (0.4 + 0.6 * rad / P["dust_disc"]) * fade
                up = P["dust_up"] * (0.4 + 0.6 * sim.rng.random()) * fade
                size = P["dust_blob_r"] * (0.6 + 0.8 * sim.rng.random())
                sim.add_blob((px, size * 0.6, pz), size, rho=P["dust_rho"], temp=0.08,
                             velocity=(out * np.cos(ang), up, out * np.sin(ang)), noise=0.6, falloff=1.0, swirl=True)
        sim.step(dt_of(t), buoyancy=P["buoyancy"], vorticity=P["vorticity"], cool=P["cool"], dissipate=P["dissipate"], drag=P["drag"])
        s += 1
        if callback is not None:
            if pending is None:
                callback(s, sim)
            else:
                while pending and sim.time >= pending[0]:
                    callback(total - len(pending), sim)
                    pending.pop(0)
        if progress is not None and s % 20 == 0:
            progress(s, sim)


def render_preview(rho, heat, dx, light=(-0.55, 0.7, 0.45), size=320):
    """One picture (camera along -z) to look at the smoke: the same quick preview as smoke_sim.render_preview, from arrays."""
    from PIL import Image
    d = rho.astype(np.float64)[:, ::-1, :]
    h = heat.astype(np.float64)[:, ::-1, :]
    nz, ny, nx = d.shape
    k = 1.0 * dx
    in_front = np.flip(np.cumsum(np.flip(d, 0), 0), 0) - d
    t_view = np.exp(-in_front * k * 4.0)
    lx, ly, lz = np.array(light) / np.linalg.norm(light)
    # Light travelling through the smoke toward the sun, by repeated shifts (cheap and good enough for a preview).
    sh = np.zeros_like(d)
    for s in range(1, 9):
        a = int(round(-lz * s * 1.5)); b = int(round(ly * s * 1.5)); c = int(round(lx * s * 1.5))
        sh += np.roll(d, (a, b, c), axis=(0, 1, 2))
    t_light = np.exp(-sh * k * 2.2)
    w = d * t_view
    ws = w.sum(0) + 1e-6
    lit = (w * (0.22 + 1.0 * t_light)).sum(0) / ws
    alpha = 1.0 - np.exp(-d.sum(0) * k * 4.0)
    heatp = (h * w).sum(0) / ws
    col = np.array([0.64, 0.53, 0.41])[None, None, :] * lit[..., None]
    glow = np.clip((heatp - 0.3) * 2.2, 0, 1)[..., None] * np.array([1.0, 0.55, 0.15])[None, None, :] * np.clip(alpha, 0, 1)[..., None]
    col = col + glow * 1.6
    sky = np.linspace(0, 1, ny)[:, None, None]
    bg = (1 - sky) * np.array([0.55, 0.68, 0.9]) + sky * np.array([0.72, 0.74, 0.76])
    img = bg * (1 - alpha[..., None]) + col * alpha[..., None]
    im = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8), "RGB")
    return im.resize((int(nx * size / ny), size), Image.BICUBIC)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "preview"
    out = sys.argv[2] if len(sys.argv) > 2 else "."
    scale = float(sys.argv[3]) if len(sys.argv) > 3 else 1.0
    if mode == "preview":
        from PIL import Image
        nx = int(128 * scale) // 2 * 2
        ny = int(96 * scale) // 2 * 2
        sim = ClSmoke(nx=nx, ny=ny, nz=nx, dx=0.125 / scale)
        print("device:", sim.device_name, "| levels:", [L["dims"] for L in sim.levels])
        marks = [0.1, 0.3, 0.7, 1.3, 2.2, 3.5]
        shots = []
        t0 = time.time()

        def cb(i, s):
            shots.append(render_preview(s.fetch(s.rho), s.fetch(s.temp), s.dx))
        dtf = lambda t: 0.008 if t < 0.4 else (0.016 if t < 1.2 else 0.024)
        run_ground_burst(sim, seconds=3.6, dt=dtf, callback=cb, frame_times=marks)
        sim.finish()
        print("simulated %.1f s in %.1f s of computing (%dx%dx%d cells)" % (sim.time, time.time() - t0, nx, ny, nx))
        w, h = shots[0].size
        sheet = Image.new("RGB", (w * 3, h * 2))
        for i, t in enumerate(shots):
            sheet.paste(t, ((i % 3) * w, (i // 3) * h))
        sheet.save(out + "/cl_preview.png")


if __name__ == "__main__":
    main()
