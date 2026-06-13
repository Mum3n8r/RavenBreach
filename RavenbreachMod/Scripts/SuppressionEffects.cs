using UnityEngine;

namespace RavenbreachMod
{
    public class SuppressionEffects : MonoBehaviour
    {
        private const float SHAKE_SLOW_HZ        = 1.8f;
        private const float SHAKE_FINE_HZ        = 4.5f;
        private const float SHAKE_MAX_SLOW       = 0.008f;
        private const float SHAKE_MAX_FINE       = 0.003f;
        private const float SHAKE_SMOOTH         = 7f;
        private const float BLAST_SHAKE_HZ       = 0.35f;
        private const float BLAST_SHAKE_MAX      = 0.032f;
        private const float VIGNETTE_PULSE_HZ    = 0.4f;
        private const float VIGNETTE_PULSE_DEPTH = 0.015f;
        private const float KILL_CAM_DURATION    = 5.0f;

        private Camera          _cam;
        private Vector3         _baseLocalPos;
        private int             _camInstanceID;
        private float           _seedX, _seedZ;
        private float           _shakeT, _vigT;
        private Texture2D       _vigTex;
        private Texture2D       _borderTex;
        private Texture2D       _blastTex;
        private SuppressionBlur _blur;

        private bool  _playerDead;
        private float _deathFade;
        private float _deathScreenTimer;

        private float _blastVig;
        private float _blastVigDecayRate;
        private float _blastShake;
        private float _blastShakeDecayRate;
        private float _blastBlur;
        private float _blastBlurDecayRate;

        private static SuppressionEffects _inst;
        public static  SuppressionEffects Instance => _inst;

        public static void TriggerDeath()
        {
            if (_inst == null) return;
            _inst._playerDead       = true;
            _inst._deathFade        = 1f;
            _inst._deathScreenTimer = 0f;
            _inst._shakeT           = 0f;
            _inst._vigT             = 0f;
            _inst._blastVig         = 0f;
            _inst._blastShake       = 0f;
            _inst._blastBlur        = 0f;
            _inst.RestoreCameraPos();
            if (_inst._blur != null) _inst._blur.BlurAmount = 0f;
            SuppressionAudio.TriggerDeath();
        }

        public static void TriggerSpawn()
        {
            if (_inst == null) return;
            _inst._playerDead       = false;
            _inst._deathFade        = 1f;
            _inst._deathScreenTimer = 0f;
            _inst._shakeT = 0f;
            _inst._vigT   = 0f;
            SuppressionAudio.TriggerSpawn();
        }

        public static void TriggerBlast(float intensity, float vigDecay,
                                        float shakeIntens, float shakeDecay)
        {
            if (_inst == null) return;
            if (intensity > _inst._blastVig)
            {
                _inst._blastVig          = intensity;
                _inst._blastVigDecayRate = vigDecay;
            }
            if (shakeIntens > _inst._blastShake)
            {
                _inst._blastShake          = shakeIntens;
                _inst._blastShakeDecayRate = shakeDecay;
            }
            _inst._blastBlur = Mathf.Max(_inst._blastBlur, intensity * 0.92f);
            _inst._blastBlurDecayRate = vigDecay * 1.5f;
        }

        private void Awake()
        {
            if (_inst != null && _inst != this) { Destroy(this); return; }
            _inst      = this;
            _seedX     = Random.value * 1000f;
            _seedZ     = Random.value * 1000f;
            _vigTex    = BuildVignetteTex(256, 0.40f, 1.42f);
            _borderTex = BuildVignetteTex(256, 0.75f, 1.00f);
            _blastTex  = BuildVignetteTex(256, 0.10f, 1.42f);
        }

        private void OnDestroy()
        {
            if (_inst == this) _inst = null;
            if (_vigTex    != null) Destroy(_vigTex);
            if (_borderTex != null) Destroy(_borderTex);
            if (_blastTex  != null) Destroy(_blastTex);
            if (_blur      != null) Destroy(_blur);
            RestoreCameraPos();
        }

