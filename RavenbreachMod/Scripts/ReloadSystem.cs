using UnityEngine;
using System.Collections;

namespace RavenbreachMod
{
    public static class ReloadSystem
    {
        private const float TACTICAL_MULT   = 0.75f;
        private const float EMPTY_MULT      = 1.20f;
        private const float SUPPRESSED_MULT = 1.60f;
        private const float CYCLE_MULT      = 1.00f;
        private const float SUP_THRESHOLD   = 50f;
        private const float FUMBLE_PER_PT   = 0.008f;
        private const float FUMBLE_PENALTY  = 0.9f;

        public static bool HandleReload(Weapon weapon, bool overrideHolstered)
        {
            if (weapon == null || weapon.reloading || weapon.configuration.spareAmmo < 0) return false;

            var sup          = weapon.GetComponentInParent<SuppressionSystem>();
            int pool         = SelectPool(weapon, sup);
            float mult       = GetMult(pool);
            float fumbleChance = pool == 2 && sup != null
                ? Mathf.Clamp01((sup.SuppressionLevel - SUP_THRESHOLD) * FUMBLE_PER_PT) : 0f;

            Plugin.Log?.LogDebug($"[Reload] Pool {pool} x{mult:F2} fumble {fumbleChance:P0}");

            // Feed debug overlay
            DebugOverlay.LastReloadPool   = pool;
            DebugOverlay.LastFumbleChance = fumbleChance;
            DebugOverlay.LogEvent($"Reload P{pool} ×{mult:F2}{(fumbleChance > 0f ? $"  fumble {fumbleChance:P0}" : "")}");

            weapon.StartCoroutine(ReloadRoutine(weapon, mult, fumbleChance));
            return true;
        }

        private static int SelectPool(Weapon w, SuppressionSystem s)
        {
            if (w.configuration.advancedReload) return 3;
            if (s != null && s.SuppressionLevel >= SUP_THRESHOLD) return 2;
            if (w.ammo == 0) return 1;
            return 0;
        }

        private static float GetMult(int p) =>
            p == 0 ? TACTICAL_MULT :
            p == 1 ? EMPTY_MULT    :
            p == 2 ? SUPPRESSED_MULT : CYCLE_MULT;

        private static IEnumerator ReloadRoutine(Weapon weapon, float mult, float fumbleChance)
        {
            weapon.reloading   = true;
            weapon.holdingFire = false;
            if (weapon.reloadAudio != null) weapon.reloadAudio.Play();
            var anim = weapon.GetComponent<Animator>();
            anim?.SetTrigger("reload");

            float t = weapon.configuration.reloadTime * mult;
            if (fumbleChance > 0f && Random.value < fumbleChance)
            {
                DebugOverlay.LogEvent("  !! Fumble !!");
                yield return new WaitForSeconds(t * 0.5f);
                yield return new WaitForSeconds(FUMBLE_PENALTY);
                yield return new WaitForSeconds(t * 0.5f);
            }
            else yield return new WaitForSeconds(t);

            weapon.ammo = weapon.configuration.useMaxAmmoPerReload
                ? Mathf.Min(weapon.configuration.ammo, weapon.ammo + weapon.configuration.maxAmmoPerReload)
                : weapon.configuration.ammo;
            weapon.reloading = false;
        }
    }
}
