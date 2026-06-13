using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;

namespace RavenbreachMod
{
    [HarmonyPatch(typeof(Weapon), "Shoot")]
    internal static class WeaponShootPatch
    {
        private const float NEAR_MISS_MAX_RADIUS   = 4.0f;
        private const float NEAR_MISS_INNER_RADIUS = 1.2f;
        private const float NEAR_MISS_FULL_RANGE   = 120f;
        private const float SUPPRESSION_INNER      = 8.0f;
        private const float SUPPRESSION_OUTER      = 3.0f;
        private const float HIT_COOLDOWN           = 0.15f;
        private const float WALL_HIT_RANGE         = 80f;
        private const float WALL_SPRAY_COOLDOWN    = 0.30f;
        private const float MIST_CHANCE            = 1.00f;

        private static readonly Dictionary<int, float> _lastHitTime        = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _lastWallSprayBySrc = new Dictionary<int, float>();

        private static float _nextPrune     = 0f;
        private const float  PRUNE_INTERVAL = 30f;

        private static readonly List<int>       _pruneBuffer  = new List<int>();
        private static readonly RaycastHit[]    _sphereBuffer = new RaycastHit[64];

        static void Postfix(Weapon __instance, Vector3 direction, bool useMuzzleDirection)
        {
            if (__instance == null) return;

            if (Time.time > _nextPrune)
            {
                _nextPrune = Time.time + PRUNE_INTERVAL;
                float cutoff = Time.time - 1f;
                _pruneBuffer.Clear();
                foreach (var k in _lastHitTime.Keys)        if (_lastHitTime[k]        < cutoff) _pruneBuffer.Add(k);
                foreach (var k in _pruneBuffer)             _lastHitTime.Remove(k);
                _pruneBuffer.Clear();
                foreach (var k in _lastWallSprayBySrc.Keys) if (_lastWallSprayBySrc[k] < cutoff) _pruneBuffer.Add(k);
                foreach (var k in _pruneBuffer)             _lastWallSprayBySrc.Remove(k);
            }

            Actor shooterActor    = __instance.user;
            int   shooterTeam     = shooterActor != null ? shooterActor.team : -1;
            bool  shooterIsPlayer = shooterActor != null
                                 && SuppressionTracker.PlayerController != null
                                 && shooterActor.controller == SuppressionTracker.PlayerController;

            Transform muzzle = __instance.CurrentMuzzle();
            if (muzzle == null) return;

            Vector3 origin = muzzle.position;
            Vector3 dir    = (useMuzzleDirection ? muzzle.forward : direction).normalized;

            // near-miss suppression — NonAlloc avoids per-shot array allocation
            int hitCount = Physics.SphereCastNonAlloc(origin, NEAR_MISS_MAX_RADIUS, dir, _sphereBuffer, NEAR_MISS_FULL_RANGE);
            for (int _i = 0; _i < hitCount; _i++)
            {
                var hit = _sphereBuffer[_i];
                if (hit.collider == null) continue;
                var sys = hit.collider.GetComponentInParent<SuppressionSystem>();
                if (sys == null) continue;
                if (shooterIsPlayer && sys == SuppressionTracker.PlayerSuppression) continue;
                if (sys == SuppressionTracker.PlayerSuppression)
                {
                    if (shooterTeam == SuppressionTracker.PlayerTeam) continue;
                }
                else
                {
                    var targetActor = sys.GetComponentInParent<Actor>();
                    if (targetActor != null && targetActor == shooterActor) continue;
                    if (targetActor != null && targetActor.team == shooterTeam) continue;
                }
                int id = sys.gameObject.GetInstanceID();
                if (_lastHitTime.TryGetValue(id, out float last) && Time.time - last < HIT_COOLDOWN) continue;
                _lastHitTime[id] = Time.time;
                float dist   = Vector3.Cross(dir, hit.point - origin).magnitude;
                float amount = dist <= NEAR_MISS_INNER_RADIUS
                    ? SUPPRESSION_INNER
                    : Mathf.Lerp(SUPPRESSION_INNER, SUPPRESSION_OUTER,
                        (dist - NEAR_MISS_INNER_RADIUS) / (NEAR_MISS_MAX_RADIUS - NEAR_MISS_INNER_RADIUS));
                sys.AddSuppression(amount);
                if (sys == SuppressionTracker.PlayerSuppression)
                {
                    DebugOverlay.LastNearMissTime = Time.time;
                    break;
                }
            }

            // wall spray — enemy shots only, per-shooter cooldown
            if (shooterIsPlayer) return;
            int srcId = shooterActor != null ? shooterActor.GetInstanceID() : __instance.GetInstanceID();
            _lastWallSprayBySrc.TryGetValue(srcId, out float lastSpray);
            if (Time.time - lastSpray < WALL_SPRAY_COOLDOWN) return;
            if (Random.value > MIST_CHANCE) return;
            if (Physics.Raycast(origin, dir, out RaycastHit wallHit, WALL_HIT_RANGE))
            {
                if (wallHit.collider.GetComponentInParent<Actor>()   != null) return;
                if (wallHit.collider.GetComponentInParent<Vehicle>() != null) return;
                _lastWallSprayBySrc[srcId] = Time.time;
                WallHitEffects.OnWallHit(wallHit.point, wallHit.normal);
            }
        }
    }

