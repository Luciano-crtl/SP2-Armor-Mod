using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight;
using Assets.Scripts.Multiplayer;
using FishNet.Serializing;
using UnityEngine;

namespace ArmorFragments
{
    // The marks of a shell on another player's armor, as the other players see them.
    //
    // Each game with the mod works out the marks of the shots IT resolves (PlateDamageMaps), so nobody else would see them: not the
    // player who was shot, not the others watching. So when a shell leaves a mark (a dent, a hole, a groove) on a plate of a vehicle that
    // belongs to a player, the shooter's mod sends one more part message (type 203, on the shooter's own vehicle, the way the ammo rack
    // fires travel) with everything that makes the mark: which player's vehicle, which part, and the PlateImpact. Every other game with
    // the mod finds that plate and draws the same mark (the same seed gives the same simulation). Players without the mod ignore it.
    //
    //   M,player,part,kind,front,u,v,dirU,dirV,angle,caliber,strength,thickness,seed,mass,speed,hardness,brittleness,kept,deflection,delay,axis
    //
    // What the sender keeps (up to MaxHistory marks, only for vehicles still alive) goes out again when a new player joins, so they see the
    // marks that were already there; a mark that arrives twice (a repeat for someone who was already here) is drawn once.
    // Fragments' pits and small holes are not sent (hundreds per shot); only the shell's own marks.
    internal static class MarksSync
    {
        public const byte Message = 203;
        private const int MaxHistory = 150;
        private const int ResendPerFrame = 3;

        private sealed class Entry
        {
            public AircraftScript Target;
            public string Text;
        }

        private static readonly List<Entry> History = new List<Entry>();
        private static readonly HashSet<long> Applied = new HashSet<long>();
        private static readonly Queue<Entry> Resend = new Queue<Entry>();
        private static int knownPlayers;
        private static bool failureLogged;

        // ---- The shooter's game -------------------------------------------------------------------

        // Called when a shell's mark has been worked out on 'part' (not for a simulated shot, which is only drawn).
        public static void Send(PartScript part, PlateImpact impact, float delay)
        {
            try
            {
                AircraftScript target = part != null ? part.Aircraft : null;
                INetworkAircraft targetNet = target != null ? target.NetworkAircraft : null;
                if (targetNet == null || !targetNet.IsInitialized || part.Part == null) return;   // not a player's vehicle: nothing to tell
                if (!AnyoneElse()) return;

                string text = Encode(targetNet.PlayerId, part.Part.Id, impact, delay);
                Entry entry = new Entry { Target = target, Text = text };
                History.Add(entry);
                if (History.Count > MaxHistory) History.RemoveAt(0);
                Transmit(text);
            }
            catch (Exception e)
            {
                Failed("Marks not sent to other players (shown once): " + e.Message);
            }
        }

        private static bool AnyoneElse()
        {
            FlightSceneScript scene = FlightSceneScript.Instance;
            return scene != null && scene.AllPlayers.Count > 1;
        }

        // Rides on the local player's own vehicle, which the game shows to everybody.
        private static bool Transmit(string text)
        {
            FlightScenePlayer player = FlightSceneScript.Instance.LocalPlayer;
            AircraftScript aircraft = player != null ? player.Aircraft : null;
            INetworkAircraft network = aircraft != null ? aircraft.NetworkAircraft : null;
            if (network == null || !network.IsInitialized || !network.IsOwner) return false;
            if (aircraft.Parts == null || aircraft.Parts.Count == 0) return false;
            network.SendPartNetworkMessage(Message, aircraft.Parts[0], (PooledWriter w) => w.WriteString(text));
            return true;
        }

        // Every frame: a new player is told what is already on the vehicles (a few marks a frame).
        public static void Tick()
        {
            try
            {
                FlightSceneScript scene = FlightSceneScript.Instance;
                if (scene == null) return;
                int players = scene.AllPlayers.Count;
                if (players > knownPlayers && History.Count > 0)
                {
                    Resend.Clear();
                    foreach (Entry entry in History)
                        if (entry.Target != null) Resend.Enqueue(entry);
                }
                knownPlayers = players;

                for (int i = 0; i < ResendPerFrame && Resend.Count > 0; i++)
                {
                    Entry entry = Resend.Dequeue();
                    if (entry.Target == null) continue;
                    if (!Transmit(entry.Text)) { Resend.Clear(); break; }
                }
            }
            catch (Exception e)
            {
                Failed("Marks sync failed (shown once): " + e.Message);
                Resend.Clear();
            }
        }

