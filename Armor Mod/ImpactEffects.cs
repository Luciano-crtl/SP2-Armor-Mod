using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using UnityEngine;

namespace ArmorFragments
{
    // What a shell hitting armor looks like in the air: a flash, a spray of sparks, a puff of paint dust and some smoke. Only a look (nothing
    // here touches the energy model): it is played where the marks are made, when the shell's ray arrives, from five particle systems kept for
    // the whole session (one Emit call per particle, no object per impact).
    //   Blocked    flash, a fountain of sparks off the plate, dust, a little smoke
    //   Ricochet   a short flash and a stream of long sparks along the way the shell bounced
    //   Penetrated the same as blocked on the way in, and a spray of hot sparks, embers and dark smoke out of the back
    //   Burst      (HE going off on the plate) a big flash, a fireball of smoke and sparks all round
    internal static class ImpactEffects
    {
        private sealed class Pending
        {
            public float At;
            public PartScript Part;
            public Vector3 Point, Normal, Direction;          // world, as seen when the impact happened
            public Vector3 LocalPoint, LocalNormal, LocalDirection;   // part-local, so a moving vehicle carries them along until the shell arrives
            public Vector3 Velocity;                           // the vehicle's own velocity
            public DamageKind Kind;
            public float Caliber, Strength, Angle, Thickness;
            public Color Paint;
        }

        private static readonly List<Pending> Queue = new List<Pending>();
        private const int MaxPerFrame = 4;
        private const float MaxDistance = 600f;   // further than this nobody sees the sparks

        private static GameObject root;
        private static ParticleSystem sparks, flash, smoke, dust, embers;
        private static Material puffMaterial, sparkMaterial, litMaterial;
        private static Texture2D puffTexture, sparkTexture, smokeTexture;
        private static bool failed;

        // ---- Asking for an effect ---------------------------------------------------------------------

