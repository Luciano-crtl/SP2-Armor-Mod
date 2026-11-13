using System.Collections.Generic;
using UnityEngine;

namespace ArmorFragments
{
    // Moves a bright head along a RayPath. Behind the head the line fades out.
    // When the head reaches the end, the tail catches up and the line disappears.
    // Each stretch of the path has its own speed (see RayPath.SpeedTo), so a ray visibly slows down
    // as it loses energy; the trail is a length of TIME behind the head, so it gets shorter when the
    // ray is slower. Purely visual: it never touches physics or damage.
    public class TracerAnimation : MonoBehaviour
    {
        private const float DefaultSpeed = 40f;   // m/s, for stretches without a speed of their own

        private RayPath path;
        private LineRenderer line;
        private float[] cumulativeDistance;   // meters along the path at each point
        private float[] cumulativeTime;       // seconds along the path at each point
        private float totalTime;
        private float totalDistance;
        private float startTime;
        private float delay;
        private float trailTime;
        private int nextMarker;
        private Color color;
        private bool continuation;    // continues a ray that arrived: its tail does not fade in at the start
        private bool tailFaded;
        private Transform anchor;     // the points travel with it (null = fixed in the world)
        private Vector3[] local;      // the points in the anchor's space
        private Transform fallback;   // used instead if the anchor's part is destroyed (switched off)
        private Vector3[] localFallback;
        private bool started;
        private readonly List<Vector3> visible = new List<Vector3>();

        // How far (meters) the head has gone along the path.
        public float HeadDistance { get; private set; }

        // Length of the path's first stretch: for the shell's ray, the flight before the impact.
        public float FirstSegment
        {
            get { return cumulativeDistance != null && cumulativeDistance.Length > 1 ? cumulativeDistance[1] : 0f; }
        }

        // The transform the path is following now: its anchor, or the fallback once the anchor is switched off.
        private Transform ActiveAnchor
        {
            get
            {
                if (anchor != null && anchor.gameObject.activeInHierarchy) return anchor;
                return fallback != null ? fallback : anchor;
            }
        }

        // A point of the path now: moved along with the anchor if there is one (and it still exists).
        private Vector3 P(int i)
        {
            if (anchor != null && anchor.gameObject.activeInHierarchy) return anchor.TransformPoint(local[i]);
            if (fallback != null) return fallback.TransformPoint(localFallback[i]);
            if (anchor != null) return anchor.TransformPoint(local[i]);   // frozen where it was switched off
            return path.Points[i];
        }

        internal void Init(RayPath path, LineRenderer line, Color color, float delay, float trailLength, bool initial)
        {
            this.path = path;
            this.line = line;
            this.color = color;
            this.continuation = path.Continuation;
            this.delay = delay;
            startTime = Time.time;

            // Transparent at the tail, solid at the head.
            line.startColor = new Color(color.r, color.g, color.b, 0f);
            line.endColor = color;

            int n = path.Points.Count;
            cumulativeDistance = new float[n];
            cumulativeTime = new float[n];
            float firstSpeed = 0f;
            for (int i = 1; i < n; i++)
            {
                float length = Vector3.Distance(path.Points[i - 1], path.Points[i]);
                float speed = path.SpeedTo[i] > 0.01f ? path.SpeedTo[i] : DefaultSpeed;
                if (firstSpeed <= 0f) firstSpeed = speed;

                cumulativeDistance[i] = cumulativeDistance[i - 1] + length;
                cumulativeTime[i] = cumulativeTime[i - 1] + length / speed;
            }
            totalDistance = cumulativeDistance[n - 1];
            totalTime = cumulativeTime[n - 1];

            // The trail covers 'trailLength' meters at the speed the ray starts with.
            trailTime = Mathf.Max(trailLength, 0.05f) / Mathf.Max(firstSpeed, 1f);

            anchor = path.Anchor;
            if (anchor != null)
            {
                local = new Vector3[n];
                for (int i = 0; i < n; i++) local[i] = anchor.InverseTransformPoint(path.Points[i]);
            }
            fallback = path.FallbackAnchor;
            if (fallback != null)
            {
                localFallback = new Vector3[n];
                for (int i = 0; i < n; i++) localFallback[i] = fallback.InverseTransformPoint(path.Points[i]);
            }

            // The shell's ray reaches its impact (the second point) at this moment.
            if (initial && n > 1) RayDrawer.NoteImpact(startTime + delay + cumulativeTime[1]);
        }

