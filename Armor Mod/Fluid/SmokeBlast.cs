// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using UnityEngine;

namespace ArmorFragments.Fluid
{
    // The sources of one cannon shot, as the balls of gas, heat, dust and speed that FluidSim.AddBlobs puts into the box: the fireball, the
    // propellant smoke, what leaves through a muzzle brake and out of the breech, and the dust the blast lifts from the ground. It knows nothing
    // of the game (the test project uses this same file), only the box's own space: x along the barrel, y up, z across.
    internal static class SmokeBlast
    {
        public static float Focus = 0.7f;           // 0 = the old round fireball, 1 = a narrow jet that shoots along the barrel
        public static float MuzzleBrake = 0.3f;     // 0..1: how much of the gas leaves sideways, as through a muzzle brake
        public static float BlastDust = 1f;         // multiplier of the dust the blast lifts
        public static float JetSpeed = 0.65f;       // multiplier of how fast the fireball and the smoke are thrown forward (a long tube of smoke comes of too much)
        public static float Density = 0.65f;        // multiplier of the smoke and the heat of every shot (the smoke of a smokeless powder is thin)
        public static float ShockWave = 1f;         // 0..2: the ring of dust that runs out over the ground and the quick push of the air around the barrel
        public static float ResidualHeat = 0.3f;    // 0..0.35: the temperature of the propellant smoke (what makes it rise; above 0.37 it also glows)

        // What the blast sees of the ground, all in the box's space.
        public sealed class Ground
        {
            public bool present;
            public Vector3 normal = Vector3.up;     // the ground's up
            public float height;                    // the muzzle's height above it (m)
            public Vector3 foot;                    // the point of the ground under the muzzle
            public int count;                       // dust spots along the line of fire
            public float dustScale = 1f;            // how much dust this kind of ground lifts (less when it is not known to be loose earth)
            public readonly Vector3[] points = new Vector3[8];
            public readonly float[] amounts = new float[8];
        }

