using UnityEngine;

namespace ArmorFragments
{
    internal enum RayKind
    {
        Hit,       // ended on a part (red)
        Miss,      // ended in the air or on a non-part object (yellow)
        Ricochet,  // segment that ended in a bounce (cyan)
    }

    // Draws a short-lived line in the world for each fragment ray.
    // Earlier notes say LineRenderer shaders can be stripped from the built game,
    // so we try several shaders and log which one worked (or that none did).
    internal static class RayDrawer
    {
        private static readonly string[] ShaderNames =
        {
            "Sprites/Default",
            "Hidden/Internal-Colored",
            "Unlit/Color",
            "Universal Render Pipeline/Unlit",
            "UI/Default",
        };

        private static Material hitMaterial;
        private static Material missMaterial;
        private static Material ricochetMaterial;
        private static bool initialized;
        private static bool available;

        private static void Init()
        {
            initialized = true;
            foreach (string name in ShaderNames)
            {
                Shader shader = Shader.Find(name);
                if (shader == null) continue;

                hitMaterial = MakeMaterial(shader, new Color(1f, 0.1f, 0.1f, 1f));
                missMaterial = MakeMaterial(shader, new Color(1f, 0.9f, 0.1f, 0.6f));
                ricochetMaterial = MakeMaterial(shader, new Color(0.1f, 0.9f, 1f, 1f));
                available = true;
                ArmorFragmentsPlugin.Log.LogInfo("Ray drawing uses shader '" + name + "'");
                return;
            }
            ArmorFragmentsPlugin.Log.LogWarning(
                "Ray drawing disabled: none of the tried shaders exist in this build");
        }

        private static Material MakeMaterial(Shader shader, Color color)
        {
            Material m = new Material(shader);
            m.color = color;
            return m;
        }

        public static void Draw(Vector3 from, Vector3 to, RayKind kind)
        {
            if (!initialized) Init();
            if (!available) return;

            GameObject go = new GameObject("ArmorFragmentRay");
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
            Material mat = kind == RayKind.Hit ? hitMaterial
                         : kind == RayKind.Ricochet ? ricochetMaterial
                         : missMaterial;
            lr.startWidth = kind == RayKind.Miss ? 0.03f : 0.06f;
            lr.endWidth = lr.startWidth;
            lr.material = mat;
            lr.startColor = lr.endColor = mat.color;

            Object.Destroy(go, ArmorFragmentsPlugin.RayLifetime.Value);
        }
    }
}
