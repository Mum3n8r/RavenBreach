using System.Collections.Generic;
using UnityEngine;

namespace RavenbreachMod
{
    public class SuppressionSystem : MonoBehaviour
    {
        public float SuppressionLevel => SuppressionManager.GetLevel(this);
        public int   Tier             => SuppressionManager.GetTier(this);
        public bool  IsPinned         => Tier >= 3;
        public bool  IsSuppressed     => Tier >= 2;
        public bool  IsInCover        { get; set; }

        public float decayRate  = 10f;
        public float decayDelay = 1.5f;

        public event System.Action<int, int> OnTierChanged;
        internal void FireTierChanged(int prev, int next) => OnTierChanged?.Invoke(prev, next);

        public void AddSuppression(float amount)    => SuppressionManager.Add(this, amount);
        public void ReduceSuppression(float amount) => SuppressionManager.Reduce(this, amount);

        private void Awake()     => SuppressionManager.Register(this);
        private void OnDestroy() => SuppressionManager.Unregister(this);
        // Update() removed — was burning Unity's update loop for nothing on every bot.
        // SuppressionManager.Tick() is driven by SuppressionTracker.Update() instead.
    }

    public static class SuppressionManager
    {
        private struct State
        {
            public float level;
            public float decayTimer;
            public int   prevTier;
        }

        private static readonly Dictionary<int, State>             _states  = new Dictionary<int, State>(64);
        private static readonly Dictionary<int, SuppressionSystem> _systems = new Dictionary<int, SuppressionSystem>(64);

        public static void Register(SuppressionSystem sys)
        {
            int id = sys.GetInstanceID();
            _systems[id] = sys;
            if (!_states.ContainsKey(id))
                _states[id] = new State { level = 0f, decayTimer = 0f, prevTier = 0 };
        }

        public static void Unregister(SuppressionSystem sys)
        {
            int id = sys.GetInstanceID();
            _systems.Remove(id);
            _states.Remove(id);
        }

        public static float GetLevel(SuppressionSystem sys)
        {
            _states.TryGetValue(sys.GetInstanceID(), out State s);
            return s.level;
        }

        public static int GetTier(SuppressionSystem sys)
        {
            _states.TryGetValue(sys.GetInstanceID(), out State s);
            return LevelToTier(s.level);
        }

        // tier 0: 0-20   unsuppressed
        // tier 1: 20-45  rattled
        // tier 2: 45-72  suppressed
        // tier 3: 72-100 pinned
        private static int LevelToTier(float level)
        {
            if (level < 20f) return 0;
            if (level < 45f) return 1;
            if (level < 72f) return 2;
            return 3;
        }

        public static void Add(SuppressionSystem sys, float amount)
        {
            int id = sys.GetInstanceID();
            _states.TryGetValue(id, out State s);
            float prev   = s.level;
            s.level      = Mathf.Clamp(s.level + amount, 0f, 100f);
            s.decayTimer = 0f;
            CheckTierChange(sys, id, ref s, LevelToTier(prev));
            _states[id]  = s;
        }

        public static void Reduce(SuppressionSystem sys, float amount)
        {
            int id = sys.GetInstanceID();
            _states.TryGetValue(id, out State s);
            float prev  = s.level;
            s.level     = Mathf.Clamp(s.level - amount, 0f, 100f);
            CheckTierChange(sys, id, ref s, LevelToTier(prev));
            _states[id] = s;
        }

        public static void Tick(float dt)
        {
            _tickKeys.Clear();
            foreach (var id in _states.Keys) _tickKeys.Add(id);

            for (int i = 0; i < _tickKeys.Count; i++)
            {
                int id = _tickKeys[i];
                if (!_systems.TryGetValue(id, out SuppressionSystem sys)) continue;

                State s = _states[id];
                if (s.level <= 0f) continue;

                s.decayTimer += dt;

                // decay delay is shorter in cover — reward getting out of the open
                float delay = sys.IsInCover ? sys.decayDelay * 0.8f : sys.decayDelay;
                if (s.decayTimer < delay) { _states[id] = s; continue; }

                float prevLevel = s.level;
                float speed     = sys.IsInCover ? 18f : 8f;
                s.level         = Mathf.Max(s.level - speed * dt, 0f);

                CheckTierChange(sys, id, ref s, LevelToTier(prevLevel));
                _states[id] = s;
            }
        }

        private static readonly List<int> _tickKeys = new List<int>(64);

        private static void CheckTierChange(SuppressionSystem sys, int id, ref State s, int prevTier)
        {
            int newTier = LevelToTier(s.level);
            if (newTier != s.prevTier)
            {
                sys.FireTierChanged(s.prevTier, newTier);
                s.prevTier = newTier;
            }
        }
    }
}
