using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RavenbreachMod
{
    // acquisition lag + target spreading + engagement roles
    [HarmonyPatch(typeof(AiActorController), "Update")]
    internal static class BotDesyncPatch
    {
        private static readonly FieldInfo _fAcqOffset =
            AccessTools.Field(typeof(AiActorController), "acquireTargetOffset");
        private static readonly FieldInfo _fAcqAction =
            AccessTools.Field(typeof(AiActorController), "acquireTargetAction");
        private static readonly FieldInfo _fTargetDist =
            AccessTools.Field(typeof(AiActorController), "targetDistance");

        private static MethodInfo _mTrueDone;
        private static readonly MethodInfo _mSquadTarget = null; // removed in EA37 — target spreading falls back to offset-only

        private static readonly Dictionary<int, float> _phase      = new Dictionary<int, float>();
        private static readonly Dictionary<int, int>   _role       = new Dictionary<int, int>();
        private static readonly Dictionary<int, float> _nextTick   = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _spreadNext = new Dictionary<int, float>();

        private static readonly Dictionary<int, (int count, float expiry)> _squadEnemyCache
            = new Dictionary<int, (int, float)>();

        private const float TICK_INTERVAL   = 0.067f;
        private const float TICK_JITTER     = 0.015f;
        private const float SPREAD_INTERVAL = 0.9f;
        private const float SPREAD_PENALTY  = 1.8f;
        private const float SQUAD_TTL       = 2.0f;

        // 1.0 = no lag, 2.5 = slow snap — prevents sky-look via ACQ_MAX_MAG clamp
        private const float ACQ_LAG_MIN = 1.0f;
        private const float ACQ_LAG_MAX = 2.5f;
        private const float ACQ_MAX_MAG = 3.5f;

        private const float ROLE_PENALTY = 1.2f;

        [HarmonyPostfix]
        public static void Postfix(AiActorController __instance)
        {
            if (__instance == null) return;
            var actor = __instance.actor;
            if (actor == null || actor.dead || !actor.aiControlled) return;
            if (actor.IsSeated()) return;
            if (!__instance.HasSpottedTarget()) return;
            if (_fAcqOffset == null) return;

            int   id  = __instance.GetInstanceID();
            float now = Time.time;

            _nextTick.TryGetValue(id, out float next);
            if (now < next) return;
            _nextTick[id] = now + TICK_INTERVAL + Random.value * TICK_JITTER;

            if (!_phase.ContainsKey(id))
            {
                _phase[id]      = BotEngagementUtil.Phase(id);
                _role[id]       = Mathf.Abs(id) % 3;
                _spreadNext[id] = now + Random.value * SPREAD_INTERVAL;
            }

            float phase = _phase[id];
            int   role  = _role[id];

            Vector3 offset = (Vector3)_fAcqOffset.GetValue(__instance);
            bool    dirty  = false;

            bool isAcquiring = offset.sqrMagnitude > 0.001f && !IsAcqDone(__instance);

            // acquisition lag — slows snap speed per bot, never sky-looks
            if (isAcquiring)
            {
                float lag = Mathf.Lerp(ACQ_LAG_MIN, ACQ_LAG_MAX, phase);
                offset = offset * lag;
                dirty  = true;
            }

            // target spreading — bots redistribute when overloaded
            _spreadNext.TryGetValue(id, out float spreadAt);
            if (now >= spreadAt && __instance.squad?.aiMembers != null)
            {
                _spreadNext[id] = now + SPREAD_INTERVAL + Random.Range(-0.15f, 0.15f);

                Actor myTarget = __instance.target;
                if (myTarget != null && !myTarget.dead)
                {
                    int coverCount = 0, squadSize = 0;
                    foreach (var m in __instance.squad.aiMembers)
                    {
                        if (m == null || m.actor == null || m.actor.dead) continue;
                        squadSize++;
                        if (m.target == myTarget) coverCount++;
                    }
                    int enemyCount = GetCachedEnemyCount(__instance, now);
                    int idealMax   = Mathf.Max(1, squadSize / Mathf.Max(1, enemyCount));

                    if (coverCount > idealMax + 1)
                    {
                        Actor better = null;
                        try { better = _mSquadTarget?.Invoke(__instance, null) as Actor; }
                        catch { }
                        if (better != null && better != myTarget)
                        {
                            offset += Random.insideUnitSphere.normalized * SPREAD_PENALTY;
                            dirty   = true;
                        }
                    }
                }
            }

            // role offset — mid/long bots get extra penalty inside their preferred range minimum
            if (_fTargetDist != null && isAcquiring)
            {
                float dist = (float)_fTargetDist.GetValue(__instance);
                if (role == 1 && dist < 35f)
                {
                    offset += Random.insideUnitSphere.normalized * (ROLE_PENALTY * (1f - dist / 35f));
                    dirty   = true;
                }
                else if (role == 2 && dist < 50f)
                {
                    offset += Random.insideUnitSphere.normalized * (ROLE_PENALTY * 1.5f * (1f - dist / 50f));
                    dirty   = true;
                }
            }

            // clamp — all systems combined can't produce sky-look
            if (dirty)
            {
                if (offset.magnitude > ACQ_MAX_MAG)
                    offset = offset.normalized * ACQ_MAX_MAG;
                _fAcqOffset.SetValue(__instance, offset);
            }
        }

        private static bool IsAcqDone(AiActorController bot)
        {
            if (_fAcqAction == null) return false;
            object action = _fAcqAction.GetValue(bot);
            if (action == null) return true;
            if (_mTrueDone == null)
                _mTrueDone = action.GetType().GetMethod("TrueDone",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_mTrueDone == null) return false;
            return (bool)_mTrueDone.Invoke(action, null);
        }

        private static int GetCachedEnemyCount(AiActorController bot, float now)
        {
            if (bot.squad == null) return 1;
            int sqId = RuntimeHelpers.GetHashCode(bot.squad);
            if (_squadEnemyCache.TryGetValue(sqId, out var cached) && now < cached.expiry)
                return cached.count;
            var actors = ActorManager.instance?.actors;
            if (actors == null) { _squadEnemyCache[sqId] = (1, now + SQUAD_TTL); return 1; }
            int count  = 0;
            int myTeam = bot.actor.team;
            Vector3 pos = bot.actor.Position();
            foreach (var a in actors)
            {
                if (a == null || a.dead || a.team == myTeam) continue;
                if (Vector3.Distance(a.Position(), pos) < 150f) count++;
            }
            count = Mathf.Max(1, count);
            _squadEnemyCache[sqId] = (count, now + SQUAD_TTL);
            return count;
        }

        public static void Cleanup(int id)
        {
            _phase.Remove(id);
            _role.Remove(id);
            _nextTick.Remove(id);
            _spreadNext.Remove(id);
        }
    }

    [HarmonyPatch(typeof(Actor), "Kill")]
    internal static class BotDesyncDeathPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Actor __instance)
        {
            var ai = __instance.controller as AiActorController;
            if (ai != null) BotDesyncPatch.Cleanup(ai.GetInstanceID());
        }
    }
}
