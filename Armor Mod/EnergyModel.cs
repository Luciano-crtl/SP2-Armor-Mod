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
        public float Hardness;   // percent, 100 = reference (from the cannon's [CAN k=] or the defaults)

        // Explosive ammunition (the game's 'Explosive' projectile type) breaks up on the plate, as it
        // always has. Anything else is armor-piercing: it goes through the plate and flies on.
        public bool Explosive;

        // Explosive ammunition: a fuse (APHE) counts down a distance of flight from the first impact; without
        // one (HE) the shell bursts at the first touch. The filler is its share of the shell's mass.
        public bool HasFuse;
        public float FuseLeft;        // meters of flight left before it bursts (only if HasFuse)
        public float FillerFraction;  // 0..1
        public float Brittleness;     // 0..1: how easily the casing breaks into small fragments

        public AmmoKind Kind
        {
            get { return !Explosive ? AmmoKind.AP : (HasFuse ? AmmoKind.APHE : AmmoKind.HE); }
        }

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
        // Fixed parts of the ricochet model (the player-facing knobs are in the settings).
        private const float ReferenceSpeed = 800f;     // m/s at which the speed term is zero
        private const float HardnessStepDegrees = 12f; // critical angle drops by this per doubling of armor/shell hardness
        private const float SpeedStepDegrees = 5f;     // critical angle rises by this per doubling of speed
        private const float OvermatchLimit = 3f;       // caliber / thickness at which ricochet becomes impossible
        // A flying piece is considered stopped when its energy falls below this fraction of what it started with.
        public const float StopFraction = 0.005f;

        // Mass (kg) of a shell of this caliber, worked out the way the game does it for a real cannon:
        // the standard projectile is a cylinder of radius r (half the caliber) and length r, made of lead
        // (11342 kg/m3). The game then stores it at 1/100, which the rest of the mod undoes.
        public static float ShellMassForCaliber(float caliberMm)
        {
            float radius = Mathf.Max(caliberMm, 0f) * 0.0005f;   // mm -> m, halved
            return Mathf.PI * radius * radius * radius * 11342f;
        }

        private const float JitterMaxDegrees = 10f;    // size of the random shift at 100% randomness

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

        // The angle (0 = head-on, 90 = grazing) from which this shell ricochets off this armor.
        // Above 90 means it cannot ricochet at all (overmatch).
        //  - hardness: the harder the armor is compared with the shell, the sooner it deflects it;
        //  - speed: a faster shell digs in and ricochets later;
        //  - overmatch: a shell much bigger than the plate is thick cuts it instead of bouncing;
        //  - then the global multiplier, and a small random shift (0..100% randomness).
        public static float RicochetCriticalAngle(ArmorTag armor, ShellInfo shell, out string breakdown)
        {
            float reference = ArmorFragmentsPlugin.ArmorRicochetAngle.Value;

            float relativeHardness = Mathf.Max(armor.Hardness, 1f) / Mathf.Max(shell.Hardness, 1f);
            float hardnessTerm = -HardnessStepDegrees * Mathf.Log(relativeHardness, 2f);

            float speedTerm = SpeedStepDegrees * Mathf.Log(Mathf.Max(shell.Speed, 1f) / ReferenceSpeed, 2f);

            float angle = Mathf.Clamp(reference + hardnessTerm + speedTerm, 10f, 85f);

            float ratio = shell.CaliberMm / Mathf.Max(armor.Thickness, 0.1f);
            float overmatch = Mathf.Clamp01((ratio - 1f) / (OvermatchLimit - 1f));
            if (overmatch >= 1f)
            {
                breakdown = "no ricochet: caliber " + shell.CaliberMm.ToString("F0") + " mm is " +
                            ratio.ToString("F1") + "x the armor thickness (overmatch)";
                return 91f;
            }
            angle += (90f - angle) * overmatch;

            angle = Mathf.Clamp(angle * ArmorFragmentsPlugin.ArmorRicochetMultiplier.Value, 0f, 90f);

            float jitter = Random.Range(-1f, 1f) * JitterMaxDegrees * (ArmorFragmentsPlugin.RicochetRandomness.Value / 100f);
            angle = Mathf.Clamp(angle + jitter, 0f, 90f);

            breakdown = "reference " + reference.ToString("F0") + ", hardness " + hardnessTerm.ToString("+0.0;-0.0") +
                        ", speed " + speedTerm.ToString("+0.0;-0.0") + ", overmatch x" + overmatch.ToString("F2") +
                        ", multiplier " + ArmorFragmentsPlugin.ArmorRicochetMultiplier.Value.ToString("F2") +
                        ", random " + jitter.ToString("+0.0;-0.0");
            return angle;
        }

        // How far (degrees) a plate turns an armor-piercing shell that goes through it, toward the plate's
        // normal (the shell "straightens up"):
        //     max x sin(obliquity) x sqrt(armor hardness / shell hardness) x min(1, thickness / caliber)
        //  - obliquity: none when head-on, more the more slanted the hit (0 = head-on, 90 = grazing);
        //  - hardness: a harder plate bends the shell more, a harder shell resists (the ratio is kept
        //    between 1/4 and 4, so the factor goes from 0.5 to 2);
        //  - thickness over caliber: a thin plate hardly turns a big shell.
        public static float DeflectionDegrees(ArmorTag armor, ShellInfo shell, float angleDegrees)
        {
            float max = ArmorFragmentsPlugin.MaxDeflection.Value;
            if (max <= 0f) return 0f;

            float obliquity = Mathf.Sin(Mathf.Clamp(angleDegrees, 0f, 90f) * Mathf.Deg2Rad);
            float hardness = Mathf.Sqrt(Mathf.Clamp(armor.Hardness / Mathf.Max(shell.Hardness, 1f), 0.25f, 4f));
            float thickness = Mathf.Clamp01(armor.Thickness / Mathf.Max(shell.CaliberMm, 1f));
            return max * obliquity * hardness * thickness;
        }

        // Energy the shell keeps after bouncing: the part of its motion along the surface survives
        // (sin^2 of the angle from the normal), minus friction. The rest stays in the armor.
        public static float RicochetEnergyOut(float shellEnergy, float angleDegrees)
        {
            float sin = Mathf.Sin(angleDegrees * Mathf.Deg2Rad);
            return shellEnergy * sin * sin * (1f - ArmorFragmentsPlugin.RicochetFriction.Value);
        }

        public static string Kilojoules(float joules)
        {
            return (joules / 1000f).ToString("F1") + " kJ";
        }
    }
}
