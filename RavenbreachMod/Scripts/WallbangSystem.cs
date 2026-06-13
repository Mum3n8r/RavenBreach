using UnityEngine;
using System.Collections.Generic;

namespace RavenbreachMod
{
    public static class WallbangSystem
    {
        private const float COST_LIGHT  = 0.25f;
        private const float COST_MEDIUM = 0.55f;
        private const float COST_HEAVY  = 0.90f;
        private const float DAMAGE_RETAIN_LIGHT  = 0.80f;
        private const float DAMAGE_RETAIN_MEDIUM = 0.55f;
        private const float DAMAGE_RETAIN_HEAVY  = 0.25f;
        private const float FRAGMENT_RADIUS           = 4.5f;
        private const float FRAGMENT_SUPPRESSION_NEAR = 18f;
        private const float FRAGMENT_SUPPRESSION_FAR  = 6f;

        private static readonly Dictionary<int, float> _budget = new Dictionary<int, float>();

        private enum WallTier { None, Light, Medium, Heavy }

        public static bool HandleHit(Projectile projectile, RaycastHit hitInfo)
        {
            if (projectile == null) return true;

            Collider col   = hitInfo.collider;
            Vector3  point = hitInfo.point;
            WallTier tier  = ClassifyCollider(col);

            int id = projectile.GetInstanceID();

            // Hit an actor or exhausted budget -- clean up and let the hit land
            if (tier == WallTier.None)
            {
                _budget.Remove(id);
                return true;
            }

            if (!_budget.TryGetValue(id, out float budget)) budget = 1.0f;

            budget -= GetCost(tier);
            if (budget <= 0f) { _budget.Remove(id); return true; }

            _budget[id] = budget;
            ScaleDamage(projectile, budget, tier);
            projectile.transform.position = point + projectile.transform.forward * 0.05f;
            return false;
        }

        private static WallTier ClassifyCollider(Collider col)
        {
            if (col == null) return WallTier.None;
            string name = (col.sharedMaterial?.name ?? col.gameObject.name).ToLowerInvariant();
            if (name.Contains("_wb_none"))   return WallTier.None;
            if (name.Contains("_wb_heavy"))  return WallTier.Heavy;
            if (name.Contains("_wb_medium")) return WallTier.Medium;
            if (name.Contains("_wb_light"))  return WallTier.Light;
            string tag = col.gameObject.tag;
            if (tag == "WB_None")   return WallTier.None;
            if (tag == "WB_Heavy")  return WallTier.Heavy;
            if (tag == "WB_Medium") return WallTier.Medium;
            if (tag == "WB_Light")  return WallTier.Light;
            return WallTier.None;
        }

        private static float GetCost(WallTier t) =>
            t == WallTier.Light ? COST_LIGHT : t == WallTier.Medium ? COST_MEDIUM : COST_HEAVY;

        private static void ScaleDamage(Projectile p, float budget, WallTier t)
        {
            float r = (t == WallTier.Light ? DAMAGE_RETAIN_LIGHT :
                       t == WallTier.Medium ? DAMAGE_RETAIN_MEDIUM : DAMAGE_RETAIN_HEAVY) * budget;
            p.configuration.damage        *= r;
            p.configuration.balanceDamage *= r;
        }

        private static void ApplyFragmentSuppression(Vector3 point)
        {
            // TODO: add team + self checks before re-enabling.
            // Shooter's projectile needs to carry team info so we can
            // only suppress enemies within the fragment radius.
            foreach (var col in Physics.OverlapSphere(point, FRAGMENT_RADIUS))
            {
                var sys = col?.GetComponentInParent<SuppressionSystem>();
                if (sys == null) continue;
                float d = Vector3.Distance(point, col.transform.position);
                sys.AddSuppression(Mathf.Lerp(FRAGMENT_SUPPRESSION_NEAR, FRAGMENT_SUPPRESSION_FAR,
                    d / FRAGMENT_RADIUS));
            }
        }
    }
}
