using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // What a ray ended on, or what an impact marker means.
    internal enum RayKind
    {
        None,        // no marker
        Hit,         // a part took the hit (red)
        Miss,        // ray ended in the air or on a non-part object (yellow)
        Ricochet,    // the ray bounced here (cyan)
        ArmorImpact, // the shell hit the armor (orange)
        Landed,      // a fragment of a simulated shot stopped here (magenta); stays until the next test
    }

    // The full route of one ray: a list of points, plus a marker at each point
    // that gets a bubble when the animation reaches it.
    internal sealed class RayPath
    {
        public readonly List<Vector3> Points = new List<Vector3>();
        public readonly List<RayKind> Markers = new List<RayKind>();
        public RayKind Final = RayKind.Miss;   // decides the line color
        public Transform Anchor;               // if set, the points travel with this transform

        // Drawn speed (m/s) for the stretch that ENDS at each point; 0 = use the default.
        // The first point's value is not used. It lets a ray slow down as it loses energy.
        public readonly List<float> SpeedTo = new List<float>();

        public void Add(Vector3 point, RayKind marker, float speed = 0f)
        {
            Points.Add(point);
            Markers.Add(marker);
            SpeedTo.Add(speed);
        }
    }

    // Creates the visuals. Earlier notes say shaders can be stripped from the built
    // game, so we try several and log which one worked (or that none did).
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

        // When the shell's ray last reached its impact (a Time.time value). The impact camera counts its
        // closing time from here, so every new impact keeps it open.
        public static float LastImpactTime;

        public static void NoteImpact(float time)
        {
            if (time > LastImpactTime) LastImpactTime = time;
        }

        // The latest shell ray (the 'initial' one) drawn, and how many have been drawn: the impact camera
        // follows the first one made after it opens.
        public static TracerAnimation InitialTracer;
        public static int InitialSerial;

        // While set, the rays drawn are attached to this transform (the part that was hit), so a moving
        // target carries them along instead of leaving them behind.
        public static Transform Anchor;

        private static Material lineMaterial;                 // white: the color comes from the line itself
        private static readonly Material[] BubbleMaterials = new Material[6];   // one per RayKind
        private static Shader shaderUsed;
        private static bool initialized;
        private static bool available;

        public static Color ColorOf(RayKind kind)
        {
            switch (kind)
            {
                case RayKind.Hit: return new Color(1f, 0.15f, 0.1f, 1f);
                case RayKind.Ricochet: return new Color(0.1f, 0.9f, 1f, 1f);
                case RayKind.ArmorImpact: return new Color(1f, 0.55f, 0.1f, 1f);
                case RayKind.Landed: return new Color(1f, 0.2f, 1f, 1f);
                default: return new Color(1f, 0.9f, 0.1f, 1f);
            }
        }

        private static void Init()
        {
            initialized = true;
            foreach (string name in ShaderNames)
            {
                Shader shader = Shader.Find(name);
                if (shader == null) continue;

                shaderUsed = shader;
                lineMaterial = MakeMaterial(shader, Color.white);
                foreach (RayKind kind in System.Enum.GetValues(typeof(RayKind)))
                    BubbleMaterials[(int)kind] = MakeMaterial(shader, ColorOf(kind));

                available = true;
                ArmorFragmentsPlugin.Log.LogInfo("Ray drawing uses shader '" + name + "'");
                return;
            }
            ArmorFragmentsPlugin.Log.LogWarning(
                "Ray drawing disabled: none of the tried shaders exist in this build");
        }

        // The see-through shader that was found, for other visuals (the X-ray view).
        // Null if none of the tried shaders exists in this build.
        public static Shader GetShader()
        {
            if (!initialized) Init();
            return available ? shaderUsed : null;
        }

        private static Material MakeMaterial(Shader shader, Color color)
        {
            Material m = new Material(shader);
            m.color = color;
            return m;
        }

        // Animates a glowing head moving along the path, with a fading trail behind it.
        // 'delay' (seconds) waits before starting, so rays can leave after the shell arrives.
        public static void Animate(RayPath path, float delay, bool initial = false)
        {
            if (path.Points.Count < 2) return;
            if (!initialized) Init();
            if (!available) return;

            GameObject go = new GameObject("ArmorFragmentRay");
            go.layer = ImpactCamera.RayLayer;
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 0;
            lr.startWidth = lr.endWidth = initial
                ? ArmorFragmentsPlugin.InitialRayWidth.Value
                : ArmorFragmentsPlugin.RayWidth.Value;
            lr.sharedMaterial = lineMaterial;   // shared: no new material per ray
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;                 // the animation turns it on when it starts

            path.Anchor = Anchor;
            TracerAnimation tracer = go.AddComponent<TracerAnimation>();
            tracer.Init(path, lr, ColorOf(path.Final), delay, ArmorFragmentsPlugin.RayTrailLength.Value, initial);
            if (initial)
            {
                InitialTracer = tracer;
                InitialSerial++;
            }
        }

        // Balls that stay (the stop points of a simulated shot) until the next test clears them.
        private static readonly List<GameObject> PersistentBubbles = new List<GameObject>();

        public static void ClearPersistent()
        {
            foreach (GameObject bubble in PersistentBubbles)
                if (bubble != null) Object.Destroy(bubble);
            PersistentBubbles.Clear();
        }

        // A small colored ball that shrinks away. The "Landed" kind does not shrink: it stays,
        // a little smaller, until ClearPersistent is called.
        public static void SpawnBubble(Vector3 position, RayKind kind)
        {
            if (!initialized) Init();
            if (!available || kind == RayKind.None) return;

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "ArmorFragmentBubble";
            go.layer = ImpactCamera.RayLayer;

            // The primitive comes with a collider; remove it NOW or later rays would hit it.
            Collider col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            Renderer r = go.GetComponent<Renderer>();
            r.sharedMaterial = BubbleMaterials[(int)kind];
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;

            go.transform.position = position;

            if (kind == RayKind.Landed)
            {
                go.AddComponent<BubbleAnimation>().Init(ArmorFragmentsPlugin.BubbleSize.Value * 0.6f, float.PositiveInfinity);
                PersistentBubbles.Add(go);
                return;
            }

            go.AddComponent<BubbleAnimation>().Init(
                ArmorFragmentsPlugin.BubbleSize.Value,
                ArmorFragmentsPlugin.BubbleLifetime.Value);
        }
    }
}
