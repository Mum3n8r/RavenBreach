using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RavenbreachMod
{
    [HarmonyPatch]
    public static class SuppressBattlePlanShowPatch
    {
        static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("BattlePlanUi"), "ShowCanvas");
        [HarmonyPrefix] public static bool Prefix() => false;
    }
    [HarmonyPatch]
    public static class SuppressBattlePlanHidePatch
    {
        static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("BattlePlanUi"), "HideCanvas");
        [HarmonyPrefix] public static bool Prefix() => false;
    }
    [HarmonyPatch]
    public static class SuppressStrategyUiPatch
    {
        static MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("StrategyUi"), "Show");
        [HarmonyPrefix] public static bool Prefix() => false;
    }

    [HarmonyPatch]
    public static class SuppressLoadoutWhileMapOpenPatch
    {
        static MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("SteelInput"), "GetButtonDown");
        [HarmonyPrefix]
        public static bool Prefix(object __0, ref bool __result)
        {
            if (TacticalMapSystem.Instance == null || !TacticalMapSystem.Instance.IsOpen) return true;
            try
            {
                int val = (int)(Enum.ToObject(__0.GetType(), __0));
                if (val == 16 || val == 34) { __result = false; return false; }
            }
            catch { }
            return true;
        }
    }

    [HarmonyPatch(typeof(FpsActorController), "IsCursorFree")]
    public static class MapOpenCursorFreePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (TacticalMapSystem.Instance != null && TacticalMapSystem.Instance.IsOpen)
                __result = true;
        }
    }

    [HarmonyPatch(typeof(Weapon), "Shoot")]
    public static class TacticalMapWeaponBlock
    {
        [HarmonyPrefix]
        public static bool Prefix()
            => TacticalMapSystem.Instance == null || !TacticalMapSystem.Instance.IsOpen;
    }

    public class TacticalMapSystem : MonoBehaviour
    {
        public static TacticalMapSystem Instance { get; private set; }
        public bool IsOpen => _phase != Phase.None;

        private enum Phase { None, InBattleDead, InBattleLive }
        private Phase _phase = Phase.None;

        private Vector3 _tPos, _tSize;
        private bool    _boundsReady = false;
        private Terrain _lastTerrain = null;
        private Texture _mapTex      = null;
        private bool    _mapReady    = false;

        private float   _mapZoom  = 1f;
        private Vector2 _mapPan   = Vector2.zero;
        private bool    _mapDrag  = false;
        private Vector2 _dragStart, _panAtDrag;

        // â”€â”€ Order state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // ── Order state (scrubbed – to be rebuilt) ──────────────────────────
        private enum OrderMode { Move, Attack, Defend, Suppress, Fallback, Hold, Flank, Regroup }
        private Squad     _selected    = null;
        private bool      _awaitDest   = false;
        private OrderMode _pendingMode = OrderMode.Move;
        private readonly Dictionary<Squad, Vector3>   _dests          = new Dictionary<Squad, Vector3>();
        private readonly Dictionary<Squad, OrderMode> _orders         = new Dictionary<Squad, OrderMode>();
        private readonly Dictionary<Squad, Vector3>   _flankWaypoints = new Dictionary<Squad, Vector3>();
        private readonly Dictionary<Squad, bool>  _interrupted    = new Dictionary<Squad, bool>();
        private readonly Dictionary<Squad, int>   _squadStrength  = new Dictionary<Squad, int>();
        private readonly HashSet<Squad>               _holdAfterObj   = new HashSet<Squad>();
        private float _lastBotCleanup = 0f;

        // === Watcher debug overlay ===
        private bool   _watcherEnabled      = false; // disabled for release
        private string _watcherLast         = "";
        private double _watcherLastOrderTime = 0;
        private readonly Queue<string> _watcherLog = new Queue<string>(64);

        private static MethodInfo _sqRegroup = null;
        private static MethodInfo _miDisableAltPath = null;
        private static FieldInfo  _fiIsCarryingOutOrder = null;
        private static FieldInfo  _fiRemainingReissues  = null;
        private static FieldInfo  _fiSeeker = null;
        private static FieldInfo  _fiTagPenalties = null;
        private static FieldInfo  _fiTraversableTags = null;
        private static bool _seekerReflTried = false;
        private static bool _orderReflTried = false;
        private float _lastArrivalCheck = 0f;

        private static void CacheOrderRefl()
        {
            if (_orderReflTried) return;
            _orderReflTried = true;
            _sqRegroup            = AccessTools.Method(typeof(Squad), "Regroup");
            _miDisableAltPath     = AccessTools.Method(typeof(AiActorController), "DisableAlternativePathPenalty");
            _fiIsCarryingOutOrder = AccessTools.Field(typeof(Squad), "isCarryingOutOrder");
            _fiRemainingReissues  = AccessTools.Field(typeof(Squad), "remainingMovementReissues");
            _fiSeeker             = AccessTools.Field(typeof(AiActorController), "seeker");
        }

        // Zero ALL A* seeker tag penalties and restore ALL traversable tags.
        // Returns a report of what was poisoned BEFORE the scrub — this names
        // the corrupted variable that warps paths into 1500-unit detours.
        private static string ScrubSeeker(AiActorController ai)
        {
            try
            {
                if (_fiSeeker == null) return "noSeekerField";
                var seeker = _fiSeeker.GetValue(ai);
                if (seeker == null) return "seekerNull";
                if (!_seekerReflTried)
                {
                    _seekerReflTried = true;
                    var st = seeker.GetType();
                    _fiTagPenalties    = AccessTools.Field(st, "tagPenalties");
                    _fiTraversableTags = AccessTools.Field(st, "traversableTags");
                }
                string report = "";
                var tp = _fiTagPenalties != null ? _fiTagPenalties.GetValue(seeker) as int[] : null;
                if (tp != null)
                {
                    for (int t = 0; t < tp.Length; t++)
                        if (tp[t] != 0) { report += "tag" + t + "=" + tp[t] + " "; tp[t] = 0; }
                }
                if (_fiTraversableTags != null)
                {
                    int trav = (int)_fiTraversableTags.GetValue(seeker);
                    if (trav != -1) { report += "trav=" + trav + " "; _fiTraversableTags.SetValue(seeker, -1); }
                }
                return report.Length == 0 ? "clean" : report.TrimEnd();
            }
            catch (Exception e) { return "scrubErr:" + e.GetType().Name; }
        }

        // VEHICLE-STYLE MOVEMENT — why vehicles always obeyed and infantry never did:
        // vehicle drivers don't run the infantry AiOrders/AiTrack redirect coroutines
        // and move via GotoExactDestination. Infantry are re-tasked by Goto(false)
        // redirects the instant they're unlocked. The engine-native fix is the same
        // lockout vehicles enjoy: OverrideDefaultMovement() makes the ENGINE reject
        // every Goto(false) redirect (IL: Goto body gate), then GotoExactDestination
        // (dest, isMovementOverride=TRUE) bypasses that same gate for OUR order.
        // Each bot is driven like a vehicle: exact coords, deaf to AI chatter.
        // ReleaseSquad restores vanilla AI on arrival/cancel.
        private void StampAndMove(Squad sq, Vector3 dest)
        {
            CacheOrderRefl();

            // Record squad strength at order time for contact-break threshold.
            int strength = 0;
            foreach (var m in sq.members) if (m?.actor != null && !m.actor.dead) strength++;
            _squadStrength[sq] = strength;
            _interrupted[sq]   = false;

            // Legacy GotoBlock stays inert (no bots armed) — but set the arm-time
            // marker so the node-dump diagnostics (PathCompleteLogger/BotStateMonitor)
            // know this squad is under an active player order.
            foreach (var ai in sq.aiMembers)
                if (ai != null) Plugin.ActiveMoveOrderBots.Remove(ai.GetInstanceID());
            Plugin.MoveOrderArmTime[sq.number]  = Time.time;
            Plugin.MoveOrderExpiries[sq.number] = Time.time + 180f;

            // Stamp the squad order so UpdateOrders aims at dest and never re-tasks.
            // (Its own MoveTo -> leader.Goto(segment,false) gets auto-rejected by the
            // override below — harmless by design.)
            if (sq.order != null)
            {
                sq.order.type = Order.OrderType.Move;
                sq.order.hasOverrideTargetPosition = true;
                sq.order.overrideTargetPosition = dest;
                sq.order.isIssuedByPlayer = true;
                sq.order.source = null;
                sq.lastReachedSpawnPoint = null;
                sq.allowRequestNewOrders = false;
                try { if (_fiIsCarryingOutOrder != null) _fiIsCarryingOutOrder.SetValue(sq, true); } catch { }
                try { if (_fiRemainingReissues  != null) _fiRemainingReissues.SetValue(sq, (byte)0); } catch { }
            }

            // Drive every bot like a vehicle.
            int n = sq.aiMembers.Count, i = 0, issued = 0;
            foreach (var ai in sq.aiMembers)
            {
                if (ai == null || ai.actor == null || ai.actor.dead) { i++; continue; }
                if (ai.actor.IsSeated() && !ai.actor.seat.IsDriverSeat()) { i++; continue; }
                try
                {
                    ai.LeaveCover();
                    ai.OverrideDefaultMovement();
                    ScrubSeeker(ai);
                    bool ok = ai.GotoExactDestination(dest + RingOffset(i, n), true);
                    if (ok) issued++;
                }
                catch { }
                i++;
            }

            Plugin.Log?.LogInfo("[VehMove] sq=" + sq.number + " dest=" + dest + " issued=" + issued + "/" + n);
        }

        // Deterministic formation ring so bots don't stack on the exact same point.
        private static Vector3 RingOffset(int i, int n)
        {
            if (i == 0 || n <= 1) return Vector3.zero;
            float ang = (i * 6.2831853f) / n;
            float r   = 2.5f + (i % 3);
            return new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
        }

        // Release the order lock + movement override so vanilla AI resumes.
        private void ReleaseSquad(Squad sq)
        {
            if (sq == null) return;
            try
            {
                sq.allowRequestNewOrders = true;
                try { if (_fiRemainingReissues != null) _fiRemainingReissues.SetValue(sq, (byte)2); } catch { }
                foreach (var ai in sq.aiMembers)
                {
                    if (ai == null) continue;
                    Plugin.ActiveMoveOrderBots.Remove(ai.GetInstanceID());
                    try { ai.ReleaseDefaultMovementOverride(); } catch { }
                }
                Plugin.MoveOrderExpiries.Remove(sq.number);
                Plugin.MoveOrderArmTime.Remove(sq.number);
                Plugin.Log?.LogInfo("[VehMove] Released sq=" + sq.number);
            }
            catch { }
        }

        // Auto-cancel on contact: leader dead OR squad dropped below 50% of order-time strength.
        // Releases movement override so bots snap back to vanilla AI (cover, fight, survive).
        private void CheckContactBreak(Squad sq)
        {
            if (sq == null || !_dests.ContainsKey(sq)) return;
            OrderMode om; _orders.TryGetValue(sq, out om);
            if (om == OrderMode.Hold || om == OrderMode.Suppress || om == OrderMode.Regroup) return;

            var ld = sq.Leader();
            bool leaderDead = ld == null || ld.actor == null || ld.actor.dead;

            int alive = 0;
            foreach (var m in sq.members) if (m?.actor != null && !m.actor.dead) alive++;
            int start; _squadStrength.TryGetValue(sq, out start);
            bool halfStrength = start > 0 && alive < start * 0.5f;

            if (leaderDead || halfStrength)
            {
                _interrupted[sq] = true;
                ReleaseSquad(sq);
                _flankWaypoints.Remove(sq);
                // Keep _dests + _orders so the map still shows the INTERRUPTED marker.
                Plugin.Log?.LogInfo("[ContactBreak] sq=" + sq.number
                    + (leaderDead ? " leaderDead" : " halfStrength alive=" + alive + "/" + start));
            }
        }

        private void IssueOrder(Squad sq, OrderMode mode, Vector3 dest)
        {
            if (sq == null) return;
            _dests[sq]  = dest;
            _orders[sq] = mode;
            _watcherLast = "ISSUEORDER:sq=" + sq.number + " mode=" + mode + " dest=" + dest;
            _watcherLog.Enqueue(_watcherLast);

            switch (mode)
            {
                case OrderMode.Move:
                    sq.engagementRule = Squad.EngagementRule.FireAtWill;
                    StampAndMove(sq, dest);
                    break;

                case OrderMode.Fallback:
                    sq.engagementRule = Squad.EngagementRule.HoldFire;
                    StampAndMove(sq, dest);
                    break;

                case OrderMode.Attack:
                    sq.engagementRule = Squad.EngagementRule.FireAtWill;
                    var attackDest = ClosestEnemySpawn(dest);
                    Vector3 finalDest = attackDest != null ? attackDest.transform.position : dest;
                    _dests[sq] = finalDest;
                    StampAndMove(sq, finalDest);
                    break;

                case OrderMode.Defend:
                    // Mirror vanilla SquadOrderDefend: move to nearest FRIENDLY spawn near click
                    sq.engagementRule = Squad.EngagementRule.OnlyAlerted;
                    var defendDest = ClosestFriendlySpawn(dest);
                    Vector3 defendPos = defendDest != null ? defendDest.transform.position : dest;
                    _dests[sq] = defendPos;
                    StampAndMove(sq, defendPos);
                    break;

                case OrderMode.Flank:
                {
                    sq.engagementRule = Squad.EngagementRule.FireAtWill;
                    var lp   = sq.Leader()?.actor?.Position() ?? dest;
                    var toT  = (dest - lp);
                    float dist = toT.magnitude;
                    toT = dist > 0.1f ? toT / dist : Vector3.forward;

                    // Pick a side randomly
                    float side = UnityEngine.Random.Range(0, 2) == 0 ? 1f : -1f;
                    var lat = Quaternion.AngleAxis(90f * side, Vector3.up) * toT;

                    // Fibonacci-spiral geometry:
                    // ctrl = wide lateral push (100% dist sideways, 20% forward)
                    //        this puts the apex at the flank, not the front
                    // end  = behind the objective (dest + full reverse of attack direction)
                    //        bots arrive from the back, not the side
                    Vector3 ctrl3 = lp + lat * dist * 1.0f + toT * dist * 0.2f;
                    Vector3 end3  = dest - toT * dist * 0.35f; // behind the obj

                    // Waypoint1 = lateral swing apex (bot physically passes through here)
                    // We shepherd them: ctrl3 first, then end3
                    Vector3[] pts = { ctrl3, end3 };
                    for (int pi = 0; pi < pts.Length; pi++)
                    {
                        RaycastHit wpHit;
                        if (Physics.Raycast(new Vector3(pts[pi].x, 800f, pts[pi].z), Vector3.down, out wpHit, 2000f,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                            pts[pi].y = wpHit.point.y;
                        else if (Terrain.activeTerrain != null)
                            pts[pi].y = Terrain.activeTerrain.SampleHeight(pts[pi]) + _tPos.y;
                    }
                    ctrl3 = pts[0]; end3 = pts[1];

                    // _flankWaypoints = lateral apex (drawn as arc ctrl, used as first bot waypoint)
                    // _dests          = final dest behind objective
                    _flankWaypoints[sq] = ctrl3;
                    _dests[sq] = end3;

                    // Drive to the lateral apex first — arrival loop chains to end3
                    StampAndMove(sq, ctrl3);
                    break;
                }

                case OrderMode.Hold:
                    sq.engagementRule = Squad.EngagementRule.OnlyAlerted;
                    CacheOrderRefl();
                    try { _sqRegroup?.Invoke(sq, null); } catch { }
                    break;

                case OrderMode.Suppress:
                    sq.engagementRule = Squad.EngagementRule.FireAtWill;
                    CacheOrderRefl();
                    try { _sqRegroup?.Invoke(sq, null); } catch { }
                    foreach (var ai in sq.aiMembers)
                    {
                        if (ai?.actor == null || ai.actor.dead) continue;
                        try { ai.LookAt(dest); } catch { }
                    }
                    break;

                case OrderMode.Regroup:
                    sq.engagementRule = Squad.EngagementRule.FireAtWill;
                    CacheOrderRefl();
                    try { _sqRegroup?.Invoke(sq, null); } catch { }
                    break;
            }
        }

        private void CancelOrder(Squad sq)
        {
            if (sq == null) return;
            ReleaseSquad(sq);
            _dests.Remove(sq);
            _orders.Remove(sq);
        }

        public void TryHoldAfterObjective(Squad sq) { }

        private SpawnPoint ClosestEnemySpawn(Vector3 pos)
        {
            int enemy = 1 - GameManager.PlayerTeam();
            SpawnPoint best = null; float bestD = float.MaxValue;
            if (ActorManager.instance?.spawnPoints == null) return null;
            foreach (var sp in ActorManager.instance.spawnPoints)
            {
                if (sp == null || sp.owner != enemy) continue;
                float d = Vector3.Distance(sp.transform.position, pos);
                if (d < bestD) { bestD = d; best = sp; }
            }
            return best;
        }

        private SpawnPoint ClosestFriendlySpawn(Vector3 pos)
        {
            int friendly = GameManager.PlayerTeam();
            SpawnPoint best = null; float bestD = float.MaxValue;
            if (ActorManager.instance?.spawnPoints == null) return null;
            foreach (var sp in ActorManager.instance.spawnPoints)
            {
                if (sp == null || sp.owner != friendly) continue;
                float d = Vector3.Distance(sp.transform.position, pos);
                if (d < bestD) { bestD = d; best = sp; }
            }
            return best;
        }

        private Texture2D _texWhite;
        private bool      _texReady = false;
        private Rect      _mapDrawRect;

        private GUIStyle _styleSm, _styleMd, _styleTitle;
        private bool     _stylesReady = false;

        private GameObject    _mapCanvas;
        private RectTransform _mapPanelRT;

        // MinimapUi reflection
        private static MethodInfo _pinToStrategyMethod    = null;
        private static MethodInfo _pinToIngameMethod      = null;
        private static FieldInfo  _minimapRTField         = null;
        private static FieldInfo  _muCanvasField          = null;
        private static FieldInfo  _actorBlipsImageField   = null;
        private static FieldInfo  _playerBlipsImageField  = null;
        private static FieldInfo  _spawnPointButtonsField = null;
        private static FieldInfo  _muStrategyParentField  = null;
        private static FieldInfo  _minimapField           = null;
        private static FieldInfo  _muVehicleBlipPrefabField = null;
        private static Sprite        _vehicleBlipSprite        = null;
        private static bool          _vehicleBlipReady         = false;
        private static bool          _reflDone                 = false;
        private static object        _muInstance               = null;

        // MinimapCamera reflection
        private static MethodInfo _mcGetTexture     = null;
        private static MethodInfo _mcWorldToNormPos = null;
        private static FieldInfo  _mcInstanceField  = null;
        private object            _minimapCamInstance = null;

        // Player cameras
        private Camera[]      _gameCams        = new Camera[0];
        private int[]         _cullMasks       = new int[0];
        private MonoBehaviour _backgroundCamMB = null;

        // Owned minimap RT
        private RectTransform _minimapRTOwned     = null;
        private Canvas        _minimapCanvasOwned = null;
        private Vector2 _origAnchorMin, _origAnchorMax, _origOffsetMin, _origOffsetMax, _origPivot;
        private Vector3 _origLocalScale;

        private bool _camBoundsReady = false;

        private static readonly Color C_BG         = new Color(0.03f, 0.05f, 0.03f, 0.98f);
        private static readonly Color C_BG_DARK    = new Color(0.02f, 0.03f, 0.02f, 1.00f);
        private static readonly Color C_ACCENT     = new Color(0.35f, 0.80f, 0.35f, 1.00f);
        private static readonly Color C_ACCENT_DIM = new Color(0.20f, 0.50f, 0.20f, 0.65f);
        private static readonly Color C_TEXT       = new Color(0.85f, 0.96f, 0.85f, 1.00f);
        private static readonly Color C_TEXT_DIM   = new Color(0.50f, 0.65f, 0.50f, 0.85f);
        private static readonly Color C_RED        = new Color(0.95f, 0.22f, 0.22f, 1.00f);
        private static readonly Color C_ORANGE     = new Color(0.95f, 0.58f, 0.10f, 1.00f);
        private static readonly Color C_BLUE       = new Color(0.28f, 0.65f, 1.00f, 1.00f);
        private static readonly Color C_BORDER     = new Color(0.22f, 0.45f, 0.22f, 0.45f);
        private static readonly Color C_BORDER_LIT = new Color(0.35f, 0.72f, 0.35f, 0.75f);
        private static readonly Color C_BORDER_DIM = new Color(0.22f, 0.40f, 0.22f, 0.30f);

        void Awake() { Instance = this; }

        void Start()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            CacheReflection();
            CreateMapCanvas();
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            DestroyCachedTextures();
            if (_mapCanvas != null) Destroy(_mapCanvas);
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _boundsReady = false; _mapReady = false; _mapTex = null; _lastTerrain = null;
            _minimapCamInstance = null; _muInstance = null;
            _camBoundsReady = false;
            _selected = null; _awaitDest = false;
            _dests.Clear(); _orders.Clear(); _flankWaypoints.Clear(); _holdAfterObj.Clear(); _lastSpotted.Clear();
            _interrupted.Clear(); _squadStrength.Clear();
            Plugin.ActiveMoveOrderBots.Clear();
            Plugin.MoveOrderExpiries.Clear();
            Plugin.MoveOrderArmTime.Clear();
        }

        private static void CacheReflection()
        {
            if (_reflDone) return;
            _reflDone = true;
            var muType = AccessTools.TypeByName("MinimapUi");
            if (muType != null)
            {
                _minimapField           = AccessTools.Field(muType, "minimap");
                _pinToStrategyMethod    = AccessTools.Method(muType, "PinToStrategyScreen");
                _pinToIngameMethod      = AccessTools.Method(muType, "PinToIngameScreen");
                _minimapRTField         = AccessTools.Field(muType, "minimap");
                _muCanvasField          = AccessTools.Field(muType, "canvas");
                _actorBlipsImageField   = AccessTools.Field(muType, "actorBlipsImage");
                _playerBlipsImageField  = AccessTools.Field(muType, "playerBlipsImage");
                _spawnPointButtonsField = AccessTools.Field(muType, "minimapSpawnPointButton");
                _muStrategyParentField  = AccessTools.Field(muType, "strategyParent");
                _muVehicleBlipPrefabField = AccessTools.Field(muType, "vehicleBlipPrefab");
            }
            var mcType = AccessTools.TypeByName("MinimapCamera");
            if (mcType != null)
            {
                _mcGetTexture     = AccessTools.Method(mcType, "GetTexture");
                _mcWorldToNormPos = AccessTools.Method(mcType, "WorldToNormalizedPosition");
                _mcInstanceField  = AccessTools.Field(mcType, "instance");
            }
        }

        private static object GetMUInstance()
        {
            if (_muInstance != null) return _muInstance;
            var muType = AccessTools.TypeByName("MinimapUi");
            if (muType == null) return null;
            _muInstance = AccessTools.Field(muType, "instance")?.GetValue(null);
            return _muInstance;
        }

        private object GetMinimapCameraInstance()
        {
            if (_minimapCamInstance != null) return _minimapCamInstance;
            try { _minimapCamInstance = _mcInstanceField?.GetValue(null); } catch { }
            return _minimapCamInstance;
        }

        private Vector2 _uvOrigin;
        private Vector2 _uvScale;
        private Vector3 _worldOrigin;

        private void CacheMinimapCamBounds()
        {
            _camBoundsReady = false;
            if (!_boundsReady) return;
            var inst = GetMinimapCameraInstance();
            if (inst == null || _mcWorldToNormPos == null) return;
            try
            {
                var p0 = new Vector3(_tPos.x, _tPos.y, _tPos.z);
                var p1 = new Vector3(_tPos.x + _tSize.x, _tPos.y, _tPos.z + _tSize.z);
                var uv0 = (Vector3)_mcWorldToNormPos.Invoke(inst, new object[] { p0 });
                var uv1 = (Vector3)_mcWorldToNormPos.Invoke(inst, new object[] { p1 });
                _uvOrigin    = new Vector2(uv0.x, uv0.y);
                _worldOrigin = p0;
                _uvScale     = new Vector2((uv1.x - uv0.x) / _tSize.x, (uv1.y - uv0.y) / _tSize.z);
                _camBoundsReady = true;
                Plugin.Log?.LogInfo(string.Format("[TactMap] UVMap origin=({0:F3},{1:F3}) scale=({2:F5},{3:F5})",
                    _uvOrigin.x, _uvOrigin.y, _uvScale.x, _uvScale.y));
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[TactMap] CacheMinimapCamBounds: " + ex.Message); }
        }

        private Texture TryGetMapTexture()
        {
            try
            {
                var inst = GetMinimapCameraInstance();
                if (inst != null && _mcGetTexture != null)
                { var tex = _mcGetTexture.Invoke(inst, null) as Texture; if (tex != null) return tex; }
            }
            catch { }
            if (_minimapField != null)
            {
                try
                {
                    var muType = AccessTools.TypeByName("MinimapUi");
                    var objs   = Resources.FindObjectsOfTypeAll(muType);
                    if (objs != null && objs.Length > 0)
                    { var ri = _minimapField.GetValue(objs[0]) as RawImage; if (ri?.texture != null) return ri.texture; }
                }
                catch { }
            }
            return null;
        }

        private void CreateMapCanvas()
        {
            _mapCanvas = new GameObject("RavenbreachMapCanvas");
            DontDestroyOnLoad(_mapCanvas);
            var c = _mapCanvas.AddComponent<Canvas>();
            c.renderMode   = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 50;
            _mapCanvas.AddComponent<GraphicRaycaster>();
            var panelGO = new GameObject("MapPanel");
            panelGO.transform.SetParent(_mapCanvas.transform, false);
            _mapPanelRT = panelGO.AddComponent<RectTransform>();
            _mapCanvas.SetActive(false);
        }

        private void SizeMapPanel()
        {
            if (_mapPanelRT == null) return;
            const float SIDE = 248f, HDR = 44f;
            float sw = Screen.width, sh = Screen.height;
            _mapPanelRT.anchorMin        = new Vector2(0f, 0f);
            _mapPanelRT.anchorMax        = new Vector2(0f, 0f);
            _mapPanelRT.pivot            = new Vector2(0f, 1f);
            _mapPanelRT.anchoredPosition = new Vector2(SIDE, sh - HDR);
            _mapPanelRT.sizeDelta        = new Vector2(sw - SIDE * 2f, sh - HDR);
        }

        private void HijackMinimapUi()
        {
            var mu = GetMUInstance();
            if (mu == null) { Plugin.Log?.LogWarning("[TactMap] MinimapUi instance null"); return; }
            try
            {
                var mb = mu as MonoBehaviour;
                if (mb != null) mb.enabled = false;
                // Extract vehicle blip sprite from the prefab
                if (!_vehicleBlipReady && _muVehicleBlipPrefabField != null)
                {
                    try
                    {
                        var prefab = _muVehicleBlipPrefabField.GetValue(mu) as GameObject;
                        if (prefab != null)
                        {
                            var img = prefab.GetComponentInChildren<UnityEngine.UI.Image>();
                            if (img != null && img.sprite != null)
                            { _vehicleBlipSprite = img.sprite; _vehicleBlipReady = true; }
                        }
                    }
                    catch { }
                }
                try { var sp = _muStrategyParentField?.GetValue(mu) as RectTransform; if (sp != null) sp.gameObject.SetActive(false); } catch { }
                _pinToStrategyMethod?.Invoke(mu, null);
                var rawImg = _minimapRTField?.GetValue(mu) as RawImage;
                if (rawImg == null) { Plugin.Log?.LogWarning("[TactMap] minimap RawImage null"); return; }
                _minimapRTOwned = rawImg.rectTransform;
                _origAnchorMin  = _minimapRTOwned.anchorMin;
                _origAnchorMax  = _minimapRTOwned.anchorMax;
                _origOffsetMin  = _minimapRTOwned.offsetMin;
                _origOffsetMax  = _minimapRTOwned.offsetMax;
                _origPivot      = _minimapRTOwned.pivot;
                _origLocalScale = _minimapRTOwned.localScale;
                _minimapCanvasOwned = _muCanvasField?.GetValue(mu) as Canvas;
                if (_minimapCanvasOwned != null) _minimapCanvasOwned.enabled = true;
                var actorBlips  = _actorBlipsImageField?.GetValue(mu)  as RawImage;
                var playerBlips = _playerBlipsImageField?.GetValue(mu) as RawImage;
                if (actorBlips  != null) actorBlips.gameObject.SetActive(false);
                if (playerBlips != null) playerBlips.gameObject.SetActive(false);
                try
                {
                    var spDict = _spawnPointButtonsField?.GetValue(mu);
                    if (spDict != null)
                    {
                        var vals = spDict.GetType().GetProperty("Values")?.GetValue(spDict) as System.Collections.IEnumerable;
                        if (vals != null)
                            foreach (var v in vals)
                            { var btn = v as UnityEngine.UI.Button; if (btn != null) btn.gameObject.SetActive(false); }
                    }
                }
                catch { }
                _minimapRTOwned.SetParent(_mapPanelRT, false);
                _minimapRTOwned.anchorMin  = Vector2.zero;
                _minimapRTOwned.anchorMax  = Vector2.one;
                _minimapRTOwned.offsetMin  = Vector2.zero;
                _minimapRTOwned.offsetMax  = Vector2.zero;
                _minimapRTOwned.localScale = Vector3.one;
                rawImg.uvRect = new Rect(0, 0, 1, 1);
                Plugin.Log?.LogInfo("[TactMap] MinimapUi hijacked");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[TactMap] HijackMinimapUi: " + ex.Message); }
        }

        private void ApplyUVRect()
        {
            if (_minimapRTOwned == null) return;
            var rawImg = _minimapRTOwned.GetComponent<RawImage>();
            if (rawImg == null) return;
            float uvW = 1f / _mapZoom;
            rawImg.uvRect = new Rect(0.5f + _mapPan.x - uvW * 0.5f, 0.5f - _mapPan.y - uvW * 0.5f, uvW, uvW);
        }

        private void ReleaseMinimapUi()
        {
            if (_minimapRTOwned == null) return;
            var mu = GetMUInstance();
            try
            {
                var actorBlips  = _actorBlipsImageField?.GetValue(mu)  as RawImage;
                var playerBlips = _playerBlipsImageField?.GetValue(mu) as RawImage;
                if (actorBlips  != null) actorBlips.gameObject.SetActive(true);
                if (playerBlips != null) playerBlips.gameObject.SetActive(true);
                try
                {
                    var spDict = _spawnPointButtonsField?.GetValue(mu);
                    if (spDict != null)
                    {
                        var vals = spDict.GetType().GetProperty("Values")?.GetValue(spDict) as System.Collections.IEnumerable;
                        if (vals != null)
                            foreach (var v in vals)
                            { var btn = v as UnityEngine.UI.Button; if (btn != null) btn.gameObject.SetActive(true); }
                    }
                }
                catch { }
                _minimapRTOwned.anchorMin  = _origAnchorMin;
                _minimapRTOwned.anchorMax  = _origAnchorMax;
                _minimapRTOwned.offsetMin  = _origOffsetMin;
                _minimapRTOwned.offsetMax  = _origOffsetMax;
                _minimapRTOwned.pivot      = _origPivot;
                _minimapRTOwned.localScale = _origLocalScale;
                var rawImg = _minimapRTOwned.GetComponent<RawImage>();
                if (rawImg != null) rawImg.uvRect = new Rect(0, 0, 1, 1);
                _pinToIngameMethod?.Invoke(mu, null);
                try { var sp = _muStrategyParentField?.GetValue(mu) as RectTransform; if (sp != null) sp.gameObject.SetActive(true); } catch { }
                if (_minimapCanvasOwned != null) _minimapCanvasOwned.enabled = false;
                var mb = mu as MonoBehaviour;
                if (mb != null) mb.enabled = true;
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[TactMap] ReleaseMinimapUi: " + ex.Message); }
            _minimapRTOwned = null; _minimapCanvasOwned = null;
        }

        private void DisablePlayerCam()
        {
            var fps = FpsActorController.instance;
            if (fps == null) return;
            var list = new List<Camera>();
            foreach (var fname in new[] { "fpCamera", "tpCamera", "fpViewModelCamera", "overrideCamera" })
            {
                try { var c = AccessTools.Field(typeof(FpsActorController), fname)?.GetValue(fps) as Camera; if (c != null && c.isActiveAndEnabled) list.Add(c); } catch { }
            }
            if (Camera.main != null && !list.Contains(Camera.main)) list.Add(Camera.main);
            try
            {
                var bcType = AccessTools.TypeByName("BackgroundCamera");
                if (bcType != null)
                {
                    var bcInst = AccessTools.Field(bcType, "instance")?.GetValue(null) as MonoBehaviour;
                    if (bcInst != null)
                    {
                        bcInst.enabled = false;
                        var bc = AccessTools.Field(bcType, "camera")?.GetValue(bcInst) as Camera;
                        if (bc != null) { if (!list.Contains(bc)) list.Add(bc); bc.enabled = false; }
                        _backgroundCamMB = bcInst;
                    }
                }
            }
            catch { }
            _gameCams  = list.ToArray();
            _cullMasks = new int[_gameCams.Length];
            for (int i = 0; i < _gameCams.Length; i++)
            { _cullMasks[i] = _gameCams[i].cullingMask; _gameCams[i].cullingMask = 0; }
        }

        private void RestorePlayerCam()
        {
            for (int i = 0; i < _gameCams.Length; i++)
                if (_gameCams[i] != null)
                { _gameCams[i].cullingMask = _cullMasks[i]; _gameCams[i].enabled = true; }
            _gameCams = new Camera[0]; _cullMasks = new int[0];
            if (_backgroundCamMB != null) { _backgroundCamMB.enabled = true; _backgroundCamMB = null; }
        }

        void Update()
        {
            if (!Application.isPlaying) return;

            if (GameManager.IsIngame())
            {
                Terrain t = Terrain.activeTerrain;
                if (t != null && t != _lastTerrain)
                { _lastTerrain = t; _tPos = t.transform.position; _tSize = t.terrainData.size; _boundsReady = true; }
                if (!_mapReady || _mapTex == null)
                { var tex = TryGetMapTexture(); if (tex != null) { _mapTex = tex; _mapReady = true; } }
            }

            if (!GameManager.IsIngame())
            { if (_phase != Phase.None) CloseMap(); _phase = Phase.None; return; }

            bool dead = IsPlayerDead();
            if (dead  && _phase == Phase.None)         { _phase = Phase.InBattleDead; OpenMap(); }
            if (!dead && _phase == Phase.InBattleDead) { _phase = Phase.None; CloseMap(); }
            if (!dead && Input.GetKeyDown(KeyCode.M))
            {
                if (_phase == Phase.InBattleLive) { _phase = Phase.None; CloseMap(); }
                else { _phase = Phase.InBattleLive; _selected = null; _awaitDest = false; OpenMap(); }
            }
            if (dead && _phase == Phase.InBattleLive) _phase = Phase.InBattleDead;
            if (_phase != Phase.None) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }

            // Prune stale entries from move order bot set every 5 seconds
            if (Time.time - _lastBotCleanup > 5f)
            {
                _lastBotCleanup = Time.time;
                var deadKeys = new System.Collections.Generic.List<int>();
                foreach (var kvp in Plugin.MoveOrderExpiries)
                    if (Time.time > kvp.Value) deadKeys.Add(kvp.Key);
                foreach (var k in deadKeys)
                {
                    foreach (var sq in ActorManager.GetSquadsOnTeam(GameManager.PlayerTeam()))
                    {
                        if (sq == null || sq.number != k) continue;
                        ReleaseSquad(sq);
                        _dests.Remove(sq);
                        _orders.Remove(sq);
                        _flankWaypoints.Remove(sq);
                        break;
                    }
                    Plugin.MoveOrderExpiries.Remove(k);
                }
            }

            // Arrival + shepherd loop
            // Arrival: leader (or any survivor) pathless within 15m of dest -> release.
            // Shepherd: any ordered bot pathless and >20m from dest -> re-issue
            // GotoExactDestination (handles async path-calc drops at order time).
            if (Time.time - _lastArrivalCheck > 1f)
            {
                _lastArrivalCheck = Time.time;
                List<Squad> released = null;
                foreach (var kvp in _dests)
                {
                    var sq = kvp.Key;
                    if (sq == null) continue;
                    OrderMode om;
                    _orders.TryGetValue(sq, out om);
                    if (om == OrderMode.Hold || om == OrderMode.Suppress || om == OrderMode.Regroup) continue;

                    // Contact-break check every second
                    CheckContactBreak(sq);
                    if (!_dests.ContainsKey(sq)) continue; // was just broken

                    // pick a reference member: leader if alive, else first survivor
                    ActorController refc = sq.Leader();
                    if (refc == null || refc.actor == null || refc.actor.dead)
                    {
                        refc = null;
                        foreach (var m in sq.aiMembers)
                            if (m != null && m.actor != null && !m.actor.dead) { refc = m; break; }
                    }
                    if (refc == null) continue;

                    bool arrived = !refc.HasPath()
                        && Vector3.Distance(refc.actor.Position(), kvp.Value) < 15f;

                    // Flank: apex arrival → chain to final dest behind objective
                    if (arrived && om == OrderMode.Flank && _flankWaypoints.ContainsKey(sq))
                    {
                        Vector3 finalDest = kvp.Value; // _dests = end3 (behind objective)
                        _flankWaypoints.Remove(sq);
                        // Update stamp dest to final position
                        if (sq.order != null)
                        {
                            sq.order.overrideTargetPosition = finalDest;
                        }
                        StampAndMove(sq, finalDest);
                        continue;
                    }

                    if (arrived && _holdAfterObj.Contains(sq))
                    {
                        ReleaseSquad(sq);
                        if (released == null) released = new List<Squad>();
                        released.Add(sq);
                        continue;
                    }

                    // shepherd stragglers back on task
                    int n = sq.aiMembers.Count, i = 0;
                    foreach (var ai in sq.aiMembers)
                    {
                        if (ai == null || ai.actor == null || ai.actor.dead) { i++; continue; }
                        if (ai.actor.IsSeated() && !ai.actor.seat.IsDriverSeat()) { i++; continue; }
                        if (!ai.HasPath() && Vector3.Distance(ai.actor.Position(), kvp.Value) > 20f)
                        {
                            ScrubSeeker(ai);
                            try { ai.GotoExactDestination(kvp.Value + RingOffset(i, n), true); } catch { }
                        }
                        i++;
                    }
                }
                if (released != null)
                    foreach (var sq in released) _orders.Remove(sq);
            }

            // Input-polled click handling â€” immune to IMGUI event consumption
            if (_phase == Phase.InBattleLive)
            {
                if (Input.GetMouseButtonDown(1))
                {
                    _awaitDest = false;
                }
                else if (Input.GetMouseButtonDown(0))
                {
                    Vector2 mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                    if (_mapDrawRect.Contains(mp))
                    {
                        if (_awaitDest && _selected != null)
                        { IssueOrder(_selected, _pendingMode, M2W(mp, _mapDrawRect)); _watcherLast="MAPCLICK:"+_pendingMode+" mp="+mp;_watcherLog.Enqueue(_watcherLast);_watcherLastOrderTime=Time.realtimeSinceStartup; _awaitDest = false; }
                        else
                        { TrySelectSquad(mp, _mapDrawRect); }
                    }
                }
            }
        }

        private void OpenMap()
        {
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            _mapZoom = 1f; _mapPan = Vector2.zero;
            EnsureTextures(); SizeMapPanel();
            if (_mapCanvas != null) _mapCanvas.SetActive(true);
            HijackMinimapUi();
            CacheMinimapCamBounds();
            CacheOrderRefl();
            DisablePlayerCam();
            if (!_mapReady || _mapTex == null)
            { var tex = TryGetMapTexture(); if (tex != null) { _mapTex = tex; _mapReady = true; } }
        }

        private void CloseMap()
        {
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
            ReleaseMinimapUi();
            if (_mapCanvas != null) _mapCanvas.SetActive(false);
            RestorePlayerCam();
        }

        private bool IsPlayerDead()
        {
            var sup = SuppressionTracker.PlayerSuppression;
            if (sup == null) return false;
            var actor = sup.GetComponentInParent<Actor>();
            return actor != null && actor.dead;
        }

        // â”€â”€ Coordinate conversion â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void WorldToUV(Vector3 w, out float u, out float v)
        {
            var inst = GetMinimapCameraInstance();
            if (inst != null && _mcWorldToNormPos != null)
            {
                try { var n = (Vector3)_mcWorldToNormPos.Invoke(inst, new object[] { w }); u = n.x; v = n.y; return; }
                catch { }
            }
            if (_camBoundsReady)
            {
                u = _uvOrigin.x + (w.x - _worldOrigin.x) * _uvScale.x;
                v = _uvOrigin.y + (w.z - _worldOrigin.z) * _uvScale.y;
                return;
            }
            u = _boundsReady ? (w.x - _tPos.x) / _tSize.x : 0f;
            v = _boundsReady ? (w.z - _tPos.z) / _tSize.z : 0f;
        }

        private Vector3 UVToWorld(float u, float v)
        {
            float wx, wz;
            if (_camBoundsReady)
            {
                wx = _worldOrigin.x + (u - _uvOrigin.x) / _uvScale.x;
                wz = _worldOrigin.z + (v - _uvOrigin.y) / _uvScale.y;
            }
            else if (_boundsReady)
            {
                wx = _tPos.x + u * _tSize.x;
                wz = _tPos.z + v * _tSize.z;
            }
            else return Vector3.zero;
            // Ground-truth Y via raycast from the sky. SampleHeight was returning a
            // constant (24.0) on this map, placing every click underground — A* then
            // snapped the path target to the tunnel/under-layer and routed every
            // squad through the distant layer entrance (the "Cancun" detour).
            float wy;
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(wx, 800f, wz), Vector3.down, out hit, 2000f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                wy = hit.point.y;
            else if (Terrain.activeTerrain != null)
                wy = Terrain.activeTerrain.SampleHeight(new Vector3(wx, 0, wz)) + _tPos.y;
            else wy = _tPos.y;
            return new Vector3(wx, wy, wz);
        }

        private Rect GetCurrentUVRect()
        {
            if (_minimapRTOwned != null)
            { var ri = _minimapRTOwned.GetComponent<RawImage>(); if (ri != null) return ri.uvRect; }
            return new Rect(0, 0, 1, 1);
        }

        private Vector2 W2M(Vector3 w, Rect _unused)
        {
            var r = _mapDrawRect; Rect uv = GetCurrentUVRect();
            float u, v; WorldToUV(w, out u, out v);
            return new Vector2(
                r.x + (u - uv.x) / uv.width  * r.width,
                r.y + (1f - (v - uv.y) / uv.height) * r.height);
        }

        private Vector3 M2W(Vector2 s, Rect _unused)
        {
            var r = _mapDrawRect; Rect uv = GetCurrentUVRect();
            float u = uv.x + (s.x - r.x) / r.width  * uv.width;
            float v = uv.y + (1f - (s.y - r.y) / r.height) * uv.height;
            return UVToWorld(u, v);
        }

        // â”€â”€ Drawing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        void OnGUI()
        {
            if (_phase == Phase.None) return;
            EnsureStyles(); EnsureTextures();
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (_phase == Phase.InBattleDead) DrawMap(false);
            else if (_phase == Phase.InBattleLive) DrawMap(true);
        }

        void DrawMap(bool isLive)
        {
            float sw = Screen.width, sh = Screen.height;
            const float SIDE = 248f, HDR = 44f;
            Rect mapRect = new Rect(SIDE, HDR, sw - SIDE * 2f, sh - HDR);
            if (_minimapRTOwned != null)
            {
                var corners = new Vector3[4];
                _minimapRTOwned.GetWorldCorners(corners);
                float left = corners[0].x, right = corners[2].x;
                float top = sh - corners[1].y, bottom = sh - corners[0].y;
                mapRect = new Rect(left, top, right - left, bottom - top);
            }
            _mapDrawRect = mapRect;

            HandleZoomPan(_mapDrawRect);
            DrawOverlays(_mapDrawRect);
            DrawSquadPanel(new Rect(0,         HDR, SIDE, sh - HDR));
            DrawOrderPanel(new Rect(sw - SIDE, HDR, SIDE, sh - HDR));
            DrawHeader(new Rect(0, 0, sw, HDR), isLive);
            if (_awaitDest) DrawCursorOverlay(_mapDrawRect);

            if (_watcherEnabled) DrawWatcherOverlay(sw, sh);
            if (!isLive && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
            { _phase = Phase.None; CloseMap(); Event.current.Use(); }
        }

        void DrawWatcherOverlay(float sw, float sh)
        {
            var bg = new Rect(sw * 0.25f, sh - 260f, sw * 0.5f, 240f);
            FillRect(bg, new Color(0,0,0,0.82f));
            DrawBorder(bg, new Color(0.3f,0.6f,0.3f,0.7f), 1f);
            float ly = bg.y + 4f, lx = bg.x + 8f, lw = bg.width - 16f;
            GUI.color = new Color(0.4f,1f,0.4f,0.9f);
            GUI.Label(new Rect(lx, ly, lw, 18), "=== WATCHER ===", _styleMd); ly += 20f;
            GUI.color = Color.white;
            string selStr = _selected == null ? "null" : "SQ." + _selected.number;
            string reflStr = "SCRUBBED";
            string sinceOrder = _watcherLastOrderTime > 0 ? ((Time.realtimeSinceStartup - _watcherLastOrderTime)).ToString("F1") + "s ago" : "never";
            GUI.Label(new Rect(lx, ly, lw, 16), "phase=" + _phase + "  sel=" + selStr + "  await=" + _awaitDest + "  pend=" + _pendingMode, _styleSm); ly += 16f;
            GUI.Label(new Rect(lx, ly, lw, 16), "mapRect=" + _mapDrawRect.ToString("F0"), _styleSm); ly += 16f;
            GUI.Label(new Rect(lx, ly, lw, 16), "mouse=" + Input.mousePosition.ToString("F0") + "  inMap=" + _mapDrawRect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)), _styleSm); ly += 16f;
            GUI.Label(new Rect(lx, ly, lw, 16), "refl=" + reflStr + "  lastOrder=" + sinceOrder, _styleSm); ly += 16f;
            GUI.Label(new Rect(lx, ly, lw, 16), "lastAction=" + _watcherLast, _styleSm); ly += 16f;
            GUI.Label(new Rect(lx, ly, lw, 16), "dests=" + _dests.Count + "  orders=" + _orders.Count, _styleSm); ly += 16f;
            // Show last 6 log entries
            GUI.color = new Color(0.7f,0.9f,0.7f,0.7f);
            var logArr = _watcherLog.ToArray();
            for (int i = Math.Max(0, logArr.Length - 6); i < logArr.Length; i++)
            {
                GUI.Label(new Rect(lx, ly, lw, 14), logArr[i], _styleSm); ly += 14f;
            }
            GUI.color = Color.white;
        }

        void DrawOverlays(Rect r)
        {
            DrawTacticalGrid(r);
            if (_boundsReady) { DrawSpawnPoints(r); DrawAllSquads(r); DrawMarkers(r); }
            DrawBorder(r, C_BORDER_LIT, 2f);
            DrawCornerBrackets(r, C_ACCENT, 20f, 2f);
            if (_mapZoom > 1.05f)
            {
                var zr = new Rect(r.x + 8, r.yMax - 22, 72, 16);
                FillRect(zr, new Color(0, 0, 0, 0.60f));
                GUI.color = C_ACCENT_DIM;
                GUI.Label(new Rect(zr.x + 4, zr.y, zr.width, zr.height), _mapZoom.ToString("F1") + "x", _styleSm);
                GUI.color = Color.white;
            }
        }

        void DrawHeader(Rect r, bool isLive)
        {
            FillRect(r, C_BG_DARK);
            FillRect(new Rect(r.x, r.yMax - 1, r.width, 1), C_BORDER_LIT);
            DrawTag(new Rect(r.x + 12, r.y + 7, 82, 30), isLive ? "TACTICAL" : "RESPAWN", isLive ? C_ACCENT : C_ORANGE);
            if (isLive && ActorManager.instance != null)
            {
                int pt = GameManager.PlayerTeam();
                CenterLabel(new Rect(r.x + 100, r.y, r.width - 200, r.height),
                    "BLUFOR  " + CountAlive(pt) + "  .  OPFOR  " + CountAlive(1 - pt), _styleTitle);
            }
            else if (!isLive) CenterLabel(r, "SELECT SPAWN  .  [ENTER] to deploy", _styleTitle);
            if (isLive)
            {
                GUI.color = C_TEXT_DIM;
                GUI.Label(new Rect(r.xMax - 290, r.y + 4, 282, r.height - 8),
                    "[M] close   scroll: zoom   mid-drag: pan   right-click: cancel", _styleSm);
                GUI.color = Color.white;
            }
        }

        void DrawSquadPanel(Rect p)
        {
            DrawPanel(p);
            DrawPanelHeader(new Rect(p.x, p.y, p.width, 36), "FRIENDLY SQUADS");
            float y = p.y + 42;
            float pulse = (Mathf.Sin(Time.time * 2.5f) + 1f) * 0.5f;

            foreach (var sq in ActorManager.GetSquadsOnTeam(GameManager.PlayerTeam()))
            {
                if (sq == null) continue;
                int alive = 0;
                foreach (var m in sq.members) if (m?.actor != null && !m.actor.dead) alive++;
                if (alive == 0) continue;

                bool sel = sq == _selected;
                _orders.TryGetValue(sq, out var om);
                const float RH = 50f;
                Rect row = new Rect(p.x + 6, y, p.width - 12, RH);
                FillRect(row, sel ? new Color(0.05f,0.18f,0.05f,0.92f) : new Color(0.04f,0.08f,0.04f,0.88f));
                FillRect(new Rect(row.x,row.y,3,RH), sel ? C_ACCENT : new Color(C_BORDER.r,C_BORDER.g,C_BORDER.b,0.6f));
                DrawBorder(row, sel ? C_BORDER_LIT : C_BORDER, 1f);
                DrawNATOUnit(new Rect(row.x+7,row.y+(RH-22f)*0.5f,32,20), sel ? C_ACCENT : C_BLUE, sq.number.ToString(), false);
                float tx = row.x + 50f;
                bool contact = sq.IsTakingFire(), combat = sq.IsInCombat();
                GUI.color = contact?new Color(1f,0.28f,0.28f,1f):combat?new Color(1f,0.72f,0.18f,1f):C_TEXT;
                bool sqInt = false; _interrupted.TryGetValue(sq, out sqInt);
                GUI.Label(new Rect(tx,row.y+7,row.width-54,18), "SQUAD "+sq.number+"  .  "+alive+" UP"+(contact?" CONTACT":combat?" COMBAT":""), _styleMd);
                GUI.color = sqInt ? C_ORANGE : (sel ? C_ACCENT : C_TEXT_DIM);
                GUI.Label(new Rect(tx,row.y+27,row.width-54,16), sqInt ? "INTERRUPTED" : om.ToString().ToUpper(), _styleSm);
                GUI.color = Color.white;
                if (GUI.Button(row, GUIContent.none, GUIStyle.none))
                { _selected = sq == _selected ? null : sq; _awaitDest = false; }
                y += RH + 5;

                if (sel && sq.aiMembers != null)
                {
                    const float MH=44f, SILO=20f;
                    float mw = p.width - 12f;
                    foreach (var ai in sq.aiMembers)
                    {
                        if (ai?.actor == null || y > p.y + p.height - 10) continue;
                        Actor actor = ai.actor;
                        bool  dead  = actor.dead;
                        float hp    = dead ? 0f : Mathf.Clamp01(actor.health / 100f);
                        Rect mr = new Rect(p.x+6,y,mw,MH);
                        FillRect(mr, new Color(0.03f,0.07f,0.03f,0.90f));
                        FillRect(new Rect(mr.x,mr.y,2,MH), dead?new Color(0.6f,0.1f,0.1f,0.8f):hp<0.3f?new Color(0.9f,0.2f,0.1f,0.8f):new Color(0.15f,0.35f,0.15f,0.6f));
                        DrawBorder(mr, new Color(0.15f,0.25f,0.15f,0.4f), 1f);
                        GUI.color = dead?new Color(0.4f,0.4f,0.4f,0.6f):C_TEXT;
                        GUI.Label(new Rect(mr.x+8,mr.y+3,mw-SILO-16,14), actor.name, _styleSm);
                        string badge = dead?"KIA":hp<0.2f?"CRIT":actor.IsSeated()?"VEH":"";
                        if (badge.Length>0) { GUI.color=(dead||badge=="CRIT")?C_RED:C_BLUE; GUI.Label(new Rect(mr.x+mw-SILO-36,mr.y+3,32,14),badge,_styleSm); }
                        Rect barBg = new Rect(mr.x+8,mr.y+MH*0.52f,mw-SILO-18,5f);
                        FillRect(barBg, new Color(0.12f,0.12f,0.12f,0.85f));
                        if (!dead&&hp>0f) FillRect(new Rect(barBg.x,barBg.y,barBg.width*hp,barBg.height),
                            hp>0.6f?new Color(0.25f,0.78f,0.25f,0.9f):hp>0.3f?new Color(0.9f,0.75f,0.15f,0.9f):new Color(0.9f,0.18f,0.18f,0.9f));
                        DrawMiniBodySilhouette(actor,mr.x+mw-SILO-4,mr.y+1,SILO,MH-2,pulse);
                        GUI.color = Color.white;
                        y += MH + 2;
                    }
                }
                if (y > p.y + p.height - 20) break;
            }
        }

        private void DrawMiniBodySilhouette(Actor actor, float sx, float sy, float sw, float sh, float pulse)
        {
            var s = InjurySystem.GetState(actor);
            float scale = sh / 124f, cx = sx + sw * 0.5f;
            DrawSilPart(cx-8*scale, sy,           16*scale,16*scale, s==null?0:s.headDamage,    BleedingSystem.IsActorBleeding(actor,BodyPart.Head),    pulse);
            DrawSilPart(cx-4*scale, sy+17*scale,   8*scale, 6*scale, s==null?0:s.neckDamage,    BleedingSystem.IsActorBleeding(actor,BodyPart.Neck),    pulse);
            DrawSilPart(cx-11*scale,sy+24*scale,  22*scale,18*scale, s==null?0:s.chestDamage,   BleedingSystem.IsActorBleeding(actor,BodyPart.Chest),   pulse);
            DrawSilPart(cx-9*scale, sy+43*scale,  18*scale,14*scale, s==null?0:s.abdomenDamage, BleedingSystem.IsActorBleeding(actor,BodyPart.Abdomen), pulse);
            DrawSilPart(cx+12*scale,sy+24*scale,   9*scale,28*scale, s==null?0:s.leftArmDamage, BleedingSystem.IsActorBleeding(actor,BodyPart.LeftArm), pulse);
            DrawSilPart(cx-21*scale,sy+24*scale,   9*scale,28*scale, s==null?0:s.rightArmDamage,BleedingSystem.IsActorBleeding(actor,BodyPart.RightArm),pulse);
            DrawSilPart(cx+2*scale, sy+58*scale,  10*scale,40*scale, s==null?0:s.leftLegDamage, BleedingSystem.IsActorBleeding(actor,BodyPart.LeftLeg), pulse);
            DrawSilPart(cx-12*scale,sy+58*scale,  10*scale,40*scale, s==null?0:s.rightLegDamage,BleedingSystem.IsActorBleeding(actor,BodyPart.RightLeg),pulse);
        }

        private void DrawSilPart(float x, float y, float w, float h, float dmg, bool bleeding, float pulse)
        {
            if (w<1f||h<1f) return;
            float sev=Mathf.Clamp01(dmg/80f);
            Color c;
            if      (sev<0.04f) c=new Color(1,1,1,0.10f);
            else if (sev<0.35f) c=Color.Lerp(new Color(1,1,1,0.12f),    new Color(1,0.9f,0,0.50f),     sev/0.35f);
            else if (sev<0.70f) c=Color.Lerp(new Color(1,0.9f,0,0.50f), new Color(1,0.35f,0,0.65f),    (sev-0.35f)/0.35f);
            else                c=Color.Lerp(new Color(1,0.35f,0,0.65f), new Color(1,0.02f,0.02f,0.82f),(sev-0.70f)/0.30f);
            if (bleeding) c=Color.Lerp(c,new Color(1f,0.03f,0.03f,0.90f),0.35f+pulse*0.45f);
            GUI.color=c; GUI.DrawTexture(new Rect(x,y,w,h),_texWhite); GUI.color=Color.white;
        }

        void DrawOrderPanel(Rect p)
        {
            DrawPanel(p);
            DrawPanelHeader(new Rect(p.x,p.y,p.width,36),"ORDERS");
            if (_selected==null)
            { GUI.color=C_TEXT_DIM; GUI.Label(new Rect(p.x+14,p.y+52,p.width-28,60),"Select a squad\nfrom the list or\nclick on the map.",_styleSm); GUI.color=Color.white; return; }

            int alive=0; foreach(var m in _selected.members) if(m?.actor!=null&&!m.actor.dead) alive++;
            var pill=new Rect(p.x+8,p.y+42,p.width-16,30);
            FillRect(pill,new Color(0.06f,0.16f,0.06f,0.85f)); FillRect(new Rect(pill.x,pill.y,3,pill.height),C_ACCENT);
            DrawBorder(pill,C_BORDER_LIT,1f); GUI.color=C_ACCENT;
            GUI.Label(new Rect(pill.x+10,pill.y+7,pill.width-16,18),"SQ."+_selected.number+"  .  "+alive+" EFFECTIVE",_styleMd);
            GUI.color=Color.white;

            float y=p.y+80, bw=p.width-16, x=p.x+8;
            const float BH=32f, GAP=3f;

            DrawSectionLabel(ref y,x,bw,"MOVEMENT");
            bool isVehicleSquad = _selected.Leader()?.actor?.IsSeated() ?? false;
            OBtn(ref y,x,bw,BH,GAP,"  MOVE TO",  OrderMode.Move,    C_BLUE,                       new Color(0.15f,0.55f,0.15f));
            OBtn(ref y,x,bw,BH,GAP,"  FALLBACK", OrderMode.Fallback,new Color(0.14f,0.20f,0.40f), new Color(0.20f,0.40f,0.80f));
            if (!isVehicleSquad)
                OBtn(ref y,x,bw,BH,GAP,"  FLANK",    OrderMode.Flank,   new Color(0.08f,0.20f,0.16f), new Color(0.14f,0.48f,0.34f));
            DrawSectionLabel(ref y,x,bw,"COMBAT");
            OBtn(ref y,x,bw,BH,GAP,"  ATTACK",   OrderMode.Attack,  new Color(0.30f,0.05f,0.05f), C_RED);
            OBtn(ref y,x,bw,BH,GAP,"  DEFEND",   OrderMode.Defend,  new Color(0.05f,0.15f,0.30f), C_BLUE);
            if (!isVehicleSquad)
                OBtn(ref y,x,bw,BH,GAP,"  SUPPRESS", OrderMode.Suppress,new Color(0.28f,0.14f,0.03f), C_ORANGE);

            DrawSectionLabel(ref y,x,bw,"POSTURE");
            bool holding=_selected.engagementRule==Squad.EngagementRule.HoldFire;
            var hRect=new Rect(x,y,bw,BH);
            FillRect(hRect,holding?new Color(0.38f,0.22f,0.03f,0.9f):new Color(0.10f,0.10f,0.03f,0.85f));
            FillRect(new Rect(hRect.x,hRect.y,3,BH),holding?C_ORANGE:C_BORDER_DIM);
            DrawBorder(hRect,holding?C_ORANGE:C_BORDER,1f);
            if(GUI.Button(hRect,GUIContent.none,GUIStyle.none))
            { _selected.engagementRule=holding?(Squad.EngagementRule)0:Squad.EngagementRule.HoldFire; _orders[_selected]=OrderMode.Hold; }
            GUI.color=holding?C_ORANGE:C_TEXT;
            GUI.Label(new Rect(hRect.x+10,hRect.y+8,bw-16,18),holding?"O  HOLD FIRE  [ACTIVE]":"O  HOLD FIRE",_styleMd);
            GUI.color=Color.white; y+=BH+GAP;

            // Hold after objective toggle
            bool holdAfter = !_holdAfterObj.Contains(_selected); // default ON — matches engine default
            DrawToggleBtn(ref y,x,bw,BH,GAP,"  HOLD ON ARRIVAL",holdAfter,C_ACCENT_DIM,
                ()=>{ if(holdAfter) _holdAfterObj.Add(_selected); else _holdAfterObj.Remove(_selected); });

            // Cancel in-progress order
            bool hasActiveOrder = _dests.ContainsKey(_selected);
            if (hasActiveOrder)
            {
                y += 4;
                DrawActionBtn(new Rect(x,y,bw,BH),"  CANCEL ORDER",new Color(0.32f,0.05f,0.05f,0.9f),C_RED,
                    ()=>{ CancelOrder(_selected); });
                y += BH + GAP;
            }

            DrawSectionLabel(ref y,x,bw,"MULTI-SQUAD");
            bool regroupP=_awaitDest&&_pendingMode==OrderMode.Regroup;
            DrawToggleBtn(ref y,x,bw,BH,GAP,"  REGROUP ALL",regroupP,C_ACCENT,
                ()=>{ _awaitDest=!regroupP; _pendingMode=OrderMode.Regroup; });

            // Exit vehicle — eject all bots in selected squad from any vehicle
            bool inVeh = _selected != null && _selected.aiMembers != null &&
                _selected.aiMembers.Exists(ai => ai?.actor != null && ai.actor.IsSeated());
            if (inVeh)
            {
                DrawActionBtn(new Rect(x,y,bw,BH),"  EXIT VEHICLE",new Color(0.18f,0.18f,0.04f,0.9f),C_ORANGE,
                    ()=>{
                        foreach (var ai in _selected.aiMembers)
                            if (ai?.actor != null && ai.actor.IsSeated())
                                try { ai.LeaveVehicle(false); } catch { }
                    });
                y += BH + GAP;
            }

            // Find nearest vehicle — use vanilla PlayerOrderEnterVehicle
            DrawActionBtn(new Rect(x,y,bw,BH),"  FIND VEHICLE",new Color(0.04f,0.14f,0.18f,0.9f),C_BLUE,
                ()=>{
                    if (_selected == null) return;
                    var ld = _selected.Leader()?.actor;
                    if (ld == null) return;
                    Vehicle bestVeh = null; float bestD = float.MaxValue;
                    foreach (var v in GameObject.FindObjectsOfType<Vehicle>())
                    {
                        if (v == null || v.dead) continue;
                        bool occupied = false;
                        foreach (var s in v.seats) if (s != null && s.IsOccupied()) { occupied = true; break; }
                        if (occupied) continue;
                        float d = Vector3.Distance(v.transform.position, ld.Position());
                        if (d < bestD) { bestD = d; bestVeh = v; }
                    }
                    if (bestVeh != null)
                    {
                        try { _selected.PlayerOrderEnterVehicle(bestVeh); }
                        catch { IssueOrder(_selected, OrderMode.Move, bestVeh.transform.position); }
                    }
                });
            y += BH + GAP;

            if (_awaitDest)
            {
                y+=8; FillRect(new Rect(x,y,bw,1),C_BORDER); y+=5;
                GUI.color=C_TEXT_DIM;
                GUI.Label(new Rect(x+4,y,bw-8,28),
                    _pendingMode==OrderMode.Regroup?"Click map to set\nregroup point.":"Click map to place\ndestination.",_styleSm);
                GUI.color=Color.white; y+=34;
                DrawActionBtn(new Rect(x,y,bw,28),"  CANCEL",new Color(0.32f,0.05f,0.05f),C_RED,
                    ()=>{ _awaitDest=false; });
            }
        }

        void DrawAllSquads(Rect r)
        {
            int pt = GameManager.PlayerTeam();
            DrawSpottedEnemyActors(r, 1 - pt);
            Squad playerSquad = null;
            try { playerSquad = LocalPlayer.squad; } catch { }
            foreach (var sq in ActorManager.GetSquadsOnTeam(pt))
            { if (sq!=null) DrawSquadOnMap(sq,r,sq==_selected,false,sq==playerSquad); }
        }

        private readonly Dictionary<int, float> _lastSpotted = new Dictionary<int, float>();
        private const float SPOT_FADE_DURATION = 30f;
        private const float SPOT_MAX_RANGE = 800f; // only show enemies spotted within this range

        void DrawSpottedEnemyActors(Rect r, int enemyTeam)
        {
            if (ActorManager.instance?.actors == null) return;
            var actorDataList = ActorManager.instance.actorData;
            float now = Time.time;
            var playerPos = LocalPlayer.actor?.Position() ?? Vector3.zero;
            foreach (var actor in ActorManager.instance.actors)
            {
                if (actor==null||actor.dead||actor.team!=enemyTeam) continue;
                // Range gate: only track enemies within SPOT_MAX_RANGE of player
                if (Vector3.Distance(actor.Position(), playerPos) > SPOT_MAX_RANGE) continue;
                int id = actor.GetInstanceID();
                try
                {
                    int idx=actor.actorIndex;
                    if (actorDataList!=null&&idx>=0&&idx<actorDataList.Count&&actorDataList[idx].visibleOnMinimap)
                        _lastSpotted[id]=now;
                }
                catch { }
                float lastSeen;
                if (!_lastSpotted.TryGetValue(id,out lastSeen)) continue;
                float age=now-lastSeen;
                if (age>SPOT_FADE_DURATION){_lastSpotted.Remove(id);continue;}
                Vector2 pos=W2M(actor.Position(),r);
                if(pos.x<r.x||pos.x>r.xMax||pos.y<r.y||pos.y>r.yMax) continue;
                DrawDot(pos,new Color(C_RED.r,C_RED.g,C_RED.b,Mathf.Lerp(0.85f,0f,age/SPOT_FADE_DURATION)),6f);
            }
            var toRemove=new List<int>();
            foreach(var kvp in _lastSpotted) if(now-kvp.Value>SPOT_FADE_DURATION) toRemove.Add(kvp.Key);
            foreach(var id in toRemove) _lastSpotted.Remove(id);
        }

        void DrawSquadOnMap(Squad sq, Rect r, bool sel, bool enemy, bool isPlayerSquad=false)
        {
            var ld=sq.Leader(); if(ld?.actor==null||ld.actor.dead) return;
            Vector2 lp=W2M(ld.actor.Position(),r);
            if(lp.x<r.x-30||lp.x>r.xMax+30||lp.y<r.y-30||lp.y>r.yMax+30) return;
            Color col=enemy?C_RED:isPlayerSquad?new Color(0.20f,1.0f,0.20f,1.0f):sel?C_ACCENT:C_BLUE;

            if (!enemy&&_dests.TryGetValue(sq,out Vector3 dw))
            {
                var dp=W2M(dw,r);
                _orders.TryGetValue(sq,out var om);
                Color lc=om==OrderMode.Attack?C_RED:om==OrderMode.Suppress?C_ORANGE:om==OrderMode.Fallback?C_BLUE:col;
                if (om==OrderMode.Flank&&_flankWaypoints.TryGetValue(sq,out Vector3 wp))
                {
                    // wp  = lateral apex (ctrl point + first bot waypoint)
                    // dp  = end3 (behind the objective, actual bot destination)
                    // Draw Bezier arc: squad → ctrl(wp) → end(dp)
                    // This produces the fibonacci-style spiral wrapping to the back
                    var ctrl2 = W2M(wp, r);
                    DrawBezierArc(lp, dp, ctrl2, new Color(lc.r,lc.g,lc.b,0.75f), 28, 6f, 4f);
                    // X at the actual arrival point (behind obj)
                    DrawX(dp, new Color(lc.r,lc.g,lc.b,0.90f), 6f);
                    // Faint dot at lateral apex so player can read the sweep direction
                    DrawDot(ctrl2, new Color(lc.r,lc.g,lc.b,0.40f), 4f);
                }
                else
                {
                    DrawDashedLine(lp,dp,new Color(lc.r,lc.g,lc.b,0.65f),7f,4f);
                    DrawX(dp,new Color(lc.r,lc.g,lc.b,0.90f),6f);
                }
            }
            foreach(var m in sq.members)
            { if(m==ld||m?.actor==null||m.actor.dead) continue; var mp=W2M(m.actor.Position(),r); if(mp.x<r.x||mp.x>r.xMax||mp.y<r.y||mp.y>r.yMax) continue; DrawDot(mp,new Color(col.r,col.g,col.b,0.55f),4f); }

            float w=sel?34f:26f, h=sel?22f:16f;
            if (ld.actor.IsSeated())
            {
                // Vehicle icon â€” same color as NATO unit would be
                float vsz = sel ? 28f : 22f;
                DrawVehicleBlip(lp, col, vsz);
            }
            else
            {
                DrawNATOUnit(new Rect(lp.x-w*0.5f,lp.y-h*0.5f,w,h),col,sq.number.ToString(),enemy);
            }
            if (!enemy)
            {
                _orders.TryGetValue(sq,out var co);
                string tag=co==OrderMode.Move?"MOV":co==OrderMode.Attack?"ATK":co==OrderMode.Defend?"DEF":co==OrderMode.Suppress?"SUP"
                          :co==OrderMode.Fallback?"FBK":co==OrderMode.Hold?"HLD"
                          :co==OrderMode.Flank?"FLK":co==OrderMode.Regroup?"RGP":"";
                bool isInt = false; _interrupted.TryGetValue(sq, out isInt);
                if (isInt) tag = "INT";
                if(tag!="")
                {
                    Color tagCol = isInt ? C_ORANGE : col;
                    var tr=new Rect(lp.x+w*0.5f+3,lp.y-9,30,14);
                    FillRect(tr,new Color(0,0,0,0.72f));
                    DrawBorder(tr,new Color(tagCol.r,tagCol.g,tagCol.b,0.45f),1f);
                    GUI.color=tagCol;
                    GUI.Label(new Rect(tr.x+2,tr.y,tr.width,tr.height),tag,_styleSm);
                    GUI.color=Color.white;
                    if (isInt) { float fl=(Mathf.Sin(Time.time*4f)+1f)*0.5f; DrawBorder(tr,new Color(C_ORANGE.r,C_ORANGE.g,C_ORANGE.b,fl*0.8f),1f); }
                }
            }
        }

        void DrawNATOUnit(Rect r, Color col, string label, bool enemy)
        {
            FillRect(r,new Color(col.r*0.15f,col.g*0.15f,col.b*0.15f,0.88f));
            DrawBorder(r,col,1.5f);
            DrawLineRaw(new Vector2(r.x+2,r.y+2),new Vector2(r.xMax-2,r.yMax-2),new Color(col.r,col.g,col.b,0.92f));
            DrawLineRaw(new Vector2(r.xMax-2,r.y+2),new Vector2(r.x+2,r.yMax-2),new Color(col.r,col.g,col.b,0.92f));
            if(!string.IsNullOrEmpty(label))
            { GUI.color=col; var lr=new GUIStyle(_styleSm){alignment=TextAnchor.UpperCenter}; GUI.Label(new Rect(r.x-4,r.yMax+1,r.width+8,13),label,lr); GUI.color=Color.white; }
        }

        void DrawSpawnPoints(Rect r)
        {
            if (ActorManager.instance?.spawnPoints==null) return;
            int pt=GameManager.PlayerTeam();
            foreach(var sp in ActorManager.instance.spawnPoints)
            {
                if(sp==null) continue;
                Vector2 p=W2M(sp.transform.position,r);
                if(p.x<r.x||p.x>r.xMax||p.y<r.y||p.y>r.yMax) continue;
                DrawDiamond(p,sp.owner==pt?C_BLUE:sp.owner==1-pt?C_RED:new Color(0.92f,0.86f,0.24f,0.92f),10f);
            }
        }

        void DrawMarkers(Rect r)
        {
            for(int i=0;i<PlanningMarkers.All.Count;i++)
            {
                var m=PlanningMarkers.All[i];
                var sp=W2M(PlanningMarkers.NormToWorld(m.normPos,_tPos,_tSize),r);
                if(sp.x<r.x||sp.x>r.xMax||sp.y<r.y||sp.y>r.yMax) continue;
                DrawDiamond(sp,PlanningMarkers.Colors[m.colorIdx],12f);
                if(!string.IsNullOrEmpty(m.label))
                { var tr=new Rect(sp.x+14,sp.y-7,70,14); FillRect(tr,new Color(0,0,0,0.65f)); GUI.color=PlanningMarkers.Colors[m.colorIdx]; GUI.Label(new Rect(tr.x+2,tr.y,tr.width,tr.height),m.label,_styleSm); GUI.color=Color.white; }
            }
        }

        void DrawTacticalGrid(Rect r)
        {
            int lines=Mathf.RoundToInt(_mapZoom*8);
            GUI.color=new Color(0.24f,0.40f,0.24f,0.12f);
            for(int i=0;i<=lines;i++)
            { GUI.DrawTexture(new Rect(r.x+r.width*i/lines,r.y,1,r.height),_texWhite); GUI.DrawTexture(new Rect(r.x,r.y+r.height*i/lines,r.width,1),_texWhite); }
            GUI.color=Color.white;
        }

        void HandleZoomPan(Rect mapRect)
        {
            var e=Event.current;
            if (e.type==EventType.ScrollWheel && mapRect.Contains(e.mousePosition))
            {
                float prev=_mapZoom;
                _mapZoom=Mathf.Clamp(_mapZoom-e.delta.y*0.10f,1f,8f);
                Vector2 cursorN=new Vector2(
                    (e.mousePosition.x-mapRect.x)/mapRect.width -0.5f,
                    (e.mousePosition.y-mapRect.y)/mapRect.height-0.5f);
                float uvShift=1f/prev-1f/_mapZoom;
                float maxPan=0.5f-0.5f/_mapZoom;
                _mapPan.x=Mathf.Clamp(_mapPan.x+cursorN.x*uvShift,-maxPan,maxPan);
                _mapPan.y=Mathf.Clamp(_mapPan.y+cursorN.y*uvShift,-maxPan,maxPan);
                ApplyUVRect(); e.Use();
            }
            if (e.type==EventType.MouseDown&&e.button==2&&mapRect.Contains(e.mousePosition))
            { _mapDrag=true; _dragStart=e.mousePosition; _panAtDrag=_mapPan; e.Use(); }
            if (_mapDrag&&e.type==EventType.MouseDrag)
            {
                float uvW=1f/_mapZoom;
                float dx=(e.mousePosition.x-_dragStart.x)/mapRect.width *uvW;
                float dy=(e.mousePosition.y-_dragStart.y)/mapRect.height*uvW;
                float maxPan=0.5f-0.5f*uvW;
                _mapPan.x=Mathf.Clamp(_panAtDrag.x+dx,-maxPan,maxPan);
                _mapPan.y=Mathf.Clamp(_panAtDrag.y+dy,-maxPan,maxPan);
                ApplyUVRect(); e.Use();
            }
            if (e.type==EventType.MouseUp&&e.button==2) _mapDrag=false;
            if (e.type==EventType.MouseDown&&e.clickCount==2&&mapRect.Contains(e.mousePosition))
            { _mapZoom=1f; _mapPan=Vector2.zero; ApplyUVRect(); e.Use(); }
        }

        void TrySelectSquad(Vector2 click, Rect r)
        {
            float best=32f; Squad hit=null;
            foreach(var sq in ActorManager.GetSquadsOnTeam(GameManager.PlayerTeam()))
            {
                if(sq==null||sq.Leader()?.actor==null) continue;
                float d=Vector2.Distance(click,W2M(sq.Leader().actor.Position(),r));
                if(d<best){best=d;hit=sq;}
                foreach(var m in sq.members)
                { if(m?.actor==null||m.actor.dead) continue; d=Vector2.Distance(click,W2M(m.actor.Position(),r)); if(d<best){best=d;hit=sq;} }
            }
            if(hit!=null){_selected=hit;_watcherLast="SELECT:hit="+hit.number;_watcherLog.Enqueue(_watcherLast);_awaitDest=false;}
        }

        void DrawCursorOverlay(Rect r)
        {
            var mp=Event.current.mousePosition; if(!r.Contains(mp)) return;
            GUI.color=new Color(C_ACCENT.r,C_ACCENT.g,C_ACCENT.b,0.85f);
            GUI.DrawTexture(new Rect(mp.x-1,mp.y-14,2,28),_texWhite);
            GUI.DrawTexture(new Rect(mp.x-14,mp.y-1,28,2),_texWhite);
            GUI.DrawTexture(new Rect(mp.x-6,mp.y-6,12,12),_texWhite);
            GUI.color=new Color(0,0,0,0.7f); GUI.DrawTexture(new Rect(mp.x-5,mp.y-5,10,10),_texWhite);
            GUI.color=Color.white;
            var lr=new Rect(mp.x+16,mp.y-9,108,18);
            FillRect(lr,new Color(0,0,0,0.72f)); DrawBorder(lr,new Color(C_ACCENT.r,C_ACCENT.g,C_ACCENT.b,0.5f),1f);
            GUI.color=C_ACCENT; GUI.Label(new Rect(lr.x+4,lr.y+2,lr.width,lr.height),"PLACE ORDER",_styleSm); GUI.color=Color.white;
        }

        // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        int CountAlive(int t)
        { int n=0; if(ActorManager.instance?.actors==null) return 0; foreach(var a in ActorManager.instance.actors) if(a!=null&&a.team==t&&!a.dead) n++; return n; }

        void FillRect(Rect r, Color c) { GUI.color=c; GUI.DrawTexture(r,_texWhite??Texture2D.whiteTexture); GUI.color=Color.white; }
        void DrawBorder(Rect r, Color c, float t) { GUI.color=c; var tw=_texWhite??Texture2D.whiteTexture; GUI.DrawTexture(new Rect(r.x,r.y,r.width,t),tw); GUI.DrawTexture(new Rect(r.x,r.yMax-t,r.width,t),tw); GUI.DrawTexture(new Rect(r.x,r.y,t,r.height),tw); GUI.DrawTexture(new Rect(r.xMax-t,r.y,t,r.height),tw); GUI.color=Color.white; }
        void DrawShadow(Rect r, float sz) { for(int i=1;i<=4;i++) FillRect(new Rect(r.x+i,r.y+i,r.width,r.height),new Color(0,0,0,0.16f/i)); }
        void DrawPanel(Rect r) { DrawShadow(r,8f); FillRect(r,C_BG); FillRect(new Rect(r.x,r.y,r.width,1),new Color(1,1,1,0.04f)); DrawBorder(r,C_BORDER,1f); }
        void DrawPanelHeader(Rect r, string label) { FillRect(r,C_BG_DARK); FillRect(new Rect(r.x,r.yMax-1,r.width,1),C_BORDER_LIT); FillRect(new Rect(r.x,r.y,3,r.height),C_ACCENT); GUI.color=C_ACCENT; GUI.Label(new Rect(r.x+12,r.y+9,r.width-20,20),label,_styleTitle); GUI.color=Color.white; }
        void DrawTag(Rect r, string label, Color col) { FillRect(r,new Color(col.r*0.18f,col.g*0.18f,col.b*0.18f,0.9f)); DrawBorder(r,col,1f); GUI.color=col; CenterLabel(r,label,_styleTitle); GUI.color=Color.white; }
        void DrawSectionLabel(ref float y, float x, float w, string label) { y+=6; GUI.color=C_TEXT_DIM; GUI.Label(new Rect(x+2,y,w,14),label,_styleSm); GUI.color=Color.white; FillRect(new Rect(x,y+14,w,1),C_BORDER); y+=18; }

        void OBtn(ref float y, float x, float bw, float bh, float gap, string label, OrderMode mode, Color idle, Color active)
        {
            bool pend=_awaitDest&&_pendingMode==mode;
            Color bg=pend?new Color(active.r*0.3f,active.g*0.3f,active.b*0.3f,0.92f):new Color(idle.r*0.4f,idle.g*0.4f,idle.b*0.4f,0.88f);
            var r=new Rect(x,y,bw,bh);
            FillRect(r,bg); FillRect(new Rect(r.x,r.y,3,bh),pend?active:new Color(idle.r,idle.g,idle.b,0.55f));
            DrawBorder(r,pend?new Color(active.r,active.g,active.b,0.55f):C_BORDER,1f);
            if(GUI.Button(r,GUIContent.none,GUIStyle.none)){_watcherLast="BTN:"+label;_watcherLog.Enqueue(_watcherLast);_awaitDest=!pend;_pendingMode=mode;}
            GUI.color=pend?active:C_TEXT; GUI.Label(new Rect(r.x+10,r.y+(bh-16)*0.5f,bw-14,16),label,_styleMd);
            GUI.color=Color.white; y+=bh+gap;
        }

        void DrawToggleBtn(ref float y, float x, float bw, float bh, float gap, string label, bool active, Color ac, Action onClick)
        {
            var r=new Rect(x,y,bw,bh);
            FillRect(r,active?new Color(ac.r*0.28f,ac.g*0.28f,ac.b*0.28f,0.92f):new Color(0.06f,0.10f,0.06f,0.85f));
            FillRect(new Rect(r.x,r.y,3,bh),active?ac:C_BORDER_DIM); DrawBorder(r,active?new Color(ac.r,ac.g,ac.b,0.55f):C_BORDER,1f);
            if(GUI.Button(r,GUIContent.none,GUIStyle.none)) onClick?.Invoke();
            GUI.color=active?ac:C_TEXT; GUI.Label(new Rect(r.x+10,r.y+(bh-16)*0.5f,bw-14,16),label,_styleMd);
            GUI.color=Color.white; y+=bh+gap;
        }

        void DrawActionBtn(Rect r, string label, Color bg, Color tc, Action onClick)
        { FillRect(r,bg); DrawBorder(r,new Color(tc.r,tc.g,tc.b,0.45f),1f); if(GUI.Button(r,GUIContent.none,GUIStyle.none)) onClick?.Invoke(); GUI.color=tc; CenterLabel(r,label,_styleMd); GUI.color=Color.white; }

        void DrawDot(Vector2 p, Color c, float sz) { FillRect(new Rect(p.x-sz*0.5f,p.y-sz*0.5f,sz,sz),c); }
        void DrawVehicleBlip(Vector2 p, Color col, float sz)
        {
            if (_vehicleBlipReady && _vehicleBlipSprite != null)
            {
                GUI.color = col;
                GUI.DrawTexture(new Rect(p.x-sz*0.5f, p.y-sz*0.5f, sz, sz),
                    _vehicleBlipSprite.texture, ScaleMode.ScaleToFit, true);
                GUI.color = Color.white;
            }
            else
            {
                // Fallback to diamond if sprite not loaded
                DrawDiamond(p, col, sz);
            }
        }
        void DrawDiamond(Vector2 p, Color c, float sz) { float h=sz*0.5f; DrawLineRaw(new Vector2(p.x,p.y-h),new Vector2(p.x+h,p.y),c); DrawLineRaw(new Vector2(p.x+h,p.y),new Vector2(p.x,p.y+h),c); DrawLineRaw(new Vector2(p.x,p.y+h),new Vector2(p.x-h,p.y),c); DrawLineRaw(new Vector2(p.x-h,p.y),new Vector2(p.x,p.y-h),c); }
        void DrawX(Vector2 p, Color c, float sz) { DrawLineRaw(new Vector2(p.x-sz,p.y-sz),new Vector2(p.x+sz,p.y+sz),c); DrawLineRaw(new Vector2(p.x+sz,p.y-sz),new Vector2(p.x-sz,p.y+sz),c); }
        void DrawCornerBrackets(Rect r, Color c, float len, float t) { GUI.color=c; var tw=_texWhite??Texture2D.whiteTexture; GUI.DrawTexture(new Rect(r.x,r.y,len,t),tw); GUI.DrawTexture(new Rect(r.x,r.y,t,len),tw); GUI.DrawTexture(new Rect(r.xMax-len,r.y,len,t),tw); GUI.DrawTexture(new Rect(r.xMax-t,r.y,t,len),tw); GUI.DrawTexture(new Rect(r.x,r.yMax-t,len,t),tw); GUI.DrawTexture(new Rect(r.x,r.yMax-len,t,len),tw); GUI.DrawTexture(new Rect(r.xMax-len,r.yMax-t,len,t),tw); GUI.DrawTexture(new Rect(r.xMax-t,r.yMax-len,t,len),tw); GUI.color=Color.white; }
        void DrawLineRaw(Vector2 a, Vector2 b, Color col) { if(Event.current.type!=EventType.Repaint) return; float len=Vector2.Distance(a,b); if(len<1) return; var saved=GUI.matrix; GUI.color=col; GUIUtility.RotateAroundPivot(Mathf.Atan2(b.y-a.y,b.x-a.x)*Mathf.Rad2Deg,a); GUI.DrawTexture(new Rect(a.x,a.y-1.2f,len,2.4f),_texWhite??Texture2D.whiteTexture); GUI.matrix=saved; GUI.color=Color.white; }
        // Draw a circular arc from point a to point b curving around centre c.
        // segments controls smoothness (12–20 is fine for map scale).
        void DrawArc(Vector2 a, Vector2 b, Vector2 centre, Color col, int segments, float dashLen, float gapLen)
        {
            if (Event.current.type != EventType.Repaint) return;
            float ra = Mathf.Atan2(a.y - centre.y, a.x - centre.x);
            float rb = Mathf.Atan2(b.y - centre.y, b.x - centre.x);
            // Always go counter-clockwise (flanking arc wraps left)
            if (rb > ra) rb -= 2f * Mathf.PI;
            float radius = Vector2.Distance(a, centre);
            float totalAngle = Mathf.Abs(ra - rb);
            float totalLen = totalAngle * radius;
            float pos = 0f; bool draw = true;
            for (int s = 0; s < segments; s++)
            {
                float t0 = (float)s / segments;
                float t1 = (float)(s + 1) / segments;
                float segLen = totalLen / segments;
                if (pos >= totalLen) break;
                float ang0 = Mathf.Lerp(ra, rb, t0);
                float ang1 = Mathf.Lerp(ra, rb, t1);
                Vector2 p0 = centre + new Vector2(Mathf.Cos(ang0), Mathf.Sin(ang0)) * radius;
                Vector2 p1 = centre + new Vector2(Mathf.Cos(ang1), Mathf.Sin(ang1)) * radius;
                // dash/gap along arc
                float segPos = 0f;
                Vector2 cur = p0;
                Vector2 dir = (p1 - p0);
                float dirLen = dir.magnitude;
                if (dirLen < 0.1f) { pos += segLen; continue; }
                dir /= dirLen;
                while (segPos < dirLen)
                {
                    float rem = draw ? dashLen : gapLen;
                    float step = Mathf.Min(rem - (pos % (dashLen + gapLen) % rem), dirLen - segPos);
                    if (draw) DrawLineRaw(cur, cur + dir * step, col);
                    cur += dir * step; segPos += step; pos += step;
                    if (step >= rem - 0.01f) draw = !draw;
                }
            }
        }

        // Quadratic Bezier arc: a to b curving through control point ctrl.
        // Much more stable than the circular arc — always bows in the right direction.
        void DrawBezierArc(Vector2 a, Vector2 b, Vector2 ctrl, Color col, int segments, float dashLen, float gapLen)
        {
            if (Event.current.type != EventType.Repaint) return;
            float totalLen = 0f;
            Vector2 prev = a;
            for (int s = 1; s <= segments; s++)
            {
                float t = (float)s / segments;
                Vector2 p = (1-t)*(1-t)*a + 2*(1-t)*t*ctrl + t*t*b;
                totalLen += Vector2.Distance(prev, p);
                prev = p;
            }
            float pos = 0f; bool draw = true;
            prev = a;
            for (int s = 1; s <= segments; s++)
            {
                float t = (float)s / segments;
                Vector2 cur = (1-t)*(1-t)*a + 2*(1-t)*t*ctrl + t*t*b;
                float segLen = Vector2.Distance(prev, cur);
                Vector2 dir  = segLen > 0.01f ? (cur - prev) / segLen : Vector2.right;
                float segPos = 0f;
                Vector2 sp = prev;
                while (segPos < segLen)
                {
                    float rem  = draw ? dashLen : gapLen;
                    float step = Mathf.Min(rem, segLen - segPos);
                    if (draw) DrawLineRaw(sp, sp + dir * step, col);
                    sp += dir * step; segPos += step; pos += step;
                    if (step >= rem - 0.01f) draw = !draw;
                }
                prev = cur;
            }
        }

        void DrawDashedLine(Vector2 a, Vector2 b, Color col, float dashLen, float gapLen) { if(Event.current.type!=EventType.Repaint) return; float total=Vector2.Distance(a,b); if(total<1) return; Vector2 dir=(b-a).normalized; float pos=0f; bool draw=true; while(pos<total){float seg=draw?dashLen:gapLen; if(draw) DrawLineRaw(a+dir*pos,a+dir*Mathf.Min(pos+seg,total),col); pos+=seg; draw=!draw;} }
        void CenterLabel(Rect r, string text, GUIStyle style) { var s=style.alignment; style.alignment=TextAnchor.MiddleCenter; GUI.Label(r,text,style); style.alignment=s; }
        void EnsureTextures() { if(_texReady) return; _texReady=true; _texWhite=new Texture2D(1,1); _texWhite.SetPixel(0,0,Color.white); _texWhite.Apply(); }
        void DestroyCachedTextures() { if(_texWhite!=null) Destroy(_texWhite); }
        void EnsureStyles()
        {
            if(_stylesReady) return; _stylesReady=true;
            _styleSm    = new GUIStyle(GUI.skin.label){fontSize=11,wordWrap=true,normal={textColor=C_TEXT}};
            _styleMd    = new GUIStyle(GUI.skin.label){fontSize=12,fontStyle=FontStyle.Bold,alignment=TextAnchor.MiddleLeft,normal={textColor=C_TEXT}};
            _styleTitle = new GUIStyle(GUI.skin.label){fontSize=12,fontStyle=FontStyle.Bold,normal={textColor=C_ACCENT}};
        }
    }
}

