using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using UnityEngine;

namespace ArmorFragments
{
    // DIAGNOSTIC (phase 1 of the energy model). Only writes to the log; changes nothing.
    //
    // The energy model needs "how far does a ray travel INSIDE a part". Method A finds it with
    // two raycasts per part: one forward gives where the ray ENTERS each collider, one cast
    // backward from beyond the end gives where it LEAVES. This probe runs method A along a line
    // and reports, per part, what it found, so we can see before relying on it:
    //   - are the parts solid (entry/exit pairs) or hollow shells (extra pairs inside)?
    //   - do the colliders look like the visible meshes (type, vertex count, size)?
    internal static class ChordProbe
    {
        private struct Interval
        {
            public float Start;
            public float End;
        }

        public static void Report(Vector3 origin, Vector3 direction, float length)
        {
            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");

            // Forward: where the line enters colliders. Backward from the far end: where it leaves.
            RaycastHit[] entries = Physics.RaycastAll(
                origin, direction, length, mask, QueryTriggerInteraction.Ignore);
            Vector3 far = origin + direction * length;
            RaycastHit[] exits = Physics.RaycastAll(
                far, -direction, length, mask, QueryTriggerInteraction.Ignore);

            // Distances along the line, per collider.
            Dictionary<Collider, List<float>> enterAt = new Dictionary<Collider, List<float>>();
            Dictionary<Collider, List<float>> leaveAt = new Dictionary<Collider, List<float>>();
            foreach (RaycastHit hit in entries) Add(enterAt, hit.collider, hit.distance);
            foreach (RaycastHit hit in exits) Add(leaveAt, hit.collider, length - hit.distance);

            // Group colliders by part.
            Dictionary<PartScript, List<Collider>> byPart = new Dictionary<PartScript, List<Collider>>();
            List<string> nonParts = new List<string>();
            HashSet<Collider> all = new HashSet<Collider>(enterAt.Keys);
            all.UnionWith(leaveAt.Keys);

            foreach (Collider collider in all)
            {
                if (collider.GetComponentInParent<CannonProjectileScript>() != null) continue;

                PartScript part = collider.GetComponentInParent<PartScript>();
                if (part == null)
                {
                    nonParts.Add(collider.name);
                    continue;
                }

                List<Collider> list;
                if (!byPart.TryGetValue(part, out list)) byPart[part] = list = new List<Collider>();
                list.Add(collider);
            }

            ArmorFragmentsPlugin.Log.LogInfo(
                "[CHORD] probe along " + length.ToString("F1") + " m: " + byPart.Count + " parts, " +
                all.Count + " colliders" +
                (nonParts.Count > 0 ? ", also non-part colliders: " + string.Join(", ", nonParts.ToArray()) : ""));

            foreach (KeyValuePair<PartScript, List<Collider>> pair in byPart)
                ReportPart(pair.Key, pair.Value, enterAt, leaveAt);
        }

        private static void ReportPart(PartScript part, List<Collider> colliders,
                                       Dictionary<Collider, List<float>> enterAt,
                                       Dictionary<Collider, List<float>> leaveAt)
        {
            List<Interval> intervals = new List<Interval>();
            StringBuilder details = new StringBuilder();
            bool irregular = false;

            foreach (Collider collider in colliders)
            {
                List<float> ins = Get(enterAt, collider);
                List<float> outs = Get(leaveAt, collider);
                ins.Sort();
                outs.Sort();

                // Pair the n-th entry with the n-th exit.
                int pairs = Mathf.Min(ins.Count, outs.Count);
                for (int i = 0; i < pairs; i++)
                {
                    if (outs[i] > ins[i]) intervals.Add(new Interval { Start = ins[i], End = outs[i] });
                    else irregular = true;
                }
                if (ins.Count != outs.Count) irregular = true;

                details.Append("\n    ").Append(Describe(collider))
                       .Append(" | entries ").Append(ins.Count).Append(", exits ").Append(outs.Count);
            }

            // Several colliders of one part may overlap: count each stretch of the line once.
            intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
            float chord = 0f;
            float currentEnd = float.NegativeInfinity;
            foreach (Interval interval in intervals)
            {
                float start = Mathf.Max(interval.Start, currentEnd);
                if (interval.End > start) chord += interval.End - start;
                currentEnd = Mathf.Max(currentEnd, interval.End);
            }

            string label = TagCodec.ParseCopy(part.Part.Name).Label;
            if (label.Length == 0) label = part.Part.Name;

            ArmorFragmentsPlugin.Log.LogInfo(
                "[CHORD] part '" + label + "': distance inside " + chord.ToString("F3") + " m, " +
                colliders.Count + " colliders, mass " + part.Part.LoadedMass.ToString("F2") +
                (irregular ? "  <-- entries and exits do not pair up (hollow, open or concave?)" : "") +
                details);
        }

        // Collider type and a rough idea of how detailed it is.
        private static string Describe(Collider collider)
        {
            string text = collider.GetType().Name + " '" + collider.name + "'";

            MeshCollider mesh = collider as MeshCollider;
            if (mesh != null)
            {
                text += mesh.sharedMesh == null
                    ? " (no mesh)"
                    : " convex=" + mesh.convex + " vertices=" + mesh.sharedMesh.vertexCount +
                      " readable=" + mesh.sharedMesh.isReadable;
            }

            Vector3 size = collider.bounds.size;
            return text + " size " + size.x.ToString("F2") + "x" + size.y.ToString("F2") + "x" + size.z.ToString("F2");
        }

        private static void Add(Dictionary<Collider, List<float>> map, Collider collider, float value)
        {
            List<float> list;
            if (!map.TryGetValue(collider, out list)) map[collider] = list = new List<float>();
            list.Add(value);
        }

        private static List<float> Get(Dictionary<Collider, List<float>> map, Collider collider)
        {
            List<float> list;
            return map.TryGetValue(collider, out list) ? list : new List<float>();
        }
    }
}
