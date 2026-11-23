// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using UnityEngine;

namespace ArmorFragments.Fluid
{
    // The colour of the ground a blast meets, for the dust it lifts: the average colour of the texture (or the terrain layers) under the point,
    // times the material's tint. The first few lookups are written to the log, so that when a kind of ground is not understood the log says
    // what it is made of.
    internal static class GroundColor
    {
        private static readonly Dictionary<int, Color> TextureColors = new Dictionary<int, Color>();
        private static int logged;

        // The dust is the ground, a little paler and greyer (it is fine powder, not the surface).
        public static Color DustFrom(Color ground)
        {
            Color pale = Color.Lerp(ground, new Color(0.78f, 0.75f, 0.70f, 1f), 0.25f);
            return new Color(Mathf.Clamp01(pale.r * 1.1f), Mathf.Clamp01(pale.g * 1.1f), Mathf.Clamp01(pale.b * 1.1f), 1f);
        }

        // Reads the colour of what the ray hit. False when it cannot be read (then the dust keeps its default colour).
        public static bool TryGet(RaycastHit hit, out Color color)
        {
            color = Color.white;
            string how = "";
            bool ok = false;
            try
            {
                Collider collider = hit.collider;
                if (collider == null) return false;

                // A Unity terrain: the layers' textures, weighted by how much of each is painted under the point.
                Terrain terrain = collider.GetComponent<Terrain>();
                if (terrain == null && collider is TerrainCollider) terrain = Terrain.activeTerrain;
                if (terrain != null && terrain.terrainData != null)
                {
                    ok = TerrainColor(terrain, hit.point, out color);
                    how = ok ? "terrain layers" : "terrain without readable layers";
                }

                if (!ok)
                {
                    Renderer renderer = collider.GetComponent<Renderer>();
                    if (renderer == null) renderer = collider.GetComponentInParent<Renderer>();
                    if (renderer != null && renderer.sharedMaterial != null)
                    {
                        ok = MaterialColor(renderer.sharedMaterial, out color);
                        how = ok ? "material " + renderer.sharedMaterial.name + " (" + (renderer.sharedMaterial.shader != null ? renderer.sharedMaterial.shader.name : "?") + ")"
                                 : "material " + renderer.sharedMaterial.name + " has no colour or texture that can be read";
                    }
                    else if (terrain == null) how = "no renderer on the collider";
                }
            }
            catch (System.Exception e)
            {
                how = "failed: " + e.Message;
                ok = false;
            }

            if (logged < 4)
            {
                logged++;
                Collider c = hit.collider;
                ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] ground under the blast: '" + (c != null ? c.name : "?") + "' (" + (c != null ? c.GetType().Name : "?") + ", layer "
                    + (c != null ? c.gameObject.layer : -1) + "): " + how + (ok ? ", colour " + color.r.ToString("F2") + "," + color.g.ToString("F2") + "," + color.b.ToString("F2") : ""));
            }
            return ok;
        }

        private static bool TerrainColor(Terrain terrain, Vector3 point, out Color color)
        {
            color = Color.white;
            TerrainData data = terrain.terrainData;
            TerrainLayer[] layers = data.terrainLayers;
            if (layers == null || layers.Length == 0) return false;
            Vector3 local = point - terrain.transform.position;
            float u = Mathf.Clamp01(local.x / Mathf.Max(data.size.x, 0.01f)), v = Mathf.Clamp01(local.z / Mathf.Max(data.size.z, 0.01f));
            int x = Mathf.Clamp(Mathf.RoundToInt(u * (data.alphamapWidth - 1)), 0, data.alphamapWidth - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(v * (data.alphamapHeight - 1)), 0, data.alphamapHeight - 1);
            float[,,] weights = data.GetAlphamaps(x, y, 1, 1);
            int count = Mathf.Min(layers.Length, weights.GetLength(2));
            Color sum = Color.black;
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                float w = weights[0, 0, i];
                if (w < 0.01f || layers[i] == null) continue;
                Color average;
                if (!Average(layers[i].diffuseTexture, out average)) continue;
                sum += average * w;
                total += w;
            }
            if (total < 0.05f) return false;
            color = sum / total;
            color.a = 1f;
            return true;
        }

        private static bool MaterialColor(Material m, out Color color)
        {
            color = Color.white;
            bool any = false;
            string[] colorNames = { "_BaseColor", "_Color" };
            foreach (string name in colorNames)
                if (m.HasProperty(name)) { color = m.GetColor(name); any = true; break; }
            string[] textureNames = { "_BaseMap", "_MainTex", "_BaseColorMap" };
            foreach (string name in textureNames)
            {
                if (!m.HasProperty(name)) continue;
                Color average;
                if (Average(m.GetTexture(name), out average))
                {
                    color = any ? new Color(color.r * average.r, color.g * average.g, color.b * average.b, 1f) : average;
                    any = true;
                }
                break;
            }
            color.a = 1f;
            return any;
        }

        // The average colour of a texture: it is squeezed into a few pixels on the graphics card and those are read back (works for textures
        // the CPU cannot read). Remembered per texture.
        private static bool Average(Texture texture, out Color color)
        {
            color = Color.white;
            if (texture == null) return false;
            int id = texture.GetInstanceID();
            if (TextureColors.TryGetValue(id, out color)) return true;
            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D read = null;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                read = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, 16, 16), 0, 0);
                Color[] pixels = read.GetPixels();
                Color sum = Color.black;
                foreach (Color p in pixels) sum += p;
                color = sum / pixels.Length;
                color.a = 1f;
                TextureColors[id] = color;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (read != null) Object.Destroy(read);
            }
        }

        public static void Shutdown()
        {
            TextureColors.Clear();
            logged = 0;
        }
    }
}
// RELEASE-STRIP-END
