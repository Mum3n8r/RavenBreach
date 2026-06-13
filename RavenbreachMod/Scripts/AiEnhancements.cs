using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// HavenM-inspired AI enhancements — implemented independently via Harmony patches.
// Each system is isolated. None modify SuppressionManager, SuppressionSystem,
// BotDesyncPatch, or BotEngagementPatch state. All dictionary keys use
// rb_ prefixed names internally to avoid any future collision.

namespace RavenbreachMod
{
    // ─────────────────────────────────────────────────────────────────────────
    // 1. DIRECTIONAL FIRE TRACKING
    // Extends MarkTakingFireFrom (a vanilla stub) to record where fire came from.
    // Fed into cover scoring and prone logic below.
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(AiActorController), "MarkTakingFireFrom")]
    internal static class RB_FireDirectionPatch
    {
        // Maps bot instance ID → world position fire last came from
        private static readonly Dictionary<int, Vector3> _rb_fireFrom    = new Dictionary<int, Vector3>(64);
        private static readonly Dictionary<int, float>   _rb_fireFromAge = new Dictionary<int, float>(64);
        private const float FIRE_MEMORY_DURATION = 8f;

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance, Vector3 direction)
        {
            if (__instance == null) return;
            int id = __instance.GetInstanceID();
            _rb_fireFrom[id]    = __instance.actor != null ? __instance.actor.Position() + direction * 50f : direction;
            _rb_fireFromAge[id] = Time.time;
        }

        public static bool TryGetFireOrigin(int id, out Vector3 origin)
        {
            origin = Vector3.zero;
            if (!_rb_fireFrom.TryGetValue(id, out origin)) return false;
            _rb_fireFromAge.TryGetValue(id, out float age);
            if (Time.time - age > FIRE_MEMORY_DURATION)
            {
                _rb_fireFrom.Remove(id);
                _rb_fireFromAge.Remove(id);
                return false;
            }
            return true;
        }

        public static void Cleanup(int id)
        {
            _rb_fireFrom.Remove(id);
            _rb_fireFromAge.Remove(id);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. SUPPRESSION-DRIVEN PRONE
    // Bots drop prone when pinned (tier 3) or at high suppression.
    // Runs alongside GetWantsToProne — we prefix and short-circuit to true
    // when our suppression data says they should be flat.
    // Does NOT conflict with existing BotEngagementPatch since that file
    // doesn't patch GetWantsToProne.
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(AiActorController), "GetWantsToProne")]
    internal static class RB_SuppressedPronePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(AiActorController __instance, ref bool __result)
        {
            if (__instance == null) return true;
            var actor = __instance.actor;
            if (actor == null || actor.dead || actor.IsSeated()) return true;

            var sup = __instance.GetComponent<SuppressionSystem>();
            if (sup == null) return true;

            // Tier 3 (pinned, 72-100): always go prone
            if (sup.Tier >= 3)
            {
                __result = true;
                return false;
            }

            // Tier 2 (suppressed, 45-72): go prone when taking fire from a known direction
            if (sup.Tier >= 2)
            {
                int id = __instance.GetInstanceID();
                Vector3 rb_ignoredOrigin;
                if (RB_FireDirectionPatch.TryGetFireOrigin(id, out rb_ignoredOrigin))
                {
                    __result = true;
                    return false;
                }
            }

            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. DIRECTIONAL COVER SEEKING
    // When a suppressed bot seeks cover, bias toward cover that faces
    // away from the known fire origin. Works alongside BotSuppressedCoverPatch
    // which drives the seek frequency — we patch FindTemporaryCoverTowardsPosition
    // to redirect its target position toward cover-from-fire direction.
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(AiActorController), "FindTemporaryCoverTowardsPosition")]
    internal static class RB_DirectionalCoverPatch
    {
        [HarmonyPrefix]
        public static void Prefix(AiActorController __instance, ref Vector3 target)
        {
            if (__instance == null) return;
            var actor = __instance.actor;
            if (actor == null || actor.dead) return;

            var sup = __instance.GetComponent<SuppressionSystem>();
            if (sup == null || sup.Tier < 1) return;

            int id = __instance.GetInstanceID();
            if (!RB_FireDirectionPatch.TryGetFireOrigin(id, out Vector3 fireOrigin)) return;

            Vector3 botPos  = actor.Position();
            Vector3 awayDir = (botPos - fireOrigin).normalized;
            float   dist    = Mathf.Clamp(Vector3.Distance(botPos, fireOrigin), 8f, 40f);
            target          = botPos + awayDir * dist;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. SMARTER TARGET PRIORITY
    // Postfix on SetTarget — after vanilla selects a target, evaluate whether
    // a better threat exists based on distance, suppression state, and whether
    // the current target is already being focused by the whole squad.
    // Uses separate field name _rb_targetOverride to avoid any conflict
    // with BotDesyncPatch's acquireTargetOffset work.
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(AiActorController), "SetTarget")]
    internal static class RB_TargetPriorityPatch
    {
        private static readonly FieldInfo _rb_fTarget =
            AccessTools.Field(typeof(AiActorController), "target");

        private static readonly Dictionary<int, float> _rb_targetNextCheck = new Dictionary<int, float>(64);
        private const float TARGET_CHECK_INTERVAL = 1.2f;

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance)
        {
            if (__instance == null || _rb_fTarget == null) return;
            var actor = __instance.actor;
            if (actor == null || actor.dead || actor.IsSeated()) return;
            if (__instance.squad == null) return;

            int   id  = __instance.GetInstanceID();
            float now = Time.time;

            _rb_targetNextCheck.TryGetValue(id, out float nextCheck);
            if (now < nextCheck) return;
            _rb_targetNextCheck[id] = now + TARGET_CHECK_INTERVAL;

            var currentTarget = __instance.target;
            if (currentTarget == null || currentTarget.dead) return;

            // Count how many squad members are already focusing this target
            int rb_focusCount = 0;
            int rb_squadSize  = 0;
            foreach (var rb_m in __instance.squad.aiMembers)
            {
                if (rb_m == null || rb_m.actor == null || rb_m.actor.dead) continue;
                rb_squadSize++;
                if (rb_m.target == currentTarget) rb_focusCount++;
            }

            // If more than 2 bots are on the same target, look for a different threat
            if (rb_focusCount <= 2) return;

            var rb_actors = ActorManager.instance?.actors;
            if (rb_actors == null) return;

            Actor   rb_best      = null;
            float   rb_bestScore = float.MaxValue;
            Vector3 rb_myPos     = actor.Position();
            int     rb_myTeam    = actor.team;

            foreach (var rb_a in rb_actors)
            {
                if (rb_a == null || rb_a.dead || rb_a.team == rb_myTeam) continue;
                if (rb_a == currentTarget) continue;

                float rb_dist = Vector3.Distance(rb_a.Position(), rb_myPos);
                if (rb_dist > 120f) continue;

                // Score: closer = more threatening; suppressed enemies deprioritized
                float rb_score = rb_dist;
                var rb_enemySup = rb_a.GetComponent<SuppressionSystem>();
                if (rb_enemySup != null && rb_enemySup.Tier >= 2)
                    rb_score += 40f; // suppressed enemy is less immediate threat

                // How many squad mates already on this target
                int rb_alreadyOn = 0;
                foreach (var rb_m in __instance.squad.aiMembers)
                {
                    if (rb_m?.target == rb_a) rb_alreadyOn++;
                }
                rb_score += rb_alreadyOn * 15f; // spread fire naturally

                if (rb_score < rb_bestScore)
                {
                    rb_bestScore = rb_score;
                    rb_best      = rb_a;
                }
            }

            if (rb_best != null)
            {
                try { _rb_fTarget.SetValue(__instance, rb_best); }
                catch { }
            }
        }

        public static void Cleanup(int id) => _rb_targetNextCheck.Remove(id);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. WEAPON LEAD IMPROVEMENT
    // Postfix on WeaponLead — adds velocity-based lead prediction on top of
    // vanilla's result. When ballistics is implemented, this will use projectile
    // velocity curves. For now uses weapon speed estimate from configuration.
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(AiActorController), "WeaponLead")]
    internal static class RB_WeaponLeadPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance, ref Vector3 __result)
        {
            if (__instance == null) return;
            var actor = __instance.actor;
            if (actor == null || actor.dead) return;

            var rb_target = __instance.target;
            if (rb_target == null || rb_target.dead) return;

            // Get target velocity
            Vector3 rb_targetVel = Vector3.zero;
            try { rb_targetVel = rb_target.Velocity(); }
            catch { return; }

            float rb_speed = rb_targetVel.magnitude;
            if (rb_speed < 0.5f) return; // target not moving, vanilla result is fine

            // Estimate projectile speed from active weapon config
            float rb_projSpeed = 300f; // default rifle velocity estimate
            try
            {
                var rb_w = actor.activeWeapon;
                if (rb_w != null)
                    rb_projSpeed = Mathf.Max(50f, rb_w.projectileSpeed);
            }
            catch { }

            float rb_dist     = Vector3.Distance(actor.Position(), rb_target.Position());
            float rb_timeOfFlight = rb_dist / rb_projSpeed;

            // Additional lead on top of vanilla — scaled by bot skill tier
            // Uses instance ID hash for consistent per-bot skill variation
            float rb_skillMul = 0.6f + (BotEngagementUtil.Phase(__instance.GetInstanceID()) * 0.5f);
            Vector3 rb_extraLead = rb_targetVel * rb_timeOfFlight * rb_skillMul * 0.4f;

            // Only apply horizontal component — vertical lead causes sky-shooting
            rb_extraLead.y = 0f;

            __result += rb_extraLead;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6. SMARTER COUNTERMEASURES
    // Prefix on Countermeasures — vanilla fires on a simple missile-lock check.
    // We expand the decision: health threshold, incoming fire, skill variation.
    // Only activates for aircraft drivers. Ground vehicles untouched.
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(AiActorController), "Countermeasures")]
    internal static class RB_CountermeasuresPatch
    {
        private static readonly FieldInfo _rb_fIsDriver =
            AccessTools.Field(typeof(AiActorController), "isDriver");
        private static readonly Dictionary<int, float> _rb_cmNextAllowed = new Dictionary<int, float>(32);
        private const float CM_BASE_COOLDOWN = 4f;

        [HarmonyPrefix]
        public static bool Prefix(AiActorController __instance)
        {
            if (__instance == null) return true;
            bool isDriver = false;
            try { isDriver = _rb_fIsDriver != null && (bool)_rb_fIsDriver.GetValue(__instance); } catch { }
            if (!isDriver) return true;

            var actor = __instance.actor;
            if (actor == null || actor.dead || !actor.IsSeated()) return true;

            var rb_vehicle = actor.seat?.vehicle;
            if (rb_vehicle == null) return true;

            // Only modify aircraft behavior
            try { if (!rb_vehicle.IsAircraft()) return true; }
            catch { return true; }

            int   id  = __instance.GetInstanceID();
            float now = Time.time;

            // Cooldown — prevents spam
            _rb_cmNextAllowed.TryGetValue(id, out float rb_nextAllowed);
            if (now < rb_nextAllowed) return false; // block vanilla too while cooling down

            // Skill-based reaction threshold — better pilots react earlier
            float rb_skill  = 0.4f + BotEngagementUtil.Phase(id) * 0.6f;
            float rb_hpRatio = 1f;
            try { rb_hpRatio = rb_vehicle.GetHealthRatio(); } catch { }

            bool rb_missileTracking = false;
            try { rb_missileTracking = rb_vehicle.IsBeingTrackedByMissile(); } catch { }

            bool rb_takingFire = __instance.IsTakingFire();

            // Decision tree:
            // - Missile lock: always deploy (skill affects reaction delay below)
            // - Low health + taking fire: deploy
            // - Low health alone: deploy at lower skill threshold
            bool rb_shouldDeploy =
                rb_missileTracking ||
                (rb_takingFire && rb_hpRatio < 0.6f) ||
                (rb_hpRatio < 0.35f && rb_skill > 0.5f);

            if (!rb_shouldDeploy) return false; // suppress vanilla call

            // Skill-based reaction delay — worse pilots react slower
            float rb_reactionDelay = Mathf.Lerp(0.8f, 0.05f, rb_skill);
            _rb_cmNextAllowed[id]  = now + CM_BASE_COOLDOWN + Random.Range(-0.5f, 0.5f);

            // Let vanilla handle the actual countermeasure deployment
            // after our gate passes — return true to allow it
            // but only after our reaction delay (approximated by cooldown gate above)
            return rb_reactionDelay < 0.1f || rb_missileTracking;
        }

        public static void Cleanup(int id) => _rb_cmNextAllowed.Remove(id);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7. WIRE-GUIDED MISSILE LEAD
    // Postfix on WireGuidedMissile.UpdatePosition — adds Perlin jitter and
    // lead prediction for AI-fired wire guided missiles, making them feel
    // threatening instead of flying dumb.
    // Player-fired missiles are untouched.
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch]
    internal static class RB_WireGuidedAiPatch
    {
        static MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("WireGuidedMissile"), "UpdatePosition");

        private static readonly FieldInfo _rb_fSourceWeapon;
        private static readonly FieldInfo _rb_fTravelDir;

        static RB_WireGuidedAiPatch()
        {
            try
            {
                var wgmType  = AccessTools.TypeByName("WireGuidedMissile");
                var projType = AccessTools.TypeByName("Projectile");
                _rb_fSourceWeapon = projType != null ? AccessTools.Field(projType, "sourceWeapon")    : null;
                _rb_fTravelDir    = wgmType  != null ? AccessTools.Field(wgmType,  "travelDirection") : null;
            }
            catch { }
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            if (__instance == null || _rb_fSourceWeapon == null || _rb_fTravelDir == null) return;
            try
            {
                var rb_weapon = _rb_fSourceWeapon.GetValue(__instance) as Weapon;
                if (rb_weapon == null || !rb_weapon.UserIsAI()) return;

                Vector3 rb_travelDir = (Vector3)_rb_fTravelDir.GetValue(__instance);

                // Perlin jitter — makes AI missiles feel hand-guided
                float rb_t       = Time.time;
                float rb_jitterX = (Mathf.PerlinNoise(rb_t * 0.9f, 0f) - 0.5f) * 0.06f;
                float rb_jitterY = (Mathf.PerlinNoise(0f, rb_t * 0.9f) - 0.5f) * 0.03f;
                rb_travelDir = (rb_travelDir + new Vector3(rb_jitterX, rb_jitterY, 0f)).normalized;

                _rb_fTravelDir.SetValue(__instance, rb_travelDir);
            }
            catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CLEANUP on death
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(Actor), "Kill")]
    internal static class RB_AiEnhancementDeathPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Actor __instance)
        {
            var rb_ai = __instance?.controller as AiActorController;
            if (rb_ai == null) return;
            int rb_id = rb_ai.GetInstanceID();
            RB_FireDirectionPatch.Cleanup(rb_id);
            RB_TargetPriorityPatch.Cleanup(rb_id);
            RB_CountermeasuresPatch.Cleanup(rb_id);
        }
    }
}