        // A shell has been resolved on an armor plate. direction: the way it was going (for a ricochet: the way it bounced off).
        // normalFacing: the plate's normal on the side the shell came from. delay: seconds until the shell's ray arrives.
        public static void Play(PartScript part, DamageKind kind, Vector3 point, Vector3 normalFacing, Vector3 direction,
                                float caliberMeters, float strength, float angleDeg, float thicknessMeters, float delay)
        {
            if (failed || !ArmorFragmentsPlugin.ImpactEffectsEnabled.Value || part == null) return;
            if (kind != DamageKind.Blocked && kind != DamageKind.Penetrated && kind != DamageKind.Ricochet && kind != DamageKind.Burst) return;
            try
            {
                Camera camera = Camera.main;
                if (camera != null && (camera.transform.position - point).sqrMagnitude > MaxDistance * MaxDistance) return;
                if (Queue.Count > 40) return;   // a flood of shells: the rest would only be noise

                Transform t = part.transform;
                Pending p = new Pending
                {
                    At = Time.time + Mathf.Max(delay, 0f),
                    Part = part,
                    Point = point,
                    Normal = normalFacing.normalized,
                    Direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : -normalFacing.normalized,
                    LocalPoint = t.InverseTransformPoint(point),
                    LocalNormal = t.InverseTransformDirection(normalFacing).normalized,
                    LocalDirection = t.InverseTransformDirection(direction).normalized,
                    Kind = kind,
                    Caliber = Mathf.Max(caliberMeters, 0.005f),
                    Strength = Mathf.Clamp01(strength),
                    Angle = angleDeg,
                    Thickness = Mathf.Max(thicknessMeters, 0f),
                    Velocity = part.Aircraft != null ? part.Aircraft.Velocity : Vector3.zero,
                    Paint = PaintOf(part),
                };
                Queue.Add(p);
            }
            catch (System.Exception e)
            {
                failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[FX] impact effects switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static Color PaintOf(PartScript part)
        {
            Color color = new Color(0.45f, 0.45f, 0.4f, 1f);
            try
            {
                float metallic, smoothness;
                if (part.Aircraft != null && part.Aircraft.Theme != null)
                    VehicleMaps.ReadPaint(part.Aircraft.Theme, part, out color, out metallic, out smoothness);
            }
            catch (System.Exception) { }
            return color;
        }

        // ---- Playing ----------------------------------------------------------------------------------

        // Every frame (the plugin's Update): the effects whose shell has arrived.
        public static void Tick()
        {
            if (Queue.Count == 0) return;
            float now = Time.time;
            int played = 0;
            for (int i = 0; i < Queue.Count && played < MaxPerFrame; i++)
            {
                Pending p = Queue[i];
                if (p.At > now) continue;
                Queue.RemoveAt(i);
                i--;
                played++;
                try
                {
                    Emit(p);
                }
                catch (System.Exception e)
                {
                    failed = true;
                    Queue.Clear();
                    ArmorFragmentsPlugin.Log.LogWarning("[FX] impact effects switched off: " + e.GetType().Name + ": " + e.Message);
                    return;
                }
            }
        }

        private static void Emit(Pending p)
        {
            if (!ArmorFragmentsPlugin.ImpactEffectsEnabled.Value || !EnsureSystems()) return;

            // Where it is now: the plate may have moved since the shell was resolved (a slow ray on the impact camera), and may be gone.
            Vector3 point = p.Point, normal = p.Normal, direction = p.Direction;
            if (p.Part != null && p.Part.gameObject.activeInHierarchy)
            {
                Transform t = p.Part.transform;
                point = t.TransformPoint(p.LocalPoint);
                normal = t.TransformDirection(p.LocalNormal).normalized;
                direction = t.TransformDirection(p.LocalDirection).normalized;
            }

            float amount = Mathf.Clamp(ArmorFragmentsPlugin.ImpactEffectsAmount.Value, 0.3f, 2f);
            float size = Mathf.Clamp(p.Caliber / 0.075f, 0.4f, 3f);   // 1 = a 75 mm shell
            float s = p.Strength;
            Vector3 along = p.Velocity;

            switch (p.Kind)
            {
                case DamageKind.Blocked:
                {
                    Vector3 bounce = Vector3.Reflect(direction, normal);
                    Flash(point + normal * 0.02f, along, Mathf.Clamp(p.Caliber * 5f, 0.12f, 1.4f) * (0.7f + 0.6f * s), 0.9f);
                    Sparks(point, normal, bounce, along, Mathf.RoundToInt((12f + 40f * s) * size * amount), 3f, 12f * (0.6f + 0.4f * size), 1f, false);
                    Dust(point, normal, along, p.Paint, Mathf.RoundToInt((5f + 6f * s) * size * amount), size);
                    Smoke(point, normal, along, 1 + Mathf.RoundToInt(2f * s * amount), size, 0.3f);
                    break;
                }
                case DamageKind.Ricochet:
                {
                    float graze = Mathf.Clamp01(p.Angle / 90f);
                    Flash(point + normal * 0.02f, along, Mathf.Clamp(p.Caliber * 3f, 0.08f, 0.8f), 0.7f);
                    // A stream of long sparks the way the shell went; a few more fall off to the sides.
                    Sparks(point, normal, direction, along, Mathf.RoundToInt((16f + 40f * graze) * size * amount), 6f, 26f, 0.14f, true);
                    Sparks(point, normal, direction, along, Mathf.RoundToInt(6f * size * amount), 2f, 9f, 1f, false);
                    Dust(point, normal, along, p.Paint, Mathf.RoundToInt(4f * size * amount), size);
                    break;
                }
                case DamageKind.Penetrated:
                {
                    Vector3 bounce = Vector3.Reflect(direction, normal);
                    Flash(point + normal * 0.02f, along, Mathf.Clamp(p.Caliber * 5f, 0.12f, 1.4f) * (0.8f + 0.6f * s), 0.9f);
                    Sparks(point, normal, bounce, along, Mathf.RoundToInt((15f + 35f * s) * size * amount), 3f, 12f * (0.6f + 0.4f * size), 1f, false);
                    Dust(point, normal, along, p.Paint, Mathf.RoundToInt((6f + 6f * s) * size * amount), size);
                    Smoke(point, normal, along, 2, size, 0.35f);

                    // Out of the back: where the shell came through, further along its way by the plate's thickness; hot and dark.
                    float cos = Mathf.Max(Mathf.Abs(Vector3.Dot(direction, normal)), 0.25f);
                    Vector3 exit = point + direction * Mathf.Min(p.Thickness / cos, 1.5f);
                    Vector3 outward = -normal;
                    Flash(exit + outward * 0.02f, along, Mathf.Clamp(p.Caliber * 6.5f, 0.15f, 1.8f) * (0.8f + 0.6f * s), 0.85f);
                    Spray(exit, direction, along, Mathf.RoundToInt((25f + 60f * s) * size * amount), size);
                    Smoke(exit, outward, along, 3 + Mathf.RoundToInt(2f * s), size, 0.7f);
                    break;
                }
                case DamageKind.Burst:
                {
                    Flash(point + normal * 0.05f, along, Mathf.Clamp(p.Caliber * 14f, 0.4f, 4f) * (0.8f + 0.4f * s), 1f);
                    Sparks(point, normal, normal, along, Mathf.RoundToInt((30f + 30f * s) * size * amount), 6f, 30f, 1f, false);
                    Dust(point, normal, along, p.Paint, Mathf.RoundToInt(12f * size * amount), size * 1.6f);
                    Smoke(point + normal * 0.1f, normal, along, 4 + Mathf.RoundToInt(2f * amount), size * 2.2f, 0.9f);
                    break;
                }
            }
        }

        // ---- The pieces -------------------------------------------------------------------------------

        // One wisp of thin smoke (the smoke that comes out of a hot barrel): a particle of the smoke system, lit by the scene, rising slowly.
        public static void Wisp(Vector3 position, Vector3 velocity, float size, float lifetime, Color color)
        {
            if (!EnsureSystems()) return;
            // Each puff is turned at random and turns slowly: with the same round shape every time they would read as pearls on a string.
            smoke.Emit(new ParticleSystem.EmitParams { position = position, velocity = velocity, startSize = size, startLifetime = lifetime, startColor = color,
                                                       rotation = Random.Range(0f, 6.2832f), angularVelocity = Random.Range(-0.5f, 0.5f) }, 1);
        }

        // A glowing speck that comes off a very hot barrel: an ember of the embers system, drifting up slowly.
        public static void Ember(Vector3 position, Vector3 velocity, float size, float lifetime, Color color)
        {
            if (!EnsureSystems()) return;
            embers.Emit(new ParticleSystem.EmitParams { position = position, velocity = velocity, startSize = size, startLifetime = lifetime, startColor = color }, 1);
        }

        // The fine matter of a muzzle blast: sparks and burning grains that fly forward (and out to the sides through a muzzle brake), slower
        // embers that hang and drift, and dark flecks of soot. `size` is the calibre / 75 mm, `amount` how much this shot burns (0..1.4), `strength`
        // the Smoke/MuzzleSparks setting; `vehicle` is the speed of the vehicle that fired.
        public static void MuzzleBlast(Vector3 muzzle, Vector3 forward, Vector3 vehicle, float size, float amount, float strength, bool brake)
        {
            if (strength <= 0.01f || !EnsureSystems()) return;
            float fine = Mathf.Clamp(0.5f + 0.5f * size, 0.5f, 2f);
            int sparkCount = Mathf.Clamp(Mathf.RoundToInt((10f + 26f * Mathf.Sqrt(size)) * amount * strength), 0, 90);
            ParticleSystemRenderer renderer = sparks.GetComponent<ParticleSystemRenderer>();
            renderer.lengthScale = 2.2f;
            Vector3 side = Vector3.Cross(forward, Vector3.up);
            if (side.sqrMagnitude < 1e-3f) side = Vector3.right;
            side.Normalize();
            for (int i = 0; i < sparkCount; i++)
            {
                bool sideways = brake && Random.value < 0.3f;
                Vector3 axis = sideways ? side * (Random.value < 0.5f ? 1f : -1f) + forward * 0.25f : forward;
                Vector3 d = Scatter(axis.normalized, sideways ? 22f : 24f);
                ParticleSystem.EmitParams e = new ParticleSystem.EmitParams
                {
                    position = muzzle + forward * 0.1f,
                    velocity = vehicle + d * Random.Range(8f, 26f) * Mathf.Sqrt(fine),
                    startSize = Random.Range(0.012f, 0.03f) * fine,
                    startLifetime = Random.Range(0.25f, 0.8f),
                    startColor = Color.Lerp(new Color(1f, 0.95f, 0.7f, 1f), new Color(1f, 0.55f, 0.15f, 1f), Random.value),
                };
                sparks.Emit(e, 1);
            }
            int emberCount = Mathf.Clamp(Mathf.RoundToInt((3f + 9f * Mathf.Sqrt(size)) * amount * strength), 0, 40);
            for (int i = 0; i < emberCount; i++)
            {
                Vector3 d = Scatter(forward, 40f);
                ParticleSystem.EmitParams e = new ParticleSystem.EmitParams
                {
                    position = muzzle + forward * Random.Range(0.1f, 0.6f * fine),
                    velocity = vehicle + d * Random.Range(2f, 9f) * Mathf.Sqrt(fine),
                    startSize = Random.Range(0.03f, 0.075f) * fine,
                    startLifetime = Random.Range(0.7f, 2.0f),
                    startColor = Color.Lerp(new Color(1f, 0.8f, 0.35f, 1f), new Color(1f, 0.45f, 0.12f, 1f), Random.value),
                };
                embers.Emit(e, 1);
            }
            int fleckCount = Mathf.Clamp(Mathf.RoundToInt((4f + 12f * Mathf.Sqrt(size)) * amount * strength), 0, 50);
            for (int i = 0; i < fleckCount; i++)
            {
                Vector3 d = Scatter(forward, 50f);
                ParticleSystem.EmitParams e = new ParticleSystem.EmitParams
                {
                    position = muzzle + forward * Random.Range(0.1f, 0.5f * fine),
                    velocity = vehicle + d * Random.Range(3f, 14f) * Mathf.Sqrt(fine),
                    startSize = Random.Range(0.03f, 0.09f) * fine,
                    startLifetime = Random.Range(0.8f, 2.2f),
                    startColor = new Color(0.06f, 0.055f, 0.05f, Random.Range(0.35f, 0.7f)),
                };
                smoke.Emit(e, 1);
            }
        }

        private static Vector3 Scatter(Vector3 axis, float coneDegrees)
        {
            Vector3 random = Random.onUnitSphere;
            Vector3 side = Vector3.Cross(axis, random);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.Cross(axis, Vector3.up);
            side.Normalize();
            return (Quaternion.AngleAxis(Random.Range(0f, coneDegrees), side) * axis).normalized;
        }

        private static void Flash(Vector3 position, Vector3 velocity, float diameter, float alpha)
        {
            ParticleSystem.EmitParams e = new ParticleSystem.EmitParams
            {
                position = position,
                velocity = velocity,
                startSize = diameter,
                startLifetime = 0.09f,
                startColor = new Color(1f, 0.82f, 0.45f, alpha),
            };
            flash.Emit(e, 1);
        }

        // 'axis' is the average way the sparks go (mixed with the surface normal so they leave the plate), spread over a cone; lengthScale is
        // how stretched each one is (a ricochet's are long streaks).
        private static void Sparks(Vector3 position, Vector3 normal, Vector3 axis, Vector3 vehicle, int count, float minSpeed, float maxSpeed,
                                   float cone, bool streaks)
        {
            if (count <= 0) return;
            count = Mathf.Min(count, 160);
            ParticleSystemRenderer renderer = sparks.GetComponent<ParticleSystemRenderer>();
            renderer.lengthScale = streaks ? 3.2f : 1.6f;
            Vector3 mix = (normal * (streaks ? 0.25f : 0.7f) + axis.normalized * 0.7f).normalized;
            float spread = streaks ? 14f : 70f * Mathf.Clamp(cone, 0.3f, 1f);
            for (int i = 0; i < count; i++)
            {
                Vector3 d = Scatter(mix, spread);
                if (Vector3.Dot(d, normal) < 0.05f) d = (d + normal * 0.6f).normalized;   // never into the plate
                float speed = Random.Range(minSpeed, maxSpeed);
                ParticleSystem.EmitParams e = new ParticleSystem.EmitParams
                {
                    position = position + normal * 0.01f,
                    velocity = vehicle + d * speed,
                    startSize = Random.Range(0.008f, 0.022f) * (streaks ? 1.3f : 1f),
                    startLifetime = Random.Range(0.25f, streaks ? 0.7f : 0.9f),
                    startColor = Color.Lerp(new Color(1f, 0.95f, 0.7f, 1f), new Color(1f, 0.6f, 0.2f, 1f), Random.value),
                };
                sparks.Emit(e, 1);
            }
        }

        // Out of the back of a plate: hot sparks and slower embers thrown the way the shell went, in a cone.
        private static void Spray(Vector3 position, Vector3 direction, Vector3 vehicle, int count, float size)
        {
            if (count <= 0) return;
            count = Mathf.Min(count, 160);
            ParticleSystemRenderer renderer = sparks.GetComponent<ParticleSystemRenderer>();
            renderer.lengthScale = 2.2f;
            for (int i = 0; i < count; i++)
            {
                Vector3 d = Scatter(direction, 28f);
                ParticleSystem.EmitParams e = new ParticleSystem.EmitParams
                {
                    position = position,
                    velocity = vehicle + d * Random.Range(6f, 30f),
                    startSize = Random.Range(0.01f, 0.028f) * Mathf.Sqrt(size),
                    startLifetime = Random.Range(0.3f, 1.0f),
                    startColor = Color.Lerp(new Color(1f, 0.85f, 0.45f, 1f), new Color(1f, 0.45f, 0.1f, 1f), Random.value),
                };
                sparks.Emit(e, 1);
            }
            for (int i = 0; i < Mathf.Max(count / 4, 2); i++)   // embers: bigger, slower, they hang in the air and glow out
            {
                Vector3 d = Scatter(direction, 40f);
                ParticleSystem.EmitParams e = new ParticleSystem.EmitParams
                {
                    position = position,
                    velocity = vehicle + d * Random.Range(1.5f, 8f),
                    startSize = Random.Range(0.02f, 0.05f) * Mathf.Sqrt(size),
                    startLifetime = Random.Range(0.8f, 1.8f),
                    startColor = new Color(1f, 0.5f + 0.3f * Random.value, 0.12f, 0.95f),
                };
                embers.Emit(e, 1);
            }
        }

        // Specks of paint blown off the plate, in the plate's own color.
        private static void Dust(Vector3 position, Vector3 normal, Vector3 vehicle, Color paint, int count, float size)
        {
            if (count <= 0) return;
            count = Mathf.Min(count, 60);
            for (int i = 0; i < count; i++)
            {
                Vector3 d = Scatter(normal, 75f);
                Color c = Color.Lerp(paint, new Color(0.35f, 0.33f, 0.3f, 1f), Random.Range(0f, 0.35f));
                c.a = Random.Range(0.45f, 0.8f);
                ParticleSystem.EmitParams e = new ParticleSystem.EmitParams
                {
                    position = position + normal * 0.01f,
                    velocity = vehicle + d * Random.Range(0.8f, 4.5f),
                    startSize = Random.Range(0.025f, 0.075f) * size,
                    startLifetime = Random.Range(0.5f, 1.2f),
                    startColor = c,
                };
                dust.Emit(e, 1);
            }
        }

        // Smoke: slow, gray, growing and thinning out; 'dark' is how black it is (0 gray, 1 almost black).
        private static void Smoke(Vector3 position, Vector3 normal, Vector3 vehicle, int count, float size, float dark)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 d = Scatter(normal, 50f);
                float shade = Mathf.Lerp(0.55f, 0.1f, dark) + Random.Range(-0.05f, 0.05f);
                ParticleSystem.EmitParams e = new ParticleSystem.EmitParams
                {
                    position = position + normal * 0.03f,
                    velocity = vehicle + d * Random.Range(0.3f, 2.2f),
                    startSize = Random.Range(0.12f, 0.24f) * size,
                    startLifetime = Random.Range(1.3f, 2.8f),
                    startColor = new Color(shade, shade, shade, Random.Range(0.28f, 0.45f)),
                };
                smoke.Emit(e, 1);
            }
        }

