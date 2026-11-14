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
    // How many, and how heavy: the casing's mass over a typical fragment mass (the setting, scaled with the
    // caliber: a bigger shell has a thicker casing, which breaks into bigger pieces), within the limits.
    // The pieces are not all alike: their masses vary (0.4 to 1.6 times the average) and, with the sideways
    // bias, more of the casing goes out sideways than along the shell's axis (a cylinder's walls throw most
    // of the metal sideways). The mass of every fragment sums to the casing's.
    //
    // Where: evenly over the sphere (stratified: each fragment takes an equal slice of it, spiralling, so a
    // few rays still cover it), around the shell's axis.
    //
    // Each fragment's diameter comes from its mass, as for the plates' fragments.
    internal static class DetonationCloud
    {
        private const float SteelDensityKgPerCm3 = 0.00785f;
        private const float GoldenAngle = 2.39996323f;
        private const int MinFragments = 3;
        private const float ReferenceCaliberMm = 75f;   // the caliber the typical fragment mass is quoted for
        private const float CaliberExponent = 2.33f;    // typical mass ~ caliber^(7/3): wall thickness ~ caliber

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

            // How many fragments.
            float caliberScale = Mathf.Pow(Mathf.Clamp(shell.CaliberMm, 5f, 500f) / ReferenceCaliberMm, CaliberExponent);
            float typicalMass = Mathf.Max(ArmorFragmentsPlugin.FragmentMass.Value / 1000f, 0.00005f) * caliberScale;
            int count = Mathf.Clamp(Mathf.RoundToInt(casing / typicalMass), MinFragments,
                                    Mathf.Max(MinFragments, ArmorFragmentsPlugin.MaxFragments.Value));

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
                // The i-th equal slice of the sphere: its height along the axis, then around it.
                float cosTheta = 1f - 2f * (i + 0.5f) / count;
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

                // More of the casing goes sideways (mean 1 over the sphere), and no two pieces are alike.
                float sideways = (1f - bias) + bias * 1.5f * sinTheta * sinTheta;
                massShare[i] = simple ? 1f : sideways * Random.Range(0.4f, 1.6f);
                massShareSum += massShare[i];
            }

            float totalEnergy = 0f;
            for (int i = 0; i < count; i++)
            {
                fragments[i].Mass = Mathf.Max(casing * massShare[i] / massShareSum, 0.000001f);
                fragments[i].Energy = 0.5f * fragments[i].Mass * fragments[i].Speed * fragments[i].Speed;
                fragments[i].AreaCm2 = simple
                    ? ArmorFragmentsPlugin.SimpleFragmentArea.Value
                    : Mathf.Pow(fragments[i].Mass / SteelDensityKgPerCm3, 2f / 3f);
                totalEnergy += fragments[i].Energy;
            }

            summary.TotalEnergy = totalEnergy;
            summary.TotalMass = casing;
            summary.Speed = Mathf.Sqrt(2f * totalEnergy / casing);
            return fragments;
        }
    }
}
