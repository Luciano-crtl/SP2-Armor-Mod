"""Makes the two "six-way lighting" lightmap textures of a smoke puff, plus a preview picture.

The puff is a made-up 3D cloud (layered noise). The camera looks at it along the Z axis. For each of the six
directions the light can come from (right, left, top, bottom, back, front) the script works out how much light
reaches every part of the cloud (light that crosses more smoke is dimmer) and flattens that into one picture.
The game's shader later picks and blends the six pictures from the real light direction.

  smoke_RLT.png : R = lit from the right, G = from the left, B = from the top, A = how opaque the puff is
  smoke_BBF.png : R = lit from the bottom, G = from the back, B = from the front, A = unused (0)
  smoke_preview.png : the puff lit from the upper right and a little from the front (just to look at)

Run: python make_smoke_maps.py <output folder> [seed]
"""
import sys
import numpy as np
from scipy import ndimage
from PIL import Image

SIZE = 256        # pixels of the picture
DEPTH = 96        # voxels along the view direction
EXTINCTION = 5.5  # how fast smoke blocks light (bigger = denser looking)


def noise3(shape, cells, rng):
    """Smooth random numbers in 0..1, `cells` random values across each axis."""
    grid = rng.random(cells)
    zoom = [shape[i] / (cells[i] - 1) for i in range(3)]
    big = ndimage.zoom(grid, zoom, order=3, mode="reflect")
    return big[: shape[0], : shape[1], : shape[2]]


def fbm(shape, rng, octaves=5, base=3):
    total = np.zeros(shape, dtype=np.float32)
    amp, norm = 1.0, 0.0
    for o in range(octaves):
        n = base * (2 ** o)
        total += amp * noise3(shape, (n + 1, n + 1, max(3, n // 2 + 2)), rng).astype(np.float32)
        norm += amp
        amp *= 0.5
    return total / norm


def build_density(rng):
    shape = (SIZE, SIZE, DEPTH)
    rows = np.linspace(-1, 1, SIZE, dtype=np.float32)[:, None, None]
    cols = np.linspace(-1, 1, SIZE, dtype=np.float32)[None, :, None]
    zs = np.linspace(-1, 1, DEPTH, dtype=np.float32)[None, None, :]
    # An uneven ball: the radius wobbles with the noise.
    warp = fbm(shape, rng, octaves=3, base=2)
    r = np.sqrt(cols ** 2 + rows ** 2 + (zs * 1.15) ** 2)
    r = r * (1.1 + 0.55 * (warp - 0.5))
    detail = fbm(shape, rng, octaves=5, base=4)
    d = (detail - 0.38) * 2.2 - (r ** 1.6) * 1.35 + 0.55
    d = np.clip(d, 0, 1)
    d = d ** 1.15
    # Nothing may touch the edge of the picture (the puff would show a straight cut there).
    edge = np.sqrt(cols ** 2 + rows ** 2)
    fade = np.clip((0.93 - edge) / 0.25, 0, 1)
    d = d * fade * fade * (3 - 2 * fade)
    return d.astype(np.float32)


def exclusive_cumsum(a, axis, reverse):
    """Sum of everything strictly on one side of each voxel along `axis`."""
    if reverse:
        a = np.flip(a, axis)
    c = np.cumsum(a, axis=axis) - a
    if reverse:
        c = np.flip(c, axis)
    return c


def bake(density):
    dz = 1.0 / DEPTH
    tau = density * EXTINCTION * dz * 2.0  # optical depth of each voxel

    # Soften the light paths a little: smoke scatters light, so shadows are never razor sharp.
    soft = ndimage.gaussian_filter(tau, sigma=(2.5, 2.5, 1.5))

    # What the camera sees: it looks along -Z from +Z, so "in front" means a higher z index.
    in_front = exclusive_cumsum(tau, 2, reverse=True)
    view_t = np.exp(-in_front)
    weight = density * view_t                       # how much each voxel shows in the picture
    weight_sum = weight.sum(axis=2)

    alpha = 1.0 - np.exp(-tau.sum(axis=2) * 1.0)

    def lit(axis, reverse):
        """Average light on the visible smoke when the light comes from one side."""
        depth = exclusive_cumsum(soft, axis, reverse)
        t = np.exp(-depth * 1.0)
        t = 0.12 + 0.88 * t                         # a little light always leaks in (multiple scattering)
        value = (weight * t).sum(axis=2)
        return np.where(weight_sum > 1e-4, value / np.maximum(weight_sum, 1e-4), 0.5)

    # Axis 0 = picture rows (row 0 is the top), axis 1 = picture columns, axis 2 = depth (high = toward the camera).
    right = lit(1, True)      # light source on the right: it passed through the higher columns first
    left = lit(1, False)
    top = lit(0, False)       # source above: it passed through the lower rows first
    bottom = lit(0, True)
    front = lit(2, True)      # source on the camera side
    back = lit(2, False)
    return alpha, right, left, top, bottom, back, front


def to_bytes(a):
    return np.clip(a * 255.0 + 0.5, 0, 255).astype(np.uint8)


def main():
    out = sys.argv[1]
    seed = int(sys.argv[2]) if len(sys.argv) > 2 else 7
    rng = np.random.default_rng(seed)

    density = build_density(rng)
    alpha, right, left, top, bottom, back, front = bake(density)

    rlt = np.stack([to_bytes(right), to_bytes(left), to_bytes(top), to_bytes(alpha)], axis=2)
    bbf = np.stack([to_bytes(bottom), to_bytes(back), to_bytes(front), np.zeros_like(to_bytes(alpha))], axis=2)
    Image.fromarray(rlt, "RGBA").save(out + "/smoke_RLT.png")
    Image.fromarray(bbf, "RGBA").save(out + "/smoke_BBF.png")

    # Preview: light from the upper right, a bit from the front, over a dark blue background.
    light = np.array([0.7, 0.55, 0.45])
    light = light / np.linalg.norm(light)
    w = light * light
    shade = w[0] * right + w[1] * top + w[2] * front
    ambient = (right + left + top + bottom + back + front) / 6.0
    color = 0.18 * ambient + 0.95 * shade
    grey = np.clip(color * 0.8, 0, 1)
    bg = np.array([0.10, 0.14, 0.22])
    img = bg[None, None, :] * (1 - alpha[..., None]) + grey[..., None] * alpha[..., None]
    Image.fromarray(to_bytes(img), "RGB").save(out + "/smoke_preview.png")
    border = max(alpha[0].max(), alpha[-1].max(), alpha[:, 0].max(), alpha[:, -1].max())
    print("max alpha %.2f, mean alpha %.3f, max alpha on the border %.4f" % (alpha.max(), alpha.mean(), border))


if __name__ == "__main__":
    main()
