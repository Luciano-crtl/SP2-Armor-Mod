using System;

namespace ArmorFragments
{
    // The shape of a shell's nose, as a profile that the impact simulation and the marks are made from. It is a procedural description (two
    // numbers), not a mesh: any projectile shape the game or a player may come up with later only has to be turned into these numbers, or the
    // profile may be given as a formula of its own (see RadiusAt: the one place that says how wide the shell is at each distance behind its tip).
    //
    //   radius(a) = r * (1 - ((L - a) / L) ^ p) ^ (1 / p)     for a between the tip (0) and the end of the nose (L), then r
    //
    //   L = NoseLength * r:  how long the nose is, in shell radii
    //   p = Fullness:        how blunt it is. p = 1 is a cone, p = 2 an ellipse (a hemisphere if L = r), more than 2 is a flat-faced nose
    //                        with rounded corners.
    //
    // Only the way the plate is PUSHED ASIDE depends on it (the shape of a crater, how a hole tears). What the shell has to pay for the metal
    // it goes through is the volume of its full caliber and does not depend on the nose, so the energy model and the simulation always agree
    // about whether a shell gets through.
    internal struct ShellProfile
    {
        public float NoseLength;   // in shell radii; 0 = the old hemisphere-ended capsule (kept as it was)
        public float Fullness;     // the exponent p

        public bool IsSet
        {
            get { return NoseLength > 0f; }
        }

        // An armor-piercing shell: a long ogive, pointed and round at the same time.
        public static ShellProfile Ogive
        {
            get { return new ShellProfile { NoseLength = 1.4f, Fullness = 1.95f }; }
        }

        // An armor-piercing shell with a filler (APHE): a shorter, fuller nose (the cavity takes the room of the sharpness).
        public static ShellProfile Capped
        {
            get { return new ShellProfile { NoseLength = 1.1f, Fullness = 2.2f }; }
        }

        // A high-explosive shell: short and nearly flat in front.
        public static ShellProfile Blunt
        {
            get { return new ShellProfile { NoseLength = 0.55f, Fullness = 3.2f }; }
        }

        // Any other nose.
        public static ShellProfile Procedural(float noseLengthInRadii, float fullness)
        {
            return new ShellProfile { NoseLength = Math.Max(noseLengthInRadii, 0.05f), Fullness = Math.Max(fullness, 1f) };
        }

        // 0 = sharp, 1 = flat-faced. (The old hemisphere counts as 0.25.)
        public float Bluntness
        {
            get
            {
                float p = IsSet ? Fullness : 2f;
                float shortness = IsSet ? Math.Min(Math.Max((1.7f - NoseLength) / 1.2f, 0f), 1f) : 0.3f;
                return Math.Min(Math.Max(0.6f * Math.Min(Math.Max((p - 1.5f) / 2f, 0f), 1f) + 0.4f * shortness, 0f), 1f);
            }
        }

        // The shell's radius at 'a' meters behind its tip, for a shell of radius 'r' (the one definition of the profile; the simulation's
        // loops and the graphics card's kernel write the same formula out for speed).
        public float RadiusAt(float a, float r)
        {
            float length = NoseLength * r;
            if (!IsSet || a >= length) return r;
            if (a <= 0f) return 0f;
            float u = (length - a) / length;
            float inner = Math.Max(1f - (float)Math.Pow(u, Fullness), 1e-6f);
            return r * (float)Math.Pow(inner, 1f / Fullness);
        }
    }
}