        // ---- The particle systems ---------------------------------------------------------------------

        private static bool EnsureSystems()
        {
            if (failed) return false;
            if (root != null) return true;
            try
            {
                Shader shader = RayDrawer.GetShader();   // the see-through shader that exists in this build (Sprites/Default)
                if (shader == null)
                {
                    failed = true;
                    ArmorFragmentsPlugin.Log.LogWarning("[FX] impact effects need a see-through shader and the build has none of those the mod tries");
                    return false;
                }
                puffTexture = SoftDisc(48, 1.6f);
                sparkTexture = SoftDisc(16, 3.5f);
                smokeTexture = CloudDisc(128);
                puffMaterial = new Material(shader) { name = "ArmorFragments puff", mainTexture = puffTexture };
                sparkMaterial = new Material(shader) { name = "ArmorFragments spark", mainTexture = sparkTexture };
                litMaterial = MakeLitMaterial();   // smoke and dust: lit by the scene (null = they use the plain see-through one)

                root = new GameObject("ArmorFragments impact effects");
                root.layer = ImpactCamera.EffectsLayer;   // the main camera draws it; the impact camera never does
                Object.DontDestroyOnLoad(root);

                flash = Make("flash", puffMaterial, ParticleSystemRenderMode.Billboard, 40, 0f, 0f, 0f,
                             Curve(1f, 0.6f, 0f), Fade(new Color(1f, 0.9f, 0.6f), new Color(1f, 0.55f, 0.2f)));
                sparks = Make("sparks", sparkMaterial, ParticleSystemRenderMode.Stretch, 1500, 1.3f, 0.35f, 0.8f,
                              Curve(1f, 0.7f, 0.15f), Fade(new Color(1f, 0.95f, 0.75f), new Color(1f, 0.45f, 0.1f)));
                embers = Make("embers", puffMaterial, ParticleSystemRenderMode.Billboard, 300, 0.5f, 0.6f, 0.9f,
                              Curve(1f, 0.8f, 0.2f), Fade(new Color(1f, 0.8f, 0.35f), new Color(0.8f, 0.15f, 0.05f)));
                dust = Make("dust", litMaterial ?? puffMaterial, ParticleSystemRenderMode.Billboard, 400, 0.25f, 1.6f, 0.9f,
                            Curve(0.6f, 1f, 1.5f), Fade(Color.white, Color.white));
                smoke = Make("smoke", litMaterial ?? puffMaterial, ParticleSystemRenderMode.Billboard, 1200, -0.02f, 1.4f, 0.9f,
                             Curve(0.45f, 1f, 1.6f), Fade(Color.white, Color.white));
                ArmorFragmentsPlugin.Log.LogInfo("[FX] impact effects ready: flash, sparks and embers glow with '" + shader.name + "'; smoke and dust use '" +
                                                 (litMaterial != null ? litMaterial.shader.name + "' (lit by the scene)" : shader.name + "' (not lit)"));
                return true;
            }
            catch (System.Exception e)
            {
                failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[FX] impact effects could not be made: " + e.GetType().Name + ": " + e.Message);
                Shutdown();
                return false;
            }
        }

