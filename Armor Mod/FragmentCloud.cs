using UnityEngine;

namespace ArmorFragments
{
    // One fragment of a penetrating shell: where it goes and what it carries.
    internal struct FragmentSpec
    {
        public Vector3 Direction;
        public float Energy;     // joules
        public float Mass;       // kilograms
        public float Speed;      // m/s, from energy and mass
        public float AreaCm2;    // cross-section (used when fragments start crossing parts)
        public float Weight;     // share of the directional distribution, 1 = average
    }

    // What the panel shows about the cloud of the last impact.
    internal struct CloudSummary
    {
        public float SigmaX;          // degrees, along the plane of incidence
        public float SigmaY;          // degrees, across it
        public float AxisTiltDegrees; // how far the cone's axis leans from the shell's path
        public float TotalEnergy;
        public float TotalMass;
        public float Speed;           // rms speed of the whole cloud
    }

    // Where the fragments of a penetrating shell go, and how much energy and mass they carry.
    //
    // Direction: not a flat cone but a probability distribution. Each fragment's direction is a
    // 2D gaussian offset around an axis:
    //   - the axis starts on the shell's path and leans toward the plate's normal on slanted hits
    //     (the shell "straightens up" going through), more so for harder plates;
    //   - the spread is wider for brittle and thick plates and narrower when much energy is left
    //     over (fast fragments leave more forward);
    //   - on slanted hits it is wider along the plane of incidence (an ellipse).
    // The fragments are not drawn at random but by stratified sampling (one per equal slice of the
    // distribution, spiralling around), so a few fragments still cover the cloud evenly.
    //
    // Energy: a fraction of the energy left over after the armor (the plate's brittleness) is shared
    // among the fragments, the central ones taking more. Mass: the plate's own spalled material
    // (brittleness x the plug the shell punched out) plus a share of the shell itself.
    internal static class FragmentCloud
    {
        private const float SteelDensityKgPerCm3 = 0.00785f;
        private const float ShellShareOfMass = 0.2f;     // part of the shell's own mass that breaks up
        private const float GoldenAngle = 2.39996323f;   // radians: spreads the fragments around the axis

