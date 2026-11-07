using UnityEngine;

namespace ArmorFragments
{
    // What a shell is, as far as the energy model cares. Units: mass in the game's own units
    // (a Rigidbody mass), speed in m/s, caliber in mm.
    internal struct ShellInfo
    {
        public float Mass;
        public float Speed;
        public float CaliberMm;

        // Kinetic energy: 1/2 m v^2.
        public float Energy
        {
            get { return 0.5f * Mass * Speed * Speed; }
        }

        // Cross-section of the shell in cm^2 (a circle of the caliber's diameter).
        public float AreaCm2
        {
            get
            {
                float radiusCm = CaliberMm * 0.1f * 0.5f;
                return Mathf.PI * radiusCm * radiusCm;
            }
        }
    }

    internal static class EnergyModel
    {
        // Energy (joules) the armor takes from the shell, which opens a plug of its own cross-section:
        //     resistance x area of the shell x thickness the shell has to cross
        // The thickness it crosses grows as the hit gets more slanted: t / cos(angle).
        // angle: 0 = head-on, 90 = grazing. The cosine is kept above 0.1 so it never divides by zero.
        public static float ArmorAbsorption(ArmorTag armor, ShellInfo shell, float angleDegrees,
                                            out float effectiveThicknessMm)
        {
            float slant = Mathf.Max(Mathf.Cos(angleDegrees * Mathf.Deg2Rad), 0.1f);
            effectiveThicknessMm = armor.Thickness / slant;

            // ArmorResistance is in joules per mm of thickness per cm^2 of shell area.
            return ArmorFragmentsPlugin.ArmorResistance.Value * effectiveThicknessMm * shell.AreaCm2;
        }

        public static string Kilojoules(float joules)
        {
            return (joules / 1000f).ToString("F1") + " kJ";
        }
    }
}
