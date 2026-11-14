using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Multiplayer;
using FishNet.Serializing;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // Ammo rack cook-offs as the other players see them.
    //
    // The game already shows every player the fires the owner's game makes on its vehicle:
    // PartDamageEffects.CreateEffect, on the owner, also sends CreateDamageEffect (a server RPC, then an observers
    // RPC that skips the owner), and each other game makes the same plain fire on its copy of the part. What does
    // not travel is what AmmoRacks does to the fire afterwards: half size, the jet's flare-up and easing, and the
    // fire that never goes out. So the owner's mod sends one more part message (on the NetSync channel, type 202)
    // naming the part and the look, and each other player's mod finds the fire the game has just made on that
    // part and dresses it the same way. If that fire doesn't show up within a moment, it lights one itself (on a
    // copy of someone else's vehicle CreateFire is not sent on).
    //
    //   R                    the rack's fire: half size, never goes out
    //   X                    the rack's fire is out (the rack exploded)
    //   J,duration,x,y,z,b   the jet: flares up to (x, y, z) x the fire with b x the flames, settles in 'duration' s
    //   V                    a vent's burn-out fire: half size, never goes out
    //   B,x,y,z              the cook-off's explosion is about to go off here (in the part's own coordinates): keep
    //                        it off this vehicle and off armor, as the owner's game does (AmmoRackBlastPatch)
    //
    // Players without the mod see the game's plain fires and its explosion.
    internal static class RackFireSync
    {
        public const byte FireMessage = 202;

        public const string RackFire = "R";
        public const string RackFireOut = "X";
        public const string VentFire = "V";

        private const float MatchWindow = 2f;    // s: a fire and the message about it belong together within this
        private const float WaitForFire = 1.5f;  // s: then we light the fire ourselves

        private sealed class Look
        {
            public string Kind;
            public float Duration;
            public Vector3 Jet;
            public float Boost;
        }

        private sealed class Waiting
        {
            public PartScript Part;
            public Look Look;
            public float Until;
        }

        private struct Made
        {
            public PartDamageEffect Effect;
            public float Time;
        }

        private static readonly Dictionary<PartScript, Made> RecentFires = new Dictionary<PartScript, Made>();
        private static readonly List<Waiting> Pending = new List<Waiting>();
        private static readonly Dictionary<PartScript, PartDamageEffect> RackFires = new Dictionary<PartScript, PartDamageEffect>();
        private static bool lightingOurOwn;
        private static bool sendFailureLogged;

        // ---- The owner's game -----------------------------------------------------------------

        public static string Jet(float duration, Vector3 jet, float boost)
        {
            return "J," + F(duration) + "," + F(jet.x) + "," + F(jet.y) + "," + F(jet.z) + "," + F(boost);
        }

        public static void SendBlast(PartScript part, Vector3 worldPosition)
        {
            if (part == null) return;
            Vector3 local = part.transform.InverseTransformPoint(worldPosition);
            Send(part, "B," + F(local.x) + "," + F(local.y) + "," + F(local.z));
        }

        // Rides on the burning vehicle itself: the game delivers a part message to every other player's copy of it.
        public static void Send(PartScript part, string look)
        {
            try
            {
                AircraftScript aircraft = part != null ? part.Aircraft : null;
                INetworkAircraft network = aircraft != null ? aircraft.NetworkAircraft : null;
                if (network == null || !network.IsInitialized || !network.IsOwner || part.Part == null) return;
                network.SendPartNetworkMessage(FireMessage, part.Part, (PooledWriter w) => w.WriteString(look));
            }
            catch (Exception e)
            {
                if (sendFailureLogged) return;
                sendFailureLogged = true;
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack fire not sent to other players (shown once): " + e.Message);
            }
        }

        // ---- Other players' games ---------------------------------------------------------------

        // A message from 'sender' (our copy of another player's vehicle) about one of its parts.
        public static void OnMessage(NetworkAircraftScript sender, int partId, string text)
        {
            AircraftScript aircraft = sender != null ? sender.AircraftScript : null;
            if (aircraft == null || !aircraft.RemoteAircraft) return;
            PartData data = aircraft.GetPartById(partId, true);
            PartScript part = data != null ? data.PartScript : null;
            if (part == null) return;

            string[] fields = text.Split(',');
            switch (fields[0])
            {
                case "B":
                    if (fields.Length < 4) return;
                    AmmoRacks.NoteRemoteBlast(part.transform.TransformPoint(Vec(fields, 1)), aircraft);
                    return;
                case "X":
                    PutOut(part);
                    return;
            }

            Look look = new Look { Kind = fields[0] };
            if (look.Kind == "J")
            {
                if (fields.Length < 6) return;
                look.Duration = Num(fields[1]);
                look.Jet = Vec(fields, 2);
                look.Boost = Num(fields[5]);
            }
            else if (look.Kind != RackFire && look.Kind != VentFire)
            {
                return;   // a newer version's message
            }

            // The game's own fire usually comes first (it was sent first).
            Made made;
            if (RecentFires.TryGetValue(part, out made) && made.Effect != null && !made.Effect.Destroyed &&
                Time.time - made.Time <= MatchWindow)
            {
                RecentFires.Remove(part);
                Dress(part, made.Effect, look);
                return;
            }
            Pending.Add(new Waiting { Part = part, Look = look, Until = Time.time + WaitForFire });
        }

        // The game made a fire (PartDamageEffects.CreateEffect): on another player's vehicle, it may be one our
        // message is about.
        public static void OnFireMade(PartScript part, PartDamageEffect effect)
        {
            if (lightingOurOwn || part == null || effect == null) return;
            if (part.Aircraft == null || !part.Aircraft.RemoteAircraft) return;

            for (int i = 0; i < Pending.Count; i++)
            {
                if (Pending[i].Part != part) continue;
                Look look = Pending[i].Look;
                Pending.RemoveAt(i);
                Dress(part, effect, look);
                return;
            }
            RecentFires[part] = new Made { Effect = effect, Time = Time.time };
        }

        // Every frame.
        public static void Tick()
        {
            if (Pending.Count == 0 && RecentFires.Count == 0) return;
            try
            {
                for (int i = 0; i < Pending.Count; i++)
                {
                    Waiting waiting = Pending[i];
                    if (waiting.Part != null && Time.time < waiting.Until) continue;
                    Pending.RemoveAt(i--);
                    LightOurOwn(waiting);
                }

                if (RecentFires.Count > 0)
                {
                    List<PartScript> old = null;
                    foreach (KeyValuePair<PartScript, Made> pair in RecentFires)
                    {
                        if (pair.Key != null && Time.time - pair.Value.Time <= MatchWindow) continue;
                        if (old == null) old = new List<PartScript>();
                        old.Add(pair.Key);
                    }
                    if (old != null) foreach (PartScript part in old) RecentFires.Remove(part);
                }
            }
            catch (Exception e)
            {
                Pending.Clear();
                RecentFires.Clear();
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack fire sync failed: " + e.Message);
            }
        }

        // The game's fire never came (lost, or the effect limit dropped it): light our own copy.
        private static void LightOurOwn(Waiting waiting)
        {
            PartScript part = waiting.Part;
            if (part == null || part.Aircraft == null || part.Aircraft.DamageEffects == null) return;

            PartDamageEffect effect;
            lightingOurOwn = true;
            try
            {
                effect = part.Aircraft.DamageEffects.CreateFire(part, null);
            }
            finally
            {
                lightingOurOwn = false;
            }
            if (effect != null) Dress(part, effect, waiting.Look);
        }

        private static void Dress(PartScript part, PartDamageEffect effect, Look look)
        {
            string name = part.Part != null ? TagCodec.Parse(part.Part.Name).Label : "part";
            switch (look.Kind)
            {
                case RackFire:
                    AmmoRacks.DressBurnOut(effect);
                    RackFires[part] = effect;
                    break;
                case VentFire:
                    AmmoRacks.DressBurnOut(effect);
                    break;
                case "J":
                    AmmoRacks.DressJet(effect, name, look.Duration, look.Jet, look.Boost);
                    break;
            }
            ArmorFragmentsPlugin.Log.LogInfo("Another player's ammo rack cook-off: fire '" + look.Kind + "' on '" + name + "' dressed like theirs");
        }

        private static void PutOut(PartScript part)
        {
            Pending.RemoveAll(w => w.Part == part && w.Look.Kind == RackFire);
            PartDamageEffect fire;
            if (!RackFires.TryGetValue(part, out fire)) return;
            RackFires.Remove(part);
            if (fire != null && !fire.Destroyed) fire.DestroyEffect();
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static float Num(string text)
        {
            float value;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0f;
        }

        private static Vector3 Vec(string[] fields, int first)
        {
            return new Vector3(Num(fields[first]), Num(fields[first + 1]), Num(fields[first + 2]));
        }
    }

    // Sees every fire the game makes, for RackFireSync.OnFireMade.
    [HarmonyPatch(typeof(PartDamageEffects), "CreateEffect",
        new Type[] { typeof(PartDamageEffects.DamageEffectType), typeof(PartScript), typeof(Vector3?), typeof(Vector3?) })]
    internal static class RackFireSeenPatch
    {
        private static void Postfix(PartDamageEffects.DamageEffectType effectType, PartScript part, PartDamageEffect __result)
        {
            if (effectType != PartDamageEffects.DamageEffectType.Fire) return;
            try
            {
                RackFireSync.OnFireMade(part, __result);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack fire sync (fire seen) failed: " + e.Message);
            }
        }
    }
}
