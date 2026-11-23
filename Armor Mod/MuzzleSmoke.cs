// RELEASE-STRIP-BEGIN
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // A puff of hot smoke at the muzzle of every cannon that fires, local or somebody else's (the game calls PlayMuzzleFlash for both).
    // The look is the MuzzleSmoke prefab of the effect bundle; SmokeSim makes it follow the wind, glow while hot, rise and swell.
    [HarmonyPatch(typeof(CannonScript), "PlayMuzzleFlash")]
    internal static class MuzzleSmokePatch
    {
        private static void Postfix(CannonScript __instance)
        {
            MuzzleSmoke.OnShot(__instance);
            BlastCloud.OnShot(__instance);
        }
    }

    internal static class MuzzleSmoke
    {
        public static bool Enabled = true;
        public static int MaxLive = 24;           // puffs alive at once; more shots than that make no smoke until some fade
        public static float MinInterval = 0.12f;  // seconds between the puffs of one cannon (a machine gun does not make a hundred)
        public static float StartKelvin = 1750f;  // gas leaving the barrel (the air is 300 K)
        public static float LifeSeconds = 12f;

        private static readonly List<GameObject> Live = new List<GameObject>();
        private static readonly Dictionary<int, float> LastShot = new Dictionary<int, float>();
        private static bool loggedFirst;

        public static void Toggle()
        {
            Enabled = !Enabled;
            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] muzzle smoke " + (Enabled ? "on" : "off"));
        }

        public static void OnShot(CannonScript cannon)
        {
            if (!Enabled || cannon == null) return;
            try
            {
                GameObject prefab = VfxBundleTest.Prefab("muzzlesmoke");
                if (prefab == null) return;

                Live.RemoveAll(g => g == null);
                if (Live.Count >= MaxLive) return;

                int id = cannon.GetInstanceID();
                float now = Time.time;
                float last;
                if (LastShot.TryGetValue(id, out last) && now - last < MinInterval) return;
                LastShot[id] = now;

                Vector3 tip = cannon.TipPosition;
                Vector3 forward = cannon.transform.forward;
                float caliber = CaliberMillimeters(cannon);
                float scale = Mathf.Clamp(caliber / 75f, 0.3f, 4f);

                Vector3 pointVelocity = Vector3.zero;
                if (cannon.PartScript != null && cannon.PartScript.Body != null && cannon.PartScript.Body.RigidBody != null)
                    pointVelocity = cannon.PartScript.Body.RigidBody.GetPointVelocity(tip);

                GameObject go = Object.Instantiate(prefab, tip, Quaternion.LookRotation(forward));
                go.name = "ArmorFragments muzzle smoke";
                VfxBundleTest.SetLayer(go, ImpactCamera.EffectsLayer);

                ParticleSystem ps = go.GetComponent<ParticleSystem>();
                ParticleSystem.MainModule main = ps.main;
                main.startSizeMultiplier *= Mathf.Pow(scale, 0.9f);       // a bigger gun: bigger puffs
                main.startSpeedMultiplier *= Mathf.Pow(scale, 0.5f);      // and a harder blast
                ParticleSystem.ShapeModule shape = ps.shape;
                shape.radius *= scale;
                ParticleSystem.EmissionModule emission = ps.emission;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.RoundToInt(8f + 8f * Mathf.Min(scale, 2f)),
                                                                    (short)Mathf.RoundToInt(12f + 10f * Mathf.Min(scale, 2f))) });

                SmokeSim sim = go.AddComponent<SmokeSim>();
                sim.StartKelvin = StartKelvin;
                if (SmokeVolume.Enabled) go.AddComponent<SmokeVolume>();   // the heavy ray-marched look; off by default
                sim.InitialVelocity = pointVelocity;

                ps.Play();
                Live.Add(go);
                Object.Destroy(go, LifeSeconds);

                if (!loggedFirst)
                {
                    loggedFirst = true;
                    ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] first muzzle smoke: caliber " + caliber.ToString("F0") + " mm, scale "
                        + scale.ToString("F2") + ", at " + tip + ", point velocity " + pointVelocity.magnitude.ToString("F1") + " m/s");
                }
            }
            catch (System.Exception e)
            {
                Enabled = false;
                ArmorFragmentsPlugin.Log.LogError("[VFXBUNDLE] muzzle smoke failed and is now off: " + e);
            }
        }

        public static void Shutdown()
        {
            foreach (GameObject go in Live) if (go != null) Object.Destroy(go);
            Live.Clear();
            LastShot.Clear();
            loggedFirst = false;
        }

        internal static float CaliberMillimeters(CannonScript cannon)
        {
            if (cannon.PartScript != null)
            {
                foreach (PartModifierData modifier in cannon.PartScript.Part.Modifiers)
                {
                    CannonData data = modifier as CannonData;
                    if (data != null) return data.CaliberInMilimeters;
                }
            }
            return 75f;
        }
    }
}
// RELEASE-STRIP-END
