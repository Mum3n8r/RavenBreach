using HarmonyLib;
using UnityEngine;

namespace RavenbreachMod
{
    // Hooks vanilla Explosion.Explode to drive suppression, blast particles, and overpressure.
    [HarmonyPatch]
    public static class ExplosionSuppressionPatch
    {
        static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("Explosion"), "Explode");

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            if (__instance == null) return;
            try
            {
                var t       = __instance.GetType();
                var posF    = AccessTools.Field(t, "position") ?? AccessTools.Field(t, "transform");
                var radF    = AccessTools.Field(t, "radius")   ?? AccessTools.Field(t, "blastRadius");
                var damF    = AccessTools.Field(t, "damage")   ?? AccessTools.Field(t, "baseDamage");

                Vector3 pos    = Vector3.zero;
                float   radius = 8f;
                float   damage = 50f;

                if (posF != null)
                {
                    var v = posF.GetValue(__instance);
                    if (v is Vector3 vv) pos = vv;
                    else if (v is Transform tr) pos = tr.position;
                }
                if (radF != null) { try { radius = System.Convert.ToSingle(radF.GetValue(__instance)); } catch { } }
                if (damF != null) { try { damage = System.Convert.ToSingle(damF.GetValue(__instance)); } catch { } }

                // Classify explosion type by damage magnitude
                ExplosionType type = damage >= 120f ? ExplosionType.Artillery
                                   : damage >= 60f  ? ExplosionType.Rocket
                                   : ExplosionType.Grenade;

                // Drive suppression on nearby bots
                if (ActorManager.instance?.actors != null)
                {
                    foreach (var actor in ActorManager.instance.actors)
                    {
                        if (actor == null || actor.dead || !actor.aiControlled) continue;
                        float dist = Vector3.Distance(actor.Position(), pos);
                        if (dist > radius * 2.5f) continue;
                        var sup = actor.GetComponent<SuppressionSystem>();
                        if (sup == null) continue;
                        float falloff = 1f - Mathf.Clamp01(dist / (radius * 2.5f));
                        sup.AddSuppression(damage * 0.4f * falloff);
                    }
                }

                // Blast particles
                BlastParticleSystem.Spawn(pos, type);

                // Overpressure venting
                OverpressureSystem.Simulate(pos, radius, type);
            }
            catch { }
        }
    }
}
