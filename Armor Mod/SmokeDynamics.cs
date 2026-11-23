// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using Assets.Scripts.Environment.Wind;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ArmorFragments
{
    // Makes a smoke Particle System (from the effect bundle) alive: it follows the game's wind, rises when it is hot, swells while
    // it is hot, cools down, and tells the shader how hot each puff is so the hot ones glow.
    //
    // Wind: every frame the game is asked for the wind at each puff (WindQueryManager: global wind, gusts, thermals, ridge lift,
    // cloud turbulence, wind zones) and the puff's velocity drifts toward it. The swirling comes from the Noise module already set
    // up in the prefab; this script scales it with the wind speed.
    //
    // Temperature (0 = same as the air, 1 = just out of the barrel):
    //   - it cools by Newton's law: the hotter above the air, the faster it loses heat; wind carries heat away faster;
    //   - buoyancy: hot gas is lighter, it accelerates upward in proportion to its temperature;
    //   - expansion: while hot a puff keeps growing (heat that has gone into the gas), and it never shrinks back;
    //   - glow: the shader reads the temperature from the red channel of the particle's color (8 bits are plenty for that);
    //     the exact value is kept here, per particle, under the particle's random seed.
    // The bundle carries the look; this script, which lives in the mod's DLL, is added to the spawned prefab.
    internal sealed class SmokeSim : MonoBehaviour
    {
        // How long a puff takes to follow the wind (seconds). Small = light smoke that goes with the wind at once.
        public static float FollowSeconds = 1.4f;
        // Extra swirl in the Noise module for each m/s of wind, on top of what the prefab sets.
        public static float SwirlPerWindSpeed = 0.08f;
        public static float MaxSwirlBoost = 1.5f;

        // Temperature.
        public static float CoolSeconds = 2.4f;        // time for the excess heat to fall to 37% in still air
        public static float CoolPerWindSpeed = 0.06f;  // each m/s of wind makes cooling this much faster (a fraction)
        public static float Buoyancy = 3.0f;           // m/s² of upward push at temperature 1
        public static float ExpandPerSecond = 0.22f;   // growth rate of a puff at temperature 1 (0.22 = +22% a second)
        public static float MaxSize = 14f;             // metres: a puff never swells past this

        // Set by whoever spawns the effect.
        public float Heat;                             // temperature of a new puff, 0..1 (0 = cold smoke)
        public float HeatSpread = 0.2f;                // random variation of that, fraction
        public Vector3 InitialVelocity;                // added to every new puff: the speed of the thing that made the smoke

        private ParticleSystem system;
        private ParticleSystem.Particle[] particles;
        private NativeArray<float3> positions;
        private NativeArray<float3> winds;
        private Dictionary<uint, float> temperature = new Dictionary<uint, float>();
        private Dictionary<uint, float> nextTemperature = new Dictionary<uint, float>();
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

                float dt = Time.deltaTime;
                float follow = 1f - Mathf.Exp(-dt / Mathf.Max(0.05f, FollowSeconds));
                float speedSum = 0f, hottest = 0f;
                nextTemperature.Clear();

                for (int i = 0; i < count; i++)
                {
                    Vector3 target = new Vector3(w[i].x, w[i].y, w[i].z);
                    float windSpeed = target.magnitude;
                    speedSum += windSpeed;

                    // Temperature: a new puff takes the heat it was made with; an old one cools.
                    uint key = particles[i].randomSeed;
                    float t;
                    if (temperature.TryGetValue(key, out t))
                    {
                        float seconds = CoolSeconds / (1f + CoolPerWindSpeed * windSpeed);
                        t *= Mathf.Exp(-dt / Mathf.Max(0.05f, seconds));
                    }
                    else
                    {
                        t = Mathf.Clamp01(Heat * (1f + HeatSpread * (UnityEngine.Random.value * 2f - 1f)));
                        particles[i].velocity += InitialVelocity;
                    }
                    nextTemperature[key] = t;
                    if (t > hottest) hottest = t;

                    Vector3 velocity = particles[i].velocity;
                    velocity += (target - velocity) * follow;     // the wind carries it
                    velocity.y += Buoyancy * t * dt;              // hot gas rises
                    particles[i].velocity = velocity;

                    // Hot gas swells (and keeps the size it reached when it cools).
                    float size = particles[i].startSize;
                    if (size < MaxSize) particles[i].startSize = Mathf.Min(MaxSize, size * (1f + ExpandPerSecond * t * dt));

                    // The temperature for the shader: red channel. (Green and blue are not used.)
                    particles[i].startColor = new Color32((byte)Mathf.RoundToInt(t * 255f), 0, 0, 255);
                }
                system.SetParticles(particles, count);

                Dictionary<uint, float> swap = temperature;
                temperature = nextTemperature;
                nextTemperature = swap;

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
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] smoke: " + count + " puff(s), wind there "
                        + new Vector3(w[0].x, w[0].y, w[0].z) + " (" + mean.ToString("F1") + " m/s on average), hottest puff "
                        + hottest.ToString("F2"));
                }
            }
            catch (System.Exception e)
            {
                failed = true;
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] the smoke's simulation stopped: " + e);
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
