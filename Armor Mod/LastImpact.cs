namespace ArmorFragments
{
    // The numbers of the most recent shell impact, shown in the in-game panel.
    internal static class LastImpact
    {
        public static bool HasData;
        public static string Kind = "-";
        public static float Angle;           // 0 = head-on, 90 = grazing
        public static float Tolerated;       // the armor ricochets from this angle up
        public static int RaysHit;           // rays that ended on a part
        public static int Ricochets;
        public static float Damage;
        public static float Time;            // UnityEngine.Time.time when it happened

        // Energy model, joules.
        public static float ShellEnergy;     // what the shell carried
        public static float Absorbed;        // what the armor took
        public static float Residual;        // what is left for the fragments

        // Fragment cloud of the last penetrating shot (zero when there was none).
        public static float SigmaX, SigmaY, AxisTilt;   // degrees
        public static float FragmentEnergy;             // joules, all fragments together
        public static float FragmentMass;               // kilograms, all fragments together
        public static float FragmentSpeed;              // m/s (rms)

        public static void ClearCloud()
        {
            SigmaX = SigmaY = AxisTilt = FragmentEnergy = FragmentMass = FragmentSpeed = 0f;
        }

        internal static void SetCloud(CloudSummary cloud)
        {
            SigmaX = cloud.SigmaX;
            SigmaY = cloud.SigmaY;
            AxisTilt = cloud.AxisTiltDegrees;
            FragmentEnergy = cloud.TotalEnergy;
            FragmentMass = cloud.TotalMass;
            FragmentSpeed = cloud.Speed;
        }

        public static void Record(string kind, float angle, float tolerated,
                                  int raysHit, int ricochets, float damage)
        {
            HasData = true;
            Kind = kind;
            Angle = angle;
            Tolerated = tolerated;
            RaysHit = raysHit;
            Ricochets = ricochets;
            Damage = damage;
            Time = UnityEngine.Time.time;
        }
    }
}
