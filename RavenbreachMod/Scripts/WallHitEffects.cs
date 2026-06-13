using System.Collections.Generic;
using UnityEngine;

namespace RavenbreachMod
{
    public class WallHitEffects : MonoBehaviour
    {
        private const float SCREEN_RANGE    = 0.6f;
        private const float SPOT_DECAY_MIN  = 0.12f;
        private const float SPOT_DECAY_MAX  = 0.28f;
        private const int   MAX_SPOTS       = 30;

        private struct DirtSpot
        {
            public Vector2 pos;
            public float   size;
            public float   alpha;
            public float   decayRate;
        }

        private readonly List<DirtSpot> _spots = new List<DirtSpot>();
        private static Texture2D _spotTex;

        private static GameObject _mistTemplate;
        private static bool       _mistCaptured = false;

        private struct MistEntry { public Vector3 pos; public float expiry; }
        private static readonly List<MistEntry> _mistCache = new List<MistEntry>();

        public static bool HasWeaponMistAt(Vector3 pos)
        {
            float now = Time.time;
            for (int i = _mistCache.Count - 1; i >= 0; i--)
            {
                if (_mistCache[i].expiry < now) { _mistCache.RemoveAt(i); continue; }
                if (Vector3.Distance(_mistCache[i].pos, pos) < 0.5f) return true;
            }
            return false;
        }

        private static void RegisterMistAt(Vector3 pos, float lifetime)
        {
            _mistCache.Add(new MistEntry { pos = pos, expiry = Time.time + lifetime });
        }

        public static void ResetMistCapture()
        {
            if (_mistTemplate == null)
                _mistCaptured = false;
        }

        public static void TryCaptureMistPrefab()
        {
            if (_mistCaptured) return;
            BuildHardcodedMistTemplate();
        }

