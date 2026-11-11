using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight;
using Assets.Scripts.Multiplayer;
using BepInEx.Configuration;
using FishNet.Serializing;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // Multiplayer plumbing of the mod, built on the game's own "part network message" channel (the one
    // magnets and targeting pods use). Two messages travel on it:
    //   CONFIG  host -> everyone: the host's balance and physics settings. A client of someone else's
    //           server uses them instead of its own while it is connected.
    //   HELLO   client -> everyone: "I run the mod". The host then knows that player's shots are already
    //           calculated at the shooter, and does not calculate them again.
    // Players without the mod receive both and ignore them (the game skips message types it does not know).
    internal static class NetSync
    {
        public const byte ConfigMessage = 200;
        public const byte HelloMessage = 201;

        private const float CheckInterval = 1f;      // seconds between looks at the session
        private const float HeartbeatInterval = 5f;  // the host repeats its settings, a client its hello
        private const float ModdedTimeout = 15f;     // a hello is trusted for this long

        // The settings that decide what happens to other players: they come from the host.
        public static readonly List<ConfigEntryBase> GlobalEntries = new List<ConfigEntryBase>();

        private static readonly Dictionary<int, float> ModdedUntil = new Dictionary<int, float>();
        private static Dictionary<ConfigEntryBase, string> userValues;   // not null while the host's values are in use
        private static bool userAutoSave;
        private static string lastApplied;
        private static string lastSent;
        private static float nextCheck;
        private static float nextHeartbeat;
        private static bool failureLogged;

        public static void RegisterGlobal(params ConfigEntryBase[] entries)
        {
            GlobalEntries.AddRange(entries);
        }

        public static bool IsGlobal(ConfigEntryBase entry)
        {
            return GlobalEntries.Contains(entry);
        }

        // ---- Where we are ----------------------------------------------------------

        private static FlightSceneNetworkScript Network()
        {
            FlightSceneScript scene = FlightSceneScript.Instance;
            return scene != null ? scene.FlightSceneNetwork : null;
        }

        // Running the server (a host is also a player).
        public static bool IsHost
        {
            get
            {
                try
                {
                    FlightSceneNetworkScript net = Network();
                    return net != null && net.IsServerStarted;
                }
                catch (Exception) { return false; }
            }
        }

        // Connected to somebody else's server.
        public static bool IsRemoteClient
        {
            get
            {
                try
                {
                    FlightSceneNetworkScript net = Network();
                    return net != null && net.IsClientStarted && !net.IsServerStarted;
                }
                catch (Exception) { return false; }
            }
        }

        // The balance settings are the host's: hidden from the menu and not editable here.
        public static bool GlobalLocked
        {
            get { return IsRemoteClient; }
        }

        public static string Role
        {
            get
            {
                if (IsHost) return "Host: your balance settings apply to everyone with the mod";
                if (IsRemoteClient)
                    return userValues != null ? "Client: balance settings come from the host"
                                              : "Client: waiting for the host's settings";
                return "Single player";
            }
        }

        // ---- Who runs the mod ------------------------------------------------------

        // True for players known to run the mod (and for ourselves).
        public static bool IsModded(int playerId)
        {
            try
            {
                FlightScenePlayer local = FlightSceneScript.Instance != null ? FlightSceneScript.Instance.LocalPlayer : null;
                if (local != null && local.NetworkPlayer != null && local.NetworkPlayer.PlayerId == playerId) return true;
            }
            catch (Exception) { }

            float until;
            return ModdedUntil.TryGetValue(playerId, out until) && Time.unscaledTime < until;
        }

        public static int ModdedCount
        {
            get { return ModdedUntil.Count(pair => Time.unscaledTime < pair.Value); }
        }

        // ---- Messages in -----------------------------------------------------------

        // Called with a message that arrived from 'sender''s aircraft.
        public static void OnMessage(byte type, string text, NetworkAircraftScript sender)
        {
            if (type == HelloMessage)
            {
                if (IsHost) ModdedUntil[sender.PlayerId] = Time.unscaledTime + ModdedTimeout;
            }
            else if (type == ConfigMessage)
            {
                // Only the host (connection 0) may set the rules.
                if (sender.OwnerId == 0 && IsRemoteClient) ApplyHostConfig(text);
            }
        }

        private static void ApplyHostConfig(string payload)
        {
            if (payload == lastApplied) return;   // the host's regular repeat

            Dictionary<string, string> values = Decode(payload);
            ConfigFile file = ArmorFragmentsPlugin.Instance.Config;

            if (userValues == null)
            {
                // First time: remember what the player had, and stop the file from being written with the host's values.
                userValues = new Dictionary<ConfigEntryBase, string>();
                foreach (ConfigEntryBase entry in GlobalEntries) userValues[entry] = entry.GetSerializedValue();
                userAutoSave = file.SaveOnConfigSet;
                file.SaveOnConfigSet = false;
            }

            int applied = 0;
            foreach (ConfigEntryBase entry in GlobalEntries)
            {
                string value;
                if (!values.TryGetValue(entry.Definition.Key, out value)) continue;
                try
                {
                    entry.SetSerializedValue(value);
                    applied++;
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Host setting '" + entry.Definition.Key + "' not applied: " + e.Message);
                }
            }

            lastApplied = payload;
            ArmorFragmentsPlugin.Log.LogInfo("Using the host's balance settings (" + applied + " values)");
        }

        // Back to the player's own values (left the server).
        public static void RestoreUser()
        {
            if (userValues == null) return;

            foreach (KeyValuePair<ConfigEntryBase, string> pair in userValues)
            {
                try { pair.Key.SetSerializedValue(pair.Value); }
                catch (Exception) { }
            }
            ArmorFragmentsPlugin.Instance.Config.SaveOnConfigSet = userAutoSave;
            userValues = null;
            lastApplied = null;
            ArmorFragmentsPlugin.Log.LogInfo("Left the server: your own balance settings are back");
        }

        // Saves the config file without ever writing the host's values into it.
        public static void SaveConfig()
        {
            ConfigFile file = ArmorFragmentsPlugin.Instance.Config;
            if (userValues == null)
            {
                file.Save();
                return;
            }

            Dictionary<ConfigEntryBase, string> host = new Dictionary<ConfigEntryBase, string>();
            foreach (ConfigEntryBase entry in GlobalEntries) host[entry] = entry.GetSerializedValue();
            try
            {
                foreach (KeyValuePair<ConfigEntryBase, string> pair in userValues) pair.Key.SetSerializedValue(pair.Value);
                file.Save();
            }
            finally
            {
                foreach (KeyValuePair<ConfigEntryBase, string> pair in host) pair.Key.SetSerializedValue(pair.Value);
            }
        }

        // ---- Messages out ----------------------------------------------------------

        // Called every frame by the plugin.
        public static void Tick()
        {
            try
            {
                if (userValues != null && !IsRemoteClient) RestoreUser();

                if (Time.unscaledTime < nextCheck) return;
                nextCheck = Time.unscaledTime + CheckInterval;

                FlightSceneNetworkScript net = Network();
                if (net == null || !net.IsClientStarted) return;
                if (FlightSceneScript.Instance.AllPlayers.Count() < 2) return;   // nobody to tell

                if (net.IsServerStarted)
                {
                    string config = Encode();
                    if (config != lastSent || Time.unscaledTime >= nextHeartbeat)
                    {
                        if (Send(ConfigMessage, config))
                        {
                            lastSent = config;
                            nextHeartbeat = Time.unscaledTime + HeartbeatInterval;
                        }
                    }
                }
                else if (Time.unscaledTime >= nextHeartbeat)
                {
                    if (Send(HelloMessage, ArmorFragmentsPlugin.Version))
                        nextHeartbeat = Time.unscaledTime + HeartbeatInterval;
                }
            }
            catch (Exception e)
            {
                if (!failureLogged)
                {
                    failureLogged = true;
                    ArmorFragmentsPlugin.Log.LogWarning("Multiplayer sync failed (shown once): " + e);
                }
            }
        }

        // The message rides on one of the local player's own parts: the game delivers it to every other player.
        private static bool Send(byte type, string payload)
        {
            FlightScenePlayer player = FlightSceneScript.Instance.LocalPlayer;
            AircraftScript aircraft = player != null ? player.Aircraft : null;
            INetworkAircraft network = aircraft != null ? aircraft.NetworkAircraft : null;
            if (network == null || !network.IsInitialized || !network.IsOwner) return false;
            if (aircraft.Parts == null || aircraft.Parts.Count == 0) return false;

            PartData part = aircraft.Parts[0];
            network.SendPartNetworkMessage(type, part, (PooledWriter w) => w.WriteString(payload));
            return true;
        }

        private static string Encode()
        {
            StringBuilder text = new StringBuilder();
            foreach (ConfigEntryBase entry in GlobalEntries)
            {
                if (text.Length > 0) text.Append(';');
                text.Append(entry.Definition.Key).Append('=').Append(entry.GetSerializedValue());
            }
            return text.ToString();
        }

        private static Dictionary<string, string> Decode(string payload)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(payload)) return values;

            foreach (string pair in payload.Split(';'))
            {
                int equals = pair.IndexOf('=');
                if (equals > 0) values[pair.Substring(0, equals)] = pair.Substring(equals + 1);
            }
            return values;
        }
    }

    // The game hands every part message to the part it names. Ours name no useful part, so they are
    // taken here first and never reach the game's own handling.
    [HarmonyPatch(typeof(NetworkAircraftScript), "ProcessPartNetworkMessage")]
    internal static class PartMessagePatch
    {
        private static bool Prefix(NetworkAircraftScript __instance, ArraySegment<byte> data)
        {
            // Message layout: type (1 byte), part id (2 bytes), then what the sender wrote.
            if (data.Array == null || data.Count < 3) return true;
            byte type = data.Array[data.Offset];
            if (type != NetSync.ConfigMessage && type != NetSync.HelloMessage) return true;

            try
            {
                PooledReader reader = ReaderPool.Retrieve(data, FishNet.InstanceFinder.NetworkManager);
                string text;
                try
                {
                    reader.ReadUInt8Unpacked();
                    reader.ReadUInt16();
                    text = reader.ReadString();
                }
                finally
                {
                    reader.Store();
                }
                NetSync.OnMessage(type, text, __instance);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Could not read a mod message: " + e.Message);
            }
            return false;
        }
    }
}