        // The URP particle shaders that take the scene's light, best first. Smoke and dust are matter that the sun and the sky light (and the
        // shadow of a vehicle darkens); the flash, the sparks and the embers are glowing, so they stay with the self-lit see-through shader.
        private static readonly string[] LitShaderNames =
        {
            "Universal Render Pipeline/Particles/Lit",
            "Universal Render Pipeline/Particles/Simple Lit",
        };

        private static Material MakeLitMaterial()
        {
            if (!ArmorFragmentsPlugin.ImpactEffectsLitSmoke.Value) return null;
            foreach (string name in LitShaderNames)
            {
                Shader shader = Shader.Find(name);
                if (shader == null || !shader.isSupported) continue;
                Material material = new Material(shader) { name = "ArmorFragments puff (lit)" };
                if (!material.HasProperty("_BaseMap")) { Object.Destroy(material); continue; }
                material.SetTexture("_BaseMap", smokeTexture != null ? smokeTexture : puffTexture);
                material.SetColor("_BaseColor", Color.white);   // the color comes from each particle
                if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0f);
                // Alpha blended, no depth writes: what the shader's own inspector sets for "Transparent / Alpha".
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetFloat("_SrcBlend", 5f);   // SrcAlpha
                material.SetFloat("_DstBlend", 10f);  // OneMinusSrcAlpha
                material.SetFloat("_ZWrite", 0f);
                material.SetFloat("_Cull", 0f);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                return material;
            }
            ArmorFragmentsPlugin.Log.LogInfo("[FX] none of the URP lit particle shaders is in this build: smoke and dust are not lit by the scene");
            return null;
        }

