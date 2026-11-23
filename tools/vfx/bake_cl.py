"""Bakes a blast / dust cloud into the two flipbook atlases of the game's six-way lit smoke shader, entirely on the GPU
(OpenCL): the fluid solver (smoke_cl.py) and the renderer below.

  <name>_RLT.png : R = lit from the right, G = from the left, B = from the top, A = opacity            (8 x 8 frames of 256 x 256)
  <name>_BBF.png : R = lit from the bottom, G = from the back, B = from the front, A = heat (0..1)
  <name>_preview.png : a few frames lit like the game does, over a sky colour

Run: python bake_cl.py <output folder> [name] [seed] [quick]
"""
import sys
import time
import numpy as np
import pyopencl as cl
from PIL import Image

import smoke_cl as S

FRAMES = 64
TILES = 8
FRAME_PX = 256
DURATION = 4.5
EXTINCTION = 5.0        # per metre per unit density: how fast smoke blocks the view
LIGHT_EXTINCTION = 3.5  # the same toward the light (a little softer: smoke scatters light forward)
DETAIL_ERODE = 0.55     # how much the noise carves the upsampled density
RHO_REF = 0.6           # the density that counts as "full"
Z_DETAIL = 0.75         # fraction of the picture's resolution used along the viewing direction
NOISE_N = 64
NOISE_PERIOD = 20.0     # sim cells of the reference resolution (12.5 cm) per noise tile; scaled to metres below
NOISE_PERIOD_M = 2.5

