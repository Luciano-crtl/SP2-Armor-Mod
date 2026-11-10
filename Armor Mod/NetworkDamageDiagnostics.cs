using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Multiplayer;
using HarmonyLib;

namespace ArmorFragments
{
    // Diagnostics for damage sent between players. A damage message goes:
    //   shooter's game -> server (host) -> the game of the aircraft's owner.
    // These two patches only write log lines, so a test shows where a message gets lost.
    // The server one is useful on the host; the target one on whoever owns the damaged aircraft.

    [HarmonyPatch(typeof(NetworkAircraftScript), "RpcLogic___RpcDamageRemotePartServer___2803290691")]
    internal static class ServerDamageLogPatch
    {
        private static void Prefix(NetworkAircraftScript __instance, int? P_0, int P_1, float P_2)
        {
            ArmorFragmentsPlugin.Log.LogInfo(
                "[NET] Server got damage " + P_2.ToString("F1") + " for part id " + P_1 +
                " of player " + __instance.PlayerId + " (attacker " + (P_0.HasValue ? P_0.Value.ToString() : "none") +
                (Assets.Scripts.Flight.FlightSceneScript.IsPeacefulMode ? ", PEACEFUL MODE: it will be dropped" : "") + ")");
        }
    }

    [HarmonyPatch(typeof(NetworkAircraftScript), "RpcLogic___RpcDamageRemotePartTarget___219172924")]
    internal static class TargetDamageLogPatch
    {
        private static void Prefix(NetworkAircraftScript __instance, int P_2, float P_3, out float __state)
        {
            PartData data = __instance.AircraftScript != null ? __instance.AircraftScript.GetPartById(P_2) : null;
            __state = data != null && data.PartScript != null ? data.PartScript.PartDamage : -1f;
        }

        private static void Postfix(NetworkAircraftScript __instance, int P_2, float P_3, float __state)
        {
            PartData data = __instance.AircraftScript != null ? __instance.AircraftScript.GetPartById(P_2) : null;
            if (data == null || data.PartScript == null)
            {
                ArmorFragmentsPlugin.Log.LogInfo(
                    "[NET] Damage " + P_3.ToString("F1") + " arrived for part id " + P_2 + ", but this game has no such part");
                return;
            }

            ArmorFragmentsPlugin.Log.LogInfo(
                "[NET] Damage " + P_3.ToString("F1") + " arrived for '" + data.Name + "' (id " + P_2 + "): its damage went " +
                __state.ToString("F1") + " -> " + data.PartScript.PartDamage.ToString("F1") +
                " of " + data.PartScript.MaxHealth.ToString("F0") + " health");
        }
    }
}