        // Adds to `list` the sources of a shot `t` seconds after it was fired. `s` is the size of the blast (calibre / 75 mm), `T` its time scale,
        // `muzzle` the end of the barrel. `amount` (0..1.4) is how much smoke, heat and dust this shot makes compared with a 75 mm gun's (a
        // small gun's puff is thin and cool), `push` how hard its jets push the air (less for each shot of a burst).
        public static void Build(List<FluidSim.Blob> list, float t, float s, float T, Vector3 muzzle, Ground ground, float amount = 1f, float push = 1f, float shock = 1f, float brakeScale = 1f)
        {
            float focus = Mathf.Clamp01(Focus), brake = Mathf.Clamp01(MuzzleBrake * brakeScale);
            float fire = 0.12f * T, smoke = 1.2f * T, dust = 0.35f * T;
            float amountAll = amount;                  // the dust keeps its own scale
            amount *= Density;

            // The fireball: a chain of balls along the barrel. Focused, the balls are narrower, fly faster and push the air forward more than
            // outward, so the front of the jet rolls up into a vortex instead of a sphere that blows up evenly.
            if (t < fire)
            {
                float fade = 1f - t / fire;
                int links = 4 + Mathf.RoundToInt(2f * focus);
                for (int k = 0; k < links; k++)
                {
                    list.Add(new FluidSim.Blob
                    {
                        center = muzzle + new Vector3((0.7f + 0.9f * k) * s, 0, 0),
                        radius = Mathf.Lerp(0.9f + 0.25f * k, 0.75f + 0.3f * k, focus) * s,
                        rho = 0.9f * amount, temp = Mathf.Lerp(1f, 0.6f + 0.08f * k, focus),      // focused: the tail is cooler, the head is hot
                        vel = new Vector3(Mathf.Lerp(14f, 20f, focus) * JetSpeed * T * fade, 0, 0) * push,
                        radial = Mathf.Lerp(10f, 5f, focus) * T * fade * push,
                        noise = 0.15f, falloff = 1.3f,
                        swirl = Mathf.Lerp(6f, 4f, focus) * T * fade, swirlFreq = 0.9f / s,
                    });
                }

                // The muzzle brake: two plumes, left and right, a little behind the end of the barrel, thrown sideways.
                if (brake > 0.01f)
                {
                    for (int side = -1; side <= 1; side += 2)
                    {
                        list.Add(new FluidSim.Blob
                        {
                            center = muzzle + new Vector3(-0.1f * s, 0, side * 0.4f * s),
                            radius = 0.55f * s,
                            rho = 0.9f * brake * amount, temp = 0.9f,
                            vel = new Vector3(-3f * T * fade, 0, side * 20f * T * fade * brake) * push,
                            radial = 2f * T * fade,
                            noise = 0.2f, falloff = 1.2f,
                            swirl = 3f * T * fade, swirlFreq = 1.1f / s,
                        });
                    }
                }
            }

            // The pressure wave in the air: for a moment a ring around the barrel, at the muzzle, pushes the air outward (no smoke in it: only
            // the speed), so the fireball and the smoke flare out sideways.
            float shockWave = ShockWave * shock;
            if (shockWave > 0.01f && t < 0.07f * T)
            {
                float fade = 1f - t / (0.07f * T);
                for (int k = 0; k < 8; k++)
                {
                    float angle = k / 8f * 2f * Mathf.PI;
                    Vector3 around = new Vector3(0f, Mathf.Cos(angle), Mathf.Sin(angle));
                    list.Add(new FluidSim.Blob
                    {
                        center = muzzle + new Vector3(0.5f * s, 0f, 0f) + around * (1.1f * s),
                        radius = 0.6f * s,
                        vel = (new Vector3(3f * T * fade, 0f, 0f) + around * (16f * T * fade * shockWave)) * push,
                        falloff = 1.0f,
                    });
                }
            }

            // The propellant smoke keeps coming out for a while: dense, so that it stays opaque as it spreads.
            if (t < smoke)
            {
                float fade = 1f - t / smoke;
                list.Add(new FluidSim.Blob
                {
                    center = muzzle + new Vector3(0.9f * s, 0, 0),
                    radius = Mathf.Lerp(1.3f, 1.0f, focus) * s,
                    rho = 1.7f * fade * amount, temp = ResidualHeat * fade * amount,
                    vel = new Vector3(Mathf.Lerp(4f, 8f, focus) * JetSpeed * T * fade, 0, 0) * push,
                    radial = 1.0f * T, noise = 0.15f, falloff = 1.1f,
                    swirl = 3f * T * fade, swirlFreq = 0.7f / s,
                });
                // What comes back out around the breech and the base of the barrel, toward the vehicle.
                list.Add(new FluidSim.Blob
                {
                    center = muzzle + new Vector3(-0.5f * s, 0, 0),
                    radius = 0.8f * s,
                    rho = 0.7f * fade * amount, temp = 0.8f * ResidualHeat * fade * amount,
                    vel = new Vector3(-4f * T * fade, 0, 0) * push,
                    radial = 1.5f * T, noise = 0.2f, falloff = 1.1f,
                    swirl = 2f * T * fade, swirlFreq = 0.8f / s,
                });
            }

            // The dust the blast lifts: spots on the ground along the line of fire, and a fan of dust that runs outward from under the muzzle
            // like the pressure wave, stronger the lower the barrel is.
            if (ground != null && ground.present && t < dust * 1.3f && BlastDust > 0.01f)
            {
                float fade = 1f - t / (dust * 1.3f);
                Vector3 up = ground.normal;
                Vector3 along = Vector3.ProjectOnPlane(Vector3.right, up);
                if (along.sqrMagnitude < 1e-4f) along = Vector3.ProjectOnPlane(Vector3.forward, up);
                along.Normalize();
                Vector3 across = Vector3.Cross(up, along);

                for (int i = 0; i < ground.count; i++)
                {
                    list.Add(new FluidSim.Blob
                    {
                        center = ground.points[i] + up * (0.3f * s),
                        radius = (1.5f + 0.35f * i) * s,
                        rho = 0f, temp = 0.05f, dust = Mathf.Min(ground.amounts[i] * BlastDust * ground.dustScale, 1.2f) * amountAll,
                        vel = (up * 3f + along * 2f) * T * fade,
                        radial = 7f * T * fade, noise = 0.25f, falloff = 1.1f,
                        swirl = 5f * T * fade, swirlFreq = 0.9f / s,
                    });
                }

                float low = Mathf.Clamp01(1f - ground.height / (5f * s));
                if (low > 0.02f && shockWave > 0.01f)
                {
                    // The pressure wave runs out over the ground in a ring all around the muzzle, thicker in front of the barrel.
                    float travel = Mathf.Min(10f * s, 12f * T * t + 0.8f * s);       // how far the front of the ring has run
                    const int arc = 16;
                    for (int j = 0; j < arc; j++)
                    {
                        float angle = (j / (float)arc) * 2f * Mathf.PI;
                        Vector3 direction = along * Mathf.Cos(angle) + across * Mathf.Sin(angle);
                        list.Add(new FluidSim.Blob
                        {
                            center = ground.foot + direction * travel + up * (0.25f * s),
                            radius = 0.9f * s + 0.1f * travel,
                            rho = 0f, temp = 0.03f, dust = 0.8f * low * BlastDust * ground.dustScale * shockWave * amountAll * (0.55f + 0.45f * Mathf.Max(0f, Mathf.Cos(angle))),
                            vel = direction * (6f * T * fade) + up * (1.5f * T * fade),
                            radial = 0f, noise = 0.3f, falloff = 1.2f,
                            swirl = 2f * T * fade, swirlFreq = 0.8f / s,
                        });
                    }
                }
            }
        }
    }
}
// RELEASE-STRIP-END