RENDER = r"""
#define LIGHT_FLOOR 0.06f   // light that always leaks in (multiple scattering): how dark the deepest shadow is
inline float reflect_to(float v, float hi)
{
    float p = 2.0f * hi;
    v = fmod(fabs(v), p);
    return v > hi ? p - v : v;
}

inline float noise3(__global const float* noise, float a, float b, float c, int n)
{
    float hi = n - 1.0f;
    float x = reflect_to(a, hi), y = reflect_to(b, hi), z = reflect_to(c, hi);
    int x0 = min((int)floor(x), n - 2), y0 = min((int)floor(y), n - 2), z0 = min((int)floor(z), n - 2);
    float fx = x - x0, fy = y - y0, fz = z - z0;
#define N3(X,Y,Z) noise[((Z) * n + (Y)) * n + (X)]
    float c00 = mix(N3(x0,y0,z0), N3(x0+1,y0,z0), fx), c10 = mix(N3(x0,y0+1,z0), N3(x0+1,y0+1,z0), fx);
    float c01 = mix(N3(x0,y0,z0+1), N3(x0+1,y0,z0+1), fx), c11 = mix(N3(x0,y0+1,z0+1), N3(x0+1,y0+1,z0+1), fx);
    return mix(mix(c00, c10, fy), mix(c01, c11, fy), fz);
#undef N3
}

constant float R1[9] = {0.844030f, -0.293128f, 0.449099f, 0.449099f, 0.844030f, -0.293128f, -0.293128f, 0.449099f, 0.844030f};
constant float R2[9] = {0.415129f, -0.772679f, -0.480244f, 0.480244f, 0.634456f, -0.605666f, 0.772679f, 0.020795f, 0.634456f};
inline float3 rot3(constant float* R, float3 v)
{
    return (float3)(R[0]*v.x + R[1]*v.y + R[2]*v.z, R[3]*v.x + R[4]*v.y + R[5]*v.z, R[6]*v.x + R[7]*v.y + R[8]*v.z);
}

inline float samp3(__global const float* f, float x, float y, float z, int nx, int ny, int nz)
{
    x = clamp(x, 0.0f, nx - 1.0f); y = clamp(y, 0.0f, ny - 1.0f); z = clamp(z, 0.0f, nz - 1.0f);
    int x0 = min((int)floor(x), nx - 2), y0 = min((int)floor(y), ny - 2), z0 = min((int)floor(z), nz - 2);
    float fx = x - x0, fy = y - y0, fz = z - z0;
#define F3(X,Y,Z) f[(((Z) * ny) + (Y)) * nx + (X)]
    float a = mix(mix(F3(x0,y0,z0), F3(x0+1,y0,z0), fx), mix(F3(x0,y0+1,z0), F3(x0+1,y0+1,z0), fx), fy);
    float b = mix(mix(F3(x0,y0,z0+1), F3(x0+1,y0,z0+1), fx), mix(F3(x0,y0+1,z0+1), F3(x0+1,y0+1,z0+1), fx), fy);
#undef F3
    return mix(a, b, fz);
}

// The picture's grid: HX x HY x HZ voxels, index (k * HY + j) * HX + i; i = x, j = y (up), k = z (toward the camera).
__kernel void build_hires(__global const float* rho, __global const float* temp,
                          __global const float* phix, __global const float* phiy, __global const float* phiz,
                          __global const float* phix2, __global const float* phiy2, __global const float* phiz2,
                          __global const float* noise, __global float* d_out, __global float* h_out,
                          int nx, int ny, int nz, int x0, int cw, int HX, int HY, int HZ, int nn,
                          float period, float erode, float rho_ref, float weight_a, float weight_b)
{
    int i = get_global_id(0), j = get_global_id(1), k = get_global_id(2);
    float sx = x0 + i * (cw - 1.0f) / (HX - 1.0f), sy = j * (ny - 1.0f) / (HY - 1.0f), sz = k * (nz - 1.0f) / (HZ - 1.0f);
    float r = samp3(rho, sx, sy, sz, nx, ny, nz);
    float h = samp3(temp, sx, sy, sz, nx, ny, nz);
    float s_ = (nn - 1.0f) / period;
    float q = (nn - 1.0f) * 0.5f;
    float n = 0.0f;
    for (int set = 0; set < 2; set++) {
        float wgt = set == 0 ? weight_a : weight_b;
        if (wgt <= 0.0f) continue;
        float px = samp3(set == 0 ? phix : phix2, sx, sy, sz, nx, ny, nz);
        float py = samp3(set == 0 ? phiy : phiy2, sx, sy, sz, nx, ny, nz);
        float pz = samp3(set == 0 ? phiz : phiz2, sx, sy, sz, nx, ny, nz);
        float3 c1 = rot3(R1, (float3)(px, py, pz)) * s_ + (nn * 0.5f);
        float n1 = noise3(noise, c1.x, c1.y, c1.z, nn);
        // second octave: finer, and turned differently
        float3 c2 = rot3(R2, (float3)(px, py, pz)) * (s_ * 2.7f) + (nn * 0.37f);
        float n2 = noise3(noise, c2.x, c2.y, c2.z, nn);
        n += wgt * (0.65f * n1 + 0.35f * n2);
    }
    float rr = clamp(r / rho_ref, 0.0f, 1.0f);
    float cut = erode * (1.0f - n) * clamp(rr * 2.5f, 0.0f, 1.0f);   // faint smoke is carved less
    float d = clamp((rr - cut) / fmax(1.0f - cut, 0.05f), 0.0f, 1.0f) * rho_ref;
    int idx = (k * HY + j) * HX + i;
    d_out[idx] = d;
    h_out[idx] = h;
}

// 5-tap box average along one axis (0 = x, 1 = y, 2 = z), counting only the taps inside the grid.
__kernel void box5(__global const float* src, __global float* dst, int axis, int HX, int HY, int HZ)
{
    int i = get_global_id(0), j = get_global_id(1), k = get_global_id(2);
    float s = 0.0f; int n = 0;
    for (int t = -2; t <= 2; t++) {
        int a = i, b = j, c = k;
        if (axis == 0) a += t; else if (axis == 1) b += t; else c += t;
        if (a < 0 || b < 0 || c < 0 || a >= HX || b >= HY || c >= HZ) continue;
        s += src[(c * HY + b) * HX + a]; n++;
    }
    dst[(k * HY + j) * HX + i] = s / n;
}

// What the camera sees, one work item per pixel: the visibility-weighted density w (kept), alpha, the sum of w and the heat.
__kernel void view_pass(__global const float* d, __global const float* heat, __global float* w_out,
                        __global float* alpha, __global float* wsum, __global float* hsum,
                        int HX, int HY, int HZ, float ext_dz)
{
    int i = get_global_id(0), j = get_global_id(1);
    float in_front = 0.0f, ws = 0.0f, hs = 0.0f;
    for (int k = HZ - 1; k >= 0; k--) {
        int idx = (k * HY + j) * HX + i;
        float dv = d[idx];
        float w = dv * exp(-in_front * ext_dz);
        w_out[idx] = w;
        ws += w; hs += w * heat[idx];
        in_front += dv;
    }
    alpha[j * HX + i] = 1.0f - exp(-in_front * ext_dz);
    wsum[j * HX + i] = ws;
    hsum[j * HX + i] = hs;
}

// Light from the camera side (front) and from behind (back): scans along z inside each pixel's column.
__kernel void z_lights(__global const float* soft, __global const float* w, __global const float* wsum,
                       __global float* front, __global float* back, int HX, int HY, int HZ, float lext_dz)
{
    int i = get_global_id(0), j = get_global_id(1);
    float ws = wsum[j * HX + i];
    float sf = 0.0f, f = 0.0f;
    for (int k = HZ - 1; k >= 0; k--) {
        int idx = (k * HY + j) * HX + i;
        f += w[idx] * (LIGHT_FLOOR + (1.0f - LIGHT_FLOOR) * exp(-sf * lext_dz));
        sf += soft[idx];
    }
    float sb = 0.0f, b = 0.0f;
    for (int k = 0; k < HZ; k++) {
        int idx = (k * HY + j) * HX + i;
        b += w[idx] * (LIGHT_FLOOR + (1.0f - LIGHT_FLOOR) * exp(-sb * lext_dz));
        sb += soft[idx];
    }
    front[j * HX + i] = ws > 1e-4f ? f / ws : 0.5f;
    back[j * HX + i] = ws > 1e-4f ? b / ws : 0.5f;
}

// Transmission toward a light from the +x side (dir 1) or the -x side (dir 0): one work item per (y, z) row.
__kernel void scan_x(__global const float* soft, __global float* T, int dir, int HX, int HY, int HZ, float lext_dx)
{
    int j = get_global_id(0), k = get_global_id(1);
    float s = 0.0f;
    for (int t = 0; t < HX; t++) {
        int i = dir ? HX - 1 - t : t;
        int idx = (k * HY + j) * HX + i;
        T[idx] = LIGHT_FLOOR + (1.0f - LIGHT_FLOOR) * exp(-s * lext_dx);
        s += soft[idx];
    }
}

__kernel void scan_y(__global const float* soft, __global float* T, int dir, int HX, int HY, int HZ, float lext_dy)
{
    int i = get_global_id(0), k = get_global_id(1);
    float s = 0.0f;
    for (int t = 0; t < HY; t++) {
        int j = dir ? HY - 1 - t : t;
        int idx = (k * HY + j) * HX + i;
        T[idx] = LIGHT_FLOOR + (1.0f - LIGHT_FLOOR) * exp(-s * lext_dy);
        s += soft[idx];
    }
}

__kernel void reduce_z(__global const float* w, __global const float* T, __global const float* wsum, __global float* out,
                       int HX, int HY, int HZ)
{
    int i = get_global_id(0), j = get_global_id(1);
    float v = 0.0f;
    for (int k = 0; k < HZ; k++) { int idx = (k * HY + j) * HX + i; v += w[idx] * T[idx]; }
    float ws = wsum[j * HX + i];
    out[j * HX + i] = ws > 1e-4f ? v / ws : 0.5f;
}
"""


