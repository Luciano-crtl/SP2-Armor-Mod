using UnityEngine;

namespace ArmorFragments
{
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

        public static void Draw(Vector3 from, Vector3 to, bool hit)
        {
            if (!initialized) Init();
            if (!available) return;

            GameObject go = new GameObject("ArmorFragmentRay");
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
            lr.startWidth = hit ? 0.06f : 0.03f;
            lr.endWidth = lr.startWidth;
            lr.material = hit ? hitMaterial : missMaterial;
            lr.startColor = lr.endColor = hit ? hitMaterial.color : missMaterial.color;

            Object.Destroy(go, ArmorFragmentsPlugin.RayLifetime.Value);
        }
    }
}