        public static void Reset()
        {
            History.Clear();
            Applied.Clear();
            Resend.Clear();
            knownPlayers = 0;
        }

        // ---- Everybody else's game ----------------------------------------------------------------

        // A message from 'sender' (our copy of another player's vehicle).
        public static void OnMessage(NetworkAircraftScript sender, string text)
        {
            try
            {
                string[] f = text.Split(',');
                if (f.Length < 21 || f[0] != "M") return;
                int playerId = (int)Num(f[1]), partId = (int)Num(f[2]);
                int seed = (int)Num(f[13]);

                long key = ((long)(sender != null ? sender.PlayerId : 0) << 48) ^ ((long)partId << 32) ^ (uint)seed;
                if (!Applied.Add(key)) return;   // already drawn (the same mark sent again for a player who joined)
                if (Applied.Count > 4000) { Applied.Clear(); Applied.Add(key); }

                AircraftScript aircraft = FindVehicle(playerId);
                if (aircraft == null) return;
                PartData data = aircraft.GetPartById(partId, true);
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) return;

                PlateImpact impact = new PlateImpact
                {
                    Kind = (DamageKind)(int)Num(f[3]),
                    Front = f[4] == "1",
                    U = Num(f[5]), V = Num(f[6]),
                    DirU = Num(f[7]), DirV = Num(f[8]),
                    Angle = Num(f[9]),
                    Caliber = Num(f[10]),
                    Strength = Num(f[11]),
                    Thickness = Num(f[12]),
                    Seed = seed,
                    ShellMass = Num(f[14]),
                    Speed = Num(f[15]),
                    PlateHardness = Num(f[16]),
                    PlateBrittleness = Num(f[17]),
                    KeptEnergy = Num(f[18]),
                    Deflection = Num(f[19]),
                    Axis = f.Length > 21 ? (int)Num(f[21]) : 0,
                };
                PlateDamageMaps.OnRemoteImpact(part, impact, Num(f[20]));
            }
            catch (Exception e)
            {
                Failed("Could not read a mark from another player (shown once): " + e.Message);
            }
        }

        // The vehicle of the player with this id (ours or somebody else's copy of it).
        private static AircraftScript FindVehicle(int playerId)
        {
            FlightSceneScript scene = FlightSceneScript.Instance;
            if (scene == null) return null;
            foreach (FlightScenePlayer player in scene.AllPlayers)
            {
                AircraftScript aircraft = player.Aircraft;
                if (aircraft != null && aircraft.NetworkAircraft != null && aircraft.NetworkAircraft.PlayerId == playerId) return aircraft;
            }
            return null;
        }

        // ---- The text ------------------------------------------------------------------------------

        private static string Encode(int playerId, int partId, PlateImpact i, float delay)
        {
            StringBuilder s = new StringBuilder("M");
            s.Append(',').Append(playerId).Append(',').Append(partId).Append(',').Append((int)i.Kind).Append(',').Append(i.Front ? "1" : "0");
            foreach (float value in new[] { i.U, i.V, i.DirU, i.DirV, i.Angle, i.Caliber, i.Strength, i.Thickness })
                s.Append(',').Append(F(value));
            s.Append(',').Append(i.Seed);
            foreach (float value in new[] { i.ShellMass, i.Speed, i.PlateHardness, i.PlateBrittleness, i.KeptEnergy, i.Deflection, delay })
                s.Append(',').Append(F(value));
            s.Append(',').Append(i.Axis);   // last, so a message from a version before the sides still reads (as the plate's own faces)
            return s.ToString();
        }

        private static string F(float value)
        {
            return value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private static float Num(string text)
        {
            float value;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0f;
        }

        private static void Failed(string message)
        {
            if (failureLogged) return;
            failureLogged = true;
            ArmorFragmentsPlugin.Log.LogWarning(message);
        }
    }
}