def make_noise(size=NOISE_N, seed=11):
    """Smooth multi-octave 3D noise in 0..1 (float32, size^3)."""
    rng = np.random.default_rng(seed)
    total = np.zeros((size, size, size), np.float32)
    amp, norm = 1.0, 0.0
    for cells in (4, 8, 16, 32):
        coarse = rng.random((cells + 1, cells + 1, cells + 1), dtype=np.float32)
        total += amp * S.resize3d_linear(coarse, (size, size, size))
        norm += amp
        amp *= 0.55
    total /= norm
    return ((total - total.min()) / (total.max() - total.min())).astype(np.float32)


class GpuBaker:
    def __init__(self, sim, crop_x):
        self.sim = sim
        self.x0, self.x1 = crop_x
        cw = self.x1 - self.x0
        self.cw = cw
        self.scale = FRAME_PX / sim.ny
        self.HY = FRAME_PX
        self.HX = int(round(cw * self.scale))
        self.HZ = max(16, int(round(sim.nz * self.scale * Z_DETAIL)))
        ctx, q = sim.ctx, sim.q
        self.q = q
        self.prg = cl.Program(ctx, RENDER).build()
        self._kernels = {}
        mf = cl.mem_flags
        n = self.HX * self.HY * self.HZ
        self.nvox = n
        pix = self.HX * self.HY
        self.d = cl.Buffer(ctx, mf.READ_WRITE, size=n * 4)
        self.heat = cl.Buffer(ctx, mf.READ_WRITE, size=n * 4)
        self.soft_a = cl.Buffer(ctx, mf.READ_WRITE, size=n * 4)
        self.soft_b = cl.Buffer(ctx, mf.READ_WRITE, size=n * 4)
        self.w = cl.Buffer(ctx, mf.READ_WRITE, size=n * 4)
        self.T = cl.Buffer(ctx, mf.READ_WRITE, size=n * 4)
        self.noise = cl.Buffer(ctx, mf.READ_ONLY | mf.COPY_HOST_PTR, hostbuf=make_noise())
        self.pix = [cl.Buffer(ctx, mf.READ_WRITE, size=pix * 4) for _ in range(10)]
        # metres per hi-res voxel
        self.dxh = sim.dx * cw / (self.HX - 1)
        self.dyh = sim.dx * sim.ny / (self.HY - 1)
        self.dzh = sim.dx * sim.nz / (self.HZ - 1)

    def _k(self, name):
        k = self._kernels.get(name)
        if k is None:
            k = self._kernels[name] = cl.Kernel(self.prg, name)
        return k

    def render(self):
        sim, q = self.sim, self.q
        i32, f32 = np.int32, np.float32
        HX, HY, HZ = self.HX, self.HY, self.HZ
        alpha, wsum, hsum, front, back, right, left, top, bottom, heat_vis = self.pix
        period = NOISE_PERIOD_M / sim.dx            # sim cells per noise tile
        wa, wb = sim.phi_weights()

        self._k("build_hires")(q, (HX, HY, HZ), None, sim.rho, sim.temp, sim.phi[0], sim.phi[1], sim.phi[2], sim.phi[3], sim.phi[4], sim.phi[5], self.noise, self.d, self.heat,
                               i32(sim.nx), i32(sim.ny), i32(sim.nz), i32(self.x0), i32(self.cw), i32(HX), i32(HY), i32(HZ), i32(NOISE_N),
                               f32(period), f32(DETAIL_ERODE), f32(RHO_REF), f32(wa), f32(wb))
        self._k("view_pass")(q, (HX, HY), None, self.d, self.heat, self.w, alpha, wsum, hsum, i32(HX), i32(HY), i32(HZ), f32(EXTINCTION * self.dzh))

        # Softened density for the light paths (a 5 x 5 x 5 box average, as three passes).
        self._k("box5")(q, (HX, HY, HZ), None, self.d, self.soft_a, i32(0), i32(HX), i32(HY), i32(HZ))
        self._k("box5")(q, (HX, HY, HZ), None, self.soft_a, self.soft_b, i32(1), i32(HX), i32(HY), i32(HZ))
        self._k("box5")(q, (HX, HY, HZ), None, self.soft_b, self.soft_a, i32(2), i32(HX), i32(HY), i32(HZ))
        soft = self.soft_a

        self._k("z_lights")(q, (HX, HY), None, soft, self.w, wsum, front, back, i32(HX), i32(HY), i32(HZ), f32(LIGHT_EXTINCTION * self.dzh))
        for dir_, out in ((1, right), (0, left)):
            self._k("scan_x")(q, (HY, HZ), None, soft, self.T, i32(dir_), i32(HX), i32(HY), i32(HZ), f32(LIGHT_EXTINCTION * self.dxh))
            self._k("reduce_z")(q, (HX, HY), None, self.w, self.T, wsum, out, i32(HX), i32(HY), i32(HZ))
        for dir_, out in ((1, top), (0, bottom)):
            self._k("scan_y")(q, (HX, HZ), None, soft, self.T, i32(dir_), i32(HX), i32(HY), i32(HZ), f32(LIGHT_EXTINCTION * self.dyh))
            self._k("reduce_z")(q, (HX, HY), None, self.w, self.T, wsum, out, i32(HX), i32(HY), i32(HZ))

        def get(b):
            a = np.empty(HX * HY, np.float32)
            cl.enqueue_copy(q, a, b)
            return a.reshape(HY, HX)
        ws = get(wsum)
        hv = np.where(ws > 1e-4, get(hsum) / np.maximum(ws, 1e-5), 0.0)
        planes = [get(right), get(left), get(top), get(alpha), get(bottom), get(back), get(front), hv]
        # Rows: the top of the picture is high y.
        out = np.stack([p[::-1] for p in planes])
        # Whatever reaches the sides or the top of the picture fades out, so the sprite never shows a hard rectangle edge
        # (the bottom is the ground and stays as it is).
        h, w = out.shape[1], out.shape[2]
        def smooth(t):
            t = np.clip(t, 0.0, 1.0)
            return t * t * (3 - 2 * t)
        xs = np.arange(w, dtype=np.float32)
        fx = smooth(xs / (0.14 * w)) * smooth((w - 1 - xs) / (0.14 * w))
        ys = np.arange(h, dtype=np.float32)
        fy = smooth(ys / (0.12 * h))                       # row 0 is the top
        out[3] *= fy[:, None] * fx[None, :]
        return (np.clip(out, 0, 1) * 255 + 0.5).astype(np.uint8)


