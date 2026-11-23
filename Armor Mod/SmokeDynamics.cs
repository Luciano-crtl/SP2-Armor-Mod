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

        // Temperature, in kelvin. The air is 300 K; gas leaves a gun barrel at about 1700 K.
        public const float AmbientKelvin = 300f;
        public const float KelvinRange = 1500f;        // the shader reads (kelvin - 300) / 1500, so 1800 K is the top of its scale
        public static float MixSeconds = 1.6f;         // mixing with the cold air: the excess heat falls to 37% in this time (still air)
        public static float MixPerWindSpeed = 0.06f;   // each m/s of wind makes the mixing this much faster (a fraction)
        public static float RadiationRate = 240f;      // K/s lost by radiation at 1000 K; grows with the 4th power of the temperature
        public static float BuoyancyFraction = 0.12f;  // upward acceleration = g * this * (excess heat / 300 K)
        public static float ExpandPerSecond = 0.9f;    // growth rate of a puff at the full 1500 K excess (0.9 = +90% a second)
        public static float MaxSize = 14f;             // metres: a puff never swells past this

        // Set by whoever spawns the effect.
        public float StartKelvin = 300f;               // temperature of a new puff (300 = cold smoke)
        public float HeatSpread = 0.1f;                // random variation of the excess heat, fraction
        public Vector3 InitialVelocity;                // added to every new puff: the speed of the thing that made the smoke
        // A baked simulation (a flipbook) already holds its own heat, rise and swelling: only the wind moves it.
        public bool Baked;

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

                    if (Baked)
                    {
                        // Only drift with the wind (and the speed of whatever made it); keep the vertex color black: no vertex heat.
                        uint bakedKey = particles[i].randomSeed;
                        if (!temperature.ContainsKey(bakedKey)) particles[i].velocity += InitialVelocity;   // a new particle
                        nextTemperature[bakedKey] = 0f;                                                      // (remembered as seen)
                        Vector3 v = particles[i].velocity;
                        v += (target - v) * follow;
                        particles[i].velocity = v;
                        particles[i].startColor = new Color32(0, 0, 0, 255);
                        continue;
                    }

                    // Temperature: a new puff takes the heat it was made with; an old one cools.
                    uint key = particles[i].randomSeed;
                    float kelvin;
                    if (temperature.TryGetValue(key, out kelvin))
                    {
                        // Mixing with the cold air (Newton) plus radiation, which is fierce while the gas is white-hot
                        // and almost nothing once it is warm: that is what makes the flash short and the smoke long-lived.
                        float seconds = MixSeconds / (1f + MixPerWindSpeed * windSpeed);
                        float excess = kelvin - AmbientKelvin;
                        float hot = kelvin / 1000f;
                        float radiation = RadiationRate * hot * hot * hot * hot;
                        excess -= (excess / Mathf.Max(0.05f, seconds) + radiation) * dt;
                        kelvin = AmbientKelvin + Mathf.Max(0f, excess);
                    }
                    else
                    {
                        float start = AmbientKelvin + (StartKelvin - AmbientKelvin) * (1f + HeatSpread * (UnityEngine.Random.value * 2f - 1f));
                        kelvin = Mathf.Max(AmbientKelvin, start);
                        particles[i].velocity += InitialVelocity;
                    }
                    nextTemperature[key] = kelvin;
                    if (kelvin > hottest) hottest = kelvin;
                    float heat = Mathf.Clamp01((kelvin - AmbientKelvin) / KelvinRange);

                    Vector3 velocity = particles[i].velocity;
                    velocity += (target - velocity) * follow;                                    // the wind carries it
                    velocity.y += 9.81f * BuoyancyFraction * (kelvin - AmbientKelvin) / AmbientKelvin * dt;   // hot gas rises
                    particles[i].velocity = velocity;

                    // Hot gas swells (and keeps the size it reached when it cools).
                    float size = particles[i].startSize;
                    if (size < MaxSize) particles[i].startSize = Mathf.Min(MaxSize, size * (1f + ExpandPerSecond * heat * dt));

                    // The temperature for the shader: red channel. (Green and blue are not used.)
                    particles[i].startColor = new Color32((byte)Mathf.RoundToInt(heat * 255f), 0, 0, 255);
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
                        + hottest.ToString("F0") + " K");
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
