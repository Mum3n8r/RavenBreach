using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RavenbreachMod
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static Plugin Instance;
        // Active player move order protection â€” populated by TacticalMapSystem
        // Keyed by squad instance ID so multiple squads don't clobber each other.
        internal static readonly System.Collections.Generic.Dictionary<int, float> MoveOrderExpiries
            = new System.Collections.Generic.Dictionary<int, float>();
        internal static readonly System.Collections.Generic.Dictionary<int, float> MoveOrderArmTime
            = new System.Collections.Generic.Dictionary<int, float>();
        internal static readonly System.Collections.Generic.HashSet<int> ActiveMoveOrderBots
            = new System.Collections.Generic.HashSet<int>();
        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log      = Logger;

            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);

            TryPatch(typeof(WeaponShootPatch));
            TryPatch(typeof(WeaponSpreadPatch));
            TryPatch(typeof(WeaponShootSpreadPatch));
            TryPatch(typeof(RecoilSwayPatch));
            TryPatch(typeof(ExplosionSuppressionPatch));
            TryPatch(typeof(AiActorControllerParametersPatch));
            TryPatch(typeof(BotSwayPatch));
            TryPatch(typeof(BotReturnFirePatch));
            TryPatch(typeof(BotProneOrderBlockPatch));
            TryPatch(typeof(BotSuppressedTakingFirePatch));
            TryPatch(typeof(BotDeathCleanupPatch));
            TryPatch(typeof(BotDesyncPatch));
            TryPatch(typeof(BotDesyncDeathPatch));
            TryPatch(typeof(BotSprintStaggerPatch));
            TryPatch(typeof(BotHaltVariationPatch));
            TryPatch(typeof(BotStrafeStaggerPatch));
            TryPatch(typeof(BotSpeedVariationPatch));
            TryPatch(typeof(BotCoverStaggerPatch));
            TryPatch(typeof(BotScopeEngagementPatch));
            TryPatch(typeof(BotSuppressedCoverPatch));
            TryPatch(typeof(BotEngagementMemoryPatch));
            // TryPatch(typeof(BotSpawnMobilizePatch)); // disabled — causes running-in-place on maps with complex navmesh
            TryPatch(typeof(BotVehicleUtilizationPatch));
            TryPatch(typeof(BotEngagementDeathPatch));
            TryPatch(typeof(DownedBotKillPatch));
            TryPatch(typeof(DownedBotFinishPatch));
            TryPatch(typeof(DecalManagerStartPatch));
            TryPatch(typeof(SuppressVanillaBloodEffectPatch));
            TryPatch(typeof(SuppressBloodParticleSpherePatch));
            TryPatch(typeof(ActorDamagePatch));
            TryPatch(typeof(ActorKillPatch));
            TryPatch(typeof(ActorDamageInjuryPatch));
            TryPatch(typeof(ActorKillInjuryPatch));
            TryPatch(typeof(AnimatorScanPatch));
            TryPatch(typeof(RagdollVelocityInheritPatch));
            TryPatch(typeof(FpsStartRagdollNoCameraSwitch));
            TryPatch(typeof(SuppressBattlePlanShowPatch));
            TryPatch(typeof(SuppressStrategyUiPatch));
            TryPatch(typeof(SuppressLoadoutWhileMapOpenPatch));
            TryPatch(typeof(MapOpenCursorFreePatch));
            TryPatch(typeof(TacticalMapWeaponBlock));
            TryPatch(typeof(PlayerDeathScreenPatch));
            TryPatch(typeof(PlayerDeathRespawnFlagPatch));
            TryPatch(typeof(PlayerSpawnScreenPatch));
            TryPatch(typeof(HideDamageIndicatorPatch));
            TryPatch(typeof(BulletFlybySoundPatch));
            TryPatch(typeof(BallisticsPatch));
            TryPatch(typeof(BallisticsGravityTweak));
            TryPatch(typeof(BallisticsDamagePatch));
            TryPatch(typeof(HeadshotInstantKillPatch));
            TryPatch(typeof(AiActorLethalityPatch));
            TryPatch(typeof(BotCrouchReductionPatch));
            TryPatch(typeof(BotStrafeTimerPatch));
            TryPatch(typeof(BotIdleAimPatch));
            // TryPatch(typeof(HoldAfterObjectivePatch)); // disabled — fires PathDone which re-issues orders mid-move
            TryPatch(typeof(RB_FireDirectionPatch));
            TryPatch(typeof(RB_SuppressedPronePatch));
            TryPatch(typeof(RB_DirectionalCoverPatch));
            TryPatch(typeof(RB_TargetPriorityPatch));
            TryPatch(typeof(RB_WeaponLeadPatch));
            TryPatch(typeof(RB_CountermeasuresPatch));
            TryPatch(typeof(RB_WireGuidedAiPatch));
            TryPatch(typeof(RB_AiEnhancementDeathPatch));
            // PlayerMoveOrderGotoBlock obsolete — OverrideDefaultMovement is the engine-native equivalent
            // TryPatch(typeof(PlayerMoveOrderGotoBlock));
            TryPatch(typeof(BlockMinimapSpawnButtonRefresh));

            RavenbreachAssetLoader.Init();

            var overlayGO = new GameObject("RavenbreachOverlay");
            DontDestroyOnLoad(overlayGO);
            overlayGO.AddComponent<DebugOverlay>();
            overlayGO.AddComponent<SuppressionTracker>();
            overlayGO.AddComponent<SuppressionEffects>();
            overlayGO.AddComponent<SuppressionMechanics>();
            overlayGO.AddComponent<SuppressionAudio>();
            overlayGO.AddComponent<HearingStressSystem>();
            overlayGO.AddComponent<SoundPropagationSystem>();
            overlayGO.AddComponent<ProceduralSoundBank>();
            overlayGO.AddComponent<WallHitEffects>();
            overlayGO.AddComponent<SprintStumbleSystem>();
            overlayGO.AddComponent<BulletHoleManager>();
            overlayGO.AddComponent<AudioOcclusionSystem>();
            overlayGO.AddComponent<BleedingSystem>();
            overlayGO.AddComponent<InjurySystem>();
            overlayGO.AddComponent<BodyPartHUD>();
            overlayGO.AddComponent<HudSystem>();
            overlayGO.AddComponent<TacticalMapSystem>();
            overlayGO.AddComponent<BulletCrackSystem>();

            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} loaded.");
        }

        private void TryPatch(System.Type t)
        {
            try { _harmony.CreateClassProcessor(t).Patch(); }
            catch (System.Exception e) { Log.LogError("[Patch FAIL] " + t.Name + ": " + e.Message); }
        }

        private void OnDestroy() { _harmony?.UnpatchSelf(); }
    }
}



