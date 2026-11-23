// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Environment.Wind;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ArmorFragments
{
    // The temperature of a cannon's barrel and the smoke it lets go of after the shot.
    //
    // Every shot heats the barrel: a few percent of the muzzle energy stays in the steel (eta = 5%), and the temperature rise is that energy over
    // the heat capacity of the barrel (its mass from the game's own formula, TotalCannonVolume x 558.45 kg, times 490 J/kg/K for steel). The heat
    // is kept as a number from 0 (cold) to 1 (600 K above the air, red hot), and is multiplied by Smoke/BarrelHeatScale (real barrels heat
    // slowly: a game wants it faster). It cools by itself with a time constant that grows with the size of the gun (40 s x sqrt(calibre / 12.7)).
    //
    // What it does: (1) the gas that stays in the bore comes out in the seconds after a shot: a puff at the muzzle that fades in 1.5 s, and a
    // thread of thin smoke that rises from the muzzle for as long as the barrel is hot: both thicker the hotter the barrel and the bigger the gun,
    // blown by the wind at the muzzle; (2) the next shot makes up to 40% more smoke (the cooling of the gas is slower in a hot bore).
    internal static class BarrelHeat
    {
        private sealed class State
        {
            public CannonScript cannon;
            public float heat;            // 0..1.5
            public float boost;           // the gas left in the bore by the last shots, 0..2: fades in 1.5 s
            public float scale;           // calibre / 75 mm
            public float timeConstant;    // s
            public float carry;           // emission not yet turned into whole particles
            public Vector3 wind;
            public float windAt;
            public bool loggedWarm, loggedHot;
        }

        private static readonly Dictionary<int, State> States = new Dictionary<int, State>();
        private static readonly List<int> Gone = new List<int>();
        private static NativeArray<float3> windPosition, windResult;
        private static float hottest;                 // for the panel
        private static string hottestName = "";

        private const float Efficiency = 0.05f;       // the share of the muzzle energy that stays in the barrel
        private const float SteelHeat = 490f;         // J / kg / K
        private const float RedHot = 600f;            // K above the air: heat = 1
        private const float BoreDensity = 558.45f;    // kg / m3 of the game's cannon volume (CannonData.Mass)

        public static bool Enabled { get { return ArmorFragmentsPlugin.SmokeBarrelHeat == null || ArmorFragmentsPlugin.SmokeBarrelHeat.Value; } }
        private static float HeatScale { get { return ArmorFragmentsPlugin.SmokeBarrelHeatScale != null ? ArmorFragmentsPlugin.SmokeBarrelHeatScale.Value : 6f; } }
        private static float SmokeStrength { get { return ArmorFragmentsPlugin.SmokeBarrelSmoke != null ? ArmorFragmentsPlugin.SmokeBarrelSmoke.Value : 1f; } }

        public static string Status
        {
            get
            {
                if (!Enabled) return "off";
                if (States.Count == 0 || hottest < 0.005f) return "all barrels cold";
                return hottestName + " at " + (hottest * 100f).ToString("F0") + "% of red hot (+" + (hottest * RedHot).ToString("F0") + " K)";
            }
        }

        // The heat this cannon has now (0 = cold), for the smoke of its next shot.
        public static float HeatOf(CannonScript cannon)
        {
            State state;
            return cannon != null && States.TryGetValue(cannon.GetInstanceID(), out state) ? state.heat : 0f;
        }

        // A shot: the barrel warms up and the bore holds some gas.
        public static void OnShot(CannonScript cannon)
        {
            if (!Enabled || cannon == null || cannon.PartScript == null) return;
            CannonData data = null;
            foreach (PartModifierData modifier in cannon.PartScript.Part.Modifiers)
            {
                data = modifier as CannonData;
                if (data != null) break;
            }
            if (data == null) return;
            int id = cannon.GetInstanceID();
            State state;
            if (!States.TryGetValue(id, out state))
            {
                state = new State { cannon = cannon };
                States[id] = state;
            }
            try
            {
                float mass = data.ProjectileVolume * 11342f;
                float velocity = Mathf.Max(data.ProjectileVelocity, 1f);
                float energy = 0.5f * mass * velocity * velocity;                                  // J
                float barrelMass = Mathf.Max(data.TotalCannonVolume * BoreDensity, 0.2f);          // kg
                float rise = Efficiency * energy / (barrelMass * SteelHeat);                        // K
                state.heat = Mathf.Min(state.heat + rise * HeatScale / RedHot, 1.5f);
                state.scale = Mathf.Clamp(data.CaliberInMilimeters / 75f, 0.2f, 3.5f);
                state.timeConstant = 40f * Mathf.Sqrt(Mathf.Max(data.CaliberInMilimeters, 10f) / 12.7f);
                state.boost = Mathf.Min(state.boost + 0.8f * FluidSmoke.CaliberAmount(state.scale), 2f);
                if (!state.loggedWarm && state.heat > 0.2f)
                {
                    state.loggedWarm = true;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] barrel heat: a " + data.CaliberInMilimeters.ToString("F0") + " mm barrel (" + barrelMass.ToString("F0") + " kg) gains " + rise.ToString("F1")
                        + " K a shot (x" + HeatScale.ToString("F0") + " for the game), now at " + (state.heat * 100f).ToString("F0") + "% of red hot, cools with a time constant of " + state.timeConstant.ToString("F0") + " s");
                }
                if (!state.loggedHot && state.heat > 0.7f)
                {
                    state.loggedHot = true;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] barrel heat: a barrel is at " + (state.heat * 100f).ToString("F0") + "% of red hot");
                }
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] barrel heat: could not read the cannon (" + e.Message + ")");
            }
        }

        // Every frame: the cooling, and the smoke that comes out of the hot barrels.
        public static void Tick(float dt)
        {
            if (States.Count == 0) return;
            float strength = Enabled ? SmokeStrength : 0f;
            Gone.Clear();
            hottest = 0f;
            foreach (KeyValuePair<int, State> pair in States)
            {
                State s = pair.Value;
                if (s.cannon == null || s.cannon.PartScript == null) { Gone.Add(pair.Key); continue; }
                s.heat *= Mathf.Exp(-dt / Mathf.Max(s.timeConstant, 1f));
                s.boost *= Mathf.Exp(-dt / 1.5f);
                if (s.heat < 0.004f && s.boost < 0.01f) { Gone.Add(pair.Key); continue; }
                if (s.heat > hottest) { hottest = s.heat; hottestName = s.cannon.PartScript.name; }
                if (strength <= 0.01f || dt <= 0f) continue;

                float intensity = s.heat + 0.8f * s.boost;                    // 0 .. 3
                // Wisps per second: more for a hot barrel and a big gun (the wisp is thicker, not just more of them), none far from the camera.
                Vector3 tip = s.cannon.TipPosition;
                Camera cam = Camera.main;
                if (cam != null && (cam.transform.position - tip).sqrMagnitude > 250f * 250f) continue;
                s.carry += 9f * intensity * (0.4f + 0.6f * Mathf.Sqrt(s.scale)) * strength * dt;
                int count = Mathf.Min((int)s.carry, 6);
                s.carry -= Mathf.Floor(s.carry);
                if (count <= 0) continue;
                if (Time.time >= s.windAt) { s.wind = WindAt(tip); s.windAt = Time.time + 0.5f; }
                Vector3 forward = s.cannon.transform.forward;
                float fine = 0.5f + 0.5f * s.scale;
                for (int i = 0; i < count; i++)
                {
                    float rise = Mathf.Clamp(0.45f + 0.5f * s.heat, 0.4f, 1.2f);
                    Vector3 velocity = s.wind * 0.6f + Vector3.up * (rise * Random.Range(0.7f, 1.3f)) + forward * Random.Range(0.1f, 0.5f) * (0.3f + 0.7f * s.boost / 2f)
                                       + Random.insideUnitSphere * 0.12f;
                    float alpha = Mathf.Clamp(0.06f + 0.07f * intensity, 0.06f, 0.26f);
                    ImpactEffects.Wisp(tip + forward * 0.04f + Random.insideUnitSphere * (0.012f * fine), velocity, Random.Range(0.04f, 0.09f) * fine, Random.Range(2f, 4.5f),
                                       new Color(0.34f, 0.33f, 0.32f, alpha));
                }
            }
            foreach (int id in Gone) States.Remove(id);
        }

        private static Vector3 WindAt(Vector3 point)
        {
            try
            {
                WindQueryManager manager = WindQueryManager.Instance;
                if (manager == null) return Vector3.zero;
                if (!windPosition.IsCreated)
                {
                    windPosition = new NativeArray<float3>(1, Allocator.Persistent);
                    windResult = new NativeArray<float3>(1, Allocator.Persistent);
                }
                windPosition[0] = point;
                manager.QueryWindVelocity(windPosition, windResult);
                Vector3 w = new Vector3(windResult[0].x, windResult[0].y, windResult[0].z);
                return float.IsNaN(w.x) || w.sqrMagnitude > 90f * 90f ? Vector3.zero : w;
            }
            catch (System.Exception) { return Vector3.zero; }
        }

        public static void Shutdown()
        {
            States.Clear();
            if (windPosition.IsCreated) windPosition.Dispose();
            if (windResult.IsCreated) windResult.Dispose();
            hottest = 0f;
        }
    }
}
// RELEASE-STRIP-END
