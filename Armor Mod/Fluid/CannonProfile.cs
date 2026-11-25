using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    // How a cannon's own settings shape its smoke, on top of what its calibre gives (FluidSmoke.CaliberAmount). What the game has for a cannon
    // (CannonData, read in the game's code): the caliber, the barrel length (0.5 to 2.5), the muzzle brake (on or off), the flash scale, and the
    // velocity of the projectile; the projectile's mass follows from its volume (11342 kg/m3, in the game's 1/100 mass units). There is NO
    // propellant charge: the velocity is a setting, and LaunchVolume is only how loud the shot sounds. So the powder is stood in for by the
    // muzzle energy, 1/2 m v2 compared with that of a 75 mm slug at 550 m/s (about 0.85 MJ) of the same size.
    //
    //   fE  = muzzle energy / (reference energy x scale^3)   (so a bigger gun at the same speed is 1: the calibre already counts that)
    //   fv  = velocity / 550 m/s
    //   lf  = 40 / (barrel length in calibres)               (a 75 mm gun with the longest barrel, 2.5, is 40 calibres: 1; shorter barrel: more)
    //
    //   amount  of smoke, heat and dust:  fE^0.25 x lf^0.25
    //   push    of the jets:              fv^0.8  x lf^0.3     (a muzzle brake, the default, turns part of the jet sideways: without one, 15% more ahead, no plumes to the sides)
    //   size    of the fireball:          fE^(1/6) x lf^0.15 x flash scale^0.5
    //   shock   of the pressure wave:     lf^0.5 x fv^0.5
    // all to the power of Smoke/CannonProfile (0 = none: only the calibre counts; 1 = as above; 2 = twice as strong), and kept within 0.5-2.
    internal struct CannonProfile
    {
        public float amount, push, size, shock, brake;
        public string text;

        public static CannonProfile Neutral { get { return new CannonProfile { amount = 1f, push = 1f, size = 1f, shock = 1f, brake = 1f, text = "no cannon data (as a 75 mm)" }; } }

        public static float Strength { get { return ArmorFragmentsPlugin.SmokeCannonProfile != null ? ArmorFragmentsPlugin.SmokeCannonProfile.Value : 1f; } }

        private const float ReferenceEnergy = 0.85e6f;      // J: a 75 mm slug (5.6 kg) at 550 m/s
        private const float ReferenceVelocity = 550f;       // m/s
        private const float ReferenceLength = 40f;          // calibres: a 75 mm gun with the longest barrel
        private const float MassPerVolume = 11342f;         // kg / m3 of the projectile (the game's)

        public static CannonProfile Of(CannonScript cannon, float scale)
        {
            if (cannon == null || cannon.PartScript == null) return Neutral;
            CannonData data = null;
            foreach (PartModifierData modifier in cannon.PartScript.Part.Modifiers)
            {
                data = modifier as CannonData;
                if (data != null) break;
            }
            if (data == null) return Neutral;
            float strength = Strength;
            try
            {
                float mass = data.ProjectileVolume * MassPerVolume;                 // kg
                float velocity = Mathf.Max(data.ProjectileVelocity, 1f);
                float energy = 0.5f * mass * velocity * velocity;
                float fE = Mathf.Clamp(energy / (ReferenceEnergy * scale * scale * scale), 0.02f, 50f);
                float fv = Mathf.Clamp(velocity / ReferenceVelocity, 0.2f, 4f);
                // The length of the barrel in calibres: its length is 1.42 x the setting (metres), its width 0.585 x the calibre setting (see
                // CannonData.TotalCannonVolume).
                float length = Mathf.Clamp(2.43f * data.BarrelLength / Mathf.Max(data.Diameter, 0.05f), 6f, 90f);
                float lf = ReferenceLength / length;
                float flash = Mathf.Clamp(data.MuzzleFlashScale, 0.2f, 4f);
                bool brake = data.MuzzleBrake;

                CannonProfile p = new CannonProfile
                {
                    amount = Limit(Mathf.Pow(fE, 0.25f) * Mathf.Pow(lf, 0.25f), strength),
                    push = Limit(Mathf.Pow(fv, 0.8f) * Mathf.Pow(lf, 0.3f) * (brake ? 1f : 1.15f), strength),
                    size = Limit(Mathf.Pow(fE, 1f / 6f) * Mathf.Pow(lf, 0.15f) * Mathf.Sqrt(flash), strength),
                    shock = Limit(Mathf.Pow(lf, 0.5f) * Mathf.Pow(fv, 0.5f), strength),
                    brake = brake ? 1f : 0f,
                };
                p.text = (data.CaliberInMilimeters).ToString("F0") + " mm, " + (energy / 1000f).ToString("F0") + " kJ at the muzzle (" + fE.ToString("F2") + "x of its calibre), " + velocity.ToString("F0") + " m/s, barrel "
                         + length.ToString("F0") + " calibres, brake " + (brake ? "on" : "off") + ", flash x" + flash.ToString("F1")
                         + " -> smoke x" + p.amount.ToString("F2") + ", jets x" + p.push.ToString("F2") + ", fireball x" + p.size.ToString("F2") + ", shock x" + p.shock.ToString("F2");
                return p;
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] cannon profile: could not read the cannon (" + e.Message + "), it counts as a 75 mm");
                return Neutral;
            }
        }

        // The factor to the power of the strength (the exponents above are for a strength of 1), kept within 0.5 to 2.
        private static float Limit(float factor, float strength)
        {
            if (strength <= 0.001f) return 1f;
            return Mathf.Clamp(Mathf.Pow(Mathf.Max(factor, 1e-3f), strength), 0.5f, 2f);
        }
    }
}