    [HarmonyPatch(typeof(Weapon), "GetCurrentSpreadMagnitude")]
    internal static class WeaponSpreadPatch
    {
        static void Postfix(Weapon __instance, ref float __result)
        {
            var sup = SuppressionTracker.PlayerSuppression;
            if (sup == null) return;
            var playerCtrl = SuppressionTracker.PlayerController;
            if (playerCtrl == null) return;
            Actor user = __instance.user;
            if (user == null || user.controller != playerCtrl) return;
            float multiplier;
            int   tier = sup.Tier;
            if      (tier == 1) multiplier = 1.3f;
            else if (tier == 2) multiplier = 1.8f;
            else if (tier == 3) multiplier = 2.8f;
            else return;
            __result *= multiplier;
        }
    }

    [HarmonyPatch(typeof(AiActorController), "SetupParameters")]
    internal static class AiActorControllerParametersPatch
    {
        static void Postfix()
        {
            var p = AiActorController.PARAMETERS;
            p.REACTION_TIME                   = 0.55f;
            p.ACQUIRE_TARGET_DURATION_BASE    = 1.9f;
            p.ACQUIRE_TARGET_OFFSET_PER_METER = 0.09f;
            p.AIM_BASE_SWAY                   = 0.008f;
            p.VISIBILITY_MULTIPLIER           = 1.6f;
            p.ALERT_SQUAD_TIME                = 2.5f;
            AiActorController.PARAMETERS = p;
        }
    }

    // Projectile.Hit catches vehicle guns, launchers, anything bypassing Weapon.Shoot.
    // per-shooter cooldown in WeaponShootPatch prevents double-triggering on weapons
    // that go through both paths.
    [HarmonyPatch(typeof(Projectile), "Hit")]
    internal static class ProjectileHitWallEffectsPatch
    {
        static void Postfix(Projectile __instance, RaycastHit hitInfo)
        {
            if (hitInfo.collider == null) return;
            if (hitInfo.collider.GetComponentInParent<Actor>()   != null) return;
            if (hitInfo.collider.GetComponentInParent<Vehicle>() != null) return;

            Actor source = null;
            try
            {
                source = AccessTools.Field(typeof(Projectile), "source")
                             ?.GetValue(__instance) as Actor;
                if (source == null)
                {
                    var wpn = AccessTools.Field(typeof(Projectile), "weapon")
                                  ?.GetValue(__instance) as Weapon;
                    source = wpn?.user;
                }
            }
            catch { }

            if (source != null && SuppressionTracker.PlayerController != null
                && source.controller == SuppressionTracker.PlayerController)
            {
                WallHitEffects.TryLiveCaptureAt(hitInfo.point);
                if (!WallHitEffects.HasWeaponMistAt(hitInfo.point))
                    WallHitEffects.SpawnImpactMist(hitInfo.point, hitInfo.normal, hitInfo);
                return;
            }
            if (source != null && source.team == SuppressionTracker.PlayerTeam) return;

            WallHitEffects.TryLiveCaptureAt(hitInfo.point);
            if (!WallHitEffects.HasWeaponMistAt(hitInfo.point))
                WallHitEffects.SpawnImpactMist(hitInfo.point, hitInfo.normal, hitInfo);
            WallHitEffects.OnWallHit(hitInfo.point, hitInfo.normal);
        }
    }
}
