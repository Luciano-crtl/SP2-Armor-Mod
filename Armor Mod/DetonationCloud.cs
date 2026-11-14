using UnityEngine;

namespace ArmorFragments
{
    // The fragments of an explosive shell that bursts: its casing, thrown off in every direction.
    //
    // How much and how fast: the filler is a share of the shell's mass, the casing is the rest. The speed the
    // explosion gives the casing (Gurney): v = G x sqrt(c/m / (1 + c/(2m))), with c the filler's mass, m the
    // casing's and G the explosive's Gurney velocity. Each fragment flies with that speed outward PLUS the
    // shell's own velocity, added as vectors, so it carries the kinetic energy the shell already had and the
    // explosion's: the fragments that go forward are the fastest, those that go back the slowest.
    //
    // How many, and how heavy: the casing's mass over the typical mass of one fragment, which is
    //   the setting (FragmentMass, for a 75 mm shell)
    //   x the caliber's scale (a bigger shell has a thicker casing, which breaks into bigger pieces)
    //   x the casing's brittleness (a brittle casing breaks into smaller pieces: 2x down to 0.25x)
    //   x the explosion's strength (a stronger burst breaks it up more: the reference is an outward speed of 800 m/s).
    // There is no limit to that number. The rays are limited (MaxVisualFragments); when there are more fragments
    // than rays, each ray stands for a bundle of them (see FragmentDistribution).
    // The pieces are not all alike: the mass of a ray varies at random (log-normal: many small, a few big) and
    // the masses add up to the casing's.
    //
    // Where: over the sphere around the shell's axis, one slice of it per ray (stratified, spiralling, so a few
    // rays still cover it). With the sideways bias more of them go sideways than along the axis (a cylinder's
    // walls throw most of the metal sideways): the density of directions follows (1-b) + b x 1.5 x sin^2(theta).
    //
    // Each fragment's cross-section comes from its mass, as for the plates' fragments.
    internal static class DetonationCloud
    {
        private const float SteelDensityKgPerCm3 = 0.00785f;
        private const float GoldenAngle = 2.39996323f;
        private const float ReferenceCaliberMm = 75f;   // the caliber the typical fragment mass is quoted for
        private const float CaliberExponent = 2.33f;    // typical mass ~ caliber^(7/3): wall thickness ~ caliber
        private const float ReferenceBurstSpeed = 800f; // outward speed (m/s) at which the burst does not change the size

        // forward: the shell's direction of flight when it bursts.
        public static FragmentSpec[] Generate(ShellInfo shell, Vector3 forward, out CloudSummary summary)
        {
            summary = new CloudSummary();

            float shellMass = Mathf.Max(shell.Mass, 0.000001f);
            float filler = Mathf.Clamp(shell.FillerFraction, 0f, 0.5f) * shellMass;
            float casing = Mathf.Max(shellMass - filler, 0.000001f);

            // Speed the explosion gives the casing (Gurney, for a cylinder).
            float ratio = filler / casing;
            float outward = ArmorFragmentsPlugin.GurneySpeed.Value * Mathf.Sqrt(ratio / (1f + 0.5f * ratio));
            float shellSpeed = Mathf.Max(shell.Speed, 0f);

            // How many fragments there are, and how many rays show them.
            float caliberScale = Mathf.Pow(Mathf.Clamp(shell.CaliberMm, 5f, 500f) / ReferenceCaliberMm, CaliberExponent);
            float brittleFactor = Mathf.Lerp(2f, 0.25f, Mathf.Clamp01(shell.Brittleness));
            float burstFactor = Mathf.Clamp(ReferenceBurstSpeed / Mathf.Max(outward, 1f), 0.5f, 2f);
            float typicalMass = Mathf.Max(ArmorFragmentsPlugin.FragmentMass.Value / 1000f, 0.00005f)
                                * caliberScale * brittleFactor * burstFactor;

            int count;
            float multiplicity;
            int realCount;
            FragmentDistribution.Plan(casing / typicalMass, out count, out multiplicity, out realCount);

            // The frame around the shell's axis.
            Vector3 axis = forward.sqrMagnitude > 0.000001f ? forward.normalized : Vector3.forward;
            Vector3 xAxis = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 yAxis = Vector3.Cross(axis, xAxis);

            bool simple = ArmorFragmentsPlugin.SimpleFragments.Value;
            float bias = Mathf.Clamp01(ArmorFragmentsPlugin.DetonationSidewaysBias.Value);

            FragmentSpec[] fragments = new FragmentSpec[count];
            float[] massShare = new float[count];
            float massShareSum = 0f;
            float startAngle = Random.value * 2f * Mathf.PI;

            for (int i = 0; i < count; i++)
            {
                // The i-th equal slice of the sphere (of the directions' density): its height along the axis,
                // then around it.
                float cosTheta = HeightOfSlice((i + 0.5f) / count, bias);
                float sinTheta = Mathf.Sqrt(Mathf.Max(0f, 1f - cosTheta * cosTheta));
                float phi = startAngle + i * GoldenAngle;
                Vector3 outwardDirection =
                    axis * cosTheta + (xAxis * Mathf.Cos(phi) + yAxis * Mathf.Sin(phi)) * sinTheta;

                // The fragment's velocity: the shell's plus the explosion's.
                Vector3 velocity = axis * shellSpeed + outwardDirection * outward;
                float speed = velocity.magnitude;
                fragments[i].Direction = speed > 0.01f ? velocity / speed : outwardDirection;
                fragments[i].Speed = speed;
                fragments[i].Weight = 1f;
                fragments[i].Multiplicity = multiplicity;

                massShare[i] = simple ? 1f : FragmentDistribution.MassFactor();
                massShareSum += massShare[i];
            }

            float totalEnergy = 0f;
            for (int i = 0; i < count; i++)
            {
                // The ray's share of the casing, split among the fragments it stands for.
                fragments[i].Mass = Mathf.Max(casing * massShare[i] / massShareSum / multiplicity, 0.000001f);
                fragments[i].Energy = 0.5f * fragments[i].Mass * fragments[i].Speed * fragments[i].Speed;
                fragments[i].AreaCm2 = simple
                    ? ArmorFragmentsPlugin.SimpleFragmentArea.Value
                    : Mathf.Pow(fragments[i].Mass / SteelDensityKgPerCm3, 2f / 3f);
                totalEnergy += fragments[i].Energy * multiplicity;
            }

            summary.TotalEnergy = totalEnergy;
            summary.TotalMass = casing;
            summary.Speed = Mathf.Sqrt(2f * totalEnergy / casing);
            summary.Count = realCount;
            summary.Rays = count;
            summary.Multiplicity = multiplicity;
            return fragments;
        }

        // The height along the axis (cos of the angle from it, -1..1) that cuts off the share 'u' (0..1) of the
        // directions. Directions are spread by the density (1-b) + 1.5 b (1 - c^2), which is flat for b = 0 and
        // thickest around the equator for b = 1. The share up to height c is
        //     F(c) = [ (1-b)(c+1) + 1.5 b ((c+1) - (c^3+1)/3) ] / 2
        // which only goes up, so the height is found by halving the range.
        private static float HeightOfSlice(float u, float bias)
        {
            float low = -1f;
            float high = 1f;
            for (int step = 0; step < 24; step++)
            {
                float mid = 0.5f * (low + high);
                float share = ((1f - bias) * (mid + 1f) +
                               1.5f * bias * ((mid + 1f) - (mid * mid * mid + 1f) / 3f)) * 0.5f;
                if (share < u) low = mid; else high = mid;
            }
            return 0.5f * (low + high);
        }
    }
}
