// RELEASE-STRIP-BEGIN
using Assets.Scripts.Environment.Wind;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ArmorFragments
{
    // Makes a smoke Particle System (from the effect bundle) react to the game's wind.
    // Every frame it asks the game for the wind at each puff (WindQueryManager: global wind, gusts, thermals, ridge lift,
    // cloud turbulence, wind zones) and lets the puff's velocity drift toward it. The swirling comes from the Noise module
    // already set up in the prefab; this script only scales that noise with how windy it is.
    // The bundle carries the look; this script, which lives in the mod's DLL, is added to the spawned prefab.
    internal sealed class SmokeWind : MonoBehaviour
    {
        // How long a puff takes to follow the wind (seconds). Small = light smoke that goes with the wind at once.
        public static float FollowSeconds = 1.4f;
        // Extra swirl in the Noise module for each m/s of wind, on top of what the prefab sets.
        public static float SwirlPerWindSpeed = 0.08f;
        public static float MaxSwirlBoost = 1.5f;

        private ParticleSystem system;
        private ParticleSystem.Particle[] particles;
        private NativeArray<float3> positions;
        private NativeArray<float3> winds;
        private float baseSwirl = -1f;
        private bool loggedFirst;
        private bool failed;

        private void Awake()
        {
            system = GetComponent<ParticleSystem>();
        }

        private void LateUpdate()
        {
            if (failed || system == null) return;
            WindQueryManager wind = WindQueryManager.Instance;
            if (wind == null) return;   // no flight scene (menus)

            try
            {
                int max = system.main.maxParticles;
                if (particles == null || particles.Length < max)
                {
                    Dispose();
                    particles = new ParticleSystem.Particle[max];
                    positions = new NativeArray<float3>(max, Allocator.Persistent);
                    winds = new NativeArray<float3>(max, Allocator.Persistent);
                }

                int count = system.GetParticles(particles);
                if (count == 0) return;

                // The particles of a world-space system are positioned in world space already.
                for (int i = 0; i < count; i++) positions[i] = particles[i].position;
                NativeArray<float3> p = positions.GetSubArray(0, count);
                NativeArray<float3> w = winds.GetSubArray(0, count);
                wind.QueryWindVelocity(p, w);

                float follow = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.05f, FollowSeconds));
                float speedSum = 0f;
                for (int i = 0; i < count; i++)
                {
                    Vector3 target = new Vector3(w[i].x, w[i].y, w[i].z);
                    speedSum += target.magnitude;
                    particles[i].velocity += (target - particles[i].velocity) * follow;
                }
                system.SetParticles(particles, count);

                // More swirl in a stronger wind.
                float mean = speedSum / count;
                ParticleSystem.NoiseModule noise = system.noise;
                if (noise.enabled)
                {
                    if (baseSwirl < 0f) baseSwirl = noise.strengthMultiplier;
                    noise.strengthMultiplier = baseSwirl * (1f + Mathf.Min(MaxSwirlBoost, mean * SwirlPerWindSpeed));
                }

                if (!loggedFirst)
                {
                    loggedFirst = true;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] smoke wind: " + count + " puff(s), the wind there is "
                        + new Vector3(w[0].x, w[0].y, w[0].z) + " (" + mean.ToString("F1") + " m/s on average)");
                }
            }
            catch (System.Exception e)
            {
                failed = true;
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] the smoke's wind stopped: " + e);
            }
        }

        private void OnDestroy()
        {
            Dispose();
        }

        private void Dispose()
        {
            if (positions.IsCreated) positions.Dispose();
            if (winds.IsCreated) winds.Dispose();
        }
    }
}
// RELEASE-STRIP-END
