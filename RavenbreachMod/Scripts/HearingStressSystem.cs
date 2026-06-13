using UnityEngine;

namespace RavenbreachMod
{
    public class HearingStressSystem : MonoBehaviour
    {
        private const float BLAST_PAUSE_SECS  = 0.10f;
        private const float STRESS_DECAY_RATE = 0.30f;
        private const float STRESS_FLOOR      = 0.4f;

        private float _hearingStress;
        private float _stressDecayHold;
        private bool  _paused;
        private float _pauseTimer;
        private float _originalVolume;

        private static HearingStressSystem _inst;

        public static void ApplyExplosionStress(float stress, float stressHoldSecs)
        {
            if (_inst == null) return;
            _inst._hearingStress   = Mathf.Min(_inst._hearingStress + stress, 100f);
            _inst._stressDecayHold = Mathf.Max(_inst._stressDecayHold, stressHoldSecs);

            if (!_inst._paused && stress > 25f)
            {
                _inst._paused       = true;
                _inst._pauseTimer   = BLAST_PAUSE_SECS;
                AudioListener.pause = true;
            }
        }

        // No-op — bullets don't damage hearing
        public static void ApplySustainedFireStress(float amount) { }

        public static void RestoreVolume()
        {
            if (_inst == null) return;
            if (_inst._hearingStress < STRESS_FLOOR)
                AudioListener.volume = _inst._originalVolume;
        }

        public float StressRatio => _hearingStress / 100f;

        private void Awake()
        {
            _inst           = this;
            _originalVolume = AudioListener.volume;
        }

        private void OnDestroy() { if (_inst == this) _inst = null; }

        private void Update()
        {
            float dt = Time.deltaTime;

            if (_paused)
            {
                _pauseTimer -= dt;
                if (_pauseTimer <= 0f)
                {
                    _paused             = false;
                    AudioListener.pause = false;
                }
                return;
            }

            if (_stressDecayHold > 0f)
                _stressDecayHold -= dt;
            else if (_hearingStress > STRESS_FLOOR)
                _hearingStress *= Mathf.Exp(-STRESS_DECAY_RATE * dt);
            else
                _hearingStress = 0f;

            // Only touch AudioListener.volume when we have active hearing stress.
            // At stress = 0, leave volume alone — DynamicAudio manages it normally.
            if (!AudioListener.pause && _hearingStress > STRESS_FLOOR)
            {
                float stressRatio    = _hearingStress / 100f;
                float volFactor      = 1f - Mathf.Pow(stressRatio, 1.5f);
                AudioListener.volume = _originalVolume * volFactor;
            }
        }
    }
}
