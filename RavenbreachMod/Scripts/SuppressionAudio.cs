using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace RavenbreachMod
{
    // ── SuppressionAudio ──────────────────────────────────────────────────────
    // Heartbeat and breathing audio driven by suppression level.
    //
    // Heartbeat: single lub-dub clip, played on a timer that speeds up with
    //            suppression. Starts at suppression 80, loud at 100.
    //
    // Breathing: long looping clip, fades in at suppression 80, full volume
    //            at 100. Only plays during heavy suppression (vignette visible).
    //
    // Clips: BepInEx/plugins/RavenbreachAssets/suppression_heartbeat.wav
    //        BepInEx/plugins/RavenbreachAssets/suppression_breathing.wav
    // ─────────────────────────────────────────────────────────────────────────

    public class SuppressionAudio : MonoBehaviour
    {
        // Volume range
        private const float AUDIO_START_SUPPRESSION = 70f;   // kicks in earlier
        private const float AUDIO_MAX_SUPPRESSION   = 100f;

        // Heartbeat — cranked up, you should feel it
        private const float HB_VOL_MIN  = 0.45f;
        private const float HB_VOL_MAX  = 1.00f;

        // Heartbeat rate
        private const float HB_RATE_MIN = 0.9f;
        private const float HB_RATE_MAX = 2.4f;

        // Breathing — heavier and louder
        private const float BR_VOL_MIN  = 0.30f;
        private const float BR_VOL_MAX  = 0.95f;

        private AudioSource _heartbeatSource;
        private AudioSource _breathingSource;
        private float       _heartbeatTimer;
        private bool        _playerDead;
        private bool        _clipsLoaded;

        private AudioClip _heartbeatClip;
        private AudioClip _breathingClip;

        private static SuppressionAudio _inst;

        private static readonly string ASSETS_DIR =
            Path.Combine(
                Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location),
                "RavenbreachAssets");

        private SuppressionSystem Sup => SuppressionTracker.PlayerSuppression;

        public static void TriggerDeath()
        {
            if (_inst == null) return;
            _inst._playerDead = true;
            if (_inst._heartbeatSource != null) _inst._heartbeatSource.Stop();
            if (_inst._breathingSource != null) _inst._breathingSource.Stop();
            AudioListener.pause  = true;
            AudioListener.volume = 0f;
        }

        public static void TriggerSpawn()
        {
            if (_inst == null) return;
            _inst._playerDead     = false;
            _inst._heartbeatTimer = 999f;
            AudioListener.pause   = false;
            HearingStressSystem.RestoreVolume();
        }

        private void OnEnable()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            AudioListener.pause = false;
        }

        private void Awake()
        {
            _inst = this;

            _heartbeatSource              = gameObject.AddComponent<AudioSource>();
            _heartbeatSource.playOnAwake  = false;
            _heartbeatSource.loop         = false;
            _heartbeatSource.spatialBlend = 0f;
            _heartbeatSource.volume       = 0f;

            _breathingSource              = gameObject.AddComponent<AudioSource>();
            _breathingSource.playOnAwake  = false;
            _breathingSource.loop         = true;
            _breathingSource.spatialBlend = 0f;
            _breathingSource.volume       = 0f;

            StartCoroutine(LoadClips());
        }

        private void OnDestroy()
        {
            if (_inst == this) _inst = null;
            if (_heartbeatClip != null) Destroy(_heartbeatClip);
            if (_breathingClip != null) Destroy(_breathingClip);
        }

        private IEnumerator LoadClips()
        {
            string hbPath = Path.Combine(ASSETS_DIR, "suppression_heartbeat.wav");
            string brPath = Path.Combine(ASSETS_DIR, "suppression_breathing.wav");

            if (File.Exists(hbPath))
            {
                UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip("file://" + hbPath, AudioType.WAV);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    _heartbeatClip = DownloadHandlerAudioClip.GetContent(req);
                else
                    Plugin.Log?.LogWarning("[SupAudio] Failed to load heartbeat: " + req.error);
                req.Dispose();
            }
            else Plugin.Log?.LogWarning("[SupAudio] suppression_heartbeat.wav not found.");

            if (File.Exists(brPath))
            {
                UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip("file://" + brPath, AudioType.WAV);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    _breathingClip = DownloadHandlerAudioClip.GetContent(req);
                else
                    Plugin.Log?.LogWarning("[SupAudio] Failed to load breathing: " + req.error);
                req.Dispose();
            }
            else Plugin.Log?.LogWarning("[SupAudio] suppression_breathing.wav not found.");

            _clipsLoaded = true;
            Plugin.Log?.LogInfo("[SupAudio] Clips loaded.");
        }

        private void Update()
        {
            var sup = Sup;
            if (sup == null || _playerDead || !_clipsLoaded) return;

            float level = sup.SuppressionLevel;
            float t     = Mathf.Clamp01((level - AUDIO_START_SUPPRESSION) /
                                        (AUDIO_MAX_SUPPRESSION - AUDIO_START_SUPPRESSION));

            HandleHeartbeat(t);
            HandleBreathing(t);
        }

        private void HandleHeartbeat(float t)
        {
            if (t <= 0f)
            {
                _heartbeatTimer = 0f;
                return;
            }

            float rate    = Mathf.Lerp(HB_RATE_MIN, HB_RATE_MAX, t);
            float interval = 1f / rate;
            float vol     = Mathf.Lerp(HB_VOL_MIN, HB_VOL_MAX, t);

            _heartbeatTimer -= Time.deltaTime;
            if (_heartbeatTimer <= 0f)
            {
                _heartbeatTimer = interval;
                if (_heartbeatClip != null)
                    _heartbeatSource.PlayOneShot(_heartbeatClip, vol);
            }
        }

        private void HandleBreathing(float t)
        {
            if (_breathingClip == null) return;

            float targetVol = t > 0f ? Mathf.Lerp(BR_VOL_MIN, BR_VOL_MAX, t) : 0f;
            _breathingSource.volume = Mathf.MoveTowards(
                _breathingSource.volume, targetVol, Time.deltaTime * 3.5f);

            // Pitch: sprint while suppressed = laboured fast breathing
            var actor = LocalPlayer.actor;
            bool sprinting = actor != null && !actor.dead
                && actor.controller != null && actor.controller.IsSprinting();
            float targetPitch = (t > 0f && sprinting)
                ? Mathf.Lerp(1.4f, 2.2f, t)   // heavy sprint breathing
                : Mathf.Lerp(1.0f, 1.25f, t);  // normal suppressed breathing
            _breathingSource.pitch = Mathf.MoveTowards(
                _breathingSource.pitch, targetPitch, Time.deltaTime * 4f);

            if (targetVol > 0f && !_breathingSource.isPlaying)
            {
                _breathingSource.clip = _breathingClip;
                _breathingSource.Play();
            }
            else if (targetVol <= 0f && _breathingSource.isPlaying &&
                     _breathingSource.volume < 0.001f)
            {
                _breathingSource.Stop();
            }
        }
    }
}
