using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RavenbreachMod
{
    internal static class BotState
    {
        public static readonly HashSet<int>           ForcedCqc          = new HashSet<int>();
        public static readonly Dictionary<int, float> CreepCooldown      = new Dictionary<int, float>();
        public static readonly Dictionary<int, float> SprintStress       = new Dictionary<int, float>();
        public static readonly Dictionary<int, float> StumbleCooldown    = new Dictionary<int, float>();
        public static readonly Dictionary<int, float> StumbleFreeze      = new Dictionary<int, float>();
        public static readonly Dictionary<int, float> DirTimers          = new Dictionary<int, float>();
        public static readonly Dictionary<int, float> ReturnFireExpiry   = new Dictionary<int, float>();
        public static readonly Dictionary<int, float> BlastStunExpiry    = new Dictionary<int, float>();

        public static void Cleanup(int id)
        {
            ForcedCqc.Remove(id);
            CreepCooldown.Remove(id);
            SprintStress.Remove(id);
            StumbleCooldown.Remove(id);
            StumbleFreeze.Remove(id);
            DirTimers.Remove(id);
            ReturnFireExpiry.Remove(id);
            BlastStunExpiry.Remove(id);
        }
    }

    [HarmonyPatch(typeof(AiActorController), "Update")]
    internal static class BotSwayPatch
    {
        private const float BOT_STRESS_RATE_T2 = 12f;
        private const float BOT_STRESS_RATE_T3 = 28f;
        private const float BOT_STRESS_DECAY   = 25f;
        private const float BOT_STUMBLE_A_T2   = 0.08f;
        private const float BOT_STUMBLE_B_T2   = 3.0f;
        private const float BOT_STUMBLE_A_T3   = 0.25f;
        private const float BOT_STUMBLE_B_T3   = 4.0f;
        private const float BOT_FREEZE_SEC     = 0.5f;
        private const float BOT_SPRINT_THRESH  = 4.0f;

        private static readonly System.Reflection.FieldInfo _offsetField  = AccessTools.Field(typeof(AiActorController), "acquireTargetOffset");
        private static readonly System.Reflection.FieldInfo _sprintField  = AccessTools.Field(typeof(AiActorController), "isSprinting");
        private static readonly System.Reflection.FieldInfo _alertField   = AccessTools.Field(typeof(AiActorController), "isAlert");

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance)
        {
            if (__instance.actor != null && __instance.actor.IsSeated()) return;

            var sup = __instance.GetComponent<SuppressionSystem>();
            int   id = __instance.GetInstanceID();
            float dt = Time.deltaTime;

            if (sup == null || sup.Tier == 0)
            {
                BotState.ForcedCqc.Remove(id);
                return;
            }

            int   tier = sup.Tier;
            float t    = Time.time + id * 0.001f;

            sup.IsInCover = __instance.IsInTemporaryCover();

            // stumble freeze — bot halts briefly mid-sprint when pinned
            BotState.StumbleFreeze.TryGetValue(id, out float freezeLeft);
            if (freezeLeft > 0f)
            {
                BotState.StumbleFreeze[id] = freezeLeft - dt;
                if (__instance.actor != null) __instance.actor.speedMultiplier = 0.10f;
                _sprintField?.SetValue(__instance, false);
                return;
            }
            else if (__instance.actor != null)
            {
                __instance.actor.speedMultiplier = 1f;
            }

            BotState.StumbleCooldown.TryGetValue(id, out float stumbleCd);
            if (stumbleCd > 0f) BotState.StumbleCooldown[id] = stumbleCd - dt;

            // accuracy noise via acquireTargetOffset
            // tier 1: rattled but functional, tier 2: shots go wide, tier 3: effectively pinned
            // only applies if BotDesyncPatch hasn't already pushed offset past our envelope
            float accuracyNoise = tier == 1 ? 0.06f : tier == 2 ? 0.18f : 0.40f;
            Vector3 current = (Vector3)(_offsetField?.GetValue(__instance) ?? Vector3.zero);

            if (current.magnitude < accuracyNoise * 2.5f)
                _offsetField?.SetValue(__instance, new Vector3(
                    Mathf.Sin(t * 1.7f + 0.5f) * accuracyNoise,
                    Mathf.Cos(t * 2.1f + 1.3f) * accuracyNoise * 0.3f,
                    Mathf.Sin(t * 1.4f + 2.0f) * accuracyNoise));

            if (tier >= 2)
            {
                bool botSprinting = __instance.actor != null
                                 && __instance.actor.Velocity().magnitude > BOT_SPRINT_THRESH;

                BotState.SprintStress.TryGetValue(id, out float stress);
                if (botSprinting)
                {
                    float rate = tier == 3 ? BOT_STRESS_RATE_T3 : BOT_STRESS_RATE_T2;
                    stress = Mathf.Min(100f, stress + rate * dt);
                    BotState.SprintStress[id] = stress;

                    float A = tier == 3 ? BOT_STUMBLE_A_T3 : BOT_STUMBLE_A_T2;
                    float B = tier == 3 ? BOT_STUMBLE_B_T3 : BOT_STUMBLE_B_T2;
                    BotState.StumbleCooldown.TryGetValue(id, out float cd);

                    if (Random.value < A * Mathf.Exp(B * stress / 100f) * dt && cd <= 0f)
                    {
                        BotState.StumbleFreeze[id]   = BOT_FREEZE_SEC;
                        BotState.StumbleCooldown[id] = 1.5f;
                        BotState.SprintStress[id]    = Mathf.Max(0f, stress - 35f);
                        _sprintField?.SetValue(__instance, false);
                        _offsetField?.SetValue(__instance, Random.insideUnitSphere * accuracyNoise * 3f);
                    }
                }
                else
                {
                    BotState.SprintStress[id] = Mathf.Max(0f, stress - BOT_STRESS_DECAY * dt);
                }

                // pinned — can't sprint
                if (tier >= 3) _sprintField?.SetValue(__instance, false);

                _alertField?.SetValue(__instance, true);
            }

            // squad leader creep — only at tier 1, advance cautiously when rattled
            // tier 2+ should be in cover, not creeping
            if (__instance.isSquadLeader && __instance.squad != null && tier == 1)
            {
                BotState.CreepCooldown.TryGetValue(id, out float lastCreep);
                if (Time.time - lastCreep > 4f)
                {
                    BotState.CreepCooldown[id] = Time.time;
                    try
                    {
                        AccessTools.Method(__instance.squad.GetType(),
                            "StartCreepingAction", new[] { typeof(float) })
                            ?.Invoke(__instance.squad, new object[] { 2.0f });
                    }
                    catch { }
                }
            }
        }
    }

    // Block prone for bots under active player move orders — they should march,
    // not drop and stare at each other mid-route.
    [HarmonyPatch]
    internal static class BotProneOrderBlockPatch
    {
        static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(AiActorController), "GetWantsToProne");

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance, ref bool __result)
        {
            if (!__result) return;
            var sq = __instance?.squad;
            if (sq != null && Plugin.MoveOrderExpiries.ContainsKey(sq.number))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(AiActorController), "IsTakingFire")]
    internal static class BotSuppressedTakingFirePatch
    {
        private static readonly System.Reflection.FieldInfo _dirField =
            AccessTools.Field(typeof(AiActorController), "takingFireDirection");

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance, ref bool __result)
        {
            if (__result) return;
            var sup = __instance.GetComponent<SuppressionSystem>();
            // tier 1+ registers as taking fire so even lightly suppressed bots seek cover
            if (sup == null || sup.Tier < 1) return;

            __result = true;

            int   id          = __instance.GetInstanceID();
            var   currentDir  = (Vector3)(_dirField?.GetValue(__instance) ?? Vector3.zero);
            BotState.DirTimers.TryGetValue(id, out float lastUpdate);

            if (currentDir == Vector3.zero || Time.time - lastUpdate > 3f)
            {
                BotState.DirTimers[id] = Time.time;
                float angle = Time.time * 0.7f + id;
                float chaos = sup.Tier >= 3 ? Random.Range(-0.5f, 0.5f) : 0f;
                _dirField?.SetValue(__instance, new Vector3(
                    Mathf.Sin(angle + chaos), 0f, Mathf.Cos(angle + chaos)));
            }
        }
    }

    public static class BotHearingStress
    {
        public static void ApplyExplosionDisorientation(AiActorController bot, float intensity)
        {
            if (bot == null) return;
            var offsetField = AccessTools.Field(typeof(AiActorController), "acquireTargetOffset");
            if (offsetField != null)
            {
                Vector3 current = (Vector3)offsetField.GetValue(bot);
                Vector3 newOffset = current + Random.insideUnitSphere.normalized * intensity * 6f;
                if (newOffset.magnitude > 4f) newOffset = newOffset.normalized * 4f;
                offsetField.SetValue(bot, newOffset);
            }
            var dirField = AccessTools.Field(typeof(AiActorController), "takingFireDirection");
            if (dirField != null)
                dirField.SetValue(bot, Random.insideUnitSphere.normalized);
        }
    }

    [HarmonyPatch(typeof(Actor), "Kill")]
    internal static class BotDeathCleanupPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Actor __instance)
        {
            var ai = __instance.controller as AiActorController;
            if (ai == null) return;
            BotState.Cleanup(ai.GetInstanceID());
        }
    }

    [HarmonyPatch(typeof(Weapon), "Shoot")]
    internal static class BotReturnFirePatch
    {
        // delay scales with tier — pinned bots can barely return fire
        private static readonly float[] DelayMin = { 0f, 0.2f, 0.8f, 1.5f };
        private static readonly float[] DelayMax = { 0f, 0.8f, 2.2f, 4.0f };

        [HarmonyPrefix]
        public static bool Prefix(Weapon __instance)
        {
            if (__instance == null || __instance.user == null) return true;
            var actor = __instance.user;
            var ai    = actor.controller as AiActorController;
            if (ai == null) return true;

            int id = ai.GetInstanceID();

            BotState.BlastStunExpiry.TryGetValue(id, out float blastExpiry);
            if (Time.time < blastExpiry) return false;

            BotState.ReturnFireExpiry.TryGetValue(id, out float fireExpiry);
            if (Time.time < fireExpiry) return false;

            var sup = ai.GetComponent<SuppressionSystem>();
            if (sup == null || sup.Tier == 0) return true;

            int   tier  = Mathf.Clamp(sup.Tier, 0, 3);
            float delay = Random.Range(DelayMin[tier], DelayMax[tier]);
            BotState.ReturnFireExpiry[id] = Time.time + delay;
            return false;
        }
    }
}
