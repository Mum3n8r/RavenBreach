using UnityEngine;

namespace RavenbreachMod
{
    public class SprintStumbleSystem : MonoBehaviour
    {
        private const float STRESS_RATE_T2    = 8f;
        private const float STRESS_RATE_T3    = 20f;
        private const float STRESS_DECAY      = 35f;
        private const float STUMBLE_A_T2      = 0.002f;
        private const float STUMBLE_B_T2      = 5.0f;
        private const float STUMBLE_A_T3      = 0.005f;
        private const float STUMBLE_B_T3      = 5.5f;
        private const float STUMBLE_COOLDOWN  = 1.8f;
        private const float SPRINT_VEL_THRESH = 4.5f;

        private float _sprintStress  = 0f;
        private float _cooldownLeft  = 0f;
        private bool  _stumbleActive = false;

        private SuppressionSystem _cachedSup;
        private float _cacheRefresh = 0f;
        private const float CACHE_INTERVAL = 0.25f;

        private static SprintStumbleSystem _inst;
        public static float PlayerSprintStress => _inst?._sprintStress ?? 0f;

        private void Awake()     { _inst = this; }
        private void OnDestroy() { if (_inst == this) _inst = null; }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (_cooldownLeft > 0f) _cooldownLeft -= dt;

            // Cache suppression lookup to avoid per-frame GetComponent
            if (Time.time > _cacheRefresh)
            {
                _cachedSup = SuppressionTracker.PlayerSuppression;
                _cacheRefresh = Time.time + CACHE_INTERVAL;
            }
            var fps = FpsActorController.instance;
            var sup = _cachedSup;

            if (fps == null || fps.actor == null || sup == null || sup.Tier < 2)
            {
                _sprintStress = Mathf.Max(0f, _sprintStress - STRESS_DECAY * dt);
                return;
            }

            if (fps.actor.IsSeated() || fps.actor.dead || fps.actor.fallenOver)
            {
                _sprintStress = Mathf.Max(0f, _sprintStress - STRESS_DECAY * dt);
                return;
            }

            bool sprinting = fps.actor.Velocity().magnitude > SPRINT_VEL_THRESH;

            if (sprinting && !_stumbleActive)
            {
                float rate = sup.Tier == 3 ? STRESS_RATE_T3 : STRESS_RATE_T2;
                _sprintStress = Mathf.Min(100f, _sprintStress + rate * dt);

                float A = sup.Tier == 3 ? STUMBLE_A_T3 : STUMBLE_A_T2;
                float B = sup.Tier == 3 ? STUMBLE_B_T3 : STUMBLE_B_T2;

                if (Random.value < A * Mathf.Exp(B * _sprintStress / 100f) * dt && _cooldownLeft <= 0f)
                {
                    _stumbleActive = true;
                    _cooldownLeft  = STUMBLE_COOLDOWN;
                    _sprintStress  = Mathf.Max(0f, _sprintStress - 38f);
                    TriggerPlayerFall(fps);
                }
            }
            else
            {
                _sprintStress = Mathf.Max(0f, _sprintStress - STRESS_DECAY * dt);
            }

            if (_stumbleActive && !fps.actor.fallenOver)
                _stumbleActive = false;
        }

        // Call Actor.FallOver directly — it fires proper ragdoll physics, animator
        // transitions, and the built-in get-up timer. No camera switch, no third
        // person view, no coroutine needed.
        public static void TriggerPlayerFall(FpsActorController fps)
        {
            if (fps?.actor == null) return;
            if (fps.actor.fallenOver || fps.actor.dead) return;
            try { fps.actor.FallOver(); }
            catch (System.Exception e)
            { Plugin.Log?.LogWarning("[Stumble] FallOver failed: " + e.Message); }
        }

        // Legacy entry point kept so other callers don't break
        public static void TriggerPlayerRagdoll(FpsActorController fps, float duration)
            => TriggerPlayerFall(fps);
    }
}
