using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// ──────────────────────────────────────────────────────────────────────────────
// DownedBotSystem
//
// When a bot takes lethal damage below DOWN_HP_THRESHOLD, instead of dying
// outright it enters a downed state: crawls, fires degraded, bleeds out.
//
// Troop counting fix: we call ActorManager.SetDead() immediately so Ravenfield's
// aliveActors dict registers the loss and reinforcement waves trigger correctly.
// We do NOT set actor.dead = true — that flag is used internally by crawl/fire
// guards and would break the downed behavior.
// Instead we track counted-as-dead separately with _countedAsDead.
// ──────────────────────────────────────────────────────────────────────────────

namespace RavenbreachMod
{
    public static class DownedConfig
    {
        public const float DOWN_HP_THRESHOLD = 35f;
        public const float DOWN_CHANCE       = 0.75f;
        public const float BLEEDOUT_TIME     = 25f;
        public const float FINISH_RANGE      = 6f;
        public const float CRAWL_SPEED       = 0.12f;
        public const float FIRE_INTERVAL_MIN = 1.8f;
        public const float FIRE_INTERVAL_MAX = 4.0f;
        public const float DOWNED_FIRE_RANGE = 18f;
        public const float DOWNED_AIM_NOISE  = 3.5f;
        public const bool  REVIVE_ENABLED    = true;
        public const float REVIVE_RANGE      = 3.0f;
        public const float REVIVE_TIME       = 4.0f;
    }

    public static class DownedBotRegistry
    {
        private static readonly Dictionary<int, DownedBotBehavior> _downed
            = new Dictionary<int, DownedBotBehavior>();

        public static void Register(Actor actor, DownedBotBehavior b)
        { if (actor != null) _downed[actor.GetInstanceID()] = b; }

        public static void Unregister(Actor actor)
        { if (actor != null) _downed.Remove(actor.GetInstanceID()); }

        public static bool IsDowned(Actor actor) =>
            actor != null && _downed.ContainsKey(actor.GetInstanceID());

        public static DownedBotBehavior Get(Actor actor)
        {
            if (actor == null) return null;
            _downed.TryGetValue(actor.GetInstanceID(), out var b);
            return b;
        }
    }

    [HarmonyPatch(typeof(Actor), "Kill")]
    public static class DownedBotKillPatch
    {
        private static readonly HashSet<int> _inBleedoutKill = new HashSet<int>();

        [HarmonyPrefix]
        public static bool Prefix(Actor __instance, DamageInfo info)
        {
            if (__instance == null)                          return true;
            if (!__instance.aiControlled)                   return true;
            if (__instance.dead)                            return true;
            if (DownedBotRegistry.IsDowned(__instance))    return true;

            if (info.type == DamageInfo.DamageSourceType.Scripted)   return true;
            if (info.type == DamageInfo.DamageSourceType.DamageZone) return true;
            if (info.type == DamageInfo.DamageSourceType.Exception)  return true;

            if (_inBleedoutKill.Contains(__instance.GetInstanceID())) return true;

            if (__instance.health > DownedConfig.DOWN_HP_THRESHOLD) return true;
            if (Random.value > DownedConfig.DOWN_CHANCE)            return true;

            TryDownBot(__instance);
            return false;
        }

        private static void TryDownBot(Actor actor)
        {
            try
            {
                var host     = new GameObject("DownedBotHost");
                host.transform.SetParent(actor.transform, false);
                var behavior = host.AddComponent<DownedBotBehavior>();
                behavior.Initialize(actor);
                DownedBotRegistry.Register(actor, behavior);
                Plugin.Log?.LogInfo($"[DOWNED] {actor.name} team={actor.team}");
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogError($"[DOWNED] TryDownBot failed: {e.Message}");
            }
        }

        public static void MarkBleedoutKill(int id)  => _inBleedoutKill.Add(id);
        public static void ClearBleedoutKill(int id) => _inBleedoutKill.Remove(id);
    }

