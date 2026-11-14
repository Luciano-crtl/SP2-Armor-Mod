using System.Globalization;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    // One round of ammunition as an ammo rack stores it: a solid cylinder made of the cartridge case
    // and the shell in front of it.
    //
    //  - Shell: the game's own projectile. Its mass is the cannon's ProjectileVolume x 11342 kg/m3 (the
    //    density CannonScript.Fire uses), its length 1.5 calibers for a Slug and 1 caliber for a Sphere
    //    (the shapes CannonData.ProjectileVolume assumes).
    //  - Case: wider than the shell (CaseDiameterRatio x caliber) and long enough to hold the propellant.
    //    Propellant grows with the muzzle energy, so a faster gun has a longer round, like real guns:
    //    about 2 litres of case per MJ, calibrated on the Pz III's two 50 mm guns (KwK 38 L/42: 2.06 kg at
    //    685 m/s -> 29 cm case, real 28.9 cm; KwK 39 L/60: 835 m/s -> 43 cm, real 42 cm).
    //    Never shorter than one caliber.
    //
    // Everything about ammo racks (capacity, the cook-off cells, the designer drawing, the blast) reads the
    // round from here. If the game's shell shapes are reworked, this is the one place to update.
    internal struct AmmoRound
    {
        public float CaliberMm;
        public bool Slug;
        public float ShellMass;     // kg
        public float Velocity;      // m/s, the cannon's muzzle velocity
        public float Diameter;      // m, the case's (the widest part of the round)
        public float CaseLength;    // m
        public float ShellLength;   // m

        // The density the game gives every cannon shell (CannonScript.Fire: volume x 11342 x 0.01).
        public const float ShellDensity = 11342f;

        public float Length
        {
            get { return CaseLength + ShellLength; }
        }

        // Muzzle energy in joules.
        public float Energy
        {
            get { return 0.5f * ShellMass * Velocity * Velocity; }
        }

        // Brass case and propellant: about 1 kg per litre of case (gives ~3.5 kg for the KwK 39 round, whose
        // shell alone is 2.06 kg).
        public const float CaseKgPerLitre = 1f;

        // Case volume in litres (the cylinder that holds the propellant).
        public float CaseLitres
        {
            get { return Mathf.PI * 0.25f * Diameter * Diameter * CaseLength * 1000f; }
        }

        // Weight of the whole round in kg (shell + case + propellant): what a rack carries per round.
        public float RoundMass
        {
            get { return ShellMass + CaseLitres * CaseKgPerLitre; }
        }

        // One round's share of a cook-off, in pounds of explosive (TNT equivalent of its propellant burning off).
        // Propellant grows with muzzle energy, so this does too. Calibrated so a 5 cm KwK 39 round (718 kJ) is
        // about 0.58 lb and a Pz III's ~90 rounds about 55 lb.
        public const float ExplosiveLbPerKiloJoule = 0.53f * 1.1f / 718f;

        public float ExplosiveLb
        {
            get { return Energy / 1000f * ExplosiveLbPerKiloJoule; }
        }

        public static AmmoRound FromCannon(CannonData cannon)
        {
            float caliber = Mathf.Max(cannon.CaliberInMilimeters, 1f) / 1000f;   // m
            bool slug = cannon.AmmoStyle.ToString() == "Slug";

            AmmoRound round = new AmmoRound
            {
                CaliberMm = caliber * 1000f,
                Slug = slug,
                ShellMass = cannon.ProjectileVolume * ShellDensity,
                Velocity = cannon.ProjectileVelocity,
                Diameter = caliber * ArmorFragmentsPlugin.CaseDiameterRatio.Value,
                ShellLength = slug ? 1.5f * caliber : caliber,
            };

            float caseArea = Mathf.PI * 0.25f * round.Diameter * round.Diameter;               // m2
            float caseVolume = ArmorFragmentsPlugin.CaseLitresPerMJ.Value * round.Energy / 1e6f / 1000f;   // m3
            round.CaseLength = Mathf.Max(caliber, caseVolume / Mathf.Max(caseArea, 1e-8f));
            return round;
        }

        // The round of the cannon on this part, if it is a cannon.
        public static bool TryFromPart(Assets.Scripts.Craft.Parts.PartScript part, out AmmoRound round)
        {
            round = default(AmmoRound);
            if (part == null || part.Part == null) return false;
            foreach (Assets.Scripts.Craft.Parts.Modifiers.PartModifierData modifier in part.Part.Modifiers)
            {
                CannonData cannon = modifier as CannonData;
                if (cannon == null) continue;
                round = FromCannon(cannon);
                return true;
            }
            return false;
        }

        // Bigger = needs more room (rack size check uses the biggest round of a group).
        public bool IsBiggerThan(AmmoRound other)
        {
            return Length * Diameter * Diameter > other.Length * other.Diameter * other.Diameter;
        }

        public bool SameSize(AmmoRound other)
        {
            return Mathf.Abs(Length - other.Length) <= 0.01f * Mathf.Max(Length, other.Length) &&
                   Mathf.Abs(Diameter - other.Diameter) <= 0.01f * Mathf.Max(Diameter, other.Diameter);
        }

        // "6.5 x 48.3 cm" (width x length)
        public string ShortSize()
        {
            return (Diameter * 100f).ToString("F1", CultureInfo.InvariantCulture) + " x " +
                   (Length * 100f).ToString("F1", CultureInfo.InvariantCulture) + " cm";
        }

        // "50 mm Slug, 900 m/s"
        public string ShortShell()
        {
            return CaliberMm.ToString("F0", CultureInfo.InvariantCulture) + " mm " + (Slug ? "Slug" : "Sphere") + ", " +
                   Velocity.ToString("F0", CultureInfo.InvariantCulture) + " m/s";
        }

        // "50 mm Slug at 900 m/s: round 65 x 483 mm"
        public string Describe()
        {
            return CaliberMm.ToString("F0", CultureInfo.InvariantCulture) + " mm " + (Slug ? "Slug" : "Sphere") +
                   " at " + Velocity.ToString("F0", CultureInfo.InvariantCulture) + " m/s: round " +
                   (Diameter * 1000f).ToString("F0", CultureInfo.InvariantCulture) + " x " +
                   (Length * 1000f).ToString("F0", CultureInfo.InvariantCulture) + " mm";
        }
    }
}
