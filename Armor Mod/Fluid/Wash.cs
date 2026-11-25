using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Propulsion.Propeller;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments.Fluid
{
    // The air that vehicles move: the wake a moving vehicle drags behind it, and the slipstream of propellers, helicopter rotors and jet
    // engines. Both are steady flows, so they are given to the smoke's fluid as balls that PULL the air inside them toward a speed
    // (FluidSim.Blob.pull), not as pushes that would pile up: a propeller that gives 25 m/s of slipstream leaves the air in front of its
    // disc at 25 m/s, however long it runs.
    //
    // What the game gives (read from the game's code):
    //  - a propeller (PropellerAssemblyScript): the force it applies to the aircraft is its private _liftForce (world space, in the game's
    //    scaled units: the real newtons are that / 0.01), applied at the part's position; the slipstream goes the other way;
    //  - helicopter rotors, tail rotors and the older propellers (BladedEngineScript): LiftForcePrimary is that force, the disc is at the
    //    rotating head and Diameter is its width;
    //  - a jet engine (JetEngineScript): the force is CenterOfThrust.forward x throttle x power, so the exhaust goes along -forward.
    // The speed of the air through a disc follows the theory of an actuator disc: v = sqrt(T / (2 rho A)); the slipstream behind it is
    // about 1.6 times that.
    internal static class Wash
    {
        private enum Kind { Propeller, Bladed, Jet }

        private sealed class Source
        {
            public Kind kind;
            public PartScript part;
            public PropellerAssemblyScript propeller;
            public BladedEngineScript bladed;
            public JetEngineScript jet;
            public bool logged;
        }

        private sealed class Craft
        {
            public float refreshAt;
            public readonly List<Source> sources = new List<Source>();
            public bool loggedWake;
        }

        private static readonly Dictionary<AircraftScript, Craft> Crafts = new Dictionary<AircraftScript, Craft>();
        private static readonly List<AircraftScript> Gone = new List<AircraftScript>();
        private static AccessTools.FieldRef<PropellerAssemblyScript, Vector3> liftRef;
        private static bool liftTried;
        private static int logLines;

        private const float ScaleToNewtons = 1f / 0.01f;     // the game keeps every force in 1/100 of the real value
        private const int MaxBlobs = 24;                      // the most balls the air of vehicles adds to one step of one cloud
        private const float MaxWashSpeed = 45f;               // m/s
        private const float MaxWakeSpeed = 30f;               // m/s

        public static bool WashOn { get { return true; } }
        public static bool WakeOn { get { return true; } }
        public static float WashStrength { get { return ArmorFragmentsPlugin.SmokeWashStrength != null ? ArmorFragmentsPlugin.SmokeWashStrength.Value : 1f; } }
        public static float WakeStrength { get { return ArmorFragmentsPlugin.SmokeWakeStrength != null ? ArmorFragmentsPlugin.SmokeWakeStrength.Value : 0.4f; } }

        // Adds to `list` the balls for the vehicles in the cloud's box. `crafts` is every vehicle that has a part in the box, with the
        // bounds of those parts; `dt` the length of the step the balls are for.
        public static void Build(List<FluidSim.Blob> list, Vector3 origin, Quaternion rotation, Vector3 extent, Vector3 drift,
                                 Dictionary<AircraftScript, Bounds> crafts, float dt)
        {
            if (crafts == null || crafts.Count == 0 || (!WashOn && !WakeOn)) return;
            int first = list.Count;
            Quaternion toGrid = Quaternion.Inverse(rotation);
            Bounds box = new Bounds(origin + rotation * (extent * 0.5f), Vector3.zero);
            float reach = extent.magnitude * 0.5f;
            float washPull = 1f - Mathf.Exp(-6f * dt), wakePull = 1f - Mathf.Exp(-2f * dt);

            foreach (KeyValuePair<AircraftScript, Bounds> pair in crafts)
            {
                AircraftScript aircraft = pair.Key;
                if (aircraft == null || list.Count - first >= MaxBlobs) continue;

                if (WashOn)
                {
                    Craft craft = CraftOf(aircraft);
                    float density = 1.225f;
                    try { density = Mathf.Max(aircraft.AtmosphereSample.AirDensity, 0.05f); } catch (System.Exception) { }
                    foreach (Source source in craft.sources)
                    {
                        if (list.Count - first >= MaxBlobs) break;
                        try { AddSource(list, source, box.center, reach, origin, toGrid, density, washPull); }
                        catch (System.Exception e) { Disable(source, e); }
                    }
                }

                if (WakeOn)
                {
                    Vector3 velocity = aircraft.Velocity;
                    float speed = velocity.magnitude;
                    if (speed >= 3f && !float.IsNaN(speed))
                    {
                        Bounds b = pair.Value;
                        Vector3 dir = velocity / speed;
                        float radius = Mathf.Clamp(b.extents.magnitude, 1f, 10f);
                        Vector3 centerWorld = b.center - dir * (0.4f * radius);
                        float target = Mathf.Min(speed * WakeStrength, MaxWakeSpeed);
                        list.Add(new FluidSim.Blob
                        {
                            center = toGrid * (centerWorld - origin),
                            radius = radius * 1.15f,
                            vel = toGrid * (dir * target - drift),
                            falloff = 1.2f,
                            pull = wakePull,
                        });
                        Craft craft = CraftOf(aircraft);
                        if (!craft.loggedWake && logLines < 10)
                        {
                            craft.loggedWake = true; logLines++;
                            ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] wake: a vehicle moving at " + speed.ToString("F1") + " m/s drags the air near it along at " + target.ToString("F1") + " m/s (radius " + radius.ToString("F1") + " m)");
                        }
                    }
                }
            }
        }

        // The slipstream of one propeller, rotor or jet engine as balls along its axis: next to the disc the air moves at the speed through
        // it, further back faster and in a wider column (the slipstream contracts first and spreads later; a plain widening cylinder is close
        // enough for smoke).
        private static void AddSource(List<FluidSim.Blob> list, Source s, Vector3 boxCenter, float reach, Vector3 origin, Quaternion toGrid, float density, float pull)
        {
            Vector3 exhaust, hub;
            float diameter, speed;
            if (s.kind == Kind.Propeller)
            {
                if (s.propeller == null || !s.propeller.isActiveAndEnabled || !s.propeller.PropellerPhysicsEnabled) return;
                Vector3 force = liftRef != null ? liftRef(s.propeller) : Vector3.zero;
                float thrust = force.magnitude * ScaleToNewtons;
                if (thrust < 30f || float.IsNaN(thrust)) return;
                exhaust = -force.normalized;
                hub = s.propeller.transform.position;
                diameter = Mathf.Max(s.propeller.Diameter, 0.3f);
                speed = Induced(thrust, diameter, density);
            }
            else if (s.kind == Kind.Bladed)
            {
                if (s.bladed == null || !s.bladed.isActiveAndEnabled) return;
                Vector3 force = s.bladed.LiftForcePrimary;
                float thrust = force.magnitude * ScaleToNewtons;
                if (thrust < 30f || float.IsNaN(thrust)) return;
                exhaust = -force.normalized;
                Transform head = s.bladed.RotatingHeadAssembly;
                hub = head != null ? head.position : s.bladed.transform.position;
                diameter = Mathf.Max(s.bladed.Diameter, 0.3f);
                speed = Induced(thrust, diameter, density);
            }
            else
            {
                if (s.jet == null || !s.jet.isActiveAndEnabled) return;
                float throttle = s.jet.EngineThrottle;
                Transform nozzle = s.jet.CenterOfThrust;
                if (throttle < 0.05f || nozzle == null) return;
                exhaust = -nozzle.forward;
                hub = nozzle.position;
                diameter = 0.9f;
                speed = Mathf.Min(MaxWashSpeed, (15f + 40f * throttle) * WashStrength);
            }
            if (speed < 1f) return;
            speed = Mathf.Min(speed * (s.kind == Kind.Jet ? 1f : WashStrength), MaxWashSpeed);

            // Is the column near the box at all? (The disc and a few diameters behind it.)
            float length = diameter * (s.kind == Kind.Jet ? 6f : 2.5f);
            Vector3 middle = hub + exhaust * (0.5f * length);
            if ((middle - boxCenter).magnitude > reach + 0.5f * length + diameter) return;

            Vector3 velocity = toGrid * (exhaust * speed);
            float radius = diameter * 0.5f;
            float[] distance = s.kind == Kind.Jet ? new[] { 0.6f, 2.0f, 4.5f } : new[] { 0.3f, 1.0f, 2.0f };
            float[] growth = { 1.05f, 1.2f, 1.45f };
            float[] strength = { 1f, 0.85f, 0.6f };
            for (int i = 0; i < 3; i++)
            {
                list.Add(new FluidSim.Blob
                {
                    center = toGrid * (hub + exhaust * (distance[i] * diameter) - origin),
                    radius = radius * growth[i] * (s.kind == Kind.Jet ? 0.7f : 1f),
                    vel = velocity * strength[i],
                    falloff = 0.9f,
                    pull = pull,
                });
            }
            if (!s.logged && logLines < 10)
            {
                s.logged = true; logLines++;
                ArmorFragmentsPlugin.Log.LogInfo("[VFXBUNDLE] wash: " + s.kind + " '" + (s.part != null ? s.part.name : "?") + "' blows the air at " + speed.ToString("F1") + " m/s, disc "
                    + diameter.ToString("F1") + " m, exhaust direction " + exhaust.ToString("F2"));
            }
        }

        // The speed of the air through a disc that makes thrust T (N): v = sqrt(T / (2 rho A)); the slipstream behind it moves about 1.6 times faster.
        private static float Induced(float thrust, float diameter, float density)
        {
            float area = Mathf.PI * 0.25f * diameter * diameter;
            return 1.6f * Mathf.Sqrt(thrust / (2f * density * area));
        }

        private static void Disable(Source s, System.Exception e)
        {
            s.propeller = null; s.bladed = null; s.jet = null;
            if (logLines < 12) { logLines++; ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] wash: a source could not be read and is ignored: " + e.Message); }
        }

        // The propellers, rotors and engines of a vehicle (looked for again every few seconds: parts can break off).
        private static Craft CraftOf(AircraftScript aircraft)
        {
            Craft craft;
            if (!Crafts.TryGetValue(aircraft, out craft))
            {
                craft = new Craft { refreshAt = -1f };
                Crafts[aircraft] = craft;
            }
            if (Time.time >= craft.refreshAt)
            {
                craft.refreshAt = Time.time + 3f;
                craft.sources.Clear();
                try
                {
                    if (!liftTried)
                    {
                        liftTried = true;
                        try { liftRef = AccessTools.FieldRefAccess<PropellerAssemblyScript, Vector3>("_liftForce"); }
                        catch (System.Exception e) { ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] wash: the propeller's force cannot be read (" + e.Message + "), propellers will not blow"); }
                    }
                    if (aircraft.Aircraft != null && aircraft.Aircraft.Assembly != null)
                    {
                        foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
                        {
                            PartScript part = data != null ? data.PartScript : null;
                            if (part == null) continue;
                            if (liftRef != null)
                            {
                                PropellerAssemblyScript propeller = part.GetModifier<PropellerAssemblyScript>();
                                if (propeller != null) { craft.sources.Add(new Source { kind = Kind.Propeller, part = part, propeller = propeller }); continue; }
                            }
                            BladedEngineScript bladed = part.GetModifier<BladedEngineScript>();
                            if (bladed != null) { craft.sources.Add(new Source { kind = Kind.Bladed, part = part, bladed = bladed }); continue; }
                            JetEngineScript jet = part.GetModifier<JetEngineScript>();
                            if (jet != null) craft.sources.Add(new Source { kind = Kind.Jet, part = part, jet = jet });
                        }
                    }
                }
                catch (System.Exception e)
                {
                    if (logLines < 12) { logLines++; ArmorFragmentsPlugin.Log.LogWarning("[VFXBUNDLE] wash: could not list the engines of a vehicle: " + e.Message); }
                }
                // Forget the vehicles that are gone.
                Gone.Clear();
                foreach (AircraftScript key in Crafts.Keys) if (key == null) Gone.Add(key);
                foreach (AircraftScript key in Gone) Crafts.Remove(key);
            }
            return craft;
        }

        public static void Shutdown()
        {
            Crafts.Clear();
            Gone.Clear();
            liftRef = null;
            liftTried = false;
            logLines = 0;
        }
    }
}