        private static void BuildHardcodedMistTemplate()
        {
            try
            {
                // Decode the captured smokepuff1 Alpha texture
                byte[] pngBytes = System.Convert.FromBase64String(MistTextureData.PNG_B64);
                var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                ImageConversion.LoadImage(tex, pngBytes);
                tex.name = "smokepuff1 Alpha";

                // Build material
                var mat = new Material(Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
                mat.mainTexture = tex;

                // Build GO + ParticleSystem
                var go = new GameObject("ImpactMistTemplate");
                var ps = go.AddComponent<ParticleSystem>();
                var renderer = go.GetComponent<ParticleSystemRenderer>();
                renderer.material = mat;

                var main = ps.main;
                main.startLifetime    = new ParticleSystem.MinMaxCurve(0f, 0.7f);
                main.startSpeed       = new ParticleSystem.MinMaxCurve(2f, 15f);
                main.startSize        = new ParticleSystem.MinMaxCurve(0f, 0.8f);
                main.gravityModifier  = new ParticleSystem.MinMaxCurve(-0.02f);
                main.maxParticles     = 1000;
                main.loop             = false;
                main.playOnAwake      = false;

                // Color over lifetime: two keyframe gradient
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var grad = new Gradient();
                grad.SetKeys(
                    new GradientColorKey[]
                    {
                        new GradientColorKey(new Color(0.7426471f, 0.6830764f, 0.6115917f), 0f),
                        new GradientColorKey(new Color(0.7132353f, 0.6555471f, 0.6555471f), 1f)
                    },
                    new GradientAlphaKey[]
                    {
                        new GradientAlphaKey(0.503f, 0f),
                        new GradientAlphaKey(0.515f, 1f)
                    }
                );
                col.color = new ParticleSystem.MinMaxGradient(grad);

                // Emission: single burst
                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = 0f;
                emission.SetBursts(new ParticleSystem.Burst[]
                {
                    new ParticleSystem.Burst(0f, 20)
                });

                // Shape: sphere radius 0.01
                var shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.01f;

                go.SetActive(false);
                Object.DontDestroyOnLoad(go);
                _mistTemplate = go;
                _mistCaptured = true;
                Plugin.Log?.LogInfo("[ImpactMist] Hardcoded mist template built.");
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogError("[ImpactMist] BuildHardcodedMistTemplate failed: " + e.Message);
            }
        }

        // No-op — live capture removed
        public static void TryLiveCaptureAt(Vector3 pos) { }

        public static void SpawnImpactMist(Vector3 pos, Vector3 normal, RaycastHit hit)
        {
            if (_mistTemplate == null) return;
            try
            {
                RegisterMistAt(pos, 2.5f);
                var go = Object.Instantiate(_mistTemplate, pos + normal * 0.015f,
                                           Quaternion.LookRotation(normal));
                go.SetActive(true);
                Object.Destroy(go, 2.5f);
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                { ps.Stop(); ps.Play(); }
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogError("[ImpactMist] " + e.Message);
            }
        }

        public static void OnWallHit(Vector3 hitPoint, Vector3 hitNormal)
        {
            if (_inst == null) return;
            var player = SuppressionTracker.PlayerController;
            if (player == null) return;

            Vector3 earPos   = player.transform.position + Vector3.up * 1.6f;
            float   dist     = Vector3.Distance(hitPoint, earPos);
            Vector3 toPlayer = (earPos - hitPoint).normalized;
            if (Vector3.Dot(hitNormal, toPlayer) < 0.15f) return;

            if (dist <= SCREEN_RANGE)
            {
                float t         = 1f - (dist / SCREEN_RANGE);
                int   spotCount = Mathf.RoundToInt(Mathf.Lerp(3f, 8f, t));
                float maxAlpha  = Mathf.Lerp(0.45f, 0.90f, t);
                _inst.AddSpots(spotCount, maxAlpha);
            }
        }

        private static WallHitEffects _inst;

        private void Awake()
        {
            _inst    = this;
            _spotTex = BuildSpotTex(32);
        }

        private void OnDestroy()
        {
            if (_inst == this) _inst = null;
            if (_spotTex != null) Destroy(_spotTex);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = _spots.Count - 1; i >= 0; i--)
            {
                var s = _spots[i];
                s.alpha -= s.decayRate * dt;
                if (s.alpha <= 0f) { _spots.RemoveAt(i); continue; }
                _spots[i] = s;
            }
        }

        private void OnGUI()
        {
            if (_spots.Count == 0 || Event.current.type != EventType.Repaint) return;
            int sw = Screen.width, sh = Screen.height;
            foreach (var s in _spots)
            {
                GUI.color = new Color(0.06f, 0.04f, 0.02f, s.alpha);
                GUI.DrawTexture(
                    new Rect(s.pos.x * sw - s.size * 0.5f,
                             s.pos.y * sh - s.size * 0.5f,
                             s.size, s.size),
                    _spotTex, ScaleMode.StretchToFill, true);
            }
            GUI.color = Color.white;
        }

        private void AddSpots(int count, float maxAlpha)
        {
            while (_spots.Count + count > MAX_SPOTS && _spots.Count > 0)
                _spots.RemoveAt(0);
            for (int i = 0; i < count; i++)
            {
                _spots.Add(new DirtSpot
                {
                    pos       = new Vector2(Random.Range(0.06f, 0.94f), Random.Range(0.06f, 0.94f)),
                    size      = Random.Range(22f, 85f),
                    alpha     = Random.Range(maxAlpha * 0.55f, maxAlpha),
                    decayRate = Random.Range(SPOT_DECAY_MIN, SPOT_DECAY_MAX)
                });
            }
        }

        private static Texture2D BuildSpotTex(int size)
        {
            var   tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float h   = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - h) / h, dy = (y - h) / h;
                float d  = Mathf.Sqrt(dx * dx + dy * dy);
                float a  = Mathf.Clamp01(1f - Mathf.Pow(d, 1.2f));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply();
            return tex;
        }
    }
}
