using HarmonyLib;
using System.Reflection;
using UnityEngine;

namespace RavenbreachMod
{
    [HarmonyPatch(typeof(Weapon), "Shoot")]
    public static class WeaponShootSpreadPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Weapon __instance)
        {
            var sup = SuppressionTracker.PlayerSuppression;
            if (sup == null) return;
            var fpsCtrl = FpsActorController.instance;
            if (fpsCtrl == null || fpsCtrl.actor == null) return;
            if (fpsCtrl.actor.activeWeapon != __instance) return;
            float multiplier;
            if      (sup.Tier == 1) multiplier = 1.25f;
            else if (sup.Tier == 2) multiplier = 1.60f;
            else if (sup.Tier == 3) multiplier = 2.20f;
            else                    multiplier = 1.00f;
            if (multiplier <= 1f) return;
            var field = AccessTools.Field(typeof(Weapon), "followupSpreadMagnitude");
            if (field == null) return;
            float current = (float)field.GetValue(__instance);
            field.SetValue(__instance, current * multiplier);
        }
    }

    [HarmonyPatch(typeof(FpsActorController), "ApplyRecoil")]
    public static class RecoilSwayPatch
    {
        private static MethodInfo _screenshakeMethod;

        [HarmonyPostfix]
        public static void Postfix(FpsActorController __instance)
        {
            var sup = SuppressionTracker.PlayerSuppression;
            if (sup == null || sup.Tier < 2) return;
            if (FpsActorController.instance != __instance) return;
            var fpParent = AccessTools.Field(typeof(FpsActorController), "fpParent").GetValue(__instance);
            if (fpParent == null) return;
            float mag   = sup.Tier == 2 ? 0.4f : 0.8f;
            int   ticks = sup.Tier == 2 ? 1    : 2;
            var m = _screenshakeMethod ?? (_screenshakeMethod = fpParent.GetType().GetMethod("ApplyScreenshake",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(float), typeof(int) }, null));
            m?.Invoke(fpParent, new object[] { mag, ticks });
        }
    }

    [HarmonyPatch(typeof(FpsActorController), "Die")]
    public static class PlayerDeathScreenPatch
    {
        [HarmonyPostfix]
        public static void Postfix(FpsActorController __instance)
        {
            if (FpsActorController.instance != __instance) return;
            SuppressionEffects.TriggerDeath();
        }
    }

    [HarmonyPatch(typeof(FpsActorController), "SpawnAt")]
    public static class PlayerSpawnScreenPatch
    {
        // only true when we saw a Die() call first — reinforcement/initial spawns don't set this
        internal static bool _pendingRespawn = false;

        [HarmonyPostfix]
        public static void Postfix(FpsActorController __instance)
        {
            if (FpsActorController.instance != __instance) return;
            if (!_pendingRespawn) return;
            _pendingRespawn = false;

            SuppressionEffects.TriggerSpawn();

            if (__instance.actor != null)
            {
                InjurySystem.ClearActor(__instance.actor);
                BleedingSystem.RemoveActor(__instance.actor);
                __instance.actor.speedMultiplier = 1.0f;
                try { __instance.actor.ForceStance(Actor.Stance.Stand); } catch { }
            }
        }
    }

    // sets the flag before SpawnAt fires
    [HarmonyPatch(typeof(FpsActorController), "Die")]
    public static class PlayerDeathRespawnFlagPatch
    {
        [HarmonyPostfix]
        public static void Postfix(FpsActorController __instance)
        {
            if (FpsActorController.instance != __instance) return;
            PlayerSpawnScreenPatch._pendingRespawn = true;
        }
    }

    [HarmonyPatch(typeof(DamageUI), "ShowDamageIndicator")]
    public static class HideDamageIndicatorPatch
    {
        [HarmonyPrefix]
        public static bool Prefix() => false;
    }
}
