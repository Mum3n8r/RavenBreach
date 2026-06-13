using HarmonyLib;
using UnityEngine;

// Simple ballistics system — adds velocity decay (drag) and wind drift to
// all projectiles. Gravity is already handled by Projectile.Configuration.gravityMultiplier.
// We patch UpdatePosition to modify velocity each frame — cheap, no fighting vanilla.

namespace RavenbreachMod
{
    public static class BallisticsSettings
    {
        // Global wind vector — magnitude and direction.
        // XZ plane only. Set by map or left as default open-field wind.
        public static Vector3 WindVector = new Vector3(2.5f, 0f, 1.2f);

        // Drag profiles by muzzle velocity (m/s):
        //   pistol  < 200    high drag
        //   SMG     200-350  medium-high drag
        //   rifle   350-550  medium drag
        //   HV      550-750  low drag
        //   sniper  > 750    very low drag
        public static float GetDragCoefficient(float speed)
        {
            if (speed < 200f) return 0.045f;
            if (speed < 350f) return 0.028f;
            if (speed < 550f) return 0.016f;
            if (speed < 750f) return 0.010f;
            return 0.006f;
        }

        // Wind influence scale — snipers get full wind, pistols barely any
        public static float GetWindInfluence(float speed)
        {
            if (speed < 200f) return 0.05f;
            if (speed < 350f) return 0.08f;
            if (speed < 550f) return 0.12f;
            if (speed < 750f) return 0.18f;
            return 0.25f;
        }
    }

    [HarmonyPatch(typeof(Projectile), "UpdatePosition")]
    public static class BallisticsPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Projectile __instance)
        {
            if (__instance == null) return;
            if (__instance.travelDistance < 2f) return;

            var cfg = __instance.configuration;
            if (cfg == null) return;

            // Skip explosives — rockets and grenades have high damage and low speed
            // Drag on them makes launchers unusable
            if (cfg.damage > 60f) return;

            float speed = cfg.speed;
            if (speed <= 0f) speed = 300f;

            float dt   = Time.deltaTime;
            float drag = BallisticsSettings.GetDragCoefficient(speed);
            float wind = BallisticsSettings.GetWindInfluence(speed);

            float currentSpeed = __instance.velocity.magnitude;
            if (currentSpeed > 0.1f)
            {
                float decayFactor = 1f - drag * currentSpeed * dt;
                decayFactor = Mathf.Clamp(decayFactor, 0.90f, 1f); // loosened cap
                __instance.velocity *= decayFactor;
            }

            float rangeFactor = Mathf.Clamp01(__instance.travelDistance / 300f);
            __instance.velocity += BallisticsSettings.WindVector * wind * rangeFactor * dt;
        }
    }

    // Scale damage by current projectile velocity vs initial muzzle velocity.
    // A slowed round at range does less damage — rewards close range, punishes spray at distance.
    [HarmonyPatch(typeof(Projectile), "Damage")]
    public static class BallisticsDamagePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Projectile __instance, ref float __result)
        {
            if (__instance?.configuration == null) return;
            float initialSpeed = __instance.configuration.speed;
            if (initialSpeed <= 0f) return;
            float currentSpeed = __instance.velocity.magnitude;
            // Ratio of current to initial speed — clamped so max damage is at muzzle
            float ratio = Mathf.Clamp(currentSpeed / initialSpeed, 0.25f, 1f);
            // Non-linear — damage doesn't fall off as fast as velocity
            // A round at 70% speed still does ~85% damage
            float scaledRatio = Mathf.Pow(ratio, 0.5f);
            __result *= scaledRatio;
        }
    }

    // Headshot = instant kill, always. No exceptions.
    // Patches Actor.Damage before health is subtracted.
    // Applies to both player and bots as targets.
    [HarmonyPatch(typeof(Actor), "Damage")]
    public static class HeadshotInstantKillPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Actor __instance, ref DamageInfo info)
        {
            if (__instance == null || __instance.dead) return;
            if (info.isSplashDamage) return;
            if (info.healthDamage <= 0f) return;
            if (info.point == Vector3.zero) return;
            if (info.type == DamageInfo.DamageSourceType.FallDamage) return;
            if (info.type == DamageInfo.DamageSourceType.DamageZone)  return;
            if (info.type == DamageInfo.DamageSourceType.Scripted)    return;

            BodyPart part = HitLocator.Classify(__instance, info.point);
            if (part == BodyPart.Head || part == BodyPart.Neck)
            {
                // Set damage to 10000 — guaranteed kill through any armor or health pool.
                // isCriticalHit suppresses any potential damage cap logic.
                info.healthDamage = 10000f;
                info.isCriticalHit = true;
            }
        }
    }

    // AI lethality — bots should die faster to realistic hits.
    // ActorDamageInjuryPatch already handles this for the player.
    // This covers AI actors — headshots and chest hits are significantly boosted.
    [HarmonyPatch(typeof(Actor), "Damage")]
    public static class AiActorLethalityPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Actor __instance, ref DamageInfo info)
        {
            if (__instance == null || !__instance.aiControlled) return;
            if (info.isSplashDamage) return;
            if (info.healthDamage <= 0f) return;
            if (info.point == Vector3.zero) return;
            if (info.type == DamageInfo.DamageSourceType.FallDamage) return;
            if (info.type == DamageInfo.DamageSourceType.DamageZone)  return;
            if (info.type == DamageInfo.DamageSourceType.Scripted)    return;

            BodyPart part = HitLocator.Classify(__instance, info.point);
            if (part == BodyPart.Unknown) return;

            // Apply hit location multiplier — same table as player but slightly
            // more generous to make bots feel appropriately fragile
            float mul = HitLocator.DamageMultiplier(part);
            // Small extra boost so firefights resolve faster
            mul *= 1.15f;
            info.healthDamage *= mul;

            if (part == BodyPart.Head || part == BodyPart.Neck)
                info.isCriticalHit = true;
        }
    }
    // Vanilla default is usually 1.0 — we push snipers higher for visible drop,
    // pull pistols down more, leave rifles roughly vanilla.
    [HarmonyPatch(typeof(Projectile), "StartTravelling")]
    public static class BallisticsGravityTweak
    {
        [HarmonyPostfix]
        public static void Postfix(Projectile __instance)
        {
            if (__instance?.configuration == null) return;
            float speed = __instance.configuration.speed;

            // Only adjust if using vanilla default gravity (1.0)
            // Don't touch projectiles that already have custom gravity set
            float current = __instance.configuration.gravityMultiplier;
            if (Mathf.Abs(current - 1f) > 0.05f) return; // custom gravity, leave it

            if      (speed < 200f) __instance.configuration.gravityMultiplier = 2.8f;  // pistol, heavy drop
            else if (speed < 350f) __instance.configuration.gravityMultiplier = 1.8f;  // SMG
            else if (speed < 550f) __instance.configuration.gravityMultiplier = 1.2f;  // rifle, slight increase
            else if (speed < 750f) __instance.configuration.gravityMultiplier = 0.9f;  // HV, nearly flat
            else                   __instance.configuration.gravityMultiplier = 0.7f;  // sniper, very flat but visible at range
        }
    }
}
