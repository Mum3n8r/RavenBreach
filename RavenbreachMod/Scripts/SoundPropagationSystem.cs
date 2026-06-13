using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// ──────────────────────────────────────────────────────────────────────────────
// SoundPropagationSystem
//
// Speed-of-sound delay for explosions only:
//   • Explosion thump — delayed by distance / 343 m/s, volume by explosion type
//   • Fake echo       — single repeat at delay + ECHO_OFFSET_SECS
//
// Gunshot thump and near-impact removed — procedural clips sounded bad at scale.
// Crack disabled until real AudioClips are sourced.
//
// Clip wiring (optional — silent but functional without it):
//   SoundPropagationSystem.Instance.DistantThumpClip = <your AudioClip>
// ──────────────────────────────────────────────────────────────────────────────

namespace RavenbreachMod
{
    public class SoundPropagationSystem : MonoBehaviour
    {
        // ── tunables ──────────────────────────────────────────────────────────
        private const float SPEED_OF_SOUND   = 343f;
        private const float MAX_DELAY        = 8f;
        private const float MIN_AUDIBLE_DIST = 20f;
        private const float ECHO_OFFSET_SECS = 0.22f;
        private const float ECHO_VOLUME_RATIO = 0.18f;
        private const float THUMP_MASTER     = 0.12f;

        // ── public clip slot ──────────────────────────────────────────────────
        public AudioClip DistantThumpClip;

        // ── singleton ─────────────────────────────────────────────────────────
        public static SoundPropagationSystem Instance { get; private set; }

        // ── internals ─────────────────────────────────────────────────────────
        private struct SoundEvent
        {
            public float FireTime;
            public float Delay;
            public float Volume;
        }

        private readonly List<SoundEvent> _pending = new List<SoundEvent>(32);
        private AudioSource _audioSource;

        // ── lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            Instance = this;

            _audioSource                   = gameObject.AddComponent<AudioSource>();
            _audioSource.spatialBlend      = 0f;
            _audioSource.playOnAwake       = false;
            _audioSource.loop              = false;
            _audioSource.bypassReverbZones = true;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ── public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Called by ExplosionSuppressionPatch for every explosion.
        /// typeScale: 1.0 = grenade, 1.5 = rocket, 2.2 = artillery
        /// </summary>
        public static void RegisterExplosion(Vector3 explosionPos, float typeScale)
        {
            if (Instance == null) return;

            var player = SuppressionTracker.PlayerController;
            if (player == null) return;

            Vector3 earPos = player.transform.position + Vector3.up * 1.6f;
            float   dist   = Vector3.Distance(explosionPos, earPos);

            if (dist < MIN_AUDIBLE_DIST) return;

            float delay = Mathf.Min(dist / SPEED_OF_SOUND, MAX_DELAY);

            // Louder for bigger explosions, falls off with distance
            float vol = Mathf.Pow(Mathf.Clamp01(1f - dist / 1500f), 0.5f)
                        * typeScale * THUMP_MASTER;

            if (vol < 0.001f) return;

            Instance._pending.Add(new SoundEvent
            {
                FireTime = Time.time,
                Delay    = delay,
                Volume   = vol
            });

            // Fake echo
            Instance._pending.Add(new SoundEvent
            {
                FireTime = Time.time,
                Delay    = delay + ECHO_OFFSET_SECS,
                Volume   = vol * ECHO_VOLUME_RATIO
            });
        }

        // ── update ────────────────────────────────────────────────────────────

        private void Update()
        {
            if (_pending.Count == 0) return;

            float now = Time.time;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var ev = _pending[i];
                if (now >= ev.FireTime + ev.Delay)
                {
                    if (_audioSource != null && DistantThumpClip != null)
                        _audioSource.PlayOneShot(DistantThumpClip, ev.Volume);
                    _pending.RemoveAt(i);
                }
            }
        }
    }
}