        // A soft round sprite (white, alpha falling off from the middle): puffs and sparks are drawn with it.
        private static Texture2D SoftDisc(int size, float sharpness)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments soft disc" };
            Color32[] pixels = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                    float a = Mathf.Clamp01(1f - d);
                    a = Mathf.Pow(a, sharpness * 0.6f + 0.4f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // A puff of smoke: a soft disc whose edge and inside are broken up by noise (three octaves of value noise), so it is not a clean round
        // blob. Dust and smoke particles use it.
        private static Texture2D CloudDisc(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments cloud disc" };
            Color32[] pixels = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                    float u = x / (float)size, v = y / (float)size;
                    float n = 0.5f * ValueNoise2(u * 4f, v * 4f, 1) + 0.3f * ValueNoise2(u * 9f, v * 9f, 2) + 0.2f * ValueNoise2(u * 19f, v * 19f, 3);
                    float a = Mathf.Pow(Mathf.Clamp01(1f - d), 1.1f) * (0.25f + 1.5f * n);
                    a *= Mathf.Clamp01((1f - d) * 4f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static float Hash2(int x, int y, int seed)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791);
            h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }

        private static float ValueNoise2(float x, float y, int seed)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            float a = Mathf.Lerp(Hash2(ix, iy, seed), Hash2(ix + 1, iy, seed), fx);
            float b = Mathf.Lerp(Hash2(ix, iy + 1, seed), Hash2(ix + 1, iy + 1, seed), fx);
            return Mathf.Lerp(a, b, fy);
        }

