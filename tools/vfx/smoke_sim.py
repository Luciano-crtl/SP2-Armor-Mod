"""A 3D smoke solver (stable fluids: MacCormack advection, vorticity confinement, buoyancy, FFT pressure solve) on the CPU with
PyTorch, used to bake smoke the game cannot simulate in real time. bake_flipbook.py turns its volumes into the lightmap atlases
that the game's shader reads.

Units are SI. Tensors are indexed [z, y, x]; y is up, the floor is y = 0. The camera of the bake looks along -z.
`python smoke_sim.py preview <folder> [n]` writes a few pictures to judge the simulation.
"""
import sys
import time
import numpy as np
import torch
import torch.nn.functional as F

torch.set_num_threads(16)
DTYPE = torch.float32


class Smoke:
    def __init__(self, nx=128, ny=96, nz=128, dx=0.125, seed=1):
        self.nx, self.ny, self.nz, self.dx = nx, ny, nz, dx
        self.gen = torch.Generator().manual_seed(seed)
        shape = (nz, ny, nx)
        self.rho = torch.zeros(shape, dtype=DTYPE)      # smoke / dust density
        self.temp = torch.zeros(shape, dtype=DTYPE)     # temperature above the air, 0..1
        self.u = torch.zeros((3,) + shape, dtype=DTYPE)  # velocity (m/s): (ux, uy, uz)
        # Texture coordinates (in cells) carried by the flow: the detail noise added at render time travels with the smoke.
        zz, yy, xx = torch.meshgrid(torch.arange(nz, dtype=DTYPE), torch.arange(ny, dtype=DTYPE), torch.arange(nx, dtype=DTYPE), indexing="ij")
        self.idx = torch.stack([xx, yy, zz])            # (3, nz, ny, nx): x, y, z in cells
        self.phi = self.idx.clone()
        self.time = 0.0

        # Pressure solve by mirroring the field across every wall (Neumann walls) and using a periodic FFT.
        def lam(n, rfft=False):
            k = torch.fft.rfftfreq(2 * n) * 2 * n if rfft else torch.fft.fftfreq(2 * n) * 2 * n
            return (2.0 * torch.cos(np.pi * k / n) - 2.0) / (dx * dx)
        self.lap = (lam(nz)[:, None, None] + lam(ny)[None, :, None] + lam(nx, True)[None, None, :]).to(torch.float32)
        self.lap[0, 0, 0] = 1.0

        # An absorbing edge so the smoke leaves the cube instead of piling up on its walls (the floor stays solid).
        def ramp(n, lo, hi, cells):
            i = torch.arange(n, dtype=DTYPE)
            w = torch.ones(n, dtype=DTYPE)
            if lo:
                w = torch.minimum(w, torch.clamp(i / cells, 0, 1))
            if hi:
                w = torch.minimum(w, torch.clamp((n - 1 - i) / cells, 0, 1))
            return w * w * (3 - 2 * w)
        edge = 14
        self.sponge = ramp(nx, True, True, edge)[None, None, :] * ramp(nz, True, True, edge)[:, None, None] * ramp(ny, False, True, edge)[None, :, None]

    # ---- coordinates -------------------------------------------------------------------------------------------------------
    def world(self):
        """Cell-centre coordinates in metres: x, y, z tensors."""
        c = (self.idx + 0.5) * self.dx
        return c[0], c[1], c[2]

    def _grid(self, pos):
        """Cell coordinates (3, nz, ny, nx) to the normalised grid_sample layout (1, nz, ny, nx, 3) with x, y, z last."""
        sx = 2.0 / (self.nx - 1)
        sy = 2.0 / (self.ny - 1)
        sz = 2.0 / (self.nz - 1)
        g = torch.stack([pos[0] * sx - 1.0, pos[1] * sy - 1.0, pos[2] * sz - 1.0], dim=-1)
        return g[None]

    def _sample(self, stack, pos):
        return F.grid_sample(stack[None], self._grid(pos), mode="bilinear", padding_mode="border", align_corners=True)[0]

    # ---- one step ----------------------------------------------------------------------------------------------------------
    def _vorticity_confinement(self, dt, eps):
        ux, uy, uz = self.u
        d = self.dx
        gz = lambda t, a: torch.gradient(t, spacing=d, dim=a)[0]
        # tensor axes: 0 = z, 1 = y, 2 = x
        wx = gz(uz, 1) - gz(uy, 0)
        wy = gz(ux, 0) - gz(uz, 2)
        wz = gz(uy, 2) - gz(ux, 1)
        mag = torch.sqrt(wx * wx + wy * wy + wz * wz)
        gx, gy, gzz = gz(mag, 2), gz(mag, 1), gz(mag, 0)
        gl = torch.sqrt(gx * gx + gy * gy + gzz * gzz) + 1e-6
        nx_, ny_, nz_ = gx / gl, gy / gl, gzz / gl
        fx = ny_ * wz - nz_ * wy
        fy = nz_ * wx - nx_ * wz
        fz = nx_ * wy - ny_ * wx
        k = eps * d * dt
        self.u[0] += k * fx
        self.u[1] += k * fy
        self.u[2] += k * fz

    def _project(self):
        d = self.dx
        ux, uy, uz = self.u
        div = (torch.gradient(ux, spacing=d, dim=2)[0] + torch.gradient(uy, spacing=d, dim=1)[0] + torch.gradient(uz, spacing=d, dim=0)[0])
        ext = torch.cat([div, div.flip(0)], 0)
        ext = torch.cat([ext, ext.flip(1)], 1)
        ext = torch.cat([ext, ext.flip(2)], 2)
        p_hat = torch.fft.rfftn(ext) / self.lap
        p_hat[0, 0, 0] = 0
        p = torch.fft.irfftn(p_hat, s=ext.shape)[: self.nz, : self.ny, : self.nx]
        self.u[0] -= torch.gradient(p, spacing=d, dim=2)[0]
        self.u[1] -= torch.gradient(p, spacing=d, dim=1)[0]
        self.u[2] -= torch.gradient(p, spacing=d, dim=0)[0]
        # Free-slip floor and walls.
        self.u[1][:, 0, :] = torch.clamp(self.u[1][:, 0, :], min=0)

    def step(self, dt, buoyancy=3.0, weight=0.0, vorticity=0.6, cool=1.7, dissipate=0.06, drag=0.05):
        self.u[1] += (buoyancy * self.temp - weight * self.rho) * dt
        self._vorticity_confinement(dt, vorticity)
        self._project()

        # Advect density, temperature, velocity and the texture coordinates together (semi-Lagrangian). The density and the
        # temperature, which carry the look, get a second, MacCormack pass (sharper, clamped to the local range).
        stack = torch.cat([self.rho[None], self.temp[None], self.u, self.phi], 0)   # 2 + 3 + 3 channels
        shift = (dt / self.dx) * self.u
        back = self.idx - shift
        fwd = self.idx + shift
        hat = self._sample(stack, back)
        scal = stack[:5]                                  # density, temperature and velocity keep their detail best with MacCormack
        tilde = self._sample(hat[:5], fwd)
        sharp = hat[:5] + 0.5 * (scal - tilde)
        hi = F.max_pool3d(scal[None], 3, 1, 1)[0]
        lo = -F.max_pool3d(-scal[None], 3, 1, 1)[0]
        sharp = torch.minimum(torch.maximum(sharp, lo), hi)
        self.rho, self.temp = sharp[0].clamp(min=0), sharp[1].clamp(min=0)
        self.u = sharp[2:5].contiguous()
        self.phi = hat[5:8].contiguous()

        # The sponge, the floor, cooling and a slow thinning.
        keep = 1.0 - (1.0 - self.sponge) * min(1.0, dt * 10.0)
        self.rho *= keep
        self.temp *= keep
        self.u *= keep
        self.u *= float(np.exp(-drag * dt))
        self.u[1][:, 0, :] = torch.clamp(self.u[1][:, 0, :], min=0)
        self.temp *= float(np.exp(-cool * dt))
        self.rho *= float(np.exp(-dissipate * dt))
        self.time += dt

    # ---- sources -----------------------------------------------------------------------------------------------------------
    def _rand(self):
        return torch.rand(self.rho.shape, generator=self.gen, dtype=DTYPE)

    def curl_noise(self, cells, amplitude):
        """A smooth, divergence-free random velocity field (the curl of a smoothed random vector potential)."""
        a = torch.rand((3,) + self.rho.shape, generator=self.gen, dtype=DTYPE)[None] - 0.5
        k = cells | 1
        for _ in range(2):
            a = F.avg_pool3d(a, k, 1, k // 2, count_include_pad=False)
        a = a[0]
        d = self.dx
        g = lambda t, ax: torch.gradient(t, spacing=d, dim=ax)[0]
        cx = g(a[2], 1) - g(a[1], 0)
        cy = g(a[0], 0) - g(a[2], 2)
        cz = g(a[1], 2) - g(a[0], 1)
        c = torch.stack([cx, cy, cz])
        peak = c.abs().max() + 1e-9
        return c * (amplitude / peak)

    def add_blob(self, centre, radius, rho=0.0, temp=0.0, velocity=(0, 0, 0), radial=0.0, noise=0.35, falloff=2.0, swirl=None):
        x, y, z = self.world()
        dx_, dy_, dz_ = x - centre[0], y - centre[1], z - centre[2]
        r = torch.sqrt(dx_ * dx_ + dy_ * dy_ + dz_ * dz_)
        w = torch.clamp(1.0 - r / radius, 0, 1) ** falloff
        w = torch.clamp(w * (1.0 + noise * (self._rand() - 0.5) * 2.0), 0, 1)
        self.rho = torch.maximum(self.rho, rho * w)
        self.temp = torch.maximum(self.temp, temp * w)
        inv = 1.0 / (r + 1e-3)
        self.u[0] += w * (velocity[0] + radial * dx_ * inv)
        self.u[1] += w * (velocity[1] + radial * dy_ * inv)
        self.u[2] += w * (velocity[2] + radial * dz_ * inv)
        if swirl is not None:
            self.u += w[None] * swirl


def ground_burst(seed=3, nx=128, ny=96, nz=128, dx=0.125):
    """A blast just above the ground: a fireball of hot gas with a ring of dust thrown off the ground, seen from any side."""
    sim = Smoke(nx=nx, ny=ny, nz=nz, dx=dx, seed=seed)
    return sim


def run_ground_burst(sim, seconds=4.0, dt=0.016, callback=None, frame_times=None, progress=None):
    """`dt` is a number or a function of the simulated time (fine steps while the blast is violent, coarse ones later)."""
    cx, cz = sim.nx * sim.dx * 0.5, sim.nz * sim.dx * 0.5
    swirl = sim.curl_noise(cells=9, amplitude=6.0)       # turbulence seeded into the source, so the billows have something to grow from
    dt_of = dt if callable(dt) else (lambda t: dt)
    pending = list(frame_times) if frame_times is not None else None
    total = len(frame_times) if frame_times is not None else 0
    s = 0
    while sim.time < seconds:
        t = sim.time
        if t < 0.10:
            # The fireball: hot gas pushed out of a ball just above the ground.
            sim.add_blob((cx, 1.2, cz), 1.2, rho=0.55, temp=1.0, radial=24.0 * (1 - t / 0.10), noise=0.5, falloff=1.6, swirl=swirl)
        if t < 0.35:
            # Dust thrown up from the ground around it.
            for ring in (2.2, 3.6):
                sim.add_blob((cx, 0.3, cz), ring, rho=0.38, temp=0.08, velocity=(0, 2.0, 0), radial=15.0 * (1 - t / 0.35),
                             noise=0.85, falloff=1.0, swirl=swirl)
        sim.step(dt_of(t))
        s += 1
        if callback is not None:
            if pending is None:
                callback(s, sim)
            else:
                while pending and sim.time >= pending[0]:
                    callback(total - len(pending), sim)
                    pending.pop(0)
        if progress is not None and s % 10 == 0:
            progress(s, sim)


def render_preview(sim, light=(-0.55, 0.7, 0.45), size=320, crop=None):
    """One picture, camera along -z, to look at the smoke. Not the baked format (see bake_flipbook.py)."""
    from PIL import Image
    rho = sim.rho.numpy().astype(np.float64)               # [z, y, x]
    heat = sim.temp.numpy().astype(np.float64)
    if crop is not None:
        x0, x1 = crop
        rho, heat = rho[:, :, x0:x1], heat[:, :, x0:x1]
    nz, ny, nx = rho.shape
    d = rho[:, ::-1, :]                                     # rows: y downward
    h = heat[:, ::-1, :]
    k = 1.0 * sim.dx
    in_front = np.flip(np.cumsum(np.flip(d, 0), 0), 0) - d
    t_view = np.exp(-in_front * k * 4.0)
    lx, ly, lz = np.array(light) / np.linalg.norm(light)
    from scipy import ndimage
    sh = np.zeros_like(d)
    for s in range(1, 9):
        sh += ndimage.shift(d, (-lz * s * 1.5, ly * s * 1.5, -lx * s * 1.5 * -1), order=1, mode="nearest")
    t_light = np.exp(-sh * k * 2.2)
    w = d * t_view
    ws = w.sum(0) + 1e-6
    lit = (w * (0.22 + 1.0 * t_light)).sum(0) / ws
    alpha = 1.0 - np.exp(-d.sum(0) * k * 4.0)
    heatp = (h * w).sum(0) / ws
    dust = np.array([0.64, 0.53, 0.41])
    col = dust[None, None, :] * lit[..., None]
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
    scale = float(sys.argv[3]) if len(sys.argv) > 3 else 1.0     # 1 = 128x96x128 cells of 12.5 cm; 0.5 = a quick look
    if mode == "preview":
        from PIL import Image
        nx = int(128 * scale) // 2 * 2
        ny = int(96 * scale) // 2 * 2
        nz = nx
        sim = ground_burst(nx=nx, ny=ny, nz=nz, dx=0.125 / scale)
        marks = [0.1, 0.3, 0.7, 1.3, 2.2, 3.5]
        shots = []
        t0 = time.time()

        def cb(i, s):
            shots.append(render_preview(s))
        run_ground_burst(sim, seconds=3.6, dt=0.016 / max(scale, 0.5) * 0.5 if scale >= 1 else 0.016, callback=cb, frame_times=marks)
        print("simulated %.1f s in %.0f s of computing (%dx%dx%d cells)" % (sim.time, time.time() - t0, nx, ny, nz))
        w, h = shots[0].size
        sheet = Image.new("RGB", (w * 3, h * 2))
        for i, t in enumerate(shots):
            sheet.paste(t, ((i % 3) * w, (i // 3) * h))
        sheet.save(out + "/sim_preview.png")


if __name__ == "__main__":
    main()