        private void Update()
        {
            if (totalDistance <= 0.0001f) { Destroy(gameObject); return; }

            float t = Time.time - startTime - delay;
            if (t < 0f) return;

            if (!started) { started = true; line.enabled = true; }

            // A ray that continues another (a shell that left a plate) starts as bright as the one that arrived,
            // and its tail fades in over the trail time, as if both were one ray. Without this its first
            // stretch would be see-through and the ray would look cut in two at the plate.
            if (continuation && !tailFaded)
            {
                float alpha = Mathf.Clamp01(1f - t / Mathf.Max(trailTime, 0.0001f));
                line.startColor = new Color(color.r, color.g, color.b, alpha);
                if (alpha <= 0f) tailFaded = true;
            }

            float tailTime = t - trailTime;
            if (tailTime >= totalTime) { Destroy(gameObject); return; }

            // A bubble appears when the head reaches each marked point.
            while (nextMarker < cumulativeTime.Length && cumulativeTime[nextMarker] <= t)
            {
                RayDrawer.SpawnBubble(P(nextMarker), path.Markers[nextMarker], ActiveAnchor);
                nextMarker++;
            }

            float head = DistanceAt(Mathf.Min(t, totalTime));
            HeadDistance = head;
            float tail = DistanceAt(Mathf.Max(tailTime, 0f));

            // Visible piece: tail point, every corner between tail and head, head point.
            visible.Clear();
            visible.Add(PointAt(tail));
            for (int i = 0; i < cumulativeDistance.Length; i++)
                if (cumulativeDistance[i] > tail && cumulativeDistance[i] < head)
                    visible.Add(P(i));
            visible.Add(PointAt(head));

            line.positionCount = visible.Count;
            for (int i = 0; i < visible.Count; i++)
                line.SetPosition(i, visible[i]);
        }

        // Where the head is (as a distance along the path) after 'time' seconds.
        private float DistanceAt(float time)
        {
            for (int i = 1; i < cumulativeTime.Length; i++)
            {
                if (time <= cumulativeTime[i])
                {
                    float span = cumulativeTime[i] - cumulativeTime[i - 1];
                    float f = span > 0.000001f ? (time - cumulativeTime[i - 1]) / span : 0f;
                    return Mathf.Lerp(cumulativeDistance[i - 1], cumulativeDistance[i], f);
                }
            }
            return totalDistance;
        }

        private Vector3 PointAt(float distance)
        {
            for (int i = 1; i < cumulativeDistance.Length; i++)
            {
                if (distance <= cumulativeDistance[i])
                {
                    float segment = cumulativeDistance[i] - cumulativeDistance[i - 1];
                    float f = segment > 0.00001f ? (distance - cumulativeDistance[i - 1]) / segment : 0f;
                    return Vector3.Lerp(P(i - 1), P(i), f);
                }
            }
            return P(path.Points.Count - 1);
        }
    }

    // Shrinks a bubble to nothing, then removes it.
    public class BubbleAnimation : MonoBehaviour
    {
        private float size;
        private float lifetime;
        private float startTime;
        private Transform anchor;    // the bubble travels with it (null = fixed in the world)
        private Vector3 local;

        internal void Init(float size, float lifetime, Transform anchor = null)
        {
            this.anchor = anchor;
            if (anchor != null) local = anchor.InverseTransformPoint(transform.position);
            this.size = size;
            this.lifetime = Mathf.Max(lifetime, 0.05f);
            startTime = Time.time;
            transform.localScale = Vector3.one * size;
        }

        private void Update()
        {
            float f = (Time.time - startTime) / lifetime;
            if (f >= 1f) { Destroy(gameObject); return; }

            // Stays on the spot of the part that was hit, even as the vehicle moves.
            if (anchor != null) transform.position = anchor.TransformPoint(local);
            transform.localScale = Vector3.one * (size * (1f - f));
        }
    }
}