def frame_times():
    # Dense early (the blast is fast), sparse late. The game plays the sheet back with the inverse curve, so time stays real.
    return [DURATION * (k / (FRAMES - 1)) ** 1.5 for k in range(FRAMES)]


def paste(atlas_a, atlas_b, k, planes):
    col, row = k % TILES, k // TILES
    ys, xs = row * FRAME_PX, col * FRAME_PX
    h, w = planes.shape[1], planes.shape[2]
    oy, ox = (FRAME_PX - h) // 2, (FRAME_PX - w) // 2
    atlas_a[ys + oy: ys + oy + h, xs + ox: xs + ox + w, 0:3] = np.moveaxis(planes[0:3], 0, 2)
    atlas_a[ys + oy: ys + oy + h, xs + ox: xs + ox + w, 3] = planes[3]
    atlas_b[ys + oy: ys + oy + h, xs + ox: xs + ox + w, 0:3] = np.moveaxis(planes[4:7], 0, 2)
    atlas_b[ys + oy: ys + oy + h, xs + ox: xs + ox + w, 3] = planes[7]


def preview(atlas_a, atlas_b, out_path, sun=(0.5, 0.6, 0.62), picks=None):
    """The six-way blend of the game's shader on a few frames, over a sky colour."""
    sun = np.array(sun) / np.linalg.norm(sun)
    w = sun * sun
    tiles = []
    if picks is None:
        times = frame_times()
        picks = [int(np.argmin([abs(t - m) for t in times])) for m in (0.15, 0.4, 0.8, 1.4, 2.1, 3.0, 3.8, 4.4)]
    for k in picks:
        col, row = k % TILES, k // TILES
        sl = (slice(row * FRAME_PX, (row + 1) * FRAME_PX), slice(col * FRAME_PX, (col + 1) * FRAME_PX))
        a = atlas_a[sl].astype(np.float32) / 255.0
        b = atlas_b[sl].astype(np.float32) / 255.0
        lx = a[..., 0] if sun[0] > 0 else a[..., 1]
        ly = a[..., 2] if sun[1] > 0 else b[..., 0]
        lz = b[..., 2] if sun[2] > 0 else b[..., 1]
        lit = w[0] * lx + w[1] * ly + w[2] * lz
        avg = (a[..., 0] + a[..., 1] + a[..., 2] + b[..., 0] + b[..., 1] + b[..., 2]) / 6.0
        body = np.array([0.55, 0.46, 0.36])[None, None, :] * (1.0 * lit + 0.45 * avg)[..., None]
        glow = np.clip((b[..., 3] - 0.3) * 2.2, 0, 1)[..., None] * np.array([1.0, 0.55, 0.15])[None, None, :] * 1.6
        alpha = a[..., 3]
        rgb = body * alpha[..., None] + glow * alpha[..., None]
        sky = np.linspace(0, 1, FRAME_PX)[:, None, None]
        bg = (1 - sky) * np.array([0.55, 0.68, 0.9]) + sky * np.array([0.72, 0.74, 0.76])
        tiles.append(np.clip(bg * (1 - alpha[..., None]) + rgb, 0, 1))
    sheet = np.vstack([np.hstack(tiles[:4]), np.hstack(tiles[4:8])])
    Image.fromarray((sheet * 255).astype(np.uint8), "RGB").save(out_path)


