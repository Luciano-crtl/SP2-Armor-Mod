using UnityEngine;

namespace ArmorFragments
{
    // The three kinds of shell the mod tells apart. The game only has two projectile types (Basic and
    // Explosive); the fuse, which the cannon's [CAN z=] tag adds, tells the explosive ones apart.
    internal enum AmmoKind
    {
        AP,     // armor-piercing: a solid shell (the game's Basic type)
        APHE,   // explosive with a fuse: goes through armor and bursts inside, after the fuse distance
        HE,     // explosive without a fuse: bursts on the first thing it touches
    }

    internal static class Ammunition
    {
        public static AmmoKind KindOf(bool explosive, CannonTag tag)
        {
            if (!explosive) return AmmoKind.AP;
            return tag != null && tag.Fuse.HasValue ? AmmoKind.APHE : AmmoKind.HE;
        }

        public static string NameOf(AmmoKind kind)
        {
            switch (kind)
            {
                case AmmoKind.APHE: return "APHE";
                case AmmoKind.HE: return "HE";
                default: return "AP";
            }
        }

        // The explosive filler (percent of the shell's mass) a shell of this kind has unless the cannon says otherwise.
        public static float DefaultFillerPercent(AmmoKind kind)
        {
            switch (kind)
            {
                case AmmoKind.APHE: return ArmorFragmentsPlugin.FillerAPHE.Value;
                case AmmoKind.HE: return ArmorFragmentsPlugin.FillerHE.Value;
                default: return 0f;
            }
        }

        // Fills in what a shell is made of: explosive or not, its fuse and its filler. 'tag' is the firing
        // cannon's [CAN] tag (null for a cannon without one).
        public static void Configure(ref ShellInfo shell, bool explosive, CannonTag tag)
        {
            AmmoKind kind = KindOf(explosive, tag);
            shell.Explosive = explosive;
            shell.HasFuse = kind == AmmoKind.APHE;
            shell.FuseLeft = shell.HasFuse ? Mathf.Max(tag.Fuse.Value, 0f) : 0f;

            float percent = tag != null && tag.FillerPercent.HasValue ? tag.FillerPercent.Value : DefaultFillerPercent(kind);
            shell.FillerFraction = explosive ? Mathf.Clamp(percent, 0f, 50f) / 100f : 0f;
        }
    }
}
