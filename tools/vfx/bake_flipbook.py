"""Bakes a blast / dust cloud simulation into two flipbook atlases for the game's six-way lit smoke shader.

  <name>_RLT.png : R = lit from the right, G = from the left, B = from the top, A = opacity            (8 x 8 frames of 256 x 256)
  <name>_BBF.png : R = lit from the bottom, G = from the back, B = from the front, A = heat (0..1)
  <name>_preview.png : a few frames lit like the game would, over a sky colour (to judge the result)

Run: python bake_flipbook.py <output folder> [name] [seed] [quick]
`quick` bakes a small, fast version to check the pipeline.
"""
import sys
import time
import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image

import smoke_sim as S

FRAMES = 64
TILES = 8
FRAME_PX = 256
DURATION = 4.5
EXTINCTION = 3.0        # per metre per unit density: how fast smoke blocks the view
LIGHT_EXTINCTION = 2.2  # the same toward the light (a little softer: smoke scatters light forward)
DETAIL_ERODE = 0.55     # how much the noise carves the upsampled density
RHO_REF = 0.6           # the density that counts as "full"
Z_DETAIL = 0.5          # fraction of the picture's resolution used along the viewing direction (saves memory)


def frame_times():
    # Dense early (the blast is fast), sparse late. The game plays the sheet back with the inverse curve, so time stays real.
    return [DURATION * (k / (FRAMES - 1)) ** 1.5 for k in range(FRAMES)]


def make_noise(size=64, seed=11):
    """Smooth multi-octave 3D noise in 0..1, as a (1, 1, D, H, W) tensor."""
    g = torch.Generator().manual_seed(seed)
    total = torch.zeros((1, 1, size, size, size))
    amp, norm = 1.0, 0.0
    for o, cells in enumerate((4, 8, 16, 32)):
        coarse = torch.rand((1, 1, cells + 1, cells + 1, cells + 1), generator=g)
        up = F.interpolate(coarse, size=(size, size, size), mode="trilinear", align_corners=True)
        total += amp * up
        norm += amp
        amp *= 0.55
    total /= norm
    lo, hi = total.min(), total.max()
    return (total - lo) / (hi - lo)


def exclusive_sum(a, dim, reverse):
    """The sum of everything strictly beyond each element along `dim` (reverse = toward lower indices of the opposite side)."""
    if reverse:
        a = a.flip(dim)
    c = torch.cumsum(a, dim) - a
    if reverse:
        c = c.flip(dim)
    return c


class Baker:
    def __init__(self, sim, crop_x, noise):
        self.sim = sim
        self.crop_x = crop_x            # (x0, x1) cells of the window that becomes the picture
        self.noise = noise

    def render(self):
        sim = self.sim
        x0, x1 = self.crop_x
        nz, ny, nx = sim.nz, sim.ny, x1 - x0
        rho = sim.rho[:, :, x0:x1]
        temp = sim.temp[:, :, x0:x1]
        phi = sim.phi[:, :, :, x0:x1]
        # The picture is square: the crop is nx wide and ny tall; the height is the larger, so it sets the scale.
        hi_xy = FRAME_PX
        scale = hi_xy / ny
        hz = max(16, int(round(nz * scale * Z_DETAIL)))   # the depth axis needs less detail than the picture plane
        hx = int(round(nx * scale))

        def up(t, mode="trilinear"):
            return F.interpolate(t[None], size=(hz, hi_xy, hx), mode=mode, align_corners=True)[0]

        d = up(rho[None])[0]
        heat = up(temp[None])[0]
        # Detail: noise that travels with the smoke (sampled at the texture coordinates the flow carried).
        coords = up(phi)                                              # (3, hz, hy, hx) in sim cells
        period = 20.0                                                 # cells per noise tile
        g = torch.stack([coords[0] / period, coords[1] / period, coords[2] / period], dim=-1)[None] * 2.0 - 1.0
        n1 = F.grid_sample(self.noise, g, mode="bilinear", padding_mode="reflection", align_corners=True)[0, 0]
        n2 = F.grid_sample(self.noise, g * 2.7 + 0.37, mode="bilinear", padding_mode="reflection", align_corners=True)[0, 0]
        n = 0.65 * n1 + 0.35 * n2
        r = (d / RHO_REF).clamp(0, 1)
        cut = DETAIL_ERODE * (1.0 - n)
        d = ((r - cut) / (1.0 - cut).clamp(min=0.05)).clamp(0, 1) * RHO_REF
        del coords, g, n1, n2, n, r, cut

        dxh = sim.dx / scale                                          # metres per hi-res cell in the picture plane
        dzh = nz * sim.dx / hz                                        # metres per hi-res cell along the view direction
        step = {0: dzh, 1: dxh, 2: dxh}                               # per tensor axis: 0 = z, 1 = y, 2 = x
        in_front = exclusive_sum(d, 0, reverse=True) * (EXTINCTION * dzh)   # camera at +z: "in front" = higher z
        w = d * torch.exp(-in_front)
        del in_front
        wsum = w.sum(0)
        alpha = 1.0 - torch.exp(-d.sum(0) * (EXTINCTION * dzh))

        # Light travelling through the smoke from each of six sides.
        soft = F.avg_pool3d(d[None, None], 5, 1, 2, count_include_pad=False)[0, 0]

        def lit(dim, reverse):
            depth = exclusive_sum(soft, dim, reverse) * (LIGHT_EXTINCTION * step[dim])
            t = 0.12 + 0.88 * torch.exp(-depth)
            del depth
            val = (w * t).sum(0) / wsum.clamp(min=1e-5)
            return torch.where(wsum > 1e-4, val, torch.full_like(val, 0.5))

        # Tensor axes: 0 = z (toward the camera = high index), 1 = y (up = high index), 2 = x.
        right = lit(2, True)       # source at +x: the light passed through the higher x first
        left = lit(2, False)
        top = lit(1, True)
        bottom = lit(1, False)
        front = lit(0, True)
        back = lit(0, False)
        heat_vis = (w * heat).sum(0) / wsum.clamp(min=1e-5)
        heat_vis = torch.where(wsum > 1e-4, heat_vis, torch.zeros_like(heat_vis))

        def img(t):
            return t.flip(0)       # rows: top of the picture = high y

        out = torch.stack([img(right), img(left), img(top), img(alpha), img(bottom), img(back), img(front), img(heat_vis)])
        return (out.clamp(0, 1) * 255 + 0.5).byte().numpy()     # (8, H, W)