def main():
    out = sys.argv[1]
    name = sys.argv[2] if len(sys.argv) > 2 else "blast"
    seed = int(sys.argv[3]) if len(sys.argv) > 3 else 3
    quick = len(sys.argv) > 4 and sys.argv[4] == "quick"
    for arg in sys.argv[5:]:                    # key=value overrides of the blast's knobs (see smoke_cl.PARAMS)
        key, value = arg.split("=")
        S.PARAMS[key] = float(value)

    if quick:
        dx = 0.125
        sim = S.ClSmoke(nx=160, ny=112, nz=128, dx=dx, seed=seed)
        crop = (int(round(3.0 / dx)), int(round(3.0 / dx)) + 112)
    else:
        dx = 0.0625
        sim = S.ClSmoke(nx=320, ny=224, nz=256, dx=dx, seed=seed)
        crop = (int(round(3.0 / dx)), int(round(3.0 / dx)) + 224)      # a 14 m window, 3 m from the left edge of the 20 m cube
    print("device %s | grid %dx%dx%d of %.1f cm | multigrid levels %s" % (sim.device_name, sim.nx, sim.ny, sim.nz, dx * 100, [L["dims"] for L in sim.levels]), flush=True)
    baker = GpuBaker(sim, crop)
    print("picture grid %dx%dx%d" % (baker.HX, baker.HY, baker.HZ), flush=True)
    atlas_a = np.zeros((TILES * FRAME_PX, TILES * FRAME_PX, 4), np.uint8)
    atlas_b = np.zeros_like(atlas_a)
    t0 = time.time()

    def on_frame(k, s):
        planes = baker.render()
        paste(atlas_a, atlas_b, k, planes)
        if k % 8 == 0 or k == FRAMES - 1:
            print("frame %2d/%d at %.2f s (%.0f s elapsed)" % (k + 1, FRAMES, s.time, time.time() - t0), flush=True)

    dt_of = lambda t: 0.008 if t < 0.4 else (0.016 if t < 1.2 else 0.024)
    S.run_ground_burst(sim, seconds=DURATION + 0.01, dt=dt_of, callback=on_frame, frame_times=frame_times())
    Image.fromarray(atlas_a, "RGBA").save("%s/%s_RLT.png" % (out, name))
    Image.fromarray(atlas_b, "RGBA").save("%s/%s_BBF.png" % (out, name))
    preview(atlas_a, atlas_b, "%s/%s_preview.png" % (out, name))
    print("done in %.0f s" % (time.time() - t0))


if __name__ == "__main__":
    main()
