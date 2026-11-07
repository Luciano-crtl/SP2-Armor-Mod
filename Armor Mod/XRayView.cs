using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // "X-ray" view: the real parts near the impact are hidden and replaced by see-through
    // copies of their meshes. The more simulated damage a part would take, the darker and
    // more solid its copy is. Purely visual: nothing about the real parts is changed
    // except that their rendering is switched off while the view is on.
    internal static class XRayView
    {
        private const int Levels = 12;

        private static readonly List<Renderer> HiddenRenderers = new List<Renderer>();
        private static readonly List<GameObject> Ghosts = new List<GameObject>();
        private static Material[] normalMaterials;   // one per damage level
        private static Material[] armorMaterials;    // same, for parts tagged [ARM]
        private static float hideAtTime;

        public static bool IsOn
        {
            get { return Ghosts.Count > 0 || HiddenRenderers.Count > 0; }
        }

        // damage: simulated damage per part. center: where the shot hit.
        public static void Show(Vector3 center, Dictionary<PartScript, float> damage)
        {
            Hide();

            Shader shader = RayDrawer.GetShader();
            if (shader == null)
            {
                ArmorFragmentsPlugin.Log.LogWarning("X-ray view unavailable: no usable shader in this build");
                return;
            }
            if (normalMaterials == null) CreateMaterials(shader);

            // Every part near the impact (and every part that took simulated damage).
            HashSet<PartScript> parts = new HashSet<PartScript>();
            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            Collider[] colliders = Physics.OverlapSphere(
                center, ArmorFragmentsPlugin.XRayRadius.Value, mask, QueryTriggerInteraction.Ignore);
            foreach (Collider collider in colliders)
            {
                PartScript part = collider.GetComponentInParent<PartScript>();
                if (part != null) parts.Add(part);
            }
            foreach (PartScript part in damage.Keys) parts.Add(part);

            float fullDark = Mathf.Max(ArmorFragmentsPlugin.XRayDarkPercent.Value / 100f, 0.01f);
            HashSet<Renderer> alreadyHidden = new HashSet<Renderer>();
            int ghostCount = 0;

            foreach (PartScript part in parts)
            {
                if (part == null) continue;

                // How dark: damage as a fraction of the part's health, scaled so that
                // 'fullDark' of the health lost means fully dark.
                float taken;
                damage.TryGetValue(part, out taken);
                float t = Mathf.Clamp01(taken / Mathf.Max(part.MaxHealth, 1f) / fullDark);
                int level = Mathf.RoundToInt(t * (Levels - 1));

                bool isArmor = TagCodec.Parse(part.Part.Name).Armor != null;
                Material material = (isArmor ? armorMaterials : normalMaterials)[level];

                // Parts can be drawn as one combined mesh per group: hide that too.
                PartGroupScript group = part.PartGroup;
                if (group != null && group.Renderer != null) HideRenderer(group.Renderer, alreadyHidden);

                foreach (MeshRenderer source in GhostSources(part))
                {
                    MeshFilter filter = source.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;

                    ghostCount += MakeGhost(source, filter.sharedMesh, material);
                    HideRenderer(source, alreadyHidden);
                }
            }

            hideAtTime = Time.time + ArmorFragmentsPlugin.XRayDuration.Value;
            ArmorFragmentsPlugin.Log.LogInfo(
                "X-ray view on: " + parts.Count + " parts, " + ghostCount + " ghost meshes");
        }

        // Switches everything back. Safe to call at any time.
        public static void Hide()
        {
            foreach (Renderer renderer in HiddenRenderers)
                if (renderer != null) renderer.forceRenderingOff = false;
            HiddenRenderers.Clear();

            foreach (GameObject ghost in Ghosts)
                if (ghost != null) Object.Destroy(ghost);
            Ghosts.Clear();
        }

        // Called every frame by the plugin: turns the view off when its time is up.
        public static void Tick()
        {
            if (IsOn && Time.time >= hideAtTime) Hide();
        }

        // The renderers that make up a part's visible mesh.
        private static IEnumerable<MeshRenderer> GhostSources(PartScript part)
        {
            HashSet<MeshRenderer> seen = new HashSet<MeshRenderer>();

            // The game's own list of the part's renderers (includes ones that are switched off
            // because the part is drawn as part of a combined mesh).
            if (part.PartMaterialScript != null)
            {
                foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
                    if (map.Renderer != null && seen.Add(map.Renderer)) yield return map.Renderer;
            }

            // Any other mesh that is currently visible (propeller blades, for example).
            foreach (MeshRenderer renderer in part.GetComponentsInChildren<MeshRenderer>())
            {
                if (renderer.name == "ArmorXRayGhost") continue;
                if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                if (renderer.enabled && seen.Add(renderer)) yield return renderer;
            }
        }

        // A see-through copy of 'mesh', attached to the same transform as the real renderer.
        private static int MakeGhost(MeshRenderer source, Mesh mesh, Material material)
        {
            GameObject ghost = new GameObject("ArmorXRayGhost");
            ghost.layer = source.gameObject.layer;
            ghost.transform.SetParent(source.transform, false);

            ghost.AddComponent<MeshFilter>().sharedMesh = mesh;

            MeshRenderer renderer = ghost.AddComponent<MeshRenderer>();
            Material[] materials = new Material[Mathf.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < materials.Length; i++) materials[i] = material;
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            Ghosts.Add(ghost);
            return 1;
        }

        // forceRenderingOff does not touch 'enabled', so the game's own show/hide logic
        // keeps working and nothing is lost when we switch it back.
        private static void HideRenderer(Renderer renderer, HashSet<Renderer> alreadyHidden)
        {
            if (renderer == null || !alreadyHidden.Add(renderer)) return;
            if (renderer.forceRenderingOff) return;   // someone else hid it: leave it alone

            renderer.forceRenderingOff = true;
            HiddenRenderers.Add(renderer);
        }

        private static void CreateMaterials(Shader shader)
        {
            normalMaterials = new Material[Levels];
            armorMaterials = new Material[Levels];

            // From almost invisible (no damage) to dark and solid (a lot of damage).
            Color normalLight = new Color(0.70f, 0.80f, 0.95f, 0.16f);
            Color armorLight = new Color(0.45f, 0.85f, 1.00f, 0.30f);
            Color dark = new Color(0.10f, 0.00f, 0.00f, 0.90f);

            for (int i = 0; i < Levels; i++)
            {
                float t = i / (float)(Levels - 1);
                normalMaterials[i] = new Material(shader) { color = Color.Lerp(normalLight, dark, t) };
                armorMaterials[i] = new Material(shader) { color = Color.Lerp(armorLight, dark, t) };
            }
        }
    }
}