def paste(atlas_a, atlas_b, k, planes):
    col, row = k % TILES, k // TILES
    ys, xs = row * FRAME_PX, col * FRAME_PX
    h, w = planes.shape[1], planes.shape[2]
    oy, ox = (FRAME_PX - h) // 2, (FRAME_PX - w) // 2
    atlas_a[ys + oy: ys + oy + h, xs + ox: xs + ox + w, 0:3] = np.moveaxis(planes[0:3], 0, 2)
    atlas_a[ys + oy: ys + oy + h, xs + ox: xs + ox + w, 3] = planes[3]
    atlas_b[ys + oy: ys + oy + h, xs + ox: xs + ox + w, 0:3] = np.moveaxis(planes[4:7], 0, 2)
    atlas_b[ys + oy: ys + oy + h, xs + ox: xs + ox + w, 3] = planes[7]


def preview(atlas_a, atlas_b, out_path, sun=(0.5, 0.6, 0.62), picks=(4, 12, 24, 36, 48, 62)):
    """The six-way blend of the game's shader on a few frames, over a sky colour."""
    sun = np.array(sun) / np.linalg.norm(sun)
    w = sun * sun
    tiles = []
    for k in picks:
        col, row = k % TILES, k // TILES
        sl = (slice(row * FRAME_PX, (row + 1) * FRAME_PX), slice(col * FRAME_PX, (col + 1) * FRAME_PX))
        a = atlas_a[sl].astype(np.float32) / 255.0
        b = atlas_b[sl].astype(np.float32) / 255.0
        # sun[0] > 0: light from the right; sun[1] > 0: from the top; sun[2] > 0: from the front.
        lx = a[..., 0] if sun[0] > 0 else a[..., 1]
        ly = a[..., 2] if sun[1] > 0 else b[..., 0]
        lz = b[..., 2] if sun[2] > 0 else b[..., 1]
        lit = w[0] * lx + w[1] * ly + w[2] * lz
        avg = (a[..., 0] + a[..., 1] + a[..., 2] + b[..., 0] + b[..., 1] + b[..., 2]) / 6.0
        dust = np.array([0.66, 0.55, 0.42])
        body = dust[None, None, :] * (1.5 * lit + 0.55 * avg)[..., None]
        glow_g = np.clip((b[..., 3] - 0.3) * 2.2, 0, 1)
        glow = glow_g[..., None] * np.array([1.0, 0.55, 0.15])[None, None, :] * 1.6
        alpha = a[..., 3]
        rgb = body * alpha[..., None] + glow * alpha[..., None]
        sky = np.linspace(0, 1, FRAME_PX)[:, None, None]
        bg = (1 - sky) * np.array([0.55, 0.68, 0.9]) + sky * np.array([0.72, 0.74, 0.76])
        tiles.append(np.clip(bg * (1 - alpha[..., None]) + rgb, 0, 1))
    sheet = np.vstack([np.hstack(tiles[:3]), np.hstack(tiles[3:6])])
    Image.fromarray((sheet * 255).astype(np.uint8), "RGB").save(out_path)


def main():
    out = sys.argv[1]
    name = sys.argv[2] if len(sys.argv) > 2 else "blast"
    seed = int(sys.argv[3]) if len(sys.argv) > 3 else 3
    quick = len(sys.argv) > 4 and sys.argv[4] == "quick"

    if quick:
        sim = S.ground_burst(seed=seed, nx=80, ny=56, nz=64, dx=0.25)
        crop = (12, 68)
    else:
        sim = S.ground_burst(seed=seed, nx=160, ny=112, nz=128, dx=0.125)
        crop = (24, 136)
    noise = make_noise()
    baker = Baker(sim, crop, noise)
    atlas_a = np.zeros((TILES * FRAME_PX, TILES * FRAME_PX, 4), np.uint8)
    atlas_b = np.zeros_like(atlas_a)
    times = frame_times()
    t0 = time.time()

    def on_frame(k, s):
        planes = baker.render()
        paste(atlas_a, atlas_b, k, planes)
        print("frame %2d/%d at %.2f s (%.0f s elapsed)" % (k + 1, FRAMES, s.time, time.time() - t0), flush=True)

    def dt_of(t):
        if quick:
            return 0.03
        return 0.008 if t < 0.4 else (0.016 if t < 1.2 else 0.024)

    S.run_ground_burst(sim, seconds=DURATION + 0.01, dt=dt_of, callback=on_frame, frame_times=times)
    Image.fromarray(atlas_a, "RGBA").save("%s/%s_RLT.png" % (out, name))
    Image.fromarray(atlas_b, "RGBA").save("%s/%s_BBF.png" % (out, name))
    preview(atlas_a, atlas_b, "%s/%s_preview.png" % (out, name))
    print("done in %.0f s" % (time.time() - t0))


if __name__ == "__main__":
    main()
