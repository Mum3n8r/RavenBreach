using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RavenbreachMod
{
    // sprint stagger
    [HarmonyPatch(typeof(AiActorController), "StartSprint")]
    internal static class BotSprintStaggerPatch
    {
        private static readonly Dictionary<int, float> _sprintAllowedAt = new Dictionary<int, float>();

        [HarmonyPrefix]
        public static bool Prefix(AiActorController __instance)
        {
            // suppressed bots need cover NOW — don't gate their movement
            var sup = __instance.GetComponent<SuppressionSystem>();
            if (sup != null && sup.Tier >= 2) return true;

            int   id    = __instance.GetInstanceID();
            float phase = BotEngagementUtil.Phase(id);

            _sprintAllowedAt.TryGetValue(id, out float allowedAt);
            if (allowedAt > 0f)
            {
                if (Time.time >= allowedAt) { _sprintAllowedAt.Remove(id); return true; }
                return false;
            }

            float delay = Mathf.Lerp(0f, 1.2f, phase);
            if (delay < 0.05f) return true;

            _sprintAllowedAt[id] = Time.time + delay;
            return false;
        }

        public static void Cleanup(int id) => _sprintAllowedAt.Remove(id);
    }

    // halt duration variation
    [HarmonyPatch(typeof(AiActorController), "Halt")]
    internal static class BotHaltVariationPatch
    {
        [HarmonyPrefix]
        public static void Prefix(AiActorController __instance, ref float duration)
        {
            float phase = BotEngagementUtil.Phase(__instance.GetInstanceID());
            duration = Mathf.Max(0.2f, duration + Mathf.Lerp(-0.35f, 0.35f, phase));
        }
    }

    // strafe stagger
    [HarmonyPatch(typeof(AiActorController), "StrafeTarget")]
    internal static class BotStrafeStaggerPatch
    {
        private static readonly Dictionary<int, float> _strafeAllowedAt = new Dictionary<int, float>();

        [HarmonyPrefix]
        public static bool Prefix(AiActorController __instance)
        {
            int   id    = __instance.GetInstanceID();
            float phase = BotEngagementUtil.Phase(id);

            _strafeAllowedAt.TryGetValue(id, out float allowedAt);
            if (allowedAt > 0f)
            {
                if (Time.time >= allowedAt) { _strafeAllowedAt.Remove(id); return true; }
                return false;
            }

            float delay = Mathf.Lerp(0f, 0.8f, phase);
            if (delay < 0.05f) return true;

            _strafeAllowedAt[id] = Time.time + delay;
            return false;
        }

        public static void Cleanup(int id) => _strafeAllowedAt.Remove(id);
    }

    // movement speed variation
    [HarmonyPatch(typeof(AiActorController), "Update")]
    internal static class BotSpeedVariationPatch
    {
        private static readonly Dictionary<int, float> _speedMul   = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _nextCheck  = new Dictionary<int, float>();

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance)
        {
            var actor = __instance.actor;
            if (actor == null || actor.dead || actor.IsSeated()) return;
            if (!__instance.IsAlert()) return;
            if (__instance.HasSpottedTarget()) return;

            var sup = __instance.GetComponent<SuppressionSystem>();
            if (sup != null && sup.Tier > 0) return;

            int   id  = __instance.GetInstanceID();
            float now = Time.time;

            _nextCheck.TryGetValue(id, out float next);
            if (now < next) return;
            _nextCheck[id] = now + 0.5f;

            if (!_speedMul.ContainsKey(id))
                _speedMul[id] = Mathf.Lerp(0.88f, 1.12f, BotEngagementUtil.Phase(id));

            float mul = _speedMul[id];
            if (Mathf.Abs(mul - 1f) > 0.02f && Mathf.Approximately(actor.speedMultiplier, 1f))
                actor.speedMultiplier = mul;
        }

        public static void Cleanup(int id) { _speedMul.Remove(id); _nextCheck.Remove(id); }
    }

    // cover seek stagger
    [HarmonyPatch(typeof(AiActorController), "FindTemporaryCoverTowardsPosition")]
    internal static class BotCoverStaggerPatch
    {
        private static readonly Dictionary<int, float> _coverAllowedAt = new Dictionary<int, float>();

        [HarmonyPrefix]
        public static bool Prefix(AiActorController __instance)
        {
            // suppressed bots seek cover immediately
            var sup = __instance.GetComponent<SuppressionSystem>();
            if (sup != null && sup.Tier >= 1) return true;

            int   id    = __instance.GetInstanceID();
            float phase = BotEngagementUtil.Phase(id);

            _coverAllowedAt.TryGetValue(id, out float allowedAt);
            if (allowedAt > 0f)
            {
                if (Time.time >= allowedAt) { _coverAllowedAt.Remove(id); return true; }
                return false;
            }

            float delay = Mathf.Lerp(0f, 0.6f, phase);
            if (delay < 0.04f) return true;

            _coverAllowedAt[id] = Time.time + delay;
            return false;
        }

        public static void Cleanup(int id) => _coverAllowedAt.Remove(id);
    }

    // EA37: MaxSpotDistance removed. Use OnSeesEnemy prefix instead.
    // - Hard cap: cancel detection entirely beyond 600m.
    // - Weapon class scaling: scale detectionSpeedMultiplier by range class.
    //   (Lower multiplier = faster detection; we REDUCE multiplier for close-range weapons
    //   to make them spot slower at range, same net effect as old MaxSpotDistance scaling.)
    [HarmonyPatch(typeof(AiActorController), "OnSeesEnemy")]
    internal static class BotScopeEngagementPatch
    {
        private const float MAX_SPOT_DISTANCE = 600f;

        [HarmonyPrefix]
        public static bool Prefix(AiActorController __instance, bool isPlayer, ref float detectionSpeedMultiplier)
        {
            var actor = __instance?.actor;
            if (actor == null) return true;

            // Hard range cap — cancel detection entirely beyond 600m
            Actor target = __instance.target;
            if (target != null)
            {
                float dist = Vector3.Distance(actor.Position(), target.Position());
                if (dist > MAX_SPOT_DISTANCE) return false; // suppress detection

                // Weapon class scaling: slow detectors at range for close-range weapons
                detectionSpeedMultiplier *= GetDistanceMul(actor);
            }
            return true;
        }

        private static float GetDistanceMul(Actor actor)
        {
            try
            {
                var w = actor.activeWeapon;
                if (w == null || WeaponManager.instance?.allWeapons == null) return 1f;
                foreach (var entry in WeaponManager.instance.allWeapons)
                {
                    if (entry?.prefab == null) continue;
                    var wc = entry.prefab.GetComponent<Weapon>();
                    if (wc == null) continue;
                    if (wc == w || entry.name == w.name)
                    {
                        switch ((int)entry.distance)
                        {
                            case 0: return 1.4f;  // pistol/SMG: 40% slower to detect at range
                            case 1: return 1.0f;  // rifle: unchanged
                            case 2: return 0.7f;  // sniper: 30% faster to detect
                            default: return 1.0f;
                        }
                    }
                }
            }
            catch { }
            return 1f;
        }
    }

    // suppressed bots actively seek cover — interval tightens with tier
    [HarmonyPatch(typeof(AiActorController), "Update")]
    internal static class BotSuppressedCoverPatch
    {
        // EA37: isInTemporaryCover is now IsInTemporaryCover() method
        // EA37: FindTemporaryCoverInRandomDirection removed — use FindTemporaryCoverTowardsDirection
        private static readonly MethodInfo _mIsInCover  =
            AccessTools.Method(typeof(AiActorController), "IsInTemporaryCover");
        private static readonly MethodInfo _mFindCover  =
            AccessTools.Method(typeof(AiActorController), "FindTemporaryCoverTowardsDirection",
                new System.Type[] { typeof(Vector3) });
        private static readonly FieldInfo _fTakingFireDir =
            AccessTools.Field(typeof(AiActorController), "takingFireDirection");

        private static readonly Dictionary<int, float> _nextCoverSeek = new Dictionary<int, float>();
        private static readonly float[] CoverInterval = { 999f, 3.0f, 1.5f, 0.8f };

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance)
        {
            if (__instance == null) return;
            var actor = __instance.actor;
            if (actor == null || actor.dead || actor.IsSeated()) return;

            var sup = __instance.GetComponent<SuppressionSystem>();
            if (sup == null || sup.Tier == 0) return;

            // Skip bots under active player move order
            var sq = __instance.squad;
            if (sq != null && Plugin.MoveOrderExpiries.ContainsKey(sq.number)) return;

            int   id  = __instance.GetInstanceID();
            float now = Time.time;

            bool inCover = false;
            try { inCover = _mIsInCover != null && (bool)_mIsInCover.Invoke(__instance, null); }
            catch { }

            if (inCover)
            {
                _nextCoverSeek[id] = now + CoverInterval[sup.Tier];
                return;
            }

            _nextCoverSeek.TryGetValue(id, out float nextSeek);
            if (now < nextSeek) return;
            _nextCoverSeek[id] = now + CoverInterval[sup.Tier];

            if (_mFindCover != null)
            {
                try
                {
                    // Seek cover away from the taking-fire direction
                    Vector3 dir = Vector3.zero;
                    try { dir = (Vector3)(_fTakingFireDir?.GetValue(__instance) ?? Vector3.zero); } catch { }
                    if (dir == Vector3.zero) dir = -actor.transform.forward;
                    _mFindCover.Invoke(__instance, new object[] { -dir }); // away from fire
                }
                catch { }
            }
        }

        public static void Cleanup(int id) => _nextCoverSeek.Remove(id);
    }

    // Engagement memory: bots hold a last-known position for 8 seconds after
    // losing sight of a target. Fixes "senile" bots that immediately forget you.
    [HarmonyPatch(typeof(AiActorController), "HasSpottedTarget")]
    internal static class BotEngagementMemoryPatch
    {
        private static readonly Dictionary<int, float>   _lastSpottedAt  = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> _lastKnownPos   = new Dictionary<int, Vector3>();
        private const float MEMORY_DURATION = 8f;

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance, ref bool __result)
        {
            if (__instance == null) return;
            int id = __instance.GetInstanceID();

            if (__result)
            {
                // Currently spotting — update memory
                _lastSpottedAt[id] = Time.time;
                if (__instance.target != null)
                    _lastKnownPos[id] = __instance.target.Position();
                return;
            }

            // Lost sight — check if within memory window
            _lastSpottedAt.TryGetValue(id, out float t);
            if (t > 0f && Time.time - t < MEMORY_DURATION)
            {
                __result = true; // pretend we still have a target
                // Move toward last known position while memory is active
                Vector3 lkp;
                if (_lastKnownPos.TryGetValue(id, out lkp))
                {
                    var sq = __instance.squad;
                    if (sq == null || !Plugin.MoveOrderExpiries.ContainsKey(sq.number))
                        try { __instance.GotoExactDestination(lkp, false); } catch { }
                }
            }
            else
            {
                _lastSpottedAt.Remove(id);
                _lastKnownPos.Remove(id);
            }
        }

        public static void Cleanup(int id) { _lastSpottedAt.Remove(id); _lastKnownPos.Remove(id); }
    }

    // On spawn: mobilize bots toward the nearest contested or enemy spawn point.
    // Fixes bots standing idle after reinforcement instead of moving to the front.
    [HarmonyPatch(typeof(AiActorController), "SpawnAt")]
    internal static class BotSpawnMobilizePatch
    {
        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance)
        {
            try
            {
                var actor = __instance?.actor;
                if (actor == null || actor.dead) return;
                // Skip parachuting bots — they have their own landing logic
                if (actor.parachuteDeployed) return;
                // Skip bots already under a player order
                var sq = __instance.squad;
                if (sq != null && Plugin.MoveOrderExpiries.ContainsKey(sq.number)) return;

                // Find nearest contested or enemy spawn point as the frontline target
                int team = actor.team;
                SpawnPoint best = null; float bestD = float.MaxValue;
                if (ActorManager.instance?.spawnPoints == null) return;
                foreach (var sp in ActorManager.instance.spawnPoints)
                {
                    if (sp == null) continue;
                    // Contested (neutral) or enemy = frontline
                    if (sp.owner == team) continue;
                    float d = Vector3.Distance(sp.transform.position, actor.Position());
                    if (d < bestD) { bestD = d; best = sp; }
                }
                if (best == null) return;

                // Small stagger so all spawning bots don't all path simultaneously
                float delay = UnityEngine.Random.Range(0.5f, 2.5f);
                __instance.actor.StartCoroutine(MobilizeAfterDelay(__instance, best.transform.position, delay));
            }
            catch { }
        }

        private static System.Collections.IEnumerator MobilizeAfterDelay(
            AiActorController ai, Vector3 dest, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (ai == null || ai.actor == null || ai.actor.dead) yield break;
            if (ai.squad != null && Plugin.MoveOrderExpiries.ContainsKey(ai.squad.number)) yield break;
            try { ai.GotoExactDestination(dest, false); } catch { }
        }
    }

    // Vehicle utilization: idle bots near an unoccupied vehicle will try to enter it.
    // Guards: not in combat, not suppressed, no player order, not already entering a vehicle,
    // squad doesn't already have a vehicle, bot has been idle (no path) for >8 seconds.
    [HarmonyPatch(typeof(AiActorController), "Update")]
    internal static class BotVehicleUtilizationPatch
    {
        private static readonly Dictionary<int, float> _idleSince  = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _nextCheck  = new Dictionary<int, float>();
        private const float IDLE_THRESHOLD = 8f;   // must be idle this long before seeking
        private const float CHECK_INTERVAL = 3f;
        private const float VEHICLE_RADIUS = 50f;

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance)
        {
            if (__instance == null) return;
            var actor = __instance.actor;
            if (actor == null || actor.dead || actor.IsSeated()) return;

            // Hard gates
            if (__instance.HasSpottedTarget()) { _idleSince.Remove(__instance.GetInstanceID()); return; }
            var sq = __instance.squad;
            if (sq != null && Plugin.MoveOrderExpiries.ContainsKey(sq.number)) return;
            if (sq != null && sq.HasSquadVehicle()) { _idleSince.Remove(__instance.GetInstanceID()); return; }
            if (__instance.IsEnteringVehicle()) return;
            var sup = __instance.GetComponent<SuppressionSystem>();
            if (sup != null && sup.Tier > 0) return;

            int   id  = __instance.GetInstanceID();
            float now = Time.time;

            // Track idle time — reset if bot has a path
            if (__instance.HasPath())
            {
                _idleSince.Remove(id);
                return;
            }
            if (!_idleSince.ContainsKey(id)) _idleSince[id] = now;
            if (now - _idleSince[id] < IDLE_THRESHOLD) return;

            // Rate limit the vehicle search
            _nextCheck.TryGetValue(id, out float next);
            if (now < next) return;
            _nextCheck[id] = now + CHECK_INTERVAL;

            // Find a nearby unoccupied vehicle with a free driver seat
            Collider[] hits = Physics.OverlapSphere(actor.Position(), VEHICLE_RADIUS);
            Vehicle bestVeh = null; float bestD = float.MaxValue;
            foreach (var col in hits)
            {
                if (col == null) continue;
                var veh = col.GetComponentInParent<Vehicle>();
                if (veh == null || veh.dead) continue;
                bool hasFreeSeat = false;
                foreach (var seat in veh.seats)
                    if (seat != null && seat.IsDriverSeat() && !seat.IsOccupied()) { hasFreeSeat = true; break; }
                if (!hasFreeSeat) continue;
                float d = Vector3.Distance(veh.transform.position, actor.Position());
                if (d < bestD) { bestD = d; bestVeh = veh; }
            }
            if (bestVeh == null) return;

            // Use the vanilla squad enter order if possible, else just walk there
            bool ordered = false;
            if (sq != null)
                try { sq.PlayerOrderEnterVehicle(bestVeh); ordered = true; } catch { }
            if (!ordered)
                try { __instance.GotoExactDestination(bestVeh.transform.position, false); } catch { }

            // Reset idle timer so this doesn't fire again immediately
            _idleSince.Remove(id);
            _nextCheck[id] = now + 30f; // long cooldown after triggering
        }

        public static void Cleanup(int id) { _idleSince.Remove(id); _nextCheck.Remove(id); }
    }

    [HarmonyPatch(typeof(Actor), "Kill")]
    internal static class BotEngagementDeathPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Actor __instance)
        {
            var ai = __instance.controller as AiActorController;
            if (ai == null) return;
            int id = ai.GetInstanceID();
            BotSprintStaggerPatch.Cleanup(id);
            BotStrafeStaggerPatch.Cleanup(id);
            BotSpeedVariationPatch.Cleanup(id);
            BotCoverStaggerPatch.Cleanup(id);
            BotSuppressedCoverPatch.Cleanup(id);
            BotEngagementMemoryPatch.Cleanup(id);
            BotVehicleUtilizationPatch.Cleanup(id);
        }
    }

    internal static class BotEngagementUtil
    {
        public static float Phase(int id)
        {
            uint h = (uint)id;
            h ^= h >> 16;
            h *= 0x45d9f3b;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }
    }
}
