using UnityEngine;

namespace RavenbreachMod
{
    public class ExhaustionSystem : MonoBehaviour
    {
        [Header("Weight")] [Range(0f,50f)] public float carriedWeightKg = 10f;
        [Header("Stamina")] [Range(0f,100f)] public float stamina = 100f;
        [Range(0f,30f)] public float baseSprintDrain = 10f;
        [Range(0f,20f)] public float baseRecovery    = 8f;

        private const float LIGHT_MAX  = 12f;
        private const float MEDIUM_MAX = 22f;
        private const float HEAVY_MAX  = 35f;

        private const float DRAIN_LIGHT    = 1.0f;
        private const float DRAIN_MEDIUM   = 1.4f;
        private const float DRAIN_HEAVY    = 2.0f;
        private const float DRAIN_OVERLOAD = 3.2f;

        private const float REC_LIGHT    = 1.0f;
        private const float REC_MEDIUM   = 0.80f;
        private const float REC_HEAVY    = 0.55f;
        private const float REC_OVERLOAD = 0.25f;

        private const float EXHAUSTED_THRESHOLD = 30f;
        private const float EXHAUSTION_SUP_RATE = 4f;

        public bool  IsSprinting   { get; set; }
        public bool  IsExhausted   => stamina < EXHAUSTED_THRESHOLD;
        public float StaminaRatio  => stamina / 100f;

        public enum WeightClass { Light, Medium, Heavy, Overloaded }
        public WeightClass CurrentWeightClass =>
            carriedWeightKg <= LIGHT_MAX  ? WeightClass.Light  :
            carriedWeightKg <= MEDIUM_MAX ? WeightClass.Medium :
            carriedWeightKg <= HEAVY_MAX  ? WeightClass.Heavy  : WeightClass.Overloaded;

        public float MovementSpeedMultiplier
        {
            get
            {
                float w = CurrentWeightClass == WeightClass.Light    ? 0f    :
                          CurrentWeightClass == WeightClass.Medium   ? 0.08f :
                          CurrentWeightClass == WeightClass.Heavy    ? 0.18f : 0.35f;
                float s = IsExhausted ? Mathf.Lerp(0.25f, 0f, StaminaRatio / (EXHAUSTED_THRESHOLD / 100f)) : 0f;
                return Mathf.Clamp01(1f - w - s);
            }
        }

        private SuppressionSystem _sup;

        private void Awake() => _sup = GetComponent<SuppressionSystem>();

        private void Update()
        {
            float drainMult = CurrentWeightClass == WeightClass.Light    ? DRAIN_LIGHT    :
                              CurrentWeightClass == WeightClass.Medium   ? DRAIN_MEDIUM   :
                              CurrentWeightClass == WeightClass.Heavy    ? DRAIN_HEAVY    : DRAIN_OVERLOAD;

            float recMult   = CurrentWeightClass == WeightClass.Light    ? REC_LIGHT    :
                              CurrentWeightClass == WeightClass.Medium   ? REC_MEDIUM   :
                              CurrentWeightClass == WeightClass.Heavy    ? REC_HEAVY    : REC_OVERLOAD;

            if (IsSprinting)
                stamina = Mathf.Clamp(stamina - baseSprintDrain * drainMult * Time.deltaTime, 0f, 100f);
            else
                stamina = Mathf.Clamp(stamina + baseRecovery * recMult * Time.deltaTime, 0f, 100f);

            if (IsExhausted && IsSprinting && _sup != null)
                _sup.AddSuppression(EXHAUSTION_SUP_RATE * Time.deltaTime);
        }

        public void SetWeight(float kg)    => carriedWeightKg = Mathf.Max(0f, kg);
        public void AddWeight(float kg)    => carriedWeightKg = Mathf.Max(0f, carriedWeightKg + kg);
        public void RemoveWeight(float kg) => carriedWeightKg = Mathf.Max(0f, carriedWeightKg - kg);
    }
}
