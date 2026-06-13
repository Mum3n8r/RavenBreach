using System.Collections.Generic;
using UnityEngine;

namespace RavenbreachMod
{
    /// <summary>
    /// Per-source audio occlusion — only processes AudioSources that belong to
    /// Actors or Vehicles. This avoids touching DynamicAudio's internal sources,
    /// bullet crack/impact sounds, and ambient effects.
    /// </summary>
    public class AudioOcclusionSystem : MonoBehaviour
    {
        private const float SCAN_RANGE          = 120f;
        private const float MIN_OCCLUSION_DIST  = 8f;
        private const float UPDATE_INTERVAL     = 0.25f;
        private const float SOURCE_LIST_REFRESH = 2.0f;
        private const float OCCLUDED_CUTOFF     = 500f;
        private const float OPEN_CUTOFF         = 22000f;
        private const float FILTER_SMOOTH       = 6f;
        private const float OCCLUDED_VOLUME_MUL = 0.35f;

        private struct SourceState
        {
            public AudioLowPassFilter filter;
            public float              originalReverbMix;
            public float              originalVolume;
            public bool               modified;
        }

        private readonly Dictionary<int, float>       _updateTimers  = new Dictionary<int, float>();
        private readonly Dictionary<int, SourceState> _states        = new Dictionary<int, SourceState>();
        private readonly Dictionary<int, float>       _targetCutoffs = new Dictionary<int, float>();

        // Only cache sources on Actors and Vehicles — avoids DynamicAudio internals
        private readonly List<AudioSource> _validSources = new List<AudioSource>();
        private float _sourceListTimer;

        private void Update()
        {
            float dt = Time.deltaTime;

            _sourceListTimer -= dt;
            if (_sourceListTimer <= 0f)
            {
                _sourceListTimer = SOURCE_LIST_REFRESH;
                RebuildSourceList();
            }

            var player = SuppressionTracker.PlayerController;
            if (player == null) return;

            // Early out: if nothing is modified and nothing is nearby, skip the loop
            if (_states.Count == 0 && _validSources.Count == 0) return;

            Vector3 earPos      = player.transform.position + Vector3.up * 1.6f;
            var     playerActor = player.GetComponentInParent<Actor>();

            foreach (var src in _validSources)
            {
                if (src == null) continue;
                // Skip non-playing sources UNLESS we have a state for them that needs restoring
                if (!src.isPlaying)
                {
                    int sid = src.GetInstanceID();
                    if (_states.TryGetValue(sid, out var existing) && existing.modified)
                        RestoreSource(sid, src);  // restore then skip
                    continue;
                }

                int     id   = src.GetInstanceID();
                Vector3 pos  = src.transform.position;
                float   dist = Vector3.Distance(pos, earPos);

                // Skip player's own sounds and close sources
                if (dist < MIN_OCCLUSION_DIST ||
                    (playerActor != null && src.GetComponentInParent<Actor>() == playerActor))
                {
                    RestoreSource(id, src);
                    continue;
                }

                if (dist > SCAN_RANGE)
                {
                    // Only need to work on this if it has an active filter
                    if (_states.ContainsKey(id))
                    {
                        _targetCutoffs[id] = OPEN_CUTOFF;
                        SmoothAndApply(id, src, dt, false);
                    }
                    continue;
                }

                _updateTimers.TryGetValue(id, out float timer);
                timer -= dt;
                if (timer > 0f)
                {
                    _updateTimers[id] = timer;
                    // Only smooth if we have an active filter — skip otherwise
                    if (_states.ContainsKey(id))
                        SmoothAndApply(id, src, dt, _states.TryGetValue(id, out var cached) && cached.modified);
                    continue;
                }
                _updateTimers[id] = UPDATE_INTERVAL + Random.value * 0.05f;

                bool occluded      = IsOccluded(pos, earPos);
                _targetCutoffs[id] = occluded ? OCCLUDED_CUTOFF : OPEN_CUTOFF;

                // If not occluded and no existing filter, nothing to do
                if (!occluded && !_states.ContainsKey(id)) continue;

                SmoothAndApply(id, src, dt, occluded);
            }
        }