        private void RefreshCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) { _cam = null; return; }
            if (_cam == null || cam.GetInstanceID() != _camInstanceID)
            {
                RestoreCameraPos();
                if (_blur != null) { Destroy(_blur); _blur = null; }
                _cam           = cam;
                _camInstanceID = cam.GetInstanceID();
                _baseLocalPos  = cam.transform.localPosition;
                _blur          = _cam.gameObject.AddComponent<SuppressionBlur>();
            }
        }

        private void RestoreCameraPos()
        {
            if (_cam != null)
                _cam.transform.localPosition = _baseLocalPos;
        }

        private void LateUpdate()
        {
            RefreshCamera();
            float dt = Time.deltaTime;

            if (_playerDead)
            {
                _deathScreenTimer += dt;
                _deathFade = _deathScreenTimer < KILL_CAM_DURATION
                    ? 1f
                    : Mathf.MoveTowards(_deathFade, 0f, dt * 2f);
                if (_blur != null) _blur.BlurAmount = 0f;
                return;
            }

            if (_deathFade > 0f)
                _deathFade = Mathf.MoveTowards(_deathFade, 0f, dt * 2f);

            if (_blastVig   > 0.001f) _blastVig   *= Mathf.Exp(-_blastVigDecayRate   * dt); else _blastVig   = 0f;
            if (_blastShake > 0.001f) _blastShake *= Mathf.Exp(-_blastShakeDecayRate * dt); else _blastShake = 0f;
            if (_blastBlur  > 0.001f) _blastBlur  *= Mathf.Exp(-_blastBlurDecayRate   * dt); else _blastBlur  = 0f;

            var   sup = SuppressionTracker.PlayerSuppression;
            float n   = sup != null ? sup.SuppressionLevel / 100f : 0f;

            _shakeT = Mathf.Lerp(_shakeT, n, SHAKE_SMOOTH * dt);
            _vigT   = Mathf.Lerp(_vigT,   n, SHAKE_SMOOTH * dt);

            if (_blur != null)
                _blur.BlurAmount = Mathf.Lerp(_blur.BlurAmount,
                    Mathf.Max(n * 0.85f, _blastBlur), SHAKE_SMOOTH * dt);

            ApplyCameraShake(dt);
        }

        private void ApplyCameraShake(float dt)
        {
            if (_cam == null) return;

            Vector3 offset = Vector3.zero;

            if (_shakeT > 0.001f)
            {
                float t  = Time.time;
                float sx = (Mathf.PerlinNoise(t * SHAKE_SLOW_HZ + _seedX,        0.5f) - 0.5f) * 2f;
                float sy = (Mathf.PerlinNoise(0.5f, t * SHAKE_SLOW_HZ + _seedZ)        - 0.5f) * 2f;
                float fx = (Mathf.PerlinNoise(t * SHAKE_FINE_HZ + _seedX + 200f,  0.3f) - 0.5f) * 2f;
                float fy = (Mathf.PerlinNoise(0.3f, t * SHAKE_FINE_HZ + _seedZ + 200f) - 0.5f) * 2f;
                float mx = (Mathf.PerlinNoise(t * 12f + _seedX, 0.8f) - 0.5f) * 2f * 0.001f;
                float my = (Mathf.PerlinNoise(0.8f, t * 12f + _seedZ) - 0.5f) * 2f * 0.001f;
                if (_shakeT > 0.5f) { sx += mx; sy += my; }
                offset += new Vector3(
                    sx * SHAKE_MAX_SLOW * _shakeT + fx * SHAKE_MAX_FINE * _shakeT,
                    sy * SHAKE_MAX_SLOW * _shakeT + fy * SHAKE_MAX_FINE * _shakeT, 0f);
            }

            if (_blastShake > 0.001f)
            {
                float t  = Time.time;
                float bx = (Mathf.PerlinNoise(t * BLAST_SHAKE_HZ + _seedX + 500f, 0.7f) - 0.5f) * 2f;
                float by = (Mathf.PerlinNoise(0.7f, t * BLAST_SHAKE_HZ + _seedZ + 500f) - 0.5f) * 2f;
                offset += new Vector3(
                    bx * BLAST_SHAKE_MAX * _blastShake,
                    by * BLAST_SHAKE_MAX * _blastShake, 0f);
            }

            if (offset != Vector3.zero)
                _cam.transform.localPosition = _baseLocalPos + offset;
            else
                _cam.transform.localPosition = Vector3.Lerp(
                    _cam.transform.localPosition, _baseLocalPos, 12f * dt);
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            int sw = Screen.width, sh = Screen.height;
            Color prev;

            if (_deathFade > 0.001f)
            {
                GUI.color = new Color(0f, 0f, 0f, _deathFade);
                GUI.DrawTexture(new Rect(0, 0, sw, sh), Texture2D.whiteTexture);
                GUI.color = Color.white;
                if (_playerDead) return;
            }

            if (_blastVig > 0.005f && _blastTex != null)
            {
                prev = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, Mathf.Clamp01(_blastVig * 0.95f));
                GUI.DrawTexture(new Rect(0, 0, sw, sh), _blastTex,
                                ScaleMode.StretchToFill, alphaBlend: true);
                GUI.color = prev;
            }

            if (_vigT < 0.002f) return;

            var sup2 = SuppressionTracker.PlayerSuppression;
            int tier = sup2 != null ? sup2.Tier : 0;

            if (_vigTex != null)
            {
                float pulse    = 1f + Mathf.Sin(Time.time * VIGNETTE_PULSE_HZ * 2f * Mathf.PI)
                                    * VIGNETTE_PULSE_DEPTH * _vigT;
                float vigAlpha = Mathf.Clamp01(_vigT * 0.60f * pulse);
                prev = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, vigAlpha);
                GUI.DrawTexture(new Rect(0, 0, sw, sh), _vigTex,
                                ScaleMode.StretchToFill, alphaBlend: true);
                GUI.color = prev;
            }

            if (tier >= 2 && _borderTex != null)
            {
                float hbRate   = tier == 2 ? 1.4f : 2.0f;
                float rawPulse = Mathf.Sin(Time.time * hbRate * 2f * Mathf.PI) * 0.5f + 0.5f;
                float hbAlpha  = rawPulse * 0.28f;
                float hbScale  = 1f + rawPulse * 0.018f;
                prev = GUI.color;
                Matrix4x4 mat = GUI.matrix;
                GUIUtility.ScaleAroundPivot(new Vector2(hbScale, hbScale),
                                            new Vector2(sw * 0.5f, sh * 0.5f));
                GUI.color = new Color(0f, 0f, 0f, hbAlpha);
                GUI.DrawTexture(new Rect(0, 0, sw, sh), _borderTex,
                                ScaleMode.StretchToFill, alphaBlend: true);
                GUI.matrix = mat;
                GUI.color  = prev;
            }
        }

        private static Texture2D BuildVignetteTex(int size, float innerEdge, float outerEdge)
        {
            var   tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float h   = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - h) / h, dy = (y - h) / h;
                float d  = Mathf.Sqrt(dx * dx + dy * dy);
                float a  = Mathf.Clamp01(Mathf.InverseLerp(innerEdge, outerEdge, d));
                tex.SetPixel(x, y, new Color(0f, 0f, 0f, a));
            }
            tex.Apply();
            return tex;
        }
    }
}
