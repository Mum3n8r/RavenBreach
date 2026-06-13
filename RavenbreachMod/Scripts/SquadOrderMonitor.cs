using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RavenbreachMod
{
    // Blocks AiTrack and AiOrders from redirecting bots mid player-order move.
    // Any Goto call with isMovementOverride=false is a combat/follow redirect —
    // block those for bots currently under a player move order.
    [HarmonyPatch(typeof(AiActorController), "Goto", new System.Type[] { typeof(Vector3), typeof(bool) })]
    public static class PlayerMoveOrderGotoBlock
    {
        [HarmonyPrefix]
        public static bool Prefix(AiActorController __instance, bool isMovementOverride)
        {
            if (isMovementOverride) return true;
            int id = __instance.GetInstanceID();
            if (!Plugin.ActiveMoveOrderBots.Contains(id)) return true;
            var sq = __instance.squad;
            if (sq == null) return true;
            float expiry;
            if (!Plugin.MoveOrderExpiries.TryGetValue(sq.number, out expiry)) return true;
            if (Time.time > expiry)
            {
                foreach (var ai in sq.aiMembers)
                    if (ai != null) Plugin.ActiveMoveOrderBots.Remove(ai.GetInstanceID());
                Plugin.MoveOrderExpiries.Remove(sq.number);
                Plugin.Log?.LogInfo("[GotoBlock] Expired sq=" + sq.number + " clearing set");
                return true;
            }
            // Only block redirects that happen AFTER the order frame.
            // Calls within the same frame as arming are from PlayerOrderMoveTo
            // spreading formation — those must be allowed through.
            float armedTime;
            if (!Plugin.MoveOrderArmTime.TryGetValue(sq.number, out armedTime)) return true;
            if (Time.time <= armedTime + 0.1f)
            {
                Plugin.Log?.LogInfo("[GotoBlock] ALLOWED (same frame spread) bot=" + id + " sq=" + sq.number);
                return true;
            }
            Plugin.Log?.LogInfo("[GotoBlock] BLOCKED bot=" + id + " sq=" + sq.number);
            return false;
        }
    }

    // Blocks MinimapUi from re-showing spawn point buttons when a base is captured.
    // CapturePoint.SetOwner calls UpdateSpawnPointButtons which re-enables the
    // vanilla minimap buttons we hid in HijackMinimapUi — undoing our hijack.
    [HarmonyPatch]
    public static class BlockMinimapSpawnButtonRefresh
    {
        static MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("MinimapUi"), "UpdateSpawnPointButtons");

        [HarmonyPrefix]
        public static bool Prefix()
            => TacticalMapSystem.Instance == null || !TacticalMapSystem.Instance.IsOpen;
    }
}