        private void RebuildSourceList()
        {
            _validSources.Clear();

            var player = SuppressionTracker.PlayerController;
            if (player == null) return;
            Vector3 earPos = player.transform.position;

            // Only pull AudioSources from actors within SCAN_RANGE — avoids
            // GetComponentsInChildren on every actor in a 200-bot match.
            var actorList = ActorManager.instance?.actors;
            if (actorList != null)
            foreach (var actor in actorList)
            {
                if (actor == null) continue;
                if (Vector3.Distance(actor.Position(), earPos) > SCAN_RANGE) continue;
                foreach (var src in actor.GetComponentsInChildren<AudioSource>())
                    if (src != null && src.spatialBlend > 0.3f)
                        _validSources.Add(src);
            }

            // Vehicles: same range cull
            foreach (var vehicle in FindObjectsOfType<Vehicle>())
            {
                if (vehicle == null) continue;
                if (Vector3.Distance(vehicle.transform.position, earPos) > SCAN_RANGE) continue;
                foreach (var src in vehicle.GetComponentsInChildren<AudioSource>())
                    if (src != null && src.spatialBlend > 0.3f)
                        _validSources.Add(src);
            }
        }

        private static bool IsOccluded(Vector3 from, Vector3 to)
        {
            if (!Physics.Linecast(from, to, out RaycastHit hit)) return false;
            if (hit.collider.GetComponentInParent<Actor>()   != null) return false;
            if (hit.collider.GetComponentInParent<Vehicle>() != null) return false;
            return true;
        }

        private void SmoothAndApply(int id, AudioSource src, float dt, bool occluded)
        {
            _targetCutoffs.TryGetValue(id, out float target);
            if (target == 0f) target = OPEN_CUTOFF;

            _states.TryGetValue(id, out SourceState state);

            if (state.filter == null)
            {
                if (target >= OPEN_CUTOFF - 1f) return;
                try
                {
                    state.filter                   = src.gameObject.GetComponent<AudioLowPassFilter>()
                                                  ?? src.gameObject.AddComponent<AudioLowPassFilter>();
                    state.filter.cutoffFrequency   = OPEN_CUTOFF;
                    state.filter.lowpassResonanceQ = 1f;
                    state.originalReverbMix        = src.reverbZoneMix;
                    state.originalVolume           = src.volume;
                    state.modified                 = false;
                    _states[id] = state;
                }
                catch { return; }
            }

            state.filter.cutoffFrequency = Mathf.Lerp(
                state.filter.cutoffFrequency, target, FILTER_SMOOTH * dt);

            bool shouldMuffle = target < OPEN_CUTOFF - 1000f;
            if (shouldMuffle && !state.modified)
            {
                src.bypassReverbZones = true;
                src.reverbZoneMix     = 0f;
                src.volume            = state.originalVolume * OCCLUDED_VOLUME_MUL;
                state.modified        = true;
                _states[id]           = state;
            }
            else if (!shouldMuffle && state.modified)
            {
                src.bypassReverbZones = false;
                src.reverbZoneMix     = state.originalReverbMix;
                src.volume            = state.originalVolume;
                state.modified        = false;
                _states[id]           = state;
            }

            if (target >= OPEN_CUTOFF - 1f && state.filter.cutoffFrequency > OPEN_CUTOFF - 100f)
                RestoreSource(id, src);
        }

        private void RestoreSource(int id, AudioSource src)
        {
            if (_states.TryGetValue(id, out SourceState state))
            {
                if (state.filter != null)
                    state.filter.cutoffFrequency = OPEN_CUTOFF;
                if (state.modified && src != null)
                {
                    src.bypassReverbZones = false;
                    src.reverbZoneMix     = state.originalReverbMix;
                    src.volume            = state.originalVolume;
                }
            }
            _states.Remove(id);
            _updateTimers.Remove(id);
            _targetCutoffs.Remove(id);
        }

        private void OnDestroy()
        {
            foreach (var kvp in _states)
                if (kvp.Value.filter != null)
                    kvp.Value.filter.cutoffFrequency = OPEN_CUTOFF;
        }
    }
}
