using UnityEngine;
using UnityEngine.SceneManagement;
using System.Reflection;
using HarmonyLib;

namespace RavenbreachMod
{
    // manages SuppressionSystem components on player and nearby bots.
    // player found via FpsActorController.instance.
    // bots within BOT_SUPPRESSION_RANGE scanned every BOT_SCAN_INTERVAL seconds.
    public class SuppressionTracker : MonoBehaviour
    {
        public static SuppressionSystem PlayerSuppression { get; private set; }
        public static FpsActorController PlayerController  { get; private set; }
        public static int                PlayerTeam        { get; private set; } = -1;

        private const float POLL_INTERVAL         = 0.5f;
        private const float BOT_SCAN_INTERVAL     = 3.0f;
        private const float BOT_SUPPRESSION_RANGE = 100f;

        private float _pollTimer;
        private float _botScanTimer;

        private static SuppressionTracker _instance;

        // cached once — avoids reflection overhead on every scan
        private static readonly FieldInfo _parachuteDeployedField  = AccessTools.Field(typeof(Actor), "parachuteDeployed");
        private static readonly FieldInfo _isScheduledToSpawnField = AccessTools.Field(typeof(Actor), "isScheduledToSpawn");

        private void Awake()
        {
            if (_instance != null) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            PlayerSuppression = null;
            PlayerController  = null;
            PlayerTeam        = -1;
            _pollTimer        = 0f;
            _botScanTimer     = 0f;
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            // one loop drives all suppression decay instead of N MonoBehaviour Updates
            SuppressionManager.Tick(dt);

            _pollTimer -= dt;
            if (_pollTimer <= 0f)
            {
                _pollTimer = POLL_INTERVAL;
                TryFindPlayer();
            }

            _botScanTimer -= dt;
            if (_botScanTimer <= 0f)
            {
                _botScanTimer = BOT_SCAN_INTERVAL;
                EnsureBotsHaveSuppressionSystems();
            }
        }

        private static void TryFindPlayer()
        {
            var fps = FpsActorController.instance;
            if (fps == null) return;

            PlayerController = fps;

            var actor = fps.GetComponentInParent<Actor>();
            if (actor != null) PlayerTeam = actor.team;

            var sys = fps.GetComponentInParent<SuppressionSystem>();
            if (sys == null)
                sys = fps.gameObject.AddComponent<SuppressionSystem>();

            PlayerSuppression = sys;
        }

        private static void EnsureBotsHaveSuppressionSystems()
        {
            if (PlayerSuppression == null) return;
            Vector3 playerPos = PlayerSuppression.transform.position;

            var actorList = ActorManager.instance?.actors;
            if (actorList == null) return;

            foreach (var actor in actorList)
            {
                if (actor == null || !actor.aiControlled) continue;

                // skip parachuting/unspawned bots — AddComponent during a reinforcement drop
                // corrupts the spawn sequence
                try
                {
                    if (_parachuteDeployedField  != null && (bool)_parachuteDeployedField.GetValue(actor))  continue;
                    if (_isScheduledToSpawnField != null && (bool)_isScheduledToSpawnField.GetValue(actor)) continue;
                }
                catch { continue; }

                var bot = actor.controller as AiActorController;
                if (bot == null) continue;
                if (Vector3.Distance(actor.Position(), playerPos) > BOT_SUPPRESSION_RANGE) continue;
                if (bot.GetComponent<SuppressionSystem>() == null)
                    bot.gameObject.AddComponent<SuppressionSystem>();
            }
        }
    }
}