    [HarmonyPatch(typeof(Actor), "Damage")]
    public static class DownedBotFinishPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Actor __instance, ref DamageInfo info)
        {
            if (__instance == null) return;
            if (!DownedBotRegistry.IsDowned(__instance)) return;

            info.healthDamage = __instance.health + 100f;
            info.type         = DamageInfo.DamageSourceType.Scripted;

            try
            {
                var playerCtrl = SuppressionTracker.PlayerController;
                if (playerCtrl != null)
                {
                    float dist = Vector3.Distance(__instance.Position(),
                        playerCtrl.transform.position);
                    if (dist <= DownedConfig.FINISH_RANGE)
                        SuppressionEffects.TriggerBlast(0.12f, 3.0f, 0f, 0f);
                }
            }
            catch { }
        }
    }

    public class DownedBotBehavior : MonoBehaviour
    {
        private Actor  _actor;
        private float  _bleedTimer;
        private float  _nextFireTime;
        private float  _reviveProgress;
        private bool   _finished;

        // Separate flag: tells Ravenfield this bot is out of the fight,
        // without touching actor.dead (which would break crawl/fire guards).
        private bool   _countedAsDead;

        private static readonly MethodInfo _setDeadMethod  =
            AccessTools.Method(typeof(ActorManager), "SetDead");
        private static readonly MethodInfo _setAliveMethod =
            AccessTools.Method(typeof(ActorManager), "SetAlive");

        private static MethodInfo _shootMethod;
        private static readonly FieldInfo _offsetField =
            AccessTools.Field(typeof(AiActorController), "acquireTargetOffset");

        static DownedBotBehavior()
        {
            _shootMethod = typeof(Weapon).GetMethod("Shoot",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        public void Initialize(Actor actor)
        {
            _actor        = actor;
            _bleedTimer   = DownedConfig.BLEEDOUT_TIME;
            _nextFireTime = Time.time + Random.Range(
                DownedConfig.FIRE_INTERVAL_MIN, DownedConfig.FIRE_INTERVAL_MAX);

            _actor.health = Random.Range(3f, 8f);

            // Register as dead with Ravenfield's troop counter so reinforcement
            // waves trigger at the correct depletion threshold.
            // We do NOT set actor.dead — that would break crawl/fire behavior.
            try
            {
                _setDeadMethod?.Invoke(ActorManager.instance, new object[] { _actor });
                _countedAsDead = true;
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning($"[DOWNED] SetDead failed: {e.Message}");
            }

            var ai = _actor.controller as AiActorController;
            if (ai != null) try { ai.enabled = false; } catch { }

            try { _actor.ForceStance(Actor.Stance.Prone); } catch { }

            StartCoroutine(DownedRoutine());
        }

        private IEnumerator DownedRoutine()
        {
            yield return new WaitForSeconds(Random.Range(0.8f, 2.0f));

            while (!_finished && _bleedTimer > 0f && _actor != null && !_actor.dead)
            {
                float dt = Time.deltaTime;
                _bleedTimer -= dt;

                if (Time.time >= _nextFireTime)
                {
                    TryFireAtEnemy();
                    _nextFireTime = Time.time + Random.Range(
                        DownedConfig.FIRE_INTERVAL_MIN, DownedConfig.FIRE_INTERVAL_MAX);
                }

                TryCrawl(dt);

                if (DownedConfig.REVIVE_ENABLED) CheckRevive(dt);

                if (_actor != null)
                    _actor.health = Mathf.Max(1f, _actor.health - 0.04f * dt);

                yield return null;
            }

            if (!_finished && _actor != null && !_actor.dead)
                Finish();
        }

        private void TryFireAtEnemy()
        {
            if (_actor == null || _actor.dead) return;

            var actors = ActorManager.instance?.actors;
            if (actors == null) return;

            Actor   nearest     = null;
            float   nearestDist = DownedConfig.DOWNED_FIRE_RANGE;

            foreach (var a in actors)
            {
                if (a == null || a.dead || a.team == _actor.team) continue;
                float d = Vector3.Distance(_actor.Position(), a.Position());
                if (d < nearestDist) { nearestDist = d; nearest = a; }
            }

            if (nearest == null) return;

            var weapon = _actor.activeWeapon;
            if (weapon == null || _shootMethod == null) return;

            Vector3 toTarget = nearest.Position() + Vector3.up - _actor.Position();
            try
            {
                _actor.transform.rotation = Quaternion.LookRotation(
                    new Vector3(toTarget.x, 0f, toTarget.z));
            }
            catch { }

            var ai = _actor.controller as AiActorController;
            if (ai != null && _offsetField != null)
            {
                Vector3 noise = Random.insideUnitSphere * DownedConfig.DOWNED_AIM_NOISE;
                noise.y *= 0.3f;
                _offsetField.SetValue(ai, noise);
            }

            try
            {
                var parameters = _shootMethod.GetParameters();
                if (parameters.Length == 0)
                    _shootMethod.Invoke(weapon, null);
                else if (parameters.Length == 2)
                    _shootMethod.Invoke(weapon, new object[] { weapon.transform.forward, false });
                else
                    _shootMethod.Invoke(weapon, new object[parameters.Length]);
            }
            catch { }
        }

        private void TryCrawl(float dt)
        {
            if (_actor == null || _actor.dead) return;

            var actors = ActorManager.instance?.actors;
            if (actors == null) return;

            Vector3 fleeDir = Vector3.zero;
            float   closest = 20f;

            foreach (var a in actors)
            {
                if (a == null || a.dead || a.team == _actor.team) continue;
                float d = Vector3.Distance(_actor.Position(), a.Position());
                if (d < closest) { closest = d; fleeDir = (_actor.Position() - a.Position()).normalized; fleeDir.y = 0f; }
            }

            if (fleeDir.magnitude < 0.01f) return;

            try { _actor.transform.position += fleeDir * DownedConfig.CRAWL_SPEED * dt; }
            catch { }
        }

        private void CheckRevive(float dt)
        {
            if (_actor == null) return;
            var actors = ActorManager.instance?.actors;
            if (actors == null) return;

            foreach (var a in actors)
            {
                if (a == null || a.dead || a.team != _actor.team || a == _actor) continue;
                if (a.aiControlled) continue;
                float d = Vector3.Distance(_actor.Position(), a.Position());
                if (d > DownedConfig.REVIVE_RANGE) continue;

                _reviveProgress += dt;
                if (_reviveProgress >= DownedConfig.REVIVE_TIME) { Revive(); return; }
                return;
            }

            _reviveProgress = Mathf.Max(0f, _reviveProgress - dt * 0.5f);
        }

        private void Revive()
        {
            if (_actor == null) return;
            _finished     = true;
            _actor.health = 25f;

            // Re-register with Ravenfield's troop counter
            if (_countedAsDead)
            {
                try { _setAliveMethod?.Invoke(ActorManager.instance, new object[] { _actor }); }
                catch { }
                _countedAsDead = false;
            }

            var ai = _actor.controller as AiActorController;
            if (ai != null) try { ai.enabled = true; } catch { }

            try { _actor.ForceStance(Actor.Stance.Stand); } catch { }

            Plugin.Log?.LogInfo($"[DOWNED] {_actor.name} REVIVED");
            DownedBotRegistry.Unregister(_actor);
            Destroy(gameObject);
        }

        public void Finish()
        {
            if (_finished) return;
            _finished = true;

            if (_actor == null || _actor.dead)
            {
                DownedBotRegistry.Unregister(_actor);
                Destroy(gameObject);
                return;
            }

            var ai = _actor.controller as AiActorController;
            if (ai != null) try { ai.enabled = true; } catch { }

            // _countedAsDead is already true from Initialize — SetDead already called.
            // Just trigger the actual kill so gore/ragdoll fires normally.
            int id = _actor.GetInstanceID();
            DownedBotKillPatch.MarkBleedoutKill(id);

            var killInfo = default(DamageInfo);
            killInfo.type         = DamageInfo.DamageSourceType.Scripted;
            killInfo.healthDamage = 999f;
            try { _actor.Kill(killInfo); } catch { }

            DownedBotKillPatch.ClearBleedoutKill(id);
            DownedBotRegistry.Unregister(_actor);
            Plugin.Log?.LogInfo($"[DOWNED] {_actor.name} BLEEDOUT");
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (_actor != null) DownedBotRegistry.Unregister(_actor);
        }
    }
}
