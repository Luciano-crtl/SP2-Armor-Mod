using UnityEngine;

namespace ArmorFragments
{
    // What the clouds of fragments (a plate's spall, a shell's casing) have in common: how many fragments there
    // really are, how many rays show them, and how their masses are spread.
    //
    // The count of real fragments has no limit of its own: it is the cloud's mass over the typical mass of one
    // fragment. Only the number of RAYS is limited (MaxVisualFragments: detail and cost). When there are more
    // fragments than rays, each ray stands for a bundle of N of them (N = fragments / rays): the penetration is
    // worked out for ONE real fragment (its mass, area and energy), and only the energy and damage left in parts
    // are multiplied by N. So the limit changes how much detail there is, never how far a fragment goes.
    internal static class FragmentDistribution
    {
        public const int MinRays = 3;                 // fewest rays that still show a cloud
        private const float MassSpread = 0.6f;        // sigma of the log-normal spread of fragment masses

        // From the number of real fragments a cloud would have (a fraction is fine) to the rays that show them.
        //   rays:         how many rays are traced and drawn
        //   multiplicity: real fragments each ray stands for (1 = one fragment per ray)
        //   total:        the number of real fragments
        public static void Plan(float realFragments, out int rays, out float multiplicity, out int total)
        {
            total = Mathf.Max(MinRays, Mathf.RoundToInt(Mathf.Clamp(realFragments, 0f, 10000000f)));
            int most = Mathf.Max(MinRays, ArmorFragmentsPlugin.MaxVisualFragments.Value);
            rays = Mathf.Min(total, most);
            multiplicity = Mathf.Max(1f, (float)total / rays);
        }

        // A random factor for the mass of one fragment, with mean 1. Log-normal: many small fragments and a few
        // big ones, as a casing really breaks. The masses of a cloud are scaled afterwards to add up to its mass.
        public static float MassFactor()
        {
            float u1 = Mathf.Max(Random.value, 0.0001f);
            float u2 = Random.value;
            float gauss = Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
            return Mathf.Clamp(Mathf.Exp(MassSpread * gauss - 0.5f * MassSpread * MassSpread), 0.1f, 5f);
        }
    }
}
