using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RavenbreachMod
{
    // Reduces excessive crouching and strafing on AI bots.
    // Bots should crouch when in cover or taking fire — not while pathing to objectives.

    // ─────────────────────────────────────────────────────────────────────────
    // 1. CROUCH REDUCTION
    // GetWantsToCrouch returns true too aggressively — any alert bot that's
    // moving while in a squad with a player leader will crouch. That causes
    // the "crouch walking across the map" problem.
    // Fix: if the bot has a path (moving with purpose), is not in cover,
    // and is not actively taking fire, return false.
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(AiActorController), "GetWantsToCrouch")]
    public static class BotCrouchReductionPatch
    {
        private static readonly System.Reflection.MethodInfo _hideBehindCover =
            AccessTools.Method(typeof(AiActorController), "HideBehindCover");
        [HarmonyPrefix]
        public static bool Prefix(AiActorController __instance, ref bool __result)
        {
            if (__instance == null) return true;

            // Allow crouch if in cover
            if (__instance.InCover()) return true;
            // HideBehindCover is private — access via reflection
            try { if ((bool)_hideBehindCover.Invoke(__instance, null)) return true; } catch { }

            // Allow crouch if actively taking fire
            if (__instance.IsTakingFire()) return true;

            // Allow crouch if spotted a target and NOT moving
            if (__instance.HasSpottedTarget() && !__instance.IsMoving()) return true;

            // Block crouch if bot has a path — moving to a destination
            if (__instance.HasPath())
            {
                __result = false;
                return false;
            }

            // Block crouch if sprinting
            if (__instance.IsSprinting())
            {
                __result = false;
                return false;
            }

            return true;
        }
    }

    // When hold-after-objective is toggled for a squad, issue a Hold order
    // when the squad leader completes their path.
    [HarmonyPatch(typeof(AiActorController), "PathDone")]
    public static class HoldAfterObjectivePatch
    {
        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance)
        {
            if (__instance == null || TacticalMapSystem.Instance == null) return;
            var sq = __instance.squad;
            if (sq == null) return;
            // Only trigger on the squad leader
            var leader = sq.Leader();
            if (leader == null || leader != __instance) return;
            TacticalMapSystem.Instance.TryHoldAfterObjective(sq);
        }
    }
    // Vanilla CanLookAround returns true whenever bot hasn't spotted a target,
    // causing the weapon to scan via raycasts that hit terrain and sky.
    // Fix: only allow look-around when stationary or in cover — not while pathing.
    [HarmonyPatch(typeof(AiActorController), "CanLookAround")]
    public static class BotIdleAimPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance, ref bool __result)
        {
            if (!__result) return;
            if (__instance == null) return;
            // If bot has a target, vanilla decides
            if (__instance.HasSpottedTarget()) return;
            // If moving with purpose, kill the look-around — face forward instead
            if (__instance.HasPath() || __instance.IsMoving())
            {
                __result = false;
            }
        }
    }
    // Strafe frequency reduction
    [HarmonyPatch(typeof(AiActorController), "Awake")]
    public static class BotStrafeTimerPatch
    {
        private static readonly FieldInfo _fStrafeMin =
            AccessTools.Field(typeof(AiActorController), "STRAFE_MIN_TIME");
        private static readonly FieldInfo _fStrafeMax =
            AccessTools.Field(typeof(AiActorController), "STRAFE_MAX_TIME");
        private static readonly FieldInfo _fStrafeSpeed =
            AccessTools.Field(typeof(AiActorController), "STRAFE_TARGET_SPEED");

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance)
        {
            if (__instance == null) return;
            // Default vanilla values are roughly 1.5s min, 3.5s max
            // Push to 3.5s min, 7s max — bots strafe roughly half as often
            try { if (_fStrafeMin != null) _fStrafeMin.SetValue(__instance, 3.5f); } catch { }
            try { if (_fStrafeMax != null) _fStrafeMax.SetValue(__instance, 7.0f); } catch { }
            // Slightly reduce strafe speed so it looks less twitchy
            try { if (_fStrafeSpeed != null) _fStrafeSpeed.SetValue(__instance, 2.2f); } catch { }
        }
    }
}
