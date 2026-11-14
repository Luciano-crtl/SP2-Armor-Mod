using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Design;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ArmorFragments
{
    // Designer only: every ammo rack is drawn see-through, with its rounds inside as cylinders (brass case,
    // steel shell toward the fuselage's front), laid out exactly as RackGrid counts them. So the player sees
    // how the shape packs the rounds while shaping it.
    //
    // The rack's own renderers are hidden with forceRenderingOff (the game's show/hide logic is untouched)
    // and a see-through copy of its mesh is drawn instead, the same trick as the X-ray view. Nothing here
    // has a collider, so clicking the rack still selects it. A selected rack's copy is tinted orange, since
    // the game's own selection highlight is on the hidden renderer.
    internal static class RackVisualizer
    {
        private const string GhostName = "AmmoRackGhost";
        private const string RoundsName = "AmmoRackRounds";
        private const float RefreshInterval = 0.25f;
        // Rounds drawn per rack. (Was 4000: a 4 m wedge holding 4536 rounds of 50 mm then looked as if a
        // side of it were empty, though the rack counted every round.) Big racks use fewer cylinder sides.
        private const int MaxDrawnRounds = 20000;

        private sealed class Visual
        {
            public PartScript Part;
            public string Signature;
            public GameObject Rounds;
            public Mesh RoundsMesh;
            public readonly List<MeshRenderer> Ghosts = new List<MeshRenderer>();
            public readonly List<Renderer> Hidden = new List<Renderer>();
            public bool ShownSelected;
        }

        private static readonly Dictionary<PartScript, Visual> Visuals = new Dictionary<PartScript, Visual>();
        private static float nextRefresh;
        private static bool warned;

        private static Material ghostMaterial, ghostSelectedMaterial, caseMaterial, shellMaterial, burningMaterial;
        private static bool materialsTried;

        // While the designer's screenshot dialog hides things (HideScriptsManager), the racks look normal.
        private static bool suspended;

        internal static void Suspend()
        {
            suspended = true;
            foreach (Visual visual in Visuals.Values)
            {
                foreach (Renderer renderer in visual.Hidden)
                    if (renderer != null) renderer.forceRenderingOff = false;
                foreach (MeshRenderer ghost in visual.Ghosts)
                    if (ghost != null) ghost.gameObject.SetActive(false);
                if (visual.Rounds != null) visual.Rounds.SetActive(false);
            }
        }

        internal static void Resume()
        {
            suspended = false;
            foreach (Visual visual in Visuals.Values)
            {
                foreach (Renderer renderer in visual.Hidden)
                    if (renderer != null) renderer.forceRenderingOff = true;
                foreach (MeshRenderer ghost in visual.Ghosts)
                    if (ghost != null) ghost.gameObject.SetActive(true);
                if (visual.Rounds != null) visual.Rounds.SetActive(true);
            }
        }

        public static void Tick()
        {
            try
            {
                if (suspended) return;
                if (Visuals.Count == 0 && Designer.Instance == null) return;   // not in the designer

                UpdateSelection();
                if (Time.unscaledTime < nextRefresh) return;
                nextRefresh = Time.unscaledTime + RefreshInterval;
                Refresh();
            }
            catch (Exception e)
            {
                if (!warned) ArmorFragmentsPlugin.Log.LogWarning("Ammo rack designer view failed: " + e);
                warned = true;
            }
        }

        private static void Refresh()
        {
            AircraftScript aircraft = Designer.Instance != null ? Designer.Instance.Aircraft : null;
            bool active = aircraft != null && aircraft.LoadContext == CraftLoadContext.Designer &&
                          aircraft.Aircraft != null && aircraft.Aircraft.Assembly != null &&
                          ArmorFragmentsPlugin.AmmoRacksEnabled.Value && ArmorFragmentsPlugin.ShowRacksInDesigner.Value &&
                          Materials();

            // The racks of the aircraft being designed, and their groups.
            Dictionary<PartScript, string> racks = new Dictionary<PartScript, string>();
            if (active)
            {
                foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
                {
                    PartScript part = data != null ? data.PartScript : null;
                    if (part == null) continue;
                    PartTags tags = TagCodec.Parse(data.Name);
                    if (tags.AmmoRack == null || tags.Armor != null || !IsFuselage(part)) continue;
                    racks[part] = AmmoRacks.GroupName(tags.AmmoRack.Group);
                }
            }

            foreach (PartScript part in Visuals.Keys.ToList())
            {
                if (part != null && racks.ContainsKey(part)) continue;
                Remove(Visuals[part]);
                Visuals.Remove(part);
            }

            foreach (KeyValuePair<PartScript, string> rack in racks)
            {
                string signature = AmmoRacks.DesignRackSignature(rack.Key, rack.Value);
                Visual visual;
                if (Visuals.TryGetValue(rack.Key, out visual))
                {
                    if (visual.Signature == signature && Intact(visual))
                    {
                        KeepHidden(visual);
                        continue;
                    }
                    Remove(visual);
                }
                Visuals[rack.Key] = Build(rack.Key, rack.Value, signature);
            }
        }

        private static bool IsFuselage(PartScript part)
        {
            foreach (PartModifierData modifier in part.Part.Modifiers)
                if (modifier is JFuselageData) return true;
            return false;
        }

        // ---- Building and removing ---------------------------------------------------------

        private static Visual Build(PartScript part, string group, string signature)
        {
            Visual visual = new Visual { Part = part, Signature = signature };
            int layer = part.gameObject.layer;

            // See-through copy of the rack's mesh; the real one is hidden.
            foreach (MeshRenderer source in XRayView.GhostSources(part).ToList())
            {
                if (source == null || !source.enabled || source.name == GhostName || source.name == RoundsName) continue;
                MeshFilter filter = source.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;

                GameObject ghost = new GameObject(GhostName);
                ghost.layer = source.gameObject.layer;
                ghost.transform.SetParent(source.transform, false);
                ghost.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                MeshRenderer renderer = ghost.AddComponent<MeshRenderer>();
                XRayView.SetMaterial(renderer, Mathf.Max(1, filter.sharedMesh.subMeshCount), ghostMaterial);
                Quiet(renderer);
                visual.Ghosts.Add(renderer);
                layer = source.gameObject.layer;

                if (!source.forceRenderingOff)
                {
                    source.forceRenderingOff = true;
                    visual.Hidden.Add(source);
                }
            }

            // The rounds, where RackGrid puts them.
            AmmoRound round;
            int cannons;
            bool mixed;
            if (AmmoRacks.DesignRound(part, group, out round, out cannons, out mixed))
            {
                RackGrid grid = RackGrid.Measure(part, round);
                if (ArmorFragmentsPlugin.DebugDumpShape.Value)
                {
                    try
                    {
                        string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "ammo-rack-shape.obj");
                        grid.DumpShape(path, part.Part.Name + " / " + round.Describe());
                        ArmorFragmentsPlugin.Log.LogInfo("Ammo rack '" + part.Part.Name + "' measured from " + grid.Method + ": " +
                            grid.Capacity + " rounds (" + grid.Describe() + "); shape written to " + path);
                    }
                    catch (Exception e)
                    {
                        ArmorFragmentsPlugin.Log.LogWarning("Ammo rack shape dump failed: " + e.Message);
                    }
                }
                if (grid.Capacity > 0)
                {
                    int sides = grid.Capacity > 6000 ? 6 : grid.Capacity > 1500 ? 8 : 12;
                    visual.RoundsMesh = BuildMesh(grid);
                    if (grid.Capacity > MaxDrawnRounds)
                        ArmorFragmentsPlugin.Log.LogWarning("Ammo rack '" + part.Part.Name + "' holds " + grid.Capacity +
                            " rounds; only the first " + MaxDrawnRounds + " are drawn in the designer (all of them count).");
                    visual.Rounds = MakeRoundsObject(part, visual.RoundsMesh, layer, false);
                }
            }
            return visual;
        }

        private static void Remove(Visual visual)
        {
            foreach (Renderer renderer in visual.Hidden)
                if (renderer != null) renderer.forceRenderingOff = false;
            foreach (MeshRenderer ghost in visual.Ghosts)
                if (ghost != null) Object.Destroy(ghost.gameObject);
            if (visual.Rounds != null) Object.Destroy(visual.Rounds);
            if (visual.RoundsMesh != null) Object.Destroy(visual.RoundsMesh);
            visual.Hidden.Clear();
            visual.Ghosts.Clear();
        }

        // The game may rebuild a fuselage's renderers (reshaping): then our copies are gone.
        private static bool Intact(Visual visual)
        {
            foreach (MeshRenderer ghost in visual.Ghosts)
                if (ghost == null) return false;
            return true;
        }

        private static void KeepHidden(Visual visual)
        {
            foreach (Renderer renderer in visual.Hidden)
                if (renderer != null && !renderer.forceRenderingOff) renderer.forceRenderingOff = true;
        }

        // Every frame (cheap): tint the selected rack, since the game's highlight is on the hidden mesh.
        private static void UpdateSelection()
        {
            foreach (Visual visual in Visuals.Values)
            {
                if (visual.Part == null || visual.Part.PartMaterialScript == null) continue;
                bool selected = visual.Part.PartMaterialScript.IsSelected;
                if (selected == visual.ShownSelected) continue;
                visual.ShownSelected = selected;
                foreach (MeshRenderer ghost in visual.Ghosts)
                {
                    if (ghost == null) continue;
                    MeshFilter filter = ghost.GetComponent<MeshFilter>();
                    int subMeshes = filter != null && filter.sharedMesh != null ? Mathf.Max(1, filter.sharedMesh.subMeshCount) : 1;
                    XRayView.SetMaterial(ghost, subMeshes, selected ? ghostSelectedMaterial : ghostMaterial);
                }
            }
        }

        // The rounds still in a rack as one mesh (also used by the X-ray view in flight, AmmoRackXRay).
        internal static Mesh BuildMesh(RackGrid grid)
        {
            int sides = grid.Capacity > 6000 ? 6 : grid.Capacity > 1500 ? 8 : 12;
            return grid.BuildRoundsMesh(MaxDrawnRounds, sides);
        }

        // A child of the part that draws the rounds mesh in meters (the part's own scale cancelled).
        // Burning racks get a glowing case color.
        internal static GameObject MakeRoundsObject(PartScript part, Mesh mesh, int layer, bool burning)
        {
            GameObject rounds = new GameObject(RoundsName);
            rounds.layer = layer;
            rounds.transform.SetParent(part.transform, false);
            Vector3 scale = part.transform.lossyScale;
            rounds.transform.localScale = new Vector3(Inverse(scale.x), Inverse(scale.y), Inverse(scale.z));
            rounds.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = rounds.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { burning ? burningMaterial : caseMaterial, shellMaterial };
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            return rounds;
        }

        private static float Inverse(float value)
        {
            return Mathf.Abs(value) > 1e-6f ? 1f / value : 1f;
        }

        private static void Quiet(MeshRenderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        // ---- Materials ---------------------------------------------------------------------

        // Rounds: a lit, solid shader if the build has one (depth-sorted, shaded), else the see-through one.
        private static readonly string[] SolidShaders =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Unlit",
        };

        internal static bool Materials()
        {
            if (materialsTried) return ghostMaterial != null && caseMaterial != null;
            materialsTried = true;

            Shader seeThrough = RayDrawer.GetShader();
            if (seeThrough == null)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack designer view off: no see-through shader in this build");
                return false;
            }
            ghostMaterial = MakeMaterial(seeThrough, new Color(0.55f, 0.78f, 1f, 0.16f), 3000);
            ghostSelectedMaterial = MakeMaterial(seeThrough, new Color(1f, 0.62f, 0.2f, 0.24f), 3000);

            Shader solid = null;
            foreach (string name in SolidShaders)
            {
                solid = Shader.Find(name);
                if (solid != null)
                {
                    ArmorFragmentsPlugin.Log.LogInfo("Ammo rack rounds are drawn with shader '" + name + "'");
                    break;
                }
            }
            if (solid == null)
            {
                solid = seeThrough;
                ArmorFragmentsPlugin.Log.LogInfo("Ammo rack rounds use the see-through shader (no solid one found)");
            }
            caseMaterial = MakeMaterial(solid, new Color(0.82f, 0.62f, 0.24f, 1f), -1);
            shellMaterial = MakeMaterial(solid, new Color(0.38f, 0.40f, 0.43f, 1f), -1);
            burningMaterial = MakeMaterial(solid, new Color(1f, 0.32f, 0.08f, 1f), -1);
            if (burningMaterial.HasProperty("_EmissionColor"))
            {
                burningMaterial.EnableKeyword("_EMISSION");
                burningMaterial.SetColor("_EmissionColor", new Color(1.2f, 0.35f, 0.05f));
            }
            return true;
        }

        private static Material MakeMaterial(Shader shader, Color color, int renderQueue)
        {
            Material material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (renderQueue >= 0) material.renderQueue = renderQueue;
            return material;
        }
    }

    // The designer's screenshot dialog hides UI gizmos with HideScriptsManager and restores them after:
    // the racks go back to their normal look meanwhile, so screenshots don't show the see-through view.
    [HarmonyLib.HarmonyPatch(typeof(Assets.Scripts.Design.UI.HideScriptsManager), "HideForScreenshot")]
    internal static class RackScreenshotHidePatch
    {
        private static void Postfix()
        {
            RackVisualizer.Suspend();
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(Assets.Scripts.Design.UI.HideScriptsManager), "Restore")]
    internal static class RackScreenshotRestorePatch
    {
        private static void Postfix()
        {
            RackVisualizer.Resume();
        }
    }
}
