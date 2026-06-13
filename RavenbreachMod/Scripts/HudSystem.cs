using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RavenbreachMod
{
    public class HudSystem : MonoBehaviour
    {
        public static readonly IngameUI.UIElement[] HIDE_ELEMENTS = new[]
        {
            IngameUI.UIElement.PlayerHealth,
            IngameUI.UIElement.WeaponInfo,
            IngameUI.UIElement.DamageVignette,
            IngameUI.UIElement.DamageDirectionInfo,
            IngameUI.UIElement.Hitmarker,
            IngameUI.UIElement.KillFeed,
            IngameUI.UIElement.SquadMemberInfo,
            IngameUI.UIElement.SquadOrderLabel,
        };

        private static HudSystem _inst;
        private float _ammoFlashT = 0f;
        private bool  _stylesReady = false;

        private GUIStyle _stCompass, _stCompassSm, _stBearing, _stAmmo;

        private void Awake()     { _inst = this; }
        private void OnDestroy() { if (_inst == this) _inst = null; SceneManager.sceneLoaded -= OnSceneLoaded; }

        public static void AssertHiddenElements()
        {
            if (IngameUI.instance == null) return;
            foreach (var el in HIDE_ELEMENTS)
                IngameUI.HideElement(el);
        }

        private void Start()
        {
            AssertHiddenElements();
            KillMinimap();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            KillMinimap();
            AssertHiddenElements();
        }

        private static void KillMinimap()
        {
            // Hide MinimapUi by component — name-based search was unreliable
            var muType = AccessTools.TypeByName("MinimapUi");
            if (muType != null)
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(muType))
                {
                    var mb = obj as MonoBehaviour;
                    if (mb == null) continue;
                    // Hide the canvas or the root GO
                    var canvas = mb.GetComponentInParent<Canvas>();
                    if (canvas != null) { canvas.enabled = false; Plugin.Log?.LogInfo("[HudSystem] Disabled MinimapUi canvas: " + canvas.name); }
                    else { mb.gameObject.SetActive(false); Plugin.Log?.LogInfo("[HudSystem] Disabled MinimapUi GO: " + mb.gameObject.name); }
                }
            }

            // Also hide StrategyUi canvas — it may be visible on scene load
            var suType = AccessTools.TypeByName("StrategyUi");
            if (suType != null)
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(suType))
                {
                    var mb = obj as MonoBehaviour;
                    if (mb == null) continue;
                    var canvas = mb.GetComponentInParent<Canvas>();
                    if (canvas != null) canvas.enabled = false;
                }
            }
        }

        private void Update()
        {
            _ammoFlashT += Time.deltaTime;
            // B key does nothing — squad overlay moved to M (TacticalMapSystem)
            AssertHiddenElements();
        }

        private void OnGUI()
        {
            if (!GameManager.IsIngame()) return;
            var fps = FpsActorController.instance;
            if (fps?.actor == null || fps.actor.dead) return;

            EnsureStyles();
            DrawCompass();
            DrawAmmoWarning(fps.actor);
            GUI.color = Color.white;
        }

        private void DrawCompass()
        {
            var   fps     = FpsActorController.instance;
            float bearing = (fps != null && fps.fpCamera != null)
                ? fps.fpCamera.transform.eulerAngles.y
                : (fps != null ? fps.transform.eulerAngles.y : 0f);
            float sw = Screen.width;
            float cw = 340f, ch = 26f, cx = (sw - cw) * 0.5f, cy = 5f;

            GUI.color = new Color(0f, 0f, 0f, 0.32f);
            GUI.DrawTexture(new Rect(cx, cy, cw, ch), Texture2D.whiteTexture);

            float pixPerDeg = cw / 120f;
            for (int deg = 0; deg < 360; deg += 5)
            {
                float delta = Mathf.DeltaAngle(bearing, deg);
                if (Mathf.Abs(delta) > 60f) continue;
                float x = cx + cw * 0.5f + delta * pixPerDeg;
                bool cardinal      = (deg % 90 == 0);
                bool intercardinal = (deg % 45 == 0);
                float tickH = cardinal ? ch*0.70f : intercardinal ? ch*0.50f : ch*0.28f;

                GUI.color = cardinal      ? new Color(1f,1f,1f,0.85f)
                          : intercardinal ? new Color(1f,1f,1f,0.55f)
                          : new Color(1f,1f,1f,0.28f);
                GUI.DrawTexture(new Rect(x-0.5f, cy+ch-tickH, 1f, tickH), Texture2D.whiteTexture);

                if (cardinal)
                {
                    string lbl = deg==0?"N":deg==90?"E":deg==180?"S":"W";
                    GUI.color = new Color(1f,1f,1f,0.95f);
                    GUI.Label(new Rect(x-8f, cy+1f, 16f, ch-7f), lbl, _stCompass);
                }
                else if (intercardinal)
                {
                    string lbl = deg==45?"NE":deg==135?"SE":deg==225?"SW":"NW";
                    GUI.color = new Color(1f,1f,1f,0.55f);
                    GUI.Label(new Rect(x-8f, cy+1f, 16f, ch-7f), lbl, _stCompassSm);
                }
            }

            GUI.color = new Color(1f,0.55f,0.2f,0.80f);
            GUI.DrawTexture(new Rect(cx+cw*0.5f-0.5f, cy, 1f, ch), Texture2D.whiteTexture);
            GUI.color = new Color(1f,1f,1f,0.55f);
            GUI.Label(new Rect(cx+cw*0.5f-20f, cy+ch+1f, 40f, 12f), ((int)bearing)+"°", _stBearing);
        }

        private enum AmmoState { Fine, Low, Empty }

        private static AmmoState GetAmmoState(Actor actor)
        {
            var w = actor?.activeWeapon;
            if (w == null) return AmmoState.Fine;
            if (w.ammo == 0) return AmmoState.Empty;
            int clip = w.configuration.ammo;
            if (clip <= 0) clip = 30;
            return (w.ammo <= clip / 3) ? AmmoState.Low : AmmoState.Fine;
        }

        private void DrawAmmoWarning(Actor actor)
        {
            var state = GetAmmoState(actor);
            if (state == AmmoState.Fine) return;

            float rate  = state == AmmoState.Empty ? 3f : 1f;
            float alpha = 0.55f + Mathf.Sin(_ammoFlashT * rate * Mathf.PI * 2f) * 0.4f;
            string text = state == AmmoState.Empty ? "MAG EMPTY" : "MAG LOW";
            Color  col  = state == AmmoState.Empty
                ? new Color(1f,0.15f,0.15f,alpha)
                : new Color(1f,0.65f,0.1f,alpha);

            float sw = Screen.width, sh = Screen.height;
            float w = 112f, h = 20f;
            float px = sw-w-14f, py = sh-h-14f;

            GUI.color = new Color(0f,0f,0f,0.3f);
            GUI.DrawTexture(new Rect(px-4f,py-3f,w+8f,h+6f), Texture2D.whiteTexture);
            GUI.color = col;
            GUI.Label(new Rect(px,py,w,h), text, _stAmmo);
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _stCompass = new GUIStyle(GUI.skin.label)
                { fontSize=10, fontStyle=FontStyle.Bold, alignment=TextAnchor.UpperCenter };
            _stCompass.normal.textColor = Color.white;

            _stCompassSm = new GUIStyle(_stCompass) { fontSize=8 };

            _stBearing = new GUIStyle(GUI.skin.label)
                { fontSize=9, alignment=TextAnchor.UpperCenter };
            _stBearing.normal.textColor = new Color(1f,1f,1f,0.65f);

            _stAmmo = new GUIStyle(GUI.skin.label)
                { fontSize=11, fontStyle=FontStyle.Bold, alignment=TextAnchor.MiddleCenter };
            _stAmmo.normal.textColor = Color.white;
        }
    }
}
