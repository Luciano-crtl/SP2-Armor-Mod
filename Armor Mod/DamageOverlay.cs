using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // The impact camera's damage view: the armor's damage map drawn over each marked plate, in red. How damaged a pixel is comes from
    // the plate's height field (the same marks the normal map and the bump map show): untouched = nothing, a little = a lighter,
    // see-through red, a lot (or a hole) = dark red, nearly solid. The colors are in the vehicle's damage atlas (DamageAtlas, filled by
    // the same worker that draws the normals); here each marked plate gets a copy of its own mesh (whose UV0 already points into the
    // atlas) that shows it.
    //
    // The copies live on the impact camera's ghost layer, which only that camera draws, and are children of the plate's renderer, so
    // they move with it. They exist only while the impact camera is open: Dispose destroys every one.
    internal sealed class DamageOverlay
    {
        public const string ObjectName = "ArmorDamageView";
        private const float SyncInterval = 0.4f;
        private const float Lift = 0.0015f;   // meters off the plate, so the copy does not fight the surface for the depth buffer

        private readonly AircraftScript aircraft;
        private readonly int layer;
        private readonly Dictionary<PartScript, GameObject> objects = new Dictionary<PartScript, GameObject>();
        private readonly List<Mesh> meshes = new List<Mesh>();
        private VehicleMaps maps;
        private Material material;
        private float nextSync;

        public DamageOverlay(AircraftScript aircraft, int layer)
        {
            this.aircraft = aircraft;
            this.layer = layer;
        }

        public int Count
        {
            get { return objects.Count; }
        }

        // Every moment: plates that have been marked since the last look get their copy. (A vehicle's maps can also be made after the
        // camera opens, when the first impact is worked out, or given back and made again.)
        public void Tick()
        {
            if (Time.unscaledTime < nextSync) return;
            nextSync = Time.unscaledTime + SyncInterval;

            VehicleMaps now = PlateDamageMaps.MapsFor(aircraft);
            if (now != maps)
            {
                Clear();
                maps = now;
            }
            if (maps == null) return;

            maps.EnsureDamage();
            if (material == null)
            {
                Shader shader = RayDrawer.GetShader();   // Sprites/Default where it exists: it draws the texture with its alpha
                if (shader == null) return;
                material = new Material(shader) { name = "ArmorFragments damage view", renderQueue = 3100 };
                if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            }
            material.mainTexture = maps.DamageAtlas;

            foreach (KeyValuePair<PartScript, PlateVisual> pair in maps.Plates)
            {
                PlateVisual plate = pair.Value;
                if (!plate.Applied || pair.Key == null || objects.ContainsKey(pair.Key)) continue;
                Make(pair.Key, plate);
            }
        }

        private void Make(PartScript part, PlateVisual plate)
        {
            Renderer renderer = plate.Info.Map.Renderer;
            Mesh mesh = plate.Info.Mesh;
            if (renderer == null || mesh == null || !mesh.isReadable) return;

            // A copy of the plate's mesh (UV0 and all), each vertex a little way out along its normal.
            Mesh copy = Object.Instantiate(mesh);
            copy.name = ObjectName;
            Vector3[] vertices = copy.vertices;
            Vector3[] normals = copy.normals;
            if (normals.Length == vertices.Length)
            {
                float scale = Mathf.Max(renderer.transform.lossyScale.magnitude / 1.732f, 0.0001f);
                for (int i = 0; i < vertices.Length; i++) vertices[i] += normals[i] * (Lift / scale);
                copy.vertices = vertices;
            }
            meshes.Add(copy);

            GameObject go = new GameObject(ObjectName);
            go.layer = layer;
            go.transform.SetParent(renderer.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = copy;
            MeshRenderer view = go.AddComponent<MeshRenderer>();
            view.sharedMaterial = material;
            view.shadowCastingMode = ShadowCastingMode.Off;
            view.receiveShadows = false;
            view.lightProbeUsage = LightProbeUsage.Off;
            view.reflectionProbeUsage = ReflectionProbeUsage.Off;
            view.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            objects[part] = go;
        }

        private void Clear()
        {
            foreach (GameObject go in objects.Values)
                if (go != null) Object.Destroy(go);
            objects.Clear();
            foreach (Mesh mesh in meshes)
                if (mesh != null) Object.Destroy(mesh);
            meshes.Clear();
        }

        public void Dispose()
        {
            Clear();
            if (material != null) Object.Destroy(material);
            material = null;
            maps = null;
        }
    }
}