        public static FragmentSpec[] Generate(int count, ArmorTag armor, ShellInfo shell, Vector3 direction,
                                              Vector3 normal, float angleDegrees, float residualEnergy,
                                              float absorbedEnergy, float effectiveThicknessMm,
                                              bool simple, out CloudSummary summary)
        {
            summary = new CloudSummary();

            float brittle = Mathf.Clamp01(armor.Brittleness / 100f);
            float fragmentEnergy = brittle * residualEnergy;
            if (count <= 0 || fragmentEnergy < 1f) return new FragmentSpec[0];   // a plate that does not shatter

            // ---- Axis and plane of incidence --------------------------------------------------
            Vector3 d = direction.normalized;
            Vector3 n = Vector3.Dot(normal, d) > 0f ? -normal : normal;   // plate normal facing the shell
            float relativeHardness = Mathf.Clamp(armor.Hardness / Mathf.Max(shell.Hardness, 1f), 0.5f, 2f);
            float tilt = Mathf.Clamp01(ArmorFragmentsPlugin.AxisTilt.Value * (angleDegrees / 90f) * relativeHardness);
            Vector3 axis = Vector3.Slerp(d, -n, tilt).normalized;

            Vector3 yAxis = Vector3.Cross(d, n);                // across the plane of incidence
            if (yAxis.sqrMagnitude < 0.000001f) yAxis = Vector3.Cross(axis, Vector3.up);
            if (yAxis.sqrMagnitude < 0.000001f) yAxis = Vector3.Cross(axis, Vector3.right);
            yAxis = (yAxis - axis * Vector3.Dot(yAxis, axis)).normalized;
            Vector3 xAxis = Vector3.Cross(yAxis, axis);         // along the plane of incidence

            // ---- Spread (degrees) -------------------------------------------------------------
            float widthFactor = 0.5f + brittle;                                     // 0.5 .. 1.5
            float thicknessFactor = 1f + 0.3f * Mathf.Clamp01(armor.Thickness / 100f);
            float surplus = residualEnergy / Mathf.Max(absorbedEnergy, 1f);
            float energyFactor = 1f / (1f + 0.15f * Mathf.Log(1f + surplus));       // 1 .. smaller
            float sigma = Mathf.Clamp(
                ArmorFragmentsPlugin.SpreadBaseAngle.Value * widthFactor * thicknessFactor * energyFactor, 2f, 45f);
            float anisotropy = 1f + (ArmorFragmentsPlugin.SpreadAnisotropy.Value - 1f) * (angleDegrees / 90f);
            float sigmaX = Mathf.Min(sigma * anisotropy, 60f);
            float sigmaY = sigma;

            // ---- Mass of the cloud ------------------------------------------------------------
            float plugMass = SteelDensityKgPerCm3 * shell.AreaCm2 * (effectiveThicknessMm * 0.1f);
            float totalMass = brittle * plugMass + ShellShareOfMass * shell.Mass;
            totalMass = Mathf.Max(totalMass, 0.0001f);

            // ---- The fragments ----------------------------------------------------------------
            FragmentSpec[] fragments = new FragmentSpec[count];
            float[] massShare = new float[count];
            float weightSum = 0f;
            float massShareSum = 0f;
            float startAngle = Random.value * 2f * Mathf.PI;

            for (int i = 0; i < count; i++)
            {
                // Stratified: fragment i takes the i-th slice of the radial distribution.
                float jitter = (Random.value - 0.5f) * 0.6f;
                float u = Mathf.Clamp((i + 0.5f + jitter) / count, 0.001f, 0.98f);
                float radius = Mathf.Sqrt(-2f * Mathf.Log(1f - u));      // in sigmas
                float phi = startAngle + i * GoldenAngle;

                float xDegrees = radius * Mathf.Cos(phi) * sigmaX;
                float yDegrees = radius * Mathf.Sin(phi) * sigmaY;
                fragments[i].Direction = Deflect(axis, xAxis, yAxis, xDegrees, yDegrees);

                // The distribution's own density: central fragments weigh more.
                fragments[i].Weight = simple ? 1f : Mathf.Exp(-0.5f * radius * radius);
                weightSum += fragments[i].Weight;

                massShare[i] = simple ? 1f : 0.6f + 0.8f * Random.value;
                massShareSum += massShare[i];
            }

            for (int i = 0; i < count; i++)
            {
                fragments[i].Energy = fragmentEnergy * fragments[i].Weight / weightSum;
                fragments[i].Mass = Mathf.Max(totalMass * massShare[i] / massShareSum, 0.000001f);
                fragments[i].Speed = Mathf.Sqrt(2f * fragments[i].Energy / fragments[i].Mass);
                fragments[i].AreaCm2 = simple
                    ? ArmorFragmentsPlugin.SimpleFragmentArea.Value
                    : Mathf.Pow(fragments[i].Mass / SteelDensityKgPerCm3, 2f / 3f);
                fragments[i].Weight = fragments[i].Weight * count / weightSum;   // mean 1, used to scale damage
            }

            summary.SigmaX = sigmaX;
            summary.SigmaY = sigmaY;
            summary.AxisTiltDegrees = Vector3.Angle(d, axis);
            summary.TotalEnergy = fragmentEnergy;
            summary.TotalMass = totalMass;
            summary.Speed = Mathf.Sqrt(2f * fragmentEnergy / totalMass);
            return fragments;
        }

        // The axis turned by the angle (in degrees) of the offset (x along xAxis, y along yAxis).
        private static Vector3 Deflect(Vector3 axis, Vector3 xAxis, Vector3 yAxis, float xDegrees, float yDegrees)
        {
            Vector3 offset = xAxis * xDegrees + yAxis * yDegrees;
            float angle = Mathf.Min(offset.magnitude, 85f);
            if (angle < 0.0001f) return axis;

            Vector3 turnAround = Vector3.Cross(axis, offset.normalized);
            return (Quaternion.AngleAxis(angle, turnAround) * axis).normalized;
        }
    }
}
