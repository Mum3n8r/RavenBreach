using UnityEngine;

namespace RavenbreachMod
{
    // War Thunder style body damage indicator — bottom-left, translucent.
    // Each region is fully localized: only the shot part changes color.
    // Clear = healthy, yellow = injured, orange = heavy, red = critical.
    // Pulses when the wound is still actively bleeding.
    public class BodyPartHUD : MonoBehaviour
    {
        private const float W   = 72f;
        private const float H   = 124f;
        private const float PAD = 12f;

        private float _pulseTimer = 0f;

        private void Update() { _pulseTimer += Time.deltaTime * 2.5f; }

        private void OnGUI()
        {
            if (!GameManager.IsIngame()) return;
            var fps = FpsActorController.instance;
            if (fps?.actor == null || fps.actor.dead) return;

            var s = InjurySystem.GetState(fps.actor);
            if (s == null) return;

            // Only show if any injury exists
            if (s.headDamage + s.neckDamage + s.chestDamage + s.abdomenDamage +
                s.leftArmDamage + s.rightArmDamage + s.leftLegDamage + s.rightLegDamage < 0.5f)
                return;

            float sw = Screen.width, sh = Screen.height;
            float px = PAD, py = sh - H - PAD;
            float pulse = (Mathf.Sin(_pulseTimer) + 1f) * 0.5f;
            var  a = fps.actor;

            // Background
            GUI.color = new Color(0f, 0f, 0f, 0.32f);
            GUI.DrawTexture(new Rect(px - 4, py - 4, W + 8, H + 8), Texture2D.whiteTexture);
            DrawBorder(new Rect(px - 4, py - 4, W + 8, H + 8), new Color(1f,1f,1f,0.12f), 1f);
            GUI.color = Color.white;

            float cx = px + W * 0.5f; // horizontal center

            // HEAD
            DrawPart(cx - 8,  py,      16, 16, s.headDamage,     BleedingSystem.IsActorBleeding(a, BodyPart.Head),     pulse);
            // NECK
            DrawPart(cx - 4,  py + 17, 8,  6,  s.neckDamage,     BleedingSystem.IsActorBleeding(a, BodyPart.Neck),     pulse);
            // CHEST
            DrawPart(cx - 11, py + 24, 22, 18, s.chestDamage,    BleedingSystem.IsActorBleeding(a, BodyPart.Chest),    pulse);
            // ABDOMEN
            DrawPart(cx - 9,  py + 43, 18, 14, s.abdomenDamage,  BleedingSystem.IsActorBleeding(a, BodyPart.Abdomen),  pulse);
            // LEFT ARM  (player's left = visually right side of silhouette)
            DrawPart(cx + 12, py + 24, 9,  28, s.leftArmDamage,  BleedingSystem.IsActorBleeding(a, BodyPart.LeftArm),  pulse);
            // RIGHT ARM
            DrawPart(cx - 21, py + 24, 9,  28, s.rightArmDamage, BleedingSystem.IsActorBleeding(a, BodyPart.RightArm), pulse);
            // LEFT LEG
            DrawPart(cx + 2,  py + 58, 10, 40, s.leftLegDamage,  BleedingSystem.IsActorBleeding(a, BodyPart.LeftLeg),  pulse);
            // RIGHT LEG
            DrawPart(cx - 12, py + 58, 10, 40, s.rightLegDamage, BleedingSystem.IsActorBleeding(a, BodyPart.RightLeg), pulse);

            GUI.color = Color.white;
        }

        private Color PartColor(float damage, bool bleeding, float pulse)
        {
            float sev = Mathf.Clamp01(damage / 80f);
            if (sev < 0.04f) return new Color(1f, 1f, 1f, 0.07f); // essentially invisible

            Color c;
            if      (sev < 0.35f) c = Color.Lerp(new Color(1,1,1,0.12f), new Color(1,0.9f,0,0.50f), sev / 0.35f);
            else if (sev < 0.70f) c = Color.Lerp(new Color(1,0.9f,0,0.50f), new Color(1,0.35f,0,0.65f), (sev-0.35f)/0.35f);
            else                  c = Color.Lerp(new Color(1,0.35f,0,0.65f), new Color(1,0.02f,0.02f,0.82f), (sev-0.70f)/0.30f);

            if (bleeding)
            {
                float p = 0.35f + pulse * 0.45f;
                c = Color.Lerp(c, new Color(1f, 0.03f, 0.03f, 0.90f), p);
            }
            return c;
        }

        private void DrawPart(float x, float y, float w, float h,
                               float damage, bool bleeding, float pulse)
        {
            GUI.color = PartColor(damage, bleeding, pulse);
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
            DrawBorder(new Rect(x, y, w, h), new Color(0, 0, 0, 0.45f), 1f);
        }

        private static void DrawBorder(Rect r, Color col, float t)
        {
            GUI.color = col;
            GUI.DrawTexture(new Rect(r.x,        r.y,        r.width, t),       Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x,        r.yMax - t, r.width, t),       Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x,        r.y,        t,       r.height),Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y,        t,       r.height),Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
