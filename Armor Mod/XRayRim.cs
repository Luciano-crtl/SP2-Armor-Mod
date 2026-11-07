using Assets.Scripts.Flight.Cameras;
using UnityEngine;

namespace ArmorFragments
{
    // Fakes a "rim light" on an X-ray ghost without a custom shader: every few hundredths
    // of a second it recomputes each vertex's color from the angle between the surface
    // and the camera. Surfaces seen edge-on get the bright rim color and more opacity,
    // surfaces facing the camera stay mostly transparent. The mesh is the ghost's OWN copy,
    // so the game's real meshes are never touched.
    public class XRayRimGhost : MonoBehaviour
    {
        private const float UpdateInterval = 0.05f;
        private const float RimPower = 2.2f;   // higher = thinner rim

        private Mesh mesh;
        private Vector3[] vertices;
        private Vector3[] normals;
        private Color[] colors;
        private Color bodyColor;
        private Color rimColor;
        private float bodyAlpha;
        private float rimAlpha;
        private float nextUpdate;

        internal void Init(Mesh copy, Color body, float bodyAlpha, Color rim, float rimAlpha)
        {
            mesh = copy;
            vertices = copy.vertices;
            normals = copy.normals;
            if (normals.Length != vertices.Length)
            {
                copy.RecalculateNormals();
                normals = copy.normals;
            }
            colors = new Color[vertices.Length];

            bodyColor = body;
            rimColor = rim;
            this.bodyAlpha = bodyAlpha;
            this.rimAlpha = rimAlpha;

            Apply();
        }

        private void Update()
        {
            if (Time.time < nextUpdate) return;
            nextUpdate = Time.time + UpdateInterval;
            Apply();
        }

        private void Apply()
        {
            if (mesh == null) return;

            CameraManagerScript manager = CameraManagerScript.Instance;
            Camera camera = (manager != null && manager.MainCamera != null) ? manager.MainCamera : Camera.main;
            Vector3 cameraLocal = camera != null
                ? transform.InverseTransformPoint(camera.transform.position)
                : Vector3.zero;

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 toCamera = cameraLocal - vertices[i];
                float length = toCamera.magnitude;
                float facing = length > 0.00001f
                    ? Mathf.Abs(Vector3.Dot(normals[i], toCamera)) / length
                    : 1f;

                // 0 where the surface faces the camera, 1 at the silhouette edge.
                float rim = Mathf.Pow(1f - Mathf.Clamp01(facing), RimPower);

                Color color = Color.Lerp(bodyColor, rimColor, rim);
                color.a = Mathf.Lerp(bodyAlpha, rimAlpha, rim);
                colors[i] = color;
            }

            mesh.colors = colors;
        }

        private void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
        }
    }
}