        private static AnimationCurve Curve(float start, float middle, float end)
        {
            return new AnimationCurve(new Keyframe(0f, start), new Keyframe(0.4f, middle), new Keyframe(1f, end));
        }

        // Color over a particle's life (it multiplies the color it was emitted with): from 'from' to 'to', and see-through at the end.
        private static Gradient Fade(Color from, Color to)
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(from, 0f), new GradientColorKey(to, 0.55f), new GradientColorKey(to, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.45f), new GradientAlphaKey(0f, 1f) });
            return gradient;
        }

        private static ParticleSystem Make(string name, Material material, ParticleSystemRenderMode mode, int max, float gravity, float drag,
                                           float velocityScale, AnimationCurve size, Gradient color)
        {
            GameObject go = new GameObject(name);
            go.layer = ImpactCamera.EffectsLayer;
            go.transform.SetParent(root.transform, false);
            ParticleSystem system = go.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.gravityModifier = gravity;
            main.startSpeed = 0f;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            main.scalingMode = ParticleSystemScalingMode.Local;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = false;

            ParticleSystem.SizeOverLifetimeModule sizeModule = system.sizeOverLifetime;
            sizeModule.enabled = true;
            sizeModule.size = new ParticleSystem.MinMaxCurve(1f, size);
            ParticleSystem.ColorOverLifetimeModule colorModule = system.colorOverLifetime;
            colorModule.enabled = true;
            colorModule.color = new ParticleSystem.MinMaxGradient(color);

            if (drag > 0f)
            {
                ParticleSystem.LimitVelocityOverLifetimeModule limit = system.limitVelocityOverLifetime;
                limit.enabled = true;
                limit.limit = 1000f;
                limit.dampen = 0f;
                limit.multiplyDragByParticleSize = false;
                limit.drag = drag;
            }

            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = mode;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (mode == ParticleSystemRenderMode.Stretch)
            {
                renderer.velocityScale = velocityScale * 0.01f;
                renderer.lengthScale = 1.6f;
            }
            system.Play();
            return system;
        }

        // The plugin is unloading (or the effects failed): the objects and what was made for them go.
        public static void Shutdown()
        {
            Queue.Clear();
            if (root != null) Object.Destroy(root);
            root = null;
            sparks = flash = smoke = dust = embers = null;
            if (puffMaterial != null) Object.Destroy(puffMaterial);
            if (sparkMaterial != null) Object.Destroy(sparkMaterial);
            if (litMaterial != null) Object.Destroy(litMaterial);
            litMaterial = null;
            if (puffTexture != null) Object.Destroy(puffTexture);
            if (smokeTexture != null) Object.Destroy(smokeTexture);
            if (sparkTexture != null) Object.Destroy(sparkTexture);
            puffMaterial = sparkMaterial = null;
            puffTexture = sparkTexture = smokeTexture = null;
        }
    }
}
