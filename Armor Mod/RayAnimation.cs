using System.Collections.Generic;
using UnityEngine;

namespace ArmorFragments
{
    // Moves a bright head along a RayPath. Behind the head the line fades out.
    // When the head reaches the end, the tail catches up and the line disappears.
    // Purely visual: it never touches physics or damage.
    public class TracerAnimation : MonoBehaviour
    {
        private RayPath path;
        private LineRenderer line;
        private float[] cumulative;     // distance along the path at each point
        private float total;
        private float startTime;
        private float delay;
        private float speed;
        private float trail;
        private int nextMarker;
        private bool started;
        private readonly List<Vector3> visible = new List<Vector3>();

        internal void Init(RayPath path, LineRenderer line, Color color,
                           float delay, float speed, float trail)
        {
            this.path = path;
            this.line = line;
            this.delay = delay;
            this.speed = Mathf.Max(speed, 0.1f);
            this.trail = Mathf.Max(trail, 0.05f);
            startTime = Time.time;

            // Transparent at the tail, solid at the head.
            line.startColor = new Color(color.r, color.g, color.b, 0f);
            line.endColor = color;

            int n = path.Points.Count;
            cumulative = new float[n];
            for (int i = 1; i < n; i++)
                cumulative[i] = cumulative[i - 1] + Vector3.Distance(path.Points[i - 1], path.Points[i]);
            total = cumulative[n - 1];
        }

        private void Update()
        {
            if (total <= 0.0001f) { Destroy(gameObject); return; }

            float t = Time.time - startTime - delay;
            if (t < 0f) return;

            if (!started) { started = true; line.enabled = true; }

            float headRaw = t * speed;          // keeps growing after the end of the path
            float tailRaw = headRaw - trail;
            if (tailRaw >= total) { Destroy(gameObject); return; }

            // A bubble appears when the head reaches each marked point.
            while (nextMarker < cumulative.Length && cumulative[nextMarker] <= headRaw)
            {
                RayDrawer.SpawnBubble(path.Points[nextMarker], path.Markers[nextMarker]);
                nextMarker++;
            }

            float head = Mathf.Min(headRaw, total);
            float tail = Mathf.Max(tailRaw, 0f);

            // Visible piece: tail point, every corner between tail and head, head point.
            visible.Clear();
            visible.Add(PointAt(tail));
            for (int i = 0; i < cumulative.Length; i++)
                if (cumulative[i] > tail && cumulative[i] < head)
                    visible.Add(path.Points[i]);
            visible.Add(PointAt(head));

            line.positionCount = visible.Count;
            for (int i = 0; i < visible.Count; i++)
                line.SetPosition(i, visible[i]);
        }

        private Vector3 PointAt(float distance)
        {
            for (int i = 1; i < cumulative.Length; i++)
            {
                if (distance <= cumulative[i])
                {
                    float segment = cumulative[i] - cumulative[i - 1];
                    float f = segment > 0.00001f ? (distance - cumulative[i - 1]) / segment : 0f;
                    return Vector3.Lerp(path.Points[i - 1], path.Points[i], f);
                }
            }
            return path.Points[path.Points.Count - 1];
        }
    }

    // Shrinks a bubble to nothing, then removes it.
    public class BubbleAnimation : MonoBehaviour
    {
        private float size;
        private float lifetime;
        private float startTime;

        internal void Init(float size, float lifetime)
        {
            this.size = size;
            this.lifetime = Mathf.Max(lifetime, 0.05f);
            startTime = Time.time;
            transform.localScale = Vector3.one * size;
        }

        private void Update()
        {
            float f = (Time.time - startTime) / lifetime;
            if (f >= 1f) { Destroy(gameObject); return; }
            transform.localScale = Vector3.one * (size * (1f - f));
        }
    }
}
