using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RavenbreachMod
{
    public enum BodyPart
    {
        Head, Neck, Chest, Abdomen, LeftArm, RightArm, LeftLeg, RightLeg, Unknown
    }

    public static class HitLocator
    {
        private const float HEAD_THRESH    = 0.88f;
        private const float NECK_THRESH    = 0.80f;
        private const float CHEST_THRESH   = 0.55f;
        private const float ABDOMEN_THRESH = 0.28f;
        private const float ARM_LATERAL    = 0.28f;

        private static readonly float[] _multipliers =
            { 2.5f, 1.8f, 1.15f, 0.90f, 0.65f, 0.65f, 0.55f, 0.55f, 1.00f };

        public static float DamageMultiplier(BodyPart part) => _multipliers[(int)part];

        public static BodyPart Classify(Actor actor, Vector3 hitPoint)
        {
            if (actor == null) return BodyPart.Unknown;
            Vector3 feet  = actor.Position();
            float   headY = feet.y + 1.65f;
            if (actor.animatedBones != null)
                foreach (var b in actor.animatedBones)
                    if (b != null && b.position.y > headY) headY = b.position.y;
            float height = headY - feet.y;
            if (height < 0.1f) return BodyPart.Unknown;

            float relH = (hitPoint.y - feet.y) / height;
            if (relH >= HEAD_THRESH) return BodyPart.Head;
            if (relH >= NECK_THRESH) return BodyPart.Neck;

            Vector3 toHit = hitPoint - feet; toHit.y = 0f;
            float lateralDist = toHit.magnitude;
            if (lateralDist > ARM_LATERAL && relH >= ABDOMEN_THRESH)
            {
                bool isRight = Vector3.Dot(toHit.normalized, actor.transform.right) > 0f;
                return isRight ? BodyPart.RightArm : BodyPart.LeftArm;
            }
            if (relH >= CHEST_THRESH)   return BodyPart.Chest;
            if (relH >= ABDOMEN_THRESH) return BodyPart.Abdomen;

            bool legRight = Vector3.Dot((hitPoint - feet).normalized, actor.transform.right) > 0f;
            return legRight ? BodyPart.RightLeg : BodyPart.LeftLeg;
        }
    }

    public class InjuryState
    {
        public float headDamage      = 0f;
        public float neckDamage      = 0f;
        public float chestDamage     = 0f;
        public float abdomenDamage   = 0f;
        public float leftArmDamage   = 0f;
        public float rightArmDamage  = 0f;
        public float leftLegDamage   = 0f;
        public float rightLegDamage  = 0f;
        public BodyPart lastPart     = BodyPart.Unknown;
        public float    lastTime     = 0f;

        public bool  AbdomenWound        => abdomenDamage  >= 20f;
        public float ChestSuppression    => Mathf.Clamp01(chestDamage / 80f);
        public float SpeedPenalty        => Mathf.Clamp01((leftLegDamage + rightLegDamage) / 120f);
        public float SwayBonus           => Mathf.Clamp01(Mathf.Max(leftArmDamage, rightArmDamage) / 80f);
        public bool  DominantArmCrippled => rightArmDamage >= 50f;
    }

    // ── Knockdown chances per body part ──────────────────────────────────────
    // Chance that a hit to this part triggers a ragdoll knockdown.
    // Scaled by hit damage — a graze is less likely to knock than a solid hit.
    public static class KnockdownChance
    {
        // Base chance at full (60+) damage hit
        private static readonly float[] _base =
        {
            0.95f,  // Head    — almost always
            0.90f,  // Neck    — almost always
            0.55f,  // Chest   — moderate
            0.40f,  // Abdomen — moderate
            0.35f,  // LeftArm
            0.35f,  // RightArm
            0.60f,  // LeftLeg — legs buckle
            0.60f,  // RightLeg
            0.00f,  // Unknown
        };

        public static float Get(BodyPart part, float damage)
        {
            float base_ = _base[(int)part];
            // Scale down for grazes — full chance at 60+ damage, near-zero at 5
            float scale = Mathf.Clamp01(damage / 60f);
            // Non-linear: even a light hit to head has real chance
            scale = Mathf.Pow(scale, part <= BodyPart.Neck ? 0.4f : 0.7f);
            return base_ * scale;
        }
    }

    // ── Ragdoll durations per body part ──────────────────────────────────────
    public static class KnockdownDuration
    {
        private static readonly float[] _min = { 1.2f, 1.1f, 0.7f, 0.6f, 0.4f, 0.4f, 0.8f, 0.8f, 0.5f };
        private static readonly float[] _max = { 2.2f, 2.0f, 1.4f, 1.2f, 0.9f, 0.9f, 1.6f, 1.6f, 1.0f };

        public static float Get(BodyPart part)
            => Random.Range(_min[(int)part], _max[(int)part]);
    }

    [HarmonyPatch(typeof(Actor), "Damage")]
    public static class ActorDamageInjuryPatch
    {
        private const float KNOCKDOWN_COOLDOWN = 2.5f;
        private static float _lastKnockdownTime = -99f;

        [HarmonyPrefix]
        public static void Prefix(Actor __instance, ref DamageInfo info)
        {
            if (__instance == null || __instance.aiControlled) return;
            if (info.isSplashDamage) return;
            if (info.type == DamageInfo.DamageSourceType.FallDamage)  return;
            if (info.type == DamageInfo.DamageSourceType.DamageZone)  return;
            if (info.type == DamageInfo.DamageSourceType.Scripted)    return;
            if (info.healthDamage <= 0f) return;
            if (info.point == Vector3.zero) return;

            BodyPart part = HitLocator.Classify(__instance, info.point);
            if (part == BodyPart.Unknown) return;

            // Apply damage multiplier
            info.healthDamage *= HitLocator.DamageMultiplier(part);
            if (part == BodyPart.Head || part == BodyPart.Neck)
                info.isCriticalHit = true;

            InjurySystem.RecordHit(__instance, part, info.healthDamage);

            // ── Hit knockdown ─────────────────────────────────────────────────
            // Don't knock if already ragdolling or on cooldown
            var fps = FpsActorController.instance;
            if (fps == null || fps.actor != __instance) return;
            if (__instance.fallenOver || __instance.dead) return;
            // Never knock the player out of a vehicle — IsSeated covers all mounted seats
            if (__instance.IsSeated()) return;
            if (Time.time - _lastKnockdownTime < KNOCKDOWN_COOLDOWN) return;

            float chance = KnockdownChance.Get(part, info.healthDamage);
            if (Random.value < chance)
            {
                _lastKnockdownTime = Time.time;
                float duration     = KnockdownDuration.Get(part);
                SprintStumbleSystem.TriggerPlayerRagdoll(fps, duration);
            }
        }
    }

    [HarmonyPatch(typeof(Actor), "Kill")]
    public static class ActorKillInjuryPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Actor __instance)
        {
            InjurySystem.ClearActor(__instance);
            BleedingSystem.RemoveActor(__instance);
        }
    }

    [HarmonyPatch(typeof(ActorManager), "StartGame")]
    public static class AnimatorScanPatch
    {
        private static bool _done = false;
        [HarmonyPostfix]
        public static void Postfix()
        {
            WallHitEffects.ResetMistCapture();
            WallHitEffects.TryCaptureMistPrefab();

            if (_done) return; _done = true;
            try
            {
                foreach (var actor in ActorManager.instance.actors)
                {
                    if (actor == null || !actor.aiControlled) continue;
                    var anim = actor.GetComponentInChildren<Animator>();
                    if (anim == null) continue;
                    Plugin.Log?.LogInfo("[AnimScan] Controller: " +
                        (anim.runtimeAnimatorController?.name ?? "null"));
                    foreach (var p in anim.parameters)
                        Plugin.Log?.LogInfo("[AnimScan] " + p.name + " (" + p.type + ")");
                    break;
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning("[AnimScan] " + ex.Message);
            }
        }
    }

    public class InjurySystem : MonoBehaviour
    {
        private static readonly Dictionary<int, InjuryState> _injuries
            = new Dictionary<int, InjuryState>();

        private static InjurySystem _inst;

        public static void RecordHit(Actor actor, BodyPart part, float damage)
        {
            int id = actor.GetInstanceID();
            if (!_injuries.TryGetValue(id, out var state))
            {
                state = new InjuryState();
                _injuries[id] = state;
            }

            state.lastPart = part;
            state.lastTime = Time.time;

            switch (part)
            {
                case BodyPart.Head:     state.headDamage     = Mathf.Min(state.headDamage     + damage, 200f); break;
                case BodyPart.Neck:     state.neckDamage     = Mathf.Min(state.neckDamage     + damage, 200f); break;
                case BodyPart.Chest:    state.chestDamage    = Mathf.Min(state.chestDamage    + damage, 200f); break;
                case BodyPart.Abdomen:  state.abdomenDamage  = Mathf.Min(state.abdomenDamage  + damage, 200f); break;
                case BodyPart.LeftArm:  state.leftArmDamage  = Mathf.Min(state.leftArmDamage  + damage, 200f); break;
                case BodyPart.RightArm: state.rightArmDamage = Mathf.Min(state.rightArmDamage + damage, 200f); break;
                case BodyPart.LeftLeg:  state.leftLegDamage  = Mathf.Min(state.leftLegDamage  + damage, 200f); break;
                case BodyPart.RightLeg: state.rightLegDamage = Mathf.Min(state.rightLegDamage + damage, 200f); break;
            }

            BleedingSystem.RegisterWound(actor, Mathf.Clamp01(damage / 60f), part);
        }

        public static void ClearActor(Actor actor)
        {
            if (actor == null) return;
            _injuries.Remove(actor.GetInstanceID());
        }

        public static InjuryState GetState(Actor actor)
        {
            if (actor == null) return null;
            _injuries.TryGetValue(actor.GetInstanceID(), out var s);
            return s;
        }

        public static float PlayerArmSwayBonus
        {
            get
            {
                var fps = FpsActorController.instance;
                if (fps?.actor == null) return 0f;
                return GetState(fps.actor)?.SwayBonus ?? 0f;
            }
        }

        private void Awake()     { _inst = this; }
        private void OnDestroy() { if (_inst == this) _inst = null; }

        private void Update()
        {
            var fps = FpsActorController.instance;
            if (fps?.actor == null || fps.actor.dead) return;
            var state = GetState(fps.actor);
            if (state == null) return;
            float dt = Time.deltaTime;

            // Leg damage slows movement
            float legPenalty = state.SpeedPenalty;
            if (legPenalty > 0.01f)
                fps.actor.speedMultiplier = Mathf.Lerp(fps.actor.speedMultiplier,
                    Mathf.Lerp(1.0f, 0.38f, legPenalty), dt * 3f);

            // Dominant arm crippled — can't fine aim
            if (state.DominantArmCrippled && fps.Aiming())
            {
                try
                {
                    typeof(FpsActorController)
                        .GetField("fineAim",
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance)
                        ?.SetValue(fps, false);
                }
                catch { }
            }

            // Abdomen wound — can't go prone
            if (state.AbdomenWound && fps.actor.stance == Actor.Stance.Prone && !fps.actor.fallenOver)
                try { fps.ForceChangeStance(Actor.Stance.Stand); } catch { }

            // Chest wound — floors suppression
            float chestSup = state.ChestSuppression;
            if (chestSup > 0.02f)
            {
                var sup = SuppressionTracker.PlayerSuppression;
                if (sup != null)
                {
                    float floor = chestSup * 0.45f;
                    if (sup.SuppressionLevel < floor)
                        sup.AddSuppression((floor - sup.SuppressionLevel) * dt * 0.5f);
                }
            }
        }
    }
}
