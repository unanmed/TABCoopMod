using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Controls deterministic simulation stepping, GUID bifurcation (Synced Entity IDs vs Local UI IDs),
    /// and deterministic Mersenne Twister (DXRandom) seeding.
    /// </summary>
    public static class LockstepClock
    {
        public const double MsPerLogicTick = 33.333333333333336; // 30 Hz IA Update
        public const double MsPerPhysicsTick = 83.33333333333333; // 12 Hz Physics Update

        /// <summary>
        /// True when a multiplayer or deterministic replay session is active.
        /// </summary>
        public static bool IsDeterministicModeActive { get; private set; }

        /// <summary>
        /// True while the main thread is executing inside a synchronized logic tick or replaying a synced command.
        /// Used by DXGuid.New() patch to separate gameplay entity IDs from local UI object IDs.
        /// </summary>
        [ThreadStatic]
        public static bool InDeterministicContext;

        /// <summary>
        /// Cross-thread flag set while a logic tick is executing. The engine runs entity updates
        /// inside a Task on a worker thread, so [ThreadStatic] InDeterministicContext cannot reach
        /// DXGuid.New()/DXRandom there; this global flag does. Safe because the main thread blocks
        /// in WaitForGameLogic until the tick finishes.
        /// </summary>
        public static volatile bool LogicTickActive;

        /// <summary>
        /// True while a save/level is being loaded. During load, DXRandom.Global draws must use the
        /// seeded shared instance. NOTE: DXGuid is deliberately NOT routed through this flag — doing
        /// so gave load-time temp/UI objects synced low IDs, polluting the entity set with a
        /// process-varying count. Load-time GUIDs stay local (high bit) and excluded from hashing.
        /// </summary>
        public static volatile bool LoadingLevel;

        /// <summary>
        /// Controls whether the fixed-step clock is allowed to advance. Default: always ready
        /// (single-process deterministic replay). Network lockstep swaps in a gated implementation.
        /// </summary>
        public static ITickGate TickGate { get; set; } = new AlwaysReadyTickGate();

        public static int CurrentSimTick => DXGame.Current?.IAIterationInt ?? 0;

        private static long _syncedEntityIdCounter = 100000L;
        private static long _syncedProjectileIdCounter = 100000L;
        private static long _localUiIdCounter = 1L;
        [ThreadStatic] private static int _projectileGuidDepth;
        private static int _syncedRandomSeed = 1337;
        public static int EffectiveLoadSeed => HasPendingPostLoadCalibration ? PendingLoadSeed : _syncedRandomSeed;
        private static DXRandom _syncedGlobalRandom;
        private static DXRandom _auxGlobalRandom;
        private static DXRandom _renderGlobalRandom;

        /// <summary>Managed thread ID executing DXGame._TaskLogic (captured in OnStartUpdate).</summary>
        public static volatile int LogicThreadId;

        internal static void CaptureLogicThread()
        {
            LogicThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        internal static bool IsLogicWorkerActive()
        {
            if (!LogicTickActive) return false;
            int lt = LogicThreadId;
            int cur = Thread.CurrentThread.ManagedThreadId;
            if (lt != 0) return cur == lt;
            int main = MainThreadId;
            return main == 0 || cur != main;
        }

        /// <summary>True while DXGame.UpdatePhysics() is on the stack (physics callbacks run outside LogicTickActive).</summary>
        public static volatile bool InPhysicsContext;

        /// <summary>Managed thread ID executing UpdatePhysics() (used to keep async physics off MainThread render RNG/GUIDs).</summary>
        public static volatile int PhysicsThreadId;

        /// <summary>True when DeterministicTaskPatch replaced Loop 2 + Quantize with the merged Two-Phase pass in the current step.</summary>
        internal static volatile bool MergedPhysicsReadbackHandled;

        /// <summary>Deterministic dynamic body count from the last completed physics step (drives AdaptiveSolverIterations).</summary>
        public static int SyncedDynamicBodyCount;

        internal static bool IsPhysicsThreadActive()
        {
            if (!InPhysicsContext) return false;
            int pt = PhysicsThreadId;
            int cur = Thread.CurrentThread.ManagedThreadId;
            return pt == 0 || cur == pt || (pt != MainThreadId && cur != MainThreadId);
        }

        /// <summary>
        /// True whenever the calling thread is currently executing deterministic simulation work
        /// (sync/replay MainThread context, async _TaskLogic worker thread, or physics step thread).
        /// Command interceptors and CBehaviour UI isolation use this so worker-thread AI/weapon ticks
        /// are never misidentified as local player UI clicks.
        /// </summary>
        public static bool IsSimulationThreadActive
        {
            get
            {
                if (InDeterministicContext) return true;
                return IsLogicWorkerActive() || IsPhysicsThreadActive();
            }
        }

        /// <summary>
        /// Only true on frames where UpdateGameLogic_Prefix allowed a logic tick to advance.
        /// Prevents UpdatePhysics() from stepping ahead during MainMenu, LoadingLevel, or PausedBySession.
        /// </summary>
        public static bool AllowPhysicsThisFrame { get; set; }

        /// <summary>
        /// Same span as InPhysicsContext, but also gates DeterministicTaskPatch.
        /// </summary>
        public static volatile bool InPhysicsStep;

        /// <summary>
        /// True only after DXPhysicWorld.Step() completes (inside OnFinishPhysics..UpdatePhysics_Exit)
        /// during Loop 2 (OnNewPositionComputed), which mutates shared DXWorldGrid cells and must
        /// run sequentially. Loop 1 (StartUpdatePhysics) only writes per-entity Body forces/fields and
        /// keeps native DXHelper_Task.For multi-threading.
        /// </summary>
        public static volatile bool InPhysicsReadback;

        /// <summary>
        /// True while ZXMapDrawer.#=z0Pkwp_k=(DXLevel) runs terrain/tree/grass generation during
        /// InitLevel. Serializing DXHelper_Task.For inside this window prevents worker threads from
        /// consuming DXRandom streams in nondeterministic order.
        /// </summary>
        public static volatile bool InMapDrawer;

        /// <summary>
        /// True while executing our deterministic end-of-physics CSprite pull-animation pass so
        /// CSprite.#=zykGuoNdyOv10tsMpy8FzNkE=() runs for every pullable entity rather than only
        /// those inside the local camera viewport during render-time UpdateSceneObjectParallel.
        /// </summary>
        public static volatile bool AllowCSpritePullUpdate;

        /// <summary>
        /// Managed thread id of the render/frame thread (captured from DXGame.StartFrame). Used to
        /// tell the level-loading worker thread apart from UI/render threads that also construct
        /// DXRandom while a save is deserializing.
        /// </summary>
        public static int MainThreadId { get; private set; }

        [ThreadStatic]
        private static bool _allowRawGameTimeSet;

        /// <summary>
        /// True only while LockstepClock explicitly updates DXGame.GameTime for a simulation step.
        /// Prevents DXGame._TaskGameTime background thread from mutating GameTime/GameTimeInt
        /// mid-tick or mid-physics.
        /// </summary>
        internal static bool AllowRawGameTimeSet
        {
            get => _allowRawGameTimeSet;
            set => _allowRawGameTimeSet = value;
        }

        internal static void SetDeterministicGameTime(DXGame game, double value)
        {
            if (game == null) return;
            _allowRawGameTimeSet = true;
            try { game.GameTime = value; }
            finally { _allowRawGameTimeSet = false; }
        }

        /// <summary>
        /// True while the game is paused by an explicit synchronized pause command. The engine's
        /// save loader sets DXGame.Paused=true for its own bookkeeping and never clears it, so the
        /// fixed-step clock must key off THIS flag rather than DXGame.Paused.
        /// </summary>
        public static bool PausedBySession { get; private set; }

        public static void SetSessionPause(bool paused)
        {
            PausedBySession = paused;
            if (paused) ResetHostTickPacing();
        }

        // Real-time monotonic pacing for live Host co-op play (offline replay remains unthrottled).
        private static double _nextHostRealTickMs;

        public static void ResetHostTickPacing() => _nextHostRealTickMs = 0.0;

        /// <summary>
        /// Advances the Host monotonic real-time accumulator and returns how many fixed 30Hz ticks
        /// should execute this frame (clamped to [0, maxTicks]). Multi-tick catchup prevents low
        /// render FPS (e.g. dual-window GPU sharing or unfocused background window) from slowing
        /// simulation below 1.0x real time and also supports >1x game speeds cleanly.
        /// </summary>
        internal static int ConsumeHostRealTimeTicks(double gameSpeed, int maxTicks = 3, double paceMultiplier = 1.0)
        {
            if (gameSpeed <= 0.001)
            {
                _nextHostRealTickMs = 0.0;
                return 0;
            }

            double clampedPace = paceMultiplier > 0.1 ? paceMultiplier : 1.0;
            double stepMs = (MsPerLogicTick * clampedPace) / gameSpeed;
            double nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            // Clamp stale backlog to <= 2 ticks so a temporary GC/physics/render spike cannot
            // trap Host in an 8-tick catchup death spiral (4 FPS).
            if (_nextHostRealTickMs <= 0.0 || nowMs - _nextHostRealTickMs > Math.Max(120.0, stepMs * 3.0))
            {
                _nextHostRealTickMs = _nextHostRealTickMs <= 0.0 ? nowMs : nowMs - stepMs * 1.5;
            }

            int count = 0;
            // 1.5ms tolerance prevents ~30-31Hz frame pacing jitter from skipping alternate frames.
            while (count < maxTicks && nowMs + 1.5 >= _nextHostRealTickMs)
            {
                count++;
                _nextHostRealTickMs += stepMs;
            }
            return count;
        }

        internal static void RefundHostRealTimeTicks(double gameSpeed, int unrunTicks)
        {
            if (unrunTicks <= 0 || gameSpeed <= 0.001 || _nextHostRealTickMs <= 0.0)
                return;
            double stepMs = MsPerLogicTick / gameSpeed;
            _nextHostRealTickMs -= stepMs * unrunTicks;
        }

        /// <summary>Executes an action under InDeterministicContext on the calling thread.</summary>
        public static void RunInDeterministicContext(Action action)
        {
            bool prev = InDeterministicContext;
            InDeterministicContext = true;
            try
            {
                EnsureBehaviourRandomOnCurrentThread();
                action?.Invoke();
            }
            finally { InDeterministicContext = prev; }
        }

        [ThreadStatic] private static int _audioRngDepth;
        [ThreadStatic] private static int _visualVfxDepth;

        /// <summary>
        /// True while executing inside ZXSystemAudio synchronous playback/selection on this thread.
        /// Audio depends on local camera frustum and singleton caches, so its PRNG draws must
        /// never consume the shared synced or scene streams.
        /// </summary>
        public static bool InAudioContext => _audioRngDepth > 0;

        /// <summary>
        /// True while executing inside the pure-visual VFX spawner (#=zg10wm6mM9hhx) on this thread.
        /// Several VFX helpers check CheckFastObjectVisibility() / IsPixelAreaVisible / VisibleCells
        /// (which depend on local camera viewport), yet draw DXRandom.Global and allocate bare DXEntities.
        /// </summary>
        public static bool InVisualVfxContext => _visualVfxDepth > 0;

        public static void EnterAudioContext() => _audioRngDepth++;

        public static void ExitAudioContext()
        {
            if (_audioRngDepth > 0) _audioRngDepth--;
        }

        public static void EnterVisualVfxContext() => _visualVfxDepth++;

        public static void ExitVisualVfxContext()
        {
            if (_visualVfxDepth > 0) _visualVfxDepth--;
        }

        public static bool ShouldUseSyncedSceneRandom()
        {
            if (InAudioContext || InVisualVfxContext) return false;
            bool onMain = Thread.CurrentThread.ManagedThreadId == MainThreadId;
            return InDeterministicContext
                || IsPhysicsThreadActive()
                || IsLogicWorkerActive()
                || (LoadingLevel && !onMain);
        }

        /// <summary>
        /// Seed stashed by the session/verifier before LoadSaveFileAndEnter. The counter must not be
        /// calibrated until the async loader finishes (see OnLevelLoadComplete), because at load start
        /// DXGame.Current.Entities still holds the OLD level and the save's GameTime is not restored.
        /// </summary>
        public static int PendingLoadSeed { get; private set; } = 1337;
        public static bool HasPendingPostLoadCalibration { get; private set; }

        /// <summary>Host's synced entity-id counter at snapshot time; applied to the Client after load.</summary>
        public static long PendingSnapshotEntityIdCounter { get; private set; }

        public static long CurrentSyncedEntityIdCounter => _syncedEntityIdCounter;

        /// <summary>Adopt the Host's entity-id counter captured in the snapshot header.</summary>
        public static void SetSyncedEntityIdCounter(long value)
        {
            if (value > 0) _syncedEntityIdCounter = value;
        }

        /// <summary>Record the current (render/frame) thread for load-vs-UI thread classification.</summary>
        public static void CaptureMainThread() => MainThreadId = Thread.CurrentThread.ManagedThreadId;

        public static void EnableDeterministicSandbox(int initialSeed, long startingEntityIdCounter = 100000L)
        {
            _syncedRandomSeed = initialSeed;
            _syncedEntityIdCounter = startingEntityIdCounter;
            _syncedGlobalRandom = new DXRandom(initialSeed);
            _auxGlobalRandom = new DXRandom(unchecked(initialSeed ^ 0x5EEDBEEF));
            _renderGlobalRandom = new DXRandom(unchecked(initialSeed ^ 0x1234ABCD));
            _syncedSceneRandom = new DXRandom(unchecked(initialSeed ^ 0x517CC1B7));
            _lastReanchoredSimTick = -1;

            // The parameterless-ctor seed counter must restart together with the shared RNG, otherwise
            // a run that allocated a different number of DXRandom instances before syncing starts
            // would get a permanently shifted seed sequence.
            DXRandomCtorPatch.ResetCounter();

            // Keep vanilla CMovable on the FindPathConcurrent branch instead of its buggy
            // IsMultiplayer branch (hardcodes cellSize=1 and recurses infinitely on null paths);
            // DeterministicPathfindingPatch executes FindPathConcurrent synchronously and preserves cellSize.
            SetEngineMultiplayerMode(false);

            // Set IsIADeterministic AFTER SetEngineMultiplayerMode(false), because SetPlayerMode
            // overwrites DXGame.IsIADeterministic = (mode == Multiplayer)!
            DXGame.IsIADeterministic = true;
            AllowPhysicsThisFrame = false;
            InvalidateAllStage4Caches();
            IsDeterministicModeActive = true;

            // Unload background async physics/time threads if they were already running
            DXGame.Current?.UnloadPhysics();
            DeterministicSandboxPatches.EnsureDedicatedSimWorkerStarted();

            ModLogger.Info($"[LockstepClock] Deterministic sandbox ENABLED (Seed={initialSeed}, StartEntityId={startingEntityIdCounter}, IsIADeterministic={DXGame.IsIADeterministic}).");
        }

        public static void DisableDeterministicSandbox()
        {
            DeterministicSandboxPatches.StopDedicatedSimWorker();
            IsDeterministicModeActive = false;
            InDeterministicContext = false;
            LogicTickActive = false;
            LogicThreadId = 0;
            InPhysicsContext = false;
            PhysicsThreadId = 0;
            InPhysicsStep = false;
            InPhysicsReadback = false;
            AllowPhysicsThisFrame = false;
            _lastReanchoredSimTick = -1;
            InvalidateAllStage4Caches();
            DeterministicSandboxPatches.ResetAsyncTickState();
            LoadingLevel = false;
            HasPendingPostLoadCalibration = false;
            PendingSnapshotEntityIdCounter = 0;
            PausedBySession = false;
            Testing.CommandCenterInvulnerability.ResetLevelFlag();
            TickGate = new AlwaysReadyTickGate();
            DXGame.IsIADeterministic = false;
            SetEngineMultiplayerMode(false);
            ModLogger.Info("[LockstepClock] Deterministic sandbox DISABLED.");
        }

        internal static void InvalidateAllStage4Caches()
        {
            ZombieWatcherCoarseGrid.Invalidate();
            Physics1uGrid.Invalidate();
            ActiveUpdateEntityManager.Invalidate();
            PrecomputedPerceptionManager.Invalidate();
            AdaptivePhysicsStepPatch.Reset();
            SyncedDynamicBodyCount = 0;
        }

        private static MethodInfo _setPlayerModeMethod;
        private static int _lastLoggedPlayerMode = -1;

        /// <summary>
        /// Switches ZXGame's ZXPlayerModeType. Multiplayer makes CMovable compute paths synchronously
        /// on the logic thread (DXWorldGrid.FindPath) instead of the nondeterministic background queue.
        /// </summary>
        public static void SetEngineMultiplayerMode(bool multiplayer)
        {
            try
            {
                if (_setPlayerModeMethod == null)
                {
                    _setPlayerModeMethod = AccessTools.Method(GameBridge.TypeZXGame,
                        ObfuscatedSymbols.Method_ZXGame_SetPlayerMode);
                }
                if (_setPlayerModeMethod == null) return;

                int modeInt = multiplayer ? 1 : 0;
                var paramType = _setPlayerModeMethod.GetParameters()[0].ParameterType;
                object mode = Enum.ToObject(paramType, modeInt);
                _setPlayerModeMethod.Invoke(null, new[] { mode });
                if (_lastLoggedPlayerMode != modeInt || ModLogger.IsVerbose)
                {
                    _lastLoggedPlayerMode = modeInt;
                    ModLogger.Info($"[LockstepClock] ZXPlayerModeType set to {(multiplayer ? "Multiplayer" : "Singleplayer")}.");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] SetEngineMultiplayerMode failed: {ex.Message}");
            }
        }

        public static ulong NextSyncedGuid()
        {
            long next = Interlocked.Increment(ref _syncedEntityIdCounter);
            return (ulong)next;
        }

        public static void EnterProjectileGuidContext() => _projectileGuidDepth++;
        public static void ExitProjectileGuidContext() { if (_projectileGuidDepth > 0) _projectileGuidDepth--; }
        public static bool IsInProjectileGuidContext => _projectileGuidDepth > 0;

        public static ulong NextSyncedProjectileGuid()
        {
            long next = Interlocked.Increment(ref _syncedProjectileIdCounter);
            return 0x4000000000000000UL | (ulong)next;
        }

        public static ulong NextLocalUiGuid()
        {
            long next = Interlocked.Increment(ref _localUiIdCounter);
            return 0x8000000000000000UL | (ulong)next;
        }

        public static DXRandom GetSyncedGlobalRandom()
        {
            if (_syncedGlobalRandom == null)
            {
                _syncedGlobalRandom = new DXRandom(_syncedRandomSeed);
            }
            return _syncedGlobalRandom;
        }

        /// <summary>
        /// Fixed-seed RNG for Global draws made OUTSIDE the logic tick (physics callbacks, scene
        /// updates). DXRandom.Global is [ThreadStatic] and seeded from new Random(), so without this
        /// those draws are nondeterministic between processes. Kept separate from the logic stream so
        /// render-frame counts can never shift the simulation's random sequence.
        /// </summary>
        public static DXRandom GetAuxGlobalRandom()
        {
            if (_auxGlobalRandom == null)
            {
                _auxGlobalRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x5EEDBEEF));
            }
            return _auxGlobalRandom;
        }

        /// <summary>
        /// Separate fixed-seed stream for pure rendering-phase draws. Keeping it apart from the
        /// physics stream means frame/viewport-dependent visual randomness can never shift the
        /// random sequence consumed by deterministic simulation callbacks.
        /// </summary>
        public static DXRandom GetRenderGlobalRandom()
        {
            if (_renderGlobalRandom == null)
            {
                _renderGlobalRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x1234ABCD));
            }
            return _renderGlobalRandom;
        }

        private static DXRandom _syncedSceneRandom = new DXRandom(0x517CC1B7);

        /// <summary>
        /// Dedicated synced RNG backing DXScene.GlobalRandom (and DXLinq.RandomElement / DXHelper_Random)
        /// during deterministic simulation and level loading, keeping it isolated from render-thread draws.
        /// </summary>
        public static DXRandom GetSyncedSceneRandom()
        {
            if (_syncedSceneRandom == null)
            {
                _syncedSceneRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x517CC1B7));
            }
            return _syncedSceneRandom;
        }

        public static int SyncedRandomSeed => _syncedRandomSeed;

        internal enum RngDomainTag : byte
        {
            None            = 0,
            EntityUpdate    = 1,
            BehaviourRun    = 2,
            SystemUpdate    = 3,
            PhysicsCallback = 4,
            CommandReplay   = 5,
            LevelLoad       = 6
        }

        internal struct RngScopeFrame
        {
            public ulong ScopeId;
            public uint DrawCounter;
            public RngDomainTag Domain;
        }

        private const int MaxRngScopeDepth = 8;
        private static int _behaviourResetGeneration;
        [ThreadStatic] private static int _behaviourResetGenerationOnThread;
        private static readonly Dictionary<Type, DXRandom> _sharedBehaviourRandoms = new Dictionary<Type, DXRandom>();
        private static DXRandom[] _sharedBehaviourRandomArray = Array.Empty<DXRandom>();
        private static readonly object _behaviourSync = new object();
        private static int _lastReanchoredSimTick = -1;
        [ThreadStatic] private static RngScopeFrame[] _rngScopeStack;
        [ThreadStatic] private static int _rngScopeDepth;

        internal static DXRandom RenderGlobalRandomRaw => _renderGlobalRandom;

        internal static bool HasActiveRngScope
        {
            get
            {
                if (InAudioContext || InVisualVfxContext || LoadingLevel) return false;
                int depth = _rngScopeDepth;
                var stack = _rngScopeStack;
                if (depth <= 0 || stack == null) return false;
                int idx = depth <= MaxRngScopeDepth ? depth - 1 : MaxRngScopeDepth - 1;
                return stack[idx].ScopeId != 0UL;
            }
        }

        internal static void PushRngScope(ulong scopeId, RngDomainTag domain)
        {
            var stack = _rngScopeStack;
            if (stack == null)
            {
                stack = new RngScopeFrame[MaxRngScopeDepth];
                _rngScopeStack = stack;
            }
            int idx = _rngScopeDepth;
            _rngScopeDepth = idx + 1;
            if (idx < MaxRngScopeDepth)
            {
                stack[idx] = new RngScopeFrame
                {
                    ScopeId = scopeId,
                    DrawCounter = 0u,
                    Domain = domain
                };
            }
        }

        internal static void PopRngScope()
        {
            int depth = _rngScopeDepth;
            if (depth <= 0) return;
            depth--;
            _rngScopeDepth = depth;
            if (depth < MaxRngScopeDepth && _rngScopeStack != null)
                _rngScopeStack[depth] = default;
        }

        public static void EnterEntityBehaviourRngScope(ulong entityId)
        {
            PushRngScope(entityId, RngDomainTag.BehaviourRun);
        }

        public static void ExitEntityBehaviourRngScope()
        {
            PopRngScope();
        }

        internal static bool TryNextScopedRandomUInt(out uint value)
        {
            int depth = _rngScopeDepth;
            if (depth <= 0 || InAudioContext || InVisualVfxContext || LoadingLevel)
            {
                value = 0u;
                return false;
            }
            var stack = _rngScopeStack;
            if (stack == null)
            {
                value = 0u;
                return false;
            }
            int idx = depth <= MaxRngScopeDepth ? depth - 1 : MaxRngScopeDepth - 1;
            ulong scopeId = stack[idx].ScopeId;
            if (scopeId == 0UL)
            {
                value = 0u;
                return false;
            }

            uint drawIdx = ++stack[idx].DrawCounter;
            ulong domainTag = (ulong)stack[idx].Domain << 56;
            uint tick = (uint)(DXGame.Current?.IAIterationInt ?? 0);
            ulong z = (scopeId ^ domainTag)
                + ((ulong)unchecked((uint)_syncedRandomSeed) << 32)
                + (ulong)tick * 0x9E3779B97F4A7C15UL
                + (ulong)drawIdx * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z = z ^ (z >> 31);
            value = (uint)(z >> 32);
            return true;
        }

        public static bool TryNextEntityBehaviourRandomUInt(out uint value)
        {
            return TryNextScopedRandomUInt(out value);
        }

        private static uint MixTickSeed(int baseSeed, int tick, uint streamSalt)
        {
            uint h = unchecked((uint)baseSeed) ^ unchecked((uint)tick * 0x9E3779B9u) ^ streamSalt;
            h ^= h >> 16;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h != 0u ? h : 1u;
        }

        /// <summary>
        /// Deterministically re-anchors the shared simulation PRNG instances and construction counter
        /// at the start of each simulation tick in O(1), isolating local 1-tick combat/projectile
        /// variance from propagating into map-wide global RNG draws. Behaviour RNG draws inside
        /// ZXBehaviour.Run() are further isolated per entity ID via ZXBehaviourRunRngScopePatch.
        /// </summary>
        public static void ReanchorSimulationRngForTick(int tick)
        {
            if (!IsDeterministicModeActive || LoadingLevel || tick < 0 || _lastReanchoredSimTick == tick)
                return;
            _lastReanchoredSimTick = tick;

            DXRandomCtorPatch.ResetSimCounterForTick(tick);

            int baseSeed = _syncedRandomSeed;
            DXRandomCtorPatch.ReseedInstance(GetSyncedGlobalRandom(), MixTickSeed(baseSeed, tick, 0x9E3779B9u));
            DXRandomCtorPatch.ReseedInstance(GetAuxGlobalRandom(), MixTickSeed(baseSeed, tick, 0x5EEDBEEFu));
            DXRandomCtorPatch.ReseedInstance(GetSyncedSceneRandom(), MixTickSeed(baseSeed, tick, 0x517CC1B7u));

            var level = DXLevel.Current;
            if (level?.Random != null)
                DXRandomCtorPatch.ReseedInstance(level.Random, MixTickSeed(baseSeed, tick, 0x243F6A88u));
        }

        /// <summary>
        /// Deterministically seeds DXLevel.Current.Random for ZXGame.ShowMayorSelection from
        /// (SyncedRandomSeed, mayorLevel) so every peer rolls identical male/female mayor choices
        /// regardless of wall-clock ExecuteWithDelay(6000) or modal-retry tick skew.
        /// </summary>
        public static void ReseedLevelRandomForMayorSelection(int mayorLevel)
        {
            var level = DXLevel.Current;
            if (!IsDeterministicModeActive || level?.Random == null) return;
            uint seed = MixTickSeed(_syncedRandomSeed, mayorLevel, 0x4D415952u);
            DXRandomCtorPatch.ReseedInstance(level.Random, seed);
            ModLogger.Info($"[MayorSync] Seeded DXLevel.Random deterministically for Mayor level={mayorLevel} (baseSeed={_syncedRandomSeed}, drawSeed=0x{seed:X8}).");
        }

        /// <summary>Re-anchors DXLevel.Current.Random back to the current simulation tick stream.</summary>
        public static void ReanchorLevelRandomToCurrentTick()
        {
            var level = DXLevel.Current;
            if (!IsDeterministicModeActive || level?.Random == null) return;
            int tick = DXGame.Current?.IAIterationInt ?? 0;
            DXRandomCtorPatch.ReseedInstance(level.Random, MixTickSeed(_syncedRandomSeed, tick, 0x243F6A88u));
        }

        /// <summary>
        /// DXBehaviour&lt;TData&gt;._GlobalRandom is [ThreadStatic], and DXGame.UpdateGameLogic runs each
        /// tick's _TaskLogic on an arbitrary ThreadPool worker thread. Giving each worker its own new
        /// DXRandom(seed) would restart or interleave per-thread sequences whenever ThreadPool picks a
        /// different worker across ticks/processes. Bind every worker thread to the SAME per-type shared
        /// DXRandom instance (safe because IsIADeterministic runs _TaskLogic sequentially under WaitForGameLogic).
        /// </summary>
        public static void EnsureBehaviourRandomOnCurrentThread()
        {
            int gen = Volatile.Read(ref _behaviourResetGeneration);
            if (_behaviourResetGenerationOnThread == gen) return;
            _behaviourResetGenerationOnThread = gen;

            try
            {
                lock (_behaviourSync)
                {
                    if (_sharedBehaviourRandoms.Count == 0) return;
                    foreach (var kv in _sharedBehaviourRandoms)
                    {
                        try
                        {
                            AccessTools.Field(kv.Key, "_Seed")?.SetValue(null, _syncedRandomSeed);
                            AccessTools.Field(kv.Key, "_GlobalRandom")?.SetValue(null, kv.Value);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] EnsureBehaviourRandomOnCurrentThread failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets the shared deterministic DXRandom instance for every closed DXBehaviour&lt;TData&gt;
        /// generic type and bumps the generation counter so the active _TaskLogic thread rebinds to it.
        /// </summary>
        public static void ResetBehaviourRandom(int seed)
        {
            try
            {
                var asm = Core.GameBridge.GameAssembly;
                if (asm == null) return;

                Type open = typeof(DXBehaviour<>);
                Type baseData = typeof(DXBehaviourData);
                int resetCount = 0;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }

                lock (_behaviourSync)
                {
                    _sharedBehaviourRandoms.Clear();
                    int idx = 0;
                    foreach (var t in types)
                    {
                        if (t == null || t.IsAbstract || t.IsGenericTypeDefinition || !baseData.IsAssignableFrom(t))
                            continue;
                        try
                        {
                            Type closed = open.MakeGenericType(t);
                            var shared = new DXRandom(unchecked(seed ^ (idx * 0x45D9F3B)));
                            _sharedBehaviourRandoms[closed] = shared;
                            AccessTools.Field(closed, "_Seed")?.SetValue(null, seed);
                            AccessTools.Field(closed, "_GlobalRandom")?.SetValue(null, shared);
                            idx++;
                            resetCount++;
                        }
                        catch { /* closed generic not constructible for this T — skip */ }
                    }
                    var arr = new DXRandom[_sharedBehaviourRandoms.Count];
                    _sharedBehaviourRandoms.Values.CopyTo(arr, 0);
                    Volatile.Write(ref _sharedBehaviourRandomArray, arr);
                }

                ModLogger.Info($"[LockstepClock] Reset DXBehaviour<*>.GlobalRandom (types={resetCount}, seed={seed}).");
                Interlocked.Increment(ref _behaviourResetGeneration);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] ResetBehaviourRandom failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans all currently loaded entities in the level and sets _syncedEntityIdCounter
        /// strictly above the maximum existing non-UI entity ID so newly spawned entities never collide.
        /// </summary>
        public static void CalibrateEntityIdCounterFromLoadedLevel(int seed)
        {
            long maxId = 100000L;
            long maxProjId = 100000L;
            if (DXGame.Current?.Entities != null)
            {
                foreach (var ent in DXGame.Current.Entities)
                {
                    if (ent == null) continue;
                    ulong hiBits = ent.ID & 0xC000000000000000UL;
                    if (hiBits == 0)
                    {
                        if ((long)ent.ID > maxId)
                            maxId = (long)ent.ID;
                    }
                    else if (hiBits == 0x4000000000000000UL)
                    {
                        long rawProj = (long)(ent.ID & 0x3FFFFFFFFFFFFFFFUL);
                        if (rawProj > maxProjId)
                            maxProjId = rawProj;
                    }
                }
            }
            // Stay clear of long.MaxValue: saved IDs are already close to 2^63-1 on late saves.
            long start = maxId > long.MaxValue - 8192L ? long.MaxValue - 8192L : maxId + 1000L;
            _syncedProjectileIdCounter = maxProjId > (long)(0x3FFFFFFFFFFFFFFFUL - 8192UL)
                ? (long)(0x3FFFFFFFFFFFFFFFUL - 8192UL)
                : maxProjId + 1000L;
            EnableDeterministicSandbox(seed, start);
        }

        /// <summary>
        /// Resets shared RNG streams right at the start of ZXSystemGameLevel.InitLevel so
        /// MapDrawer terrain generation always sees the exact same deterministic sequence regardless
        /// of draws made during ReadSaveFile.
        /// </summary>
        public static void ResetSyncedRandomStreamsForInitLevel()
        {
            _syncedGlobalRandom = new DXRandom(_syncedRandomSeed);
            _auxGlobalRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x5EEDBEEF));
            _syncedSceneRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x517CC1B7));
            _lastReanchoredSimTick = -1;
            DXRandomCtorPatch.ResetCounter();
            DeterministicPathfindingPatch.InvalidatePathCache();
            CMovableSpriteProgressSyncPatch.ResetCache();
            InvalidateAllStage4Caches();
        }

        /// <summary>
        /// Called before LoadSaveFileAndEnter. The actual calibration is deferred to the level-load
        /// completion postfix, when the async loader has restored every entity and GameTime.
        /// </summary>
        public static void RequestPostLoadCalibration(int seed, long snapshotEntityIdCounter = 0, bool markLoading = true)
        {
            PendingLoadSeed = seed;
            PendingSnapshotEntityIdCounter = snapshotEntityIdCounter;
            HasPendingPostLoadCalibration = true;
            // Always synchronize LoadingLevel with markLoading so an aborted earlier save load cannot
            // leak LoadingLevel=true into a subsequent New Survival MapGenerator run.
            LoadingLevel = markLoading;
            AllowPhysicsThisFrame = false;
            DXGame.LastGameTimeIAUpdate = 0.0;
            DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0;
        }

        internal static void ClearLoadingFlag()
        {
            LoadingLevel = false;
        }

        /// <summary>
        /// Applies the full deterministic baseline to a loaded level (synced entity ID counter,
        /// sorted entity/physics lists, seeded RNGs, cleared path cache, aligned logic/physics
        /// clocks, normalized CMovable speeds & body velocity/force, and frozen camera intro).
        /// Called symmetrically on both Host and Client.
        /// </summary>
        public static void ApplyDeterministicLevelBaseline(int seed, long snapshotCounter = 0)
        {
            CalibrateEntityIdCounterFromLoadedLevel(seed);

            // A Client adopts the Host's snapshot-time counter so entities spawned on either side
            // after the snapshot share one ID space. Only move forward, never rewind below loaded IDs.
            if (snapshotCounter > _syncedEntityIdCounter)
                _syncedEntityIdCounter = snapshotCounter;
            RenderVisualInterpolationPatch.Reset();
            InvalidateAllStage4Caches();
            LoadingLevel = false;

            GameBridge.SortEntitiesById();
            ResetColonistSystem();
            NormalizeMovablesAndPhysicsState();
            GameBridge.SeedLevelRandom(seed);
            ResetBehaviourRandom(seed);
            _lastReanchoredSimTick = -1;
            DXWorldGrid.ResetPathsCached();
            GameBridge.AlignSimulationClocks();
            // The 6s opening camera pan is wall-clock driven; on two processes it lands on
            // different frames, changing which cells are "watched" (and thus visibility-driven
            // RNG/sprite work). Freeze it to the save's camera for a reproducible world.
            GameBridge.DisableCameraIntroAnimation();
        }

        /// <summary>
        /// Resets SysColonists' round-robin building cursor and rebuilds its building list in sorted
        /// entity order so wandering colonists pick identical destinations across Host and Client.
        /// </summary>
        private static void ResetColonistSystem()
        {
            try
            {
                var tSys = Core.GameBridge.GameAssembly?.GetType(Core.ObfuscatedSymbols.Type_SysColonists);
                if (tSys == null) return;
                object inst = AccessTools.Method(tSys, Core.ObfuscatedSymbols.Method_SysColonists_GetCurrent)?.Invoke(null, null);
                if (inst == null) return;

                var fCursor = AccessTools.Field(tSys, Core.ObfuscatedSymbols.Field_SysColonists_Cursor);
                int prevCursor = fCursor != null ? (int)fCursor.GetValue(inst) : -1;
                fCursor?.SetValue(inst, 0);
                AccessTools.Method(tSys, Core.ObfuscatedSymbols.Method_SysColonists_RebuildList)?.Invoke(inst, null);

                int count = 0;
                var list = AccessTools.Field(tSys, Core.ObfuscatedSymbols.Field_SysColonists_List)?.GetValue(inst) as System.Collections.IList;
                if (list != null) count = list.Count;
                ModLogger.Info($"[LockstepClock] Reset SysColonists (prevCursor={prevCursor}, buildings={count}).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] ResetColonistSystem failed: {ex.Message}");
            }
        }

        private static bool _movableMetaInit;
        private static Type _typeCMovable;
        private static FieldInfo _fMovableCachedSpeed;
        private static MethodInfo _mMovableComputeSpeed;

        private static void NormalizeMovablesAndPhysicsState()
        {
            try
            {
                var game = DXGame.Current;
                if (game?.Entities == null) return;

                if (!_movableMetaInit)
                {
                    _movableMetaInit = true;
                    _typeCMovable = GameBridge.GameAssembly?.GetType("ZX.Components.CMovable");
                    if (_typeCMovable != null)
                    {
                        _fMovableCachedSpeed = _typeCMovable.GetField("#=zLnAE$gO8EOG2",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _mMovableComputeSpeed = AccessTools.Method(_typeCMovable, "#=zRwhr6_cVbBr_", Type.EmptyTypes);
                    }
                }

                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;

                    if (_typeCMovable != null && _fMovableCachedSpeed != null && _mMovableComputeSpeed != null && e.HasComponents)
                    {
                        var comps = e.Components;
                        if (comps != null)
                        {
                            for (int c = 0; c < comps.Count; c++)
                            {
                                var comp = comps[c];
                                if (comp != null && _typeCMovable.IsInstanceOfType(comp))
                                {
                                    float spd = (float)_mMovableComputeSpeed.Invoke(comp, null);
                                    _fMovableCachedSpeed.SetValue(comp, spd);
                                    break;
                                }
                            }
                        }
                    }

                    var body = e.Body;
                    if (body != null && body.Handle != IntPtr.Zero && e.PhysicalType != DXBodyType.Static)
                    {
                        body.PhysicVelocity = PointF.Empty;
                        body.PhysicForce = PointF.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] NormalizeMovablesAndPhysicsState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs on the main thread from ZXSystemGameLevel.#=zwK_pUIeRBpbp Postfix — the first moment
        /// the loaded level is fully restored. Calibrates synced entity IDs, sorts update order,
        /// seeds level RNG and zero-phases the logic/physics clocks.
        /// </summary>
        public static void OnLevelLoadComplete()
        {
            if (!IsDeterministicModeActive)
            {
                LoadingLevel = false;
                return;
            }

            try
            {
                int seed = HasPendingPostLoadCalibration ? PendingLoadSeed : _syncedRandomSeed;
                long snapshotCounter = PendingSnapshotEntityIdCounter;
                HasPendingPostLoadCalibration = false;
                PendingSnapshotEntityIdCounter = 0;

                if (Sync.CoopSessionManager.IsSessionActive)
                {
                    bool isHost = Sync.CoopSessionManager.Transport != null && Sync.CoopSessionManager.Transport.IsHost;
                    if (isHost)
                    {
                        if (!Sync.CoopSessionManager.IsHostSnapshotReloadPending)
                        {
                            // Initial Host load before exporting coop_host_snapshot.zxsav: calibrate ID counter
                            // and suppress the 6s intro zoom/blur/HUD slide so the exported snapshot retains
                            // the normal starting camera and never leaks intro state into the snapshot reload.
                            CalibrateEntityIdCounterFromLoadedLevel(seed);
                            GameBridge.DisableCameraIntroAnimation();
                            LoadingLevel = false;
                            PausedBySession = true;
                            var initHostGame = DXGame.Current;
                            if (initHostGame != null)
                            {
                                if (initHostGame.Paused) initHostGame.Paused = false;
                                bool keepLocked = Sync.CoopSessionManager.IsSessionActive;
                                if (keepLocked)
                                {
                                    initHostGame.InputEnabled = false;
                                }
                                else if (!initHostGame.InputEnabled)
                                {
                                    initHostGame.InputEnabled = true;
                                    ModLogger.Info("[LockstepClock] Restored InputEnabled=true after initial Host level load.");
                                }
                            }
                            GameBridge.ClearWaitCursor();
                            GameBridge.ClearLoadingFadeOverlay();
                            ModLogger.Info($"[LockstepClock] Host initial level load complete: counter={_syncedEntityIdCounter}, holding paused for snapshot export. | {GameBridge.GetHudDiagSummary()}");
                            return;
                        }

                        ApplyDeterministicLevelBaseline(seed, snapshotCounter);
                        PausedBySession = true;
                        var hostGame = DXGame.Current;
                        if (hostGame != null)
                        {
                            if (hostGame.Paused) hostGame.Paused = false;
                            bool keepLocked = Sync.CoopSessionManager.IsSessionActive;
                            if (keepLocked)
                            {
                                hostGame.InputEnabled = false;
                            }
                            else if (!hostGame.InputEnabled)
                            {
                                hostGame.InputEnabled = true;
                                ModLogger.Info("[LockstepClock] Restored InputEnabled=true after Host snapshot reload (intro-zoom bypass).");
                            }
                        }
                        GameBridge.ClearWaitCursor();
                        GameBridge.ClearLoadingFadeOverlay();
                        ModLogger.Info($"[LockstepClock] Host snapshot reload complete: baseline applied (seed={seed}, counter={_syncedEntityIdCounter}). | {GameBridge.GetHudDiagSummary()}");
                        return;
                    }

                    // Client: apply baseline and keep paused until Host sends SessionStart.
                    ApplyDeterministicLevelBaseline(seed, snapshotCounter);
                    PausedBySession = true;
                    var clientGame = DXGame.Current;
                    if (clientGame != null)
                    {
                        if (clientGame.Paused)
                        {
                            clientGame.Paused = false;
                            ModLogger.Info("[LockstepClock] Cleared load-time pause on Client (session remains paused until SessionStart).");
                        }
                        bool keepLocked = Sync.CoopSessionManager.IsSessionActive;
                        if (keepLocked)
                        {
                            clientGame.InputEnabled = false;
                        }
                        else if (!clientGame.InputEnabled)
                        {
                            clientGame.InputEnabled = true;
                            ModLogger.Info("[LockstepClock] Restored InputEnabled=true on Client after snapshot load.");
                        }
                    }
                    GameBridge.ClearWaitCursor();
                    GameBridge.ClearLoadingFadeOverlay();
                    ModLogger.Info($"[LockstepClock] Client level load complete: ID counter -> {_syncedEntityIdCounter}, seed={seed}, waiting for SessionStart.");
                    return;
                }

                ApplyDeterministicLevelBaseline(seed, snapshotCounter);

                // The engine's save loader leaves DXGame.Paused = true (only the "Continue" button
                // clears it). Our fixed-step clock honors Paused by freezing, so a programmatic load
                // would never tick. Restore the running state here.
                PausedBySession = false;
                var game = DXGame.Current;
                if (game != null)
                {
                    if (game.Paused)
                    {
                        game.Paused = false;
                        ModLogger.Info("[LockstepClock] Cleared load-time pause at level completion.");
                    }
                    if (!game.InputEnabled) game.InputEnabled = true;
                }
                GameBridge.ClearWaitCursor();
                GameBridge.ClearLoadingFadeOverlay();

                ModLogger.Info($"[LockstepClock] Level load complete: ID counter -> {_syncedEntityIdCounter}, seed={seed}, clocks aligned.");
            }
            catch (Exception ex)
            {
                LoadingLevel = false;
                ModLogger.Error("[LockstepClock] OnLevelLoadComplete failed", ex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float QuantizeScalar(float value, double scale, double invScale)
        {
            double rounded = Math.Round((double)value * scale, MidpointRounding.ToEven) * invScale;
            float q = (float)rounded;
            return q == 0f ? 0f : q;
        }

        internal static float QuantizeCoord(float value)
        {
            int scaleInt = Testing.TestHarnessConfig.PhysicsQuantScale;
            if (scaleInt <= 0 || !IsFinite(value)) return value;
            double scale = scaleInt;
            return QuantizeScalar(value, scale, 1.0 / scale);
        }

        /// <summary>
        /// Quantizes active dynamic bodies' positions and velocities to a fixed binary grid (default 1/256 cell)
        /// at the end of each physics step so Chipmunk floating-point mantissa tail differences cannot accumulate
        /// across steps or trigger cpBBTree / broadphase boundary splits between peers.
        /// </summary>
        internal static void QuantizePhysicsBodiesPostStep()
        {
            int scaleInt = Testing.TestHarnessConfig.PhysicsQuantScale;
            if (scaleInt <= 0) return;

            var bodies = DXPhysicWorld.Bodies;
            if (bodies == null || bodies.Count == 0) return;

            bool samplePerf = DeterministicSandboxPatches.DiagPerfSamplingActive;
            long t0 = samplePerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            double scale = scaleInt;
            double invScale = 1.0 / scale;
            int nCells = DXWorldGrid.NCells;
            float maxCoord = nCells > 1 ? nCells - (float)invScale : float.MaxValue;
            int modified = 0;

            int dynTotal = 0;

            lock (DXGame.SyncLogic)
            {
                int count = bodies.Count;
                for (int i = 0; i < count; i++)
                {
                    DXPhysicBody body = bodies[i];
                    if (body == null || body.Handle == IntPtr.Zero) continue;

                    DXEntity ent = body.GameEntity;
                    if (ent == null || ent.Disposed) continue;
                    if (ent.PhysicalType != DXBodyType.Dynamic) continue;
                    if ((ent.ID & 0x8000000000000000UL) != 0UL) continue;
                    dynTotal++;

                    // Fast path: after DXPhysicBody.OnNewPositionComputed(), any stationary body whose
                    // managed Velocity is zero and PositionComputed == LastPositionComputed == ent.Position
                    // and already lies on the quantization grid needs zero native P/Invoke calls.
                    PointF managedVel = body.Velocity;
                    if (managedVel.X == 0f && managedVel.Y == 0f)
                    {
                        PointF managedPos = body.PositionComputed;
                        if (managedPos == body.LastPositionComputed && managedPos == ent.Position && managedPos == ent.LastPosition
                            && IsFinite(managedPos.X) && IsFinite(managedPos.Y)
                            && QuantizeScalar(managedPos.X, scale, invScale) == managedPos.X
                            && QuantizeScalar(managedPos.Y, scale, invScale) == managedPos.Y)
                        {
                            continue;
                        }
                    }

                    PointF rawPhysPos = body.PhysicPosition;
                    bool posValid = IsFinite(rawPhysPos.X) && IsFinite(rawPhysPos.Y);
                    PointF rawPhysVel = body.PhysicVelocity;
                    bool velValid = IsFinite(rawPhysVel.X) && IsFinite(rawPhysVel.Y);

                    bool anyChanged = false;

                    if (posValid)
                    {
                        float qx = QuantizeScalar(rawPhysPos.X, scale, invScale);
                        float qy = QuantizeScalar(rawPhysPos.Y, scale, invScale);
                        if (nCells > 1)
                        {
                            if (rawPhysPos.X >= 0f && qx < 0f) qx = 0f;
                            else if (rawPhysPos.X < nCells && qx >= nCells) qx = maxCoord;
                            if (rawPhysPos.Y >= 0f && qy < 0f) qy = 0f;
                            else if (rawPhysPos.Y < nCells && qy >= nCells) qy = maxCoord;
                        }
                        var qPos = new PointF(qx, qy);

                        if (rawPhysPos.X != qx || rawPhysPos.Y != qy)
                        {
                            body.PhysicPosition = qPos;
                            anyChanged = true;
                        }

                        // Keep LastPositionComputed / ent.LastPosition untouched when the body moved this step
                        // so RenderVisualInterpolationPatch continues interpolating cleanly; only sync Last*
                        // when snapping an already-stationary body onto the grid.
                        bool wasStationary = body.LastPositionComputed == body.PositionComputed && body.PositionComputed == rawPhysPos;
                        if (body.PositionComputed.X != qx || body.PositionComputed.Y != qy)
                        {
                            body.PositionComputed = qPos;
                            anyChanged = true;
                        }
                        if (wasStationary && (body.LastPositionComputed.X != qx || body.LastPositionComputed.Y != qy))
                        {
                            body.LastPositionComputed = qPos;
                        }

                        PointF entPos = ent.Position;
                        if (entPos.X != qx || entPos.Y != qy)
                        {
                            ent.Position = qPos;
                            anyChanged = true;
                        }
                        if (wasStationary && (ent.LastPosition.X != qx || ent.LastPosition.Y != qy))
                        {
                            ent.LastPosition = qPos;
                        }
                    }

                    if (velValid)
                    {
                        float qvx = QuantizeScalar(rawPhysVel.X, scale, invScale);
                        float qvy = QuantizeScalar(rawPhysVel.Y, scale, invScale);
                        var qVel = new PointF(qvx, qvy);

                        if (rawPhysVel.X != qvx || rawPhysVel.Y != qvy)
                        {
                            body.PhysicVelocity = qVel;
                            anyChanged = true;
                        }
                        if (body.Velocity.X != qvx || body.Velocity.Y != qvy)
                        {
                            body.Velocity = qVel;
                            anyChanged = true;
                        }
                    }

                    if (anyChanged) modified++;
                }
            }

            LockstepClock.SyncedDynamicBodyCount = dynTotal;
            if (samplePerf)
            {
                DeterministicSandboxPatches.DiagPhysQuantTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                DeterministicSandboxPatches.DiagPhysQuantModified += modified;
                DeterministicSandboxPatches.DiagPhysQuantDynTotal += dynTotal;
            }
        }

        private struct NativePhysWrite
        {
            public DXPhysicBody Body;
            public bool WritePos;
            public PointF Pos;
            public bool WriteVel;
            public PointF Vel;
        }

        private struct CrossedBodyWrite
        {
            public DXPhysicBody Body;
            public DXEntity Ent;
            public bool HighBitVfx;
            public bool DidMoveStep;
            public bool WriteNativePos;
            public bool WriteNativeVel;
            public bool WritePosComp;
            public bool WriteLastComp;
            public bool WriteEntLastPos;
            public bool WriteManagedVel;
            public PointF TargetPos;
            public PointF TargetVel;
            public PointF EntPos;
            public PointF NewLastComp;
            public PointF NewEntLastPos;
            public bool AnyChanged;
        }

        internal struct PullCandidate
        {
            public DXEntity Ent;
            public object CSprite;
        }

        private const int MaxPhysicsWorkers = 8;
        private static bool _mergedPhysAccessorsTried;
        private static bool _mergedPhysAccessorsReady;
        private static Func<DXEntity, PointF> _physGetRawPos;
        private static Action<DXEntity, PointF> _physSetRawPos;
        private static Func<DXEntity, PointF> _physGetRawLastPos;
        private static Action<DXEntity, PointF> _physSetRawLastPos;
        private static Func<DXEntity, Point> _physGetRawCell;
        private static List<NativePhysWrite>[] _physNativeQueues;
        private static List<CrossedBodyWrite>[] _physCrossedQueues;
        private static List<PullCandidate>[] _physPullCandidateQueues;
        internal static int _physPullWorkerCount;
        internal static DXLevel _physPullLevel;
        private static int[] _physModCounts;
        private static int[] _physDynCounts;

        internal static unsafe class NativePhysicsDirectAccessor
        {
            private const string NativeDll = "DXPhysicsNative.dll";

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr Space_Create(float gravityX, float gravityY, int iterations);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern void Space_Dispose(IntPtr space);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern void Space_Step(IntPtr space, float timeStep);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr Body_CreateCircle(IntPtr space, float mass, float x, float y, float radius);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern void Body_Dispose(IntPtr body);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern float Body_GetMass(IntPtr body);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern PointF Body_GetPosition(IntPtr body);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern void Body_SetPosition(IntPtr body, float x, float y);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern PointF Body_GetVelocity(IntPtr body);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern void Body_SetVelocity(IntPtr body, float x, float y);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern PointF Body_GetForce(IntPtr body);

            [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
            private static extern void Body_SetForce(IntPtr body, float x, float y);

            // Verified x64 Chipmunk offsets in DXPhysicsNative.dll (0xB8-byte cpBody, 0x1B8-byte cpSpace)
            private const int SpaceIterationsOffset = 0x00;
            private const int BodyStructSize = 0xB8;
            private const int BodyMassOffset = 0x10;
            private const int BodyCogOffset = 0x20;
            private const int BodyPosOffset = 0x28;
            private const int BodyVelOffset = 0x30;
            private const int BodyForceOffset = 0x38;
            private const int BodyAngleOffset = 0x40;
            private const int BodyRotOffset = 0x4C;      // 4 floats: a, b, c, d (identity = 1, 0, 0, 1)
            private const int BodyTxOffset = 0x5C;       // 2 floats: tx, ty (== p when cog == (0,0))
            private const int BodyArbiterListOffset = 0x90;
            private const int BodyConstraintListOffset = 0x98;
            private const int BodySleepingRootOffset = 0xA0;
            private const int BodyIdleTimeOffset = 0xB0;
            private const int ArbiterBodyAOffset = 0x28;
            private const int ArbiterBodyBOffset = 0x30;
            private const int ArbiterNextAOffset = 0x38;
            private const int ArbiterNextBOffset = 0x48;

            private static readonly bool _disabledByEnv =
                string.Equals(Environment.GetEnvironmentVariable("TABCOOP_DISABLE_DIRECT_PHYS"), "1", StringComparison.Ordinal);

            private static readonly object _verifyLock = new object();
            private static volatile bool _verifiedAttempted;
            private static volatile bool _enabled;

            internal static bool IsEnabled => _enabled;

            internal static bool TryVerifyAndEnable()
            {
                if (_enabled) return true;
                if (_verifiedAttempted || _disabledByEnv) return false;
                lock (_verifyLock)
                {
                    if (_verifiedAttempted) return _enabled;
                    _verifiedAttempted = true;
                    _enabled = RunSandboxVerification();
                    return _enabled;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static PointF ReadPos(IntPtr handle) => *(PointF*)((byte*)handle + BodyPosOffset);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static PointF ReadVel(IntPtr handle) => *(PointF*)((byte*)handle + BodyVelOffset);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static bool TryDirectWritePhase1LockFree(
                IntPtr handle,
                bool writePos,
                PointF pos,
                bool writeVel,
                PointF vel)
            {
                byte* b = (byte*)handle;
                if (*(IntPtr*)(b + BodySleepingRootOffset) != IntPtr.Zero) return false;
                if (*(IntPtr*)(b + BodyArbiterListOffset) != IntPtr.Zero) return false;
                if (*(IntPtr*)(b + BodyConstraintListOffset) != IntPtr.Zero) return false;
                if (*(float*)(b + BodyMassOffset) >= float.PositiveInfinity) return false;
                if (*(float*)(b + BodyIdleTimeOffset) >= float.PositiveInfinity) return false;
                if (writePos)
                {
                    if (*(long*)(b + BodyCogOffset) != 0L) return false;
                    if (*(float*)(b + BodyAngleOffset) != 0f) return false;
                    if (*(float*)(b + BodyRotOffset) != 1f
                        || *(float*)(b + BodyRotOffset + 4) != 0f
                        || *(float*)(b + BodyRotOffset + 8) != 0f
                        || *(float*)(b + BodyRotOffset + 12) != 1f)
                        return false;
                    *(PointF*)(b + BodyPosOffset) = pos;
                    *(PointF*)(b + BodyTxOffset) = pos;
                }
                if (writeVel)
                {
                    *(PointF*)(b + BodyVelOffset) = vel;
                }
                *(float*)(b + BodyIdleTimeOffset) = 0f;
                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static bool TryDirectWriteSequential(
                IntPtr handle,
                bool writePos,
                PointF pos,
                bool writeVel,
                PointF vel)
            {
                if (handle == IntPtr.Zero) return false;
                byte* b = (byte*)handle;
                if (*(IntPtr*)(b + BodySleepingRootOffset) != IntPtr.Zero) return false;
                if (*(IntPtr*)(b + BodyConstraintListOffset) != IntPtr.Zero) return false;
                if (*(float*)(b + BodyMassOffset) >= float.PositiveInfinity) return false;
                if (*(float*)(b + BodyIdleTimeOffset) >= float.PositiveInfinity) return false;
                if (writePos)
                {
                    if (*(long*)(b + BodyCogOffset) != 0L) return false;
                    if (*(float*)(b + BodyAngleOffset) != 0f) return false;
                    if (*(float*)(b + BodyRotOffset) != 1f
                        || *(float*)(b + BodyRotOffset + 4) != 0f
                        || *(float*)(b + BodyRotOffset + 8) != 0f
                        || *(float*)(b + BodyRotOffset + 12) != 1f)
                        return false;
                }

                // Walk arbiterList only to verify all dynamic neighbors are awake; if any neighbor
                // were sleeping we fall back cleanly to native Body_SetPosition / Body_SetVelocity.
                IntPtr arb = *(IntPtr*)(b + BodyArbiterListOffset);
                if (arb != IntPtr.Zero)
                {
                    int hops = 0;
                    while (arb != IntPtr.Zero)
                    {
                        if (++hops > 1024) return false;
                        byte* aPtr = (byte*)arb;
                        IntPtr bodyA = *(IntPtr*)(aPtr + ArbiterBodyAOffset);
                        IntPtr other = (bodyA == handle) ? *(IntPtr*)(aPtr + ArbiterBodyBOffset) : bodyA;
                        if (other == IntPtr.Zero) return false;
                        byte* oPtr = (byte*)other;
                        if (*(IntPtr*)(oPtr + BodySleepingRootOffset) != IntPtr.Zero) return false;
                        arb = (bodyA == handle) ? *(IntPtr*)(aPtr + ArbiterNextAOffset) : *(IntPtr*)(aPtr + ArbiterNextBOffset);
                    }
                    // Second pass: clear dynamic neighbor idleTime (exact match to cpBodyActivate).
                    arb = *(IntPtr*)(b + BodyArbiterListOffset);
                    while (arb != IntPtr.Zero)
                    {
                        byte* aPtr = (byte*)arb;
                        IntPtr bodyA = *(IntPtr*)(aPtr + ArbiterBodyAOffset);
                        IntPtr other = (bodyA == handle) ? *(IntPtr*)(aPtr + ArbiterBodyBOffset) : bodyA;
                        byte* oPtr = (byte*)other;
                        if (*(float*)(oPtr + BodyIdleTimeOffset) < float.PositiveInfinity)
                            *(float*)(oPtr + BodyIdleTimeOffset) = 0f;
                        arb = (bodyA == handle) ? *(IntPtr*)(aPtr + ArbiterNextAOffset) : *(IntPtr*)(aPtr + ArbiterNextBOffset);
                    }
                }

                if (writePos)
                {
                    *(PointF*)(b + BodyPosOffset) = pos;
                    *(PointF*)(b + BodyTxOffset) = pos;
                }
                if (writeVel)
                {
                    *(PointF*)(b + BodyVelOffset) = vel;
                }
                *(float*)(b + BodyIdleTimeOffset) = 0f;
                return true;
            }

            internal static bool TrySetSpaceIterations(IntPtr spaceHandle, int iterations)
            {
                if (!_enabled || spaceHandle == IntPtr.Zero || iterations <= 0) return false;
                *(int*)((byte*)spaceHandle + SpaceIterationsOffset) = iterations;
                return true;
            }

            private static bool RunSandboxVerification()
            {
                IntPtr sp = IntPtr.Zero;
                IntPtr b1 = IntPtr.Zero;
                IntPtr b2 = IntPtr.Zero;
                try
                {
                    sp = Space_Create(1.25f, -2.5f, 10);
                    if (sp == IntPtr.Zero)
                    {
                        ModLogger.Warn("[Perf] NativePhysicsDirectAccessor disabled: Space_Create returned null.");
                        return false;
                    }
                    byte* spPtr = (byte*)sp;
                    if (*(int*)(spPtr + SpaceIterationsOffset) != 10
                        || *(float*)(spPtr + 4) != 1.25f
                        || *(float*)(spPtr + 8) != -2.5f)
                    {
                        ModLogger.Warn("[Perf] NativePhysicsDirectAccessor disabled: cpSpace layout mismatch.");
                        return false;
                    }

                    b1 = Body_CreateCircle(sp, 2.5f, 12.25f, 34.75f, 0.5f);
                    b2 = Body_CreateCircle(sp, 2.5f, 12.75f, 34.75f, 0.5f);
                    if (b1 == IntPtr.Zero || b2 == IntPtr.Zero)
                    {
                        ModLogger.Warn("[Perf] NativePhysicsDirectAccessor disabled: Body_CreateCircle returned null.");
                        return false;
                    }

                    Body_SetVelocity(b1, -1.5f, 2.25f);
                    Body_SetForce(b1, 7.0f, -8.5f);
                    byte* p1 = (byte*)b1;

                    PointF gp = Body_GetPosition(b1);
                    PointF gv = Body_GetVelocity(b1);
                    PointF gf = Body_GetForce(b1);
                    float gm = Body_GetMass(b1);
                    if (gm != 2.5f || *(float*)(p1 + BodyMassOffset) != gm
                        || *(long*)(p1 + BodyCogOffset) != 0L
                        || gp.X != 12.25f || gp.Y != 34.75f
                        || *(float*)(p1 + BodyPosOffset) != gp.X || *(float*)(p1 + BodyPosOffset + 4) != gp.Y
                        || gv.X != -1.5f || gv.Y != 2.25f
                        || *(float*)(p1 + BodyVelOffset) != gv.X || *(float*)(p1 + BodyVelOffset + 4) != gv.Y
                        || gf.X != 7.0f || gf.Y != -8.5f
                        || *(float*)(p1 + BodyForceOffset) != gf.X || *(float*)(p1 + BodyForceOffset + 4) != gf.Y
                        || *(float*)(p1 + BodyAngleOffset) != 0f
                        || *(IntPtr*)(p1 + BodyArbiterListOffset) != IntPtr.Zero
                        || *(IntPtr*)(p1 + BodyConstraintListOffset) != IntPtr.Zero
                        || *(IntPtr*)(p1 + BodySleepingRootOffset) != IntPtr.Zero
                        || *(float*)(p1 + BodyIdleTimeOffset) != 0f)
                    {
                        ModLogger.Warn("[Perf] NativePhysicsDirectAccessor disabled: cpBody field offset verification failed.");
                        return false;
                    }

                    // 1. Snapshot diff of Body_SetVelocity across all 0xB8 cpBody bytes
                    *(float*)(p1 + BodyIdleTimeOffset) = 1.25f;
                    byte[] before = new byte[BodyStructSize];
                    byte[] after = new byte[BodyStructSize];
                    Marshal.Copy(b1, before, 0, BodyStructSize);
                    Body_SetVelocity(b1, 4.75f, -3.125f);
                    Marshal.Copy(b1, after, 0, BodyStructSize);
                    for (int i = 0; i < BodyStructSize; i++)
                    {
                        if (before[i] == after[i]) continue;
                        bool allowed = (i >= BodyVelOffset && i < BodyVelOffset + 8)
                            || (i >= BodyIdleTimeOffset && i < BodyIdleTimeOffset + 4);
                        if (!allowed)
                        {
                            ModLogger.Warn($"[Perf] NativePhysicsDirectAccessor disabled: unexpected SetVelocity write at +0x{i:X2}.");
                            return false;
                        }
                    }
                    if (*(float*)(p1 + BodyIdleTimeOffset) != 0f)
                    {
                        ModLogger.Warn("[Perf] NativePhysicsDirectAccessor disabled: SetVelocity did not clear idleTime.");
                        return false;
                    }

                    // 2. Snapshot diff of Body_SetPosition across all 0xB8 cpBody bytes
                    *(float*)(p1 + BodyIdleTimeOffset) = 2.5f;
                    Marshal.Copy(b1, before, 0, BodyStructSize);
                    Body_SetPosition(b1, 99.5f, 123.75f);
                    Marshal.Copy(b1, after, 0, BodyStructSize);
                    for (int i = 0; i < BodyStructSize; i++)
                    {
                        if (before[i] == after[i]) continue;
                        bool allowed = (i >= BodyPosOffset && i < BodyPosOffset + 8)
                            || (i >= BodyTxOffset && i < BodyTxOffset + 8)
                            || (i >= BodyIdleTimeOffset && i < BodyIdleTimeOffset + 4);
                        if (!allowed)
                        {
                            ModLogger.Warn($"[Perf] NativePhysicsDirectAccessor disabled: unexpected SetPosition write at +0x{i:X2}.");
                            return false;
                        }
                    }
                    if (*(float*)(p1 + BodyPosOffset) != 99.5f || *(float*)(p1 + BodyPosOffset + 4) != 123.75f
                        || *(float*)(p1 + BodyTxOffset) != 99.5f || *(float*)(p1 + BodyTxOffset + 4) != 123.75f
                        || *(float*)(p1 + BodyRotOffset) != 1f || *(float*)(p1 + BodyRotOffset + 4) != 0f
                        || *(float*)(p1 + BodyRotOffset + 8) != 0f || *(float*)(p1 + BodyRotOffset + 12) != 1f
                        || *(float*)(p1 + BodyIdleTimeOffset) != 0f)
                    {
                        ModLogger.Warn("[Perf] NativePhysicsDirectAccessor disabled: SetPosition transform check failed.");
                        return false;
                    }

                    // Verify direct write produces bit-identical memory vs Body_SetPosition
                    if (!TryDirectWritePhase1LockFree(b1, true, new PointF(-45.25f, 77.125f), true, new PointF(-0.5f, 1.25f)))
                    {
                        ModLogger.Warn("[Perf] NativePhysicsDirectAccessor disabled: TryDirectWritePhase1LockFree rejected test body.");
                        return false;
                    }
                    Marshal.Copy(b1, before, 0, BodyStructSize);
                    Body_SetPosition(b1, -45.25f, 77.125f);
                    Body_SetVelocity(b1, -0.5f, 1.25f);
                    Marshal.Copy(b1, after, 0, BodyStructSize);
                    for (int i = 0; i < BodyStructSize; i++)
                    {
                        if (before[i] != after[i])
                        {
                            ModLogger.Warn($"[Perf] NativePhysicsDirectAccessor disabled: direct vs P/Invoke mismatch at +0x{i:X2}.");
                            return false;
                        }
                    }

                    // 3. Verify arbiterList neighbor activation loop after Space_Step collision
                    Body_SetPosition(b1, 12.25f, 34.75f);
                    Space_Step(sp, 1f / 12f);
                    if (*(IntPtr*)(p1 + BodyArbiterListOffset) == IntPtr.Zero)
                    {
                        ModLogger.Warn("[Perf] NativePhysicsDirectAccessor disabled: overlapping Space_Step produced no arbiter.");
                        return false;
                    }
                    byte* p2 = (byte*)b2;
                    *(float*)(p1 + BodyIdleTimeOffset) = 2.0f;
                    *(float*)(p2 + BodyIdleTimeOffset) = 3.5f;
                    if (!TryDirectWriteSequential(b1, true, new PointF(12.375f, 34.875f), true, new PointF(0.25f, -0.25f))
                        || *(float*)(p1 + BodyIdleTimeOffset) != 0f
                        || *(float*)(p2 + BodyIdleTimeOffset) != 0f)
                    {
                        ModLogger.Warn("[Perf] NativePhysicsDirectAccessor disabled: sequential arbiter neighbor activation verification failed.");
                        return false;
                    }

                    ModLogger.Info("[Perf] NativePhysicsDirectAccessor self-check VERIFIED (0xB8 cpBody + 0x1B8 cpSpace bit-exact).");
                    return true;
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[Perf] NativePhysicsDirectAccessor verification exception (falling back to P/Invoke): {ex.Message}");
                    return false;
                }
                finally
                {
                    if (b1 != IntPtr.Zero) try { Body_Dispose(b1); } catch { }
                    if (b2 != IntPtr.Zero) try { Body_Dispose(b2); } catch { }
                    if (sp != IntPtr.Zero) try { Space_Dispose(sp); } catch { }
                }
            }
        }

        private static bool EnsureMergedPhysicsAccessors()
        {
            if (_mergedPhysAccessorsTried) return _mergedPhysAccessorsReady;
            _mergedPhysAccessorsTried = true;
            try
            {
                NativePhysicsDirectAccessor.TryVerifyAndEnable();
                var fPos = typeof(DXEntity).GetField("_Position", BindingFlags.NonPublic | BindingFlags.Instance);
                var fLastPos = typeof(DXEntity).GetField("_LastPosition", BindingFlags.NonPublic | BindingFlags.Instance);
                var fCell = typeof(DXEntity).GetField("_Cell", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fPos == null || fPos.FieldType != typeof(PointF)
                    || fLastPos == null || fLastPos.FieldType != typeof(PointF)
                    || fCell == null || fCell.FieldType != typeof(Point))
                {
                    ModLogger.Warn("[Perf] Merged physics accessors missing DXEntity private fields; using sequential fallback.");
                    return false;
                }

                var mod = typeof(LockstepClock).Module;

                var dmGp = new DynamicMethod("TABCoop_PhysGetRawPos", typeof(PointF), new[] { typeof(DXEntity) }, mod, true);
                var ilGp = dmGp.GetILGenerator();
                ilGp.Emit(OpCodes.Ldarg_0); ilGp.Emit(OpCodes.Ldfld, fPos); ilGp.Emit(OpCodes.Ret);
                _physGetRawPos = (Func<DXEntity, PointF>)dmGp.CreateDelegate(typeof(Func<DXEntity, PointF>));

                var dmSp = new DynamicMethod("TABCoop_PhysSetRawPos", typeof(void), new[] { typeof(DXEntity), typeof(PointF) }, mod, true);
                var ilSp = dmSp.GetILGenerator();
                ilSp.Emit(OpCodes.Ldarg_0); ilSp.Emit(OpCodes.Ldarg_1); ilSp.Emit(OpCodes.Stfld, fPos); ilSp.Emit(OpCodes.Ret);
                _physSetRawPos = (Action<DXEntity, PointF>)dmSp.CreateDelegate(typeof(Action<DXEntity, PointF>));

                var dmGlp = new DynamicMethod("TABCoop_PhysGetRawLastPos", typeof(PointF), new[] { typeof(DXEntity) }, mod, true);
                var ilGlp = dmGlp.GetILGenerator();
                ilGlp.Emit(OpCodes.Ldarg_0); ilGlp.Emit(OpCodes.Ldfld, fLastPos); ilGlp.Emit(OpCodes.Ret);
                _physGetRawLastPos = (Func<DXEntity, PointF>)dmGlp.CreateDelegate(typeof(Func<DXEntity, PointF>));

                var dmSlp = new DynamicMethod("TABCoop_PhysSetRawLastPos", typeof(void), new[] { typeof(DXEntity), typeof(PointF) }, mod, true);
                var ilSlp = dmSlp.GetILGenerator();
                ilSlp.Emit(OpCodes.Ldarg_0); ilSlp.Emit(OpCodes.Ldarg_1); ilSlp.Emit(OpCodes.Stfld, fLastPos); ilSlp.Emit(OpCodes.Ret);
                _physSetRawLastPos = (Action<DXEntity, PointF>)dmSlp.CreateDelegate(typeof(Action<DXEntity, PointF>));

                var dmGc = new DynamicMethod("TABCoop_PhysGetRawCell", typeof(Point), new[] { typeof(DXEntity) }, mod, true);
                var ilGc = dmGc.GetILGenerator();
                ilGc.Emit(OpCodes.Ldarg_0); ilGc.Emit(OpCodes.Ldfld, fCell); ilGc.Emit(OpCodes.Ret);
                _physGetRawCell = (Func<DXEntity, Point>)dmGc.CreateDelegate(typeof(Func<DXEntity, Point>));

                _physNativeQueues = new List<NativePhysWrite>[MaxPhysicsWorkers];
                _physCrossedQueues = new List<CrossedBodyWrite>[MaxPhysicsWorkers];
                _physPullCandidateQueues = new List<PullCandidate>[MaxPhysicsWorkers];
                for (int w = 0; w < MaxPhysicsWorkers; w++)
                {
                    _physNativeQueues[w] = new List<NativePhysWrite>(256);
                    _physCrossedQueues[w] = new List<CrossedBodyWrite>(64);
                    _physPullCandidateQueues[w] = new List<PullCandidate>(64);
                }
                _physModCounts = new int[MaxPhysicsWorkers];
                _physDynCounts = new int[MaxPhysicsWorkers];

                _mergedPhysAccessorsReady = true;
                ModLogger.Info("[Perf] Compiled DynamicMethods for Two-Phase Merged Physics Readback+Quantize.");
            }
            catch (Exception ex)
            {
                _mergedPhysAccessorsReady = false;
                ModLogger.Warn($"[Perf] EnsureMergedPhysicsAccessors failed (using sequential fallback): {ex.Message}");
            }
            return _mergedPhysAccessorsReady;
        }

        private static bool ApplyQuantizeSingleBody(DXPhysicBody body, int scaleInt, double scale, double invScale, int nCells, float maxCoord)
        {
            if (scaleInt <= 0) return false;
            DXEntity ent = body.GameEntity;
            if (ent == null || ent.Disposed || ent.PhysicalType != DXBodyType.Dynamic || (ent.ID & 0x8000000000000000UL) != 0UL)
                return false;

            PointF rawPhysPos = body.PhysicPosition;
            bool posValid = IsFinite(rawPhysPos.X) && IsFinite(rawPhysPos.Y);
            PointF rawPhysVel = body.PhysicVelocity;
            bool velValid = IsFinite(rawPhysVel.X) && IsFinite(rawPhysVel.Y);
            bool anyChanged = false;

            if (posValid)
            {
                float qx = QuantizeScalar(rawPhysPos.X, scale, invScale);
                float qy = QuantizeScalar(rawPhysPos.Y, scale, invScale);
                if (nCells > 1)
                {
                    if (rawPhysPos.X >= 0f && qx < 0f) qx = 0f;
                    else if (rawPhysPos.X < nCells && qx >= nCells) qx = maxCoord;
                    if (rawPhysPos.Y >= 0f && qy < 0f) qy = 0f;
                    else if (rawPhysPos.Y < nCells && qy >= nCells) qy = maxCoord;
                }
                var qPos = new PointF(qx, qy);

                if (rawPhysPos.X != qx || rawPhysPos.Y != qy)
                {
                    body.PhysicPosition = qPos;
                    anyChanged = true;
                }

                bool wasStationary = body.LastPositionComputed == body.PositionComputed && body.PositionComputed == rawPhysPos;
                if (body.PositionComputed.X != qx || body.PositionComputed.Y != qy)
                {
                    body.PositionComputed = qPos;
                    anyChanged = true;
                }
                if (wasStationary && (body.LastPositionComputed.X != qx || body.LastPositionComputed.Y != qy))
                {
                    body.LastPositionComputed = qPos;
                }

                PointF entPos = ent.Position;
                if (entPos.X != qx || entPos.Y != qy)
                {
                    ent.Position = qPos;
                    anyChanged = true;
                }
                if (wasStationary && (ent.LastPosition.X != qx || ent.LastPosition.Y != qy))
                {
                    ent.LastPosition = qPos;
                }
            }

            if (velValid)
            {
                float qvx = QuantizeScalar(rawPhysVel.X, scale, invScale);
                float qvy = QuantizeScalar(rawPhysVel.Y, scale, invScale);
                var qVel = new PointF(qvx, qvy);

                if (rawPhysVel.X != qvx || rawPhysVel.Y != qvy)
                {
                    body.PhysicVelocity = qVel;
                    anyChanged = true;
                }
                if (body.Velocity.X != qvx || body.Velocity.Y != qvy)
                {
                    body.Velocity = qVel;
                    anyChanged = true;
                }
            }
            return anyChanged;
        }

        private static void ProcessPhysicsReadbackWorker(
            int workerIdx,
            IList<DXPhysicBody> bodies,
            int sliceStart,
            int sliceEnd,
            int scaleInt,
            double scale,
            double invScale,
            int nCells,
            float maxCoord,
            bool collectPull,
            bool pullColdScan,
            HashSet<DXEntity> activePullSet)
        {
            var nativeQ = _physNativeQueues[workerIdx];
            var crossedQ = _physCrossedQueues[workerIdx];
            var pullQ = _physPullCandidateQueues[workerIdx];
            nativeQ.Clear();
            crossedQ.Clear();
            pullQ.Clear();
            int modCount = 0;
            int dynCount = 0;

            var getPos = _physGetRawPos;
            var setPos = _physSetRawPos;
            var getLastPos = _physGetRawLastPos;
            var setLastPos = _physSetRawLastPos;
            var getCell = _physGetRawCell;
            bool directPhys = NativePhysicsDirectAccessor.IsEnabled;

            for (int i = sliceStart; i < sliceEnd; i++)
            {
                DXPhysicBody body = bodies[i];
                if (body == null) continue;
                IntPtr h = body.Handle;
                if (h == IntPtr.Zero) continue;

                DXEntity ent = body.GameEntity;
                if (ent == null || ent.Disposed) continue;
                if (ent.PhysicalType != DXBodyType.Dynamic) continue;

                if ((ent.ID & 0x8000000000000000UL) != 0UL)
                {
                    crossedQ.Add(new CrossedBodyWrite { Body = body, Ent = ent, HighBitVfx = true });
                    continue;
                }

                dynCount++;

                PointF entPos = getPos(ent);
                PointF entLastPos = getLastPos(ent);
                PointF rawPhysPos = directPhys ? NativePhysicsDirectAccessor.ReadPos(h) : body.PhysicPosition;
                bool didMoveStep = !(entPos == rawPhysPos) || !(entLastPos == rawPhysPos);

                PointF managedVel = body.Velocity;
                PointF oldPosComp = body.PositionComputed;
                PointF oldLastComp = body.LastPositionComputed;

                if (!didMoveStep && managedVel.X == 0f && managedVel.Y == 0f && oldPosComp == oldLastComp && oldPosComp == entPos)
                {
                    if (scaleInt <= 0
                        || (IsFinite(oldPosComp.X) && IsFinite(oldPosComp.Y)
                            && QuantizeScalar(oldPosComp.X, scale, invScale) == oldPosComp.X
                            && QuantizeScalar(oldPosComp.Y, scale, invScale) == oldPosComp.Y))
                    {
                        if (collectPull && (pullColdScan || (activePullSet != null && activePullSet.Contains(ent))))
                        {
                            if (CSpritePullIsolationPatch.TryPreparePullCandidate(ent, out object cs))
                                pullQ.Add(new PullCandidate { Ent = ent, CSprite = cs });
                        }
                        continue;
                    }
                }

                bool posValid = IsFinite(rawPhysPos.X) && IsFinite(rawPhysPos.Y);
                PointF qPos = rawPhysPos;
                if (scaleInt > 0 && posValid)
                {
                    float qx = QuantizeScalar(rawPhysPos.X, scale, invScale);
                    float qy = QuantizeScalar(rawPhysPos.Y, scale, invScale);
                    if (nCells > 1)
                    {
                        if (rawPhysPos.X >= 0f && qx < 0f) qx = 0f;
                        else if (rawPhysPos.X < nCells && qx >= nCells) qx = maxCoord;
                        if (rawPhysPos.Y >= 0f && qy < 0f) qy = 0f;
                        else if (rawPhysPos.Y < nCells && qy >= nCells) qy = maxCoord;
                    }
                    qPos = new PointF(qx, qy);
                }

                PointF rawPhysVel = directPhys ? NativePhysicsDirectAccessor.ReadVel(h) : body.PhysicVelocity;
                bool velValid = IsFinite(rawPhysVel.X) && IsFinite(rawPhysVel.Y);
                PointF qVel = rawPhysVel;
                if (scaleInt > 0 && velValid)
                {
                    qVel = new PointF(
                        QuantizeScalar(rawPhysVel.X, scale, invScale),
                        QuantizeScalar(rawPhysVel.Y, scale, invScale));
                }

                PointF targetPos = (scaleInt > 0 && posValid) ? qPos : rawPhysPos;
                PointF targetVel = (scaleInt > 0 && velValid) ? qVel : rawPhysVel;

                if (collectPull)
                {
                    PointF pullVel = (didMoveStep || (scaleInt > 0 && velValid)) ? targetVel : managedVel;
                    if (pullColdScan || pullVel.X * pullVel.X + pullVel.Y * pullVel.Y > 0.04f || (activePullSet != null && activePullSet.Contains(ent)))
                    {
                        if (CSpritePullIsolationPatch.TryPreparePullCandidate(ent, out object cs))
                            pullQ.Add(new PullCandidate { Ent = ent, CSprite = cs });
                    }
                }

                bool writeNativePos = scaleInt > 0 && posValid && (rawPhysPos.X != qPos.X || rawPhysPos.Y != qPos.Y);
                bool writeNativeVel = scaleInt > 0 && velValid && (rawPhysVel.X != qVel.X || rawPhysVel.Y != qVel.Y);
                bool anyChanged = writeNativePos || writeNativeVel;

                bool wasStationary;
                PointF newLastComp;
                PointF newEntLastPos;
                bool writePosComp = false;
                bool writeLastComp = false;
                bool writeEntLastPos = false;
                bool writeManagedVel = false;

                if (didMoveStep)
                {
                    wasStationary = scaleInt > 0 && posValid && oldPosComp == rawPhysPos;
                    newLastComp = wasStationary ? targetPos : oldPosComp;
                    newEntLastPos = wasStationary ? targetPos : entPos;

                    if (scaleInt > 0 && posValid && (rawPhysPos.X != qPos.X || rawPhysPos.Y != qPos.Y))
                        anyChanged = true;
                    if (scaleInt > 0 && posValid && (entPos.X != qPos.X || entPos.Y != qPos.Y))
                        anyChanged = true;
                    if (scaleInt > 0 && velValid && (rawPhysVel.X != qVel.X || rawPhysVel.Y != qVel.Y))
                        anyChanged = true;

                    writeManagedVel = true;
                    writeLastComp = true;
                    writePosComp = true;
                    writeEntLastPos = true;
                }
                else
                {
                    wasStationary = scaleInt > 0 && posValid && oldLastComp == oldPosComp && oldPosComp == rawPhysPos;
                    newLastComp = qPos;
                    newEntLastPos = qPos;
                    if (scaleInt > 0 && posValid)
                    {
                        if (oldPosComp.X != qPos.X || oldPosComp.Y != qPos.Y)
                        {
                            writePosComp = true;
                            anyChanged = true;
                        }
                        if (wasStationary && (oldLastComp.X != qPos.X || oldLastComp.Y != qPos.Y))
                            writeLastComp = true;
                        if (entPos.X != qPos.X || entPos.Y != qPos.Y)
                            anyChanged = true;
                        if (wasStationary && (entLastPos.X != qPos.X || entLastPos.Y != qPos.Y))
                            writeEntLastPos = true;
                    }
                    if (scaleInt > 0 && velValid && (managedVel.X != qVel.X || managedVel.Y != qVel.Y))
                    {
                        writeManagedVel = true;
                        anyChanged = true;
                    }
                }

                // Non-one-cell dynamic entities or any entity crossing a grid cell between entPos -> rawPhysPos / targetPos
                // are deferred to Phase 2 so DXWorldGrid.SetNewCellFor executes strictly in ascending body index order.
                bool touchesGrid = targetPos != entPos || (didMoveStep && rawPhysPos != entPos);
                if (touchesGrid)
                {
                    bool deferToCrossed = !ent.IsOneCellSize;
                    if (!deferToCrossed)
                    {
                        Point curCell = getCell(ent);
                        deferToCrossed = (int)targetPos.X != curCell.X || (int)targetPos.Y != curCell.Y
                            || (didMoveStep && ((int)rawPhysPos.X != curCell.X || (int)rawPhysPos.Y != curCell.Y));
                    }
                    if (deferToCrossed)
                    {
                        crossedQ.Add(new CrossedBodyWrite
                        {
                            Body = body,
                            Ent = ent,
                            HighBitVfx = false,
                            DidMoveStep = didMoveStep,
                            WriteNativePos = writeNativePos,
                            WriteNativeVel = writeNativeVel,
                            WritePosComp = writePosComp,
                            WriteLastComp = writeLastComp,
                            WriteEntLastPos = writeEntLastPos,
                            WriteManagedVel = writeManagedVel,
                            TargetPos = targetPos,
                            TargetVel = targetVel,
                            EntPos = entPos,
                            NewLastComp = newLastComp,
                            NewEntLastPos = newEntLastPos,
                            AnyChanged = anyChanged
                        });
                        continue;
                    }
                }

                if (writeManagedVel) body.Velocity = targetVel;
                if (writeLastComp) body.LastPositionComputed = newLastComp;
                if (writePosComp) body.PositionComputed = targetPos;
                if (writeEntLastPos) setLastPos(ent, newEntLastPos);
                if (targetPos.X != entPos.X || targetPos.Y != entPos.Y)
                    setPos(ent, targetPos);

                if (writeNativePos || writeNativeVel)
                {
                    if (!directPhys || !didMoveStep
                        || !NativePhysicsDirectAccessor.TryDirectWritePhase1LockFree(h, writeNativePos, qPos, writeNativeVel, qVel))
                    {
                        nativeQ.Add(new NativePhysWrite
                        {
                            Body = body,
                            WritePos = writeNativePos,
                            Pos = qPos,
                            WriteVel = writeNativeVel,
                            Vel = qVel
                        });
                    }
                }
                if (anyChanged) modCount++;
            }

            _physModCounts[workerIdx] = modCount;
            _physDynCounts[workerIdx] = dynCount;
        }

        internal static bool TryConsumeMergedPullCandidates(out List<PullCandidate>[] queues, out int workerCount)
        {
            workerCount = _physPullWorkerCount;
            _physPullWorkerCount = 0;
            if (workerCount > 0 && object.ReferenceEquals(_physPullLevel, DXLevel.Current) && _physPullCandidateQueues != null)
            {
                _physPullLevel = null;
                queues = _physPullCandidateQueues;
                return true;
            }
            _physPullLevel = null;
            queues = null;
            workerCount = 0;
            return false;
        }

        internal static bool TryRunMergedPhysicsReadbackAndQuantize(int start, int end)
        {
            if (DXProject.IsInDesigner) return false;
            var bodies = DXPhysicWorld.Bodies;
            if (bodies == null || start < 0 || end < start || end > bodies.Count)
                return false;
            if (!EnsureMergedPhysicsAccessors())
                return false;

            _physPullWorkerCount = 0;
            _physPullLevel = null;

            int total = end - start;
            if (total == 0)
            {
                LockstepClock.SyncedDynamicBodyCount = 0;
                MergedPhysicsReadbackHandled = true;
                return true;
            }

            bool samplePerf = DeterministicSandboxPatches.DiagPerfSamplingActive;
            int scaleInt = Testing.TestHarnessConfig.PhysicsQuantScale;
            double scale = scaleInt > 0 ? scaleInt : 1.0;
            double invScale = 1.0 / scale;
            int nCells = DXWorldGrid.NCells;
            float maxCoord = nCells > 1 ? nCells - (float)invScale : float.MaxValue;

            bool collectPull = CSpritePullIsolationPatch.TryBeginMergedPullCollection(out bool pullColdScan, out HashSet<DXEntity> activePullSet);

            int workerCount = total >= 512 ? Testing.TestHarnessConfig.ParallelWorkers : 1;
            if (workerCount > MaxPhysicsWorkers) workerCount = MaxPhysicsWorkers;
            if (workerCount < 1) workerCount = 1;

            if (workerCount == 1)
            {
                ProcessPhysicsReadbackWorker(0, bodies, start, end, scaleInt, scale, invScale, nCells, maxCoord, collectPull, pullColdScan, activePullSet);
            }
            else
            {
                Parallel.For(0, workerCount, w =>
                {
                    int wStart = start + (int)(((long)total * w) / workerCount);
                    int wEnd = start + (int)(((long)total * (w + 1)) / workerCount);
                    ProcessPhysicsReadbackWorker(w, bodies, wStart, wEnd, scaleInt, scale, invScale, nCells, maxCoord, collectPull, pullColdScan, activePullSet);
                });
            }

            if (collectPull)
            {
                _physPullWorkerCount = workerCount;
                _physPullLevel = DXLevel.Current;
            }

            int totalModified = 0;
            int totalDyn = 0;
            var setLastPos = _physSetRawLastPos;

            // Phase 2A: Drain ALL remaining native non-crossed Chipmunk writes in ascending body index order first,
            // matching workerCount==1 ordering before any CrossedBodyWrite (which invokes ent.Position / grid updates).
            bool directPhysSeq = NativePhysicsDirectAccessor.IsEnabled;
            for (int w = 0; w < workerCount; w++)
            {
                totalModified += _physModCounts[w];
                totalDyn += _physDynCounts[w];

                var nq = _physNativeQueues[w];
                for (int i = 0; i < nq.Count; i++)
                {
                    var item = nq[i];
                    IntPtr h = item.Body.Handle;
                    if (h == IntPtr.Zero) continue;
                    if (directPhysSeq && NativePhysicsDirectAccessor.TryDirectWriteSequential(h, item.WritePos, item.Pos, item.WriteVel, item.Vel))
                        continue;
                    if (item.WritePos) item.Body.PhysicPosition = item.Pos;
                    if (item.WriteVel) item.Body.PhysicVelocity = item.Vel;
                }
                nq.Clear();
            }

            // Phase 2B: Drain ALL crossed writes (Chipmunk + managed ent.Position / DXWorldGrid.SetNewCellFor)
            // sequentially in ascending worker + body index order.
            for (int w = 0; w < workerCount; w++)
            {
                var cq = _physCrossedQueues[w];
                for (int i = 0; i < cq.Count; i++)
                {
                    var c = cq[i];
                    var b = c.Body;
                    if (b == null || b.Handle == IntPtr.Zero || c.Ent == null || c.Ent.Disposed) continue;
                    if (c.HighBitVfx || scaleInt <= 0)
                    {
                        b.OnNewPositionComputed();
                        continue;
                    }
                    if ((c.WriteNativePos || c.WriteNativeVel)
                        && (!directPhysSeq || !NativePhysicsDirectAccessor.TryDirectWriteSequential(b.Handle, c.WriteNativePos, c.TargetPos, c.WriteNativeVel, c.TargetVel)))
                    {
                        if (c.WriteNativePos) b.PhysicPosition = c.TargetPos;
                        if (c.WriteNativeVel) b.PhysicVelocity = c.TargetVel;
                    }
                    if (c.WriteManagedVel) b.Velocity = c.TargetVel;
                    if (c.WriteLastComp) b.LastPositionComputed = c.NewLastComp;
                    if (c.WritePosComp) b.PositionComputed = c.TargetPos;
                    if (c.WriteEntLastPos) setLastPos(c.Ent, c.NewEntLastPos);
                    if (c.TargetPos.X != c.EntPos.X || c.TargetPos.Y != c.EntPos.Y)
                        c.Ent.Position = c.TargetPos;
                    if (c.AnyChanged) totalModified++;
                }
                cq.Clear();
            }

            MergedPhysicsReadbackHandled = true;
            LockstepClock.SyncedDynamicBodyCount = totalDyn;
            if (samplePerf)
            {
                DeterministicSandboxPatches.DiagPhysQuantModified += totalModified;
                DeterministicSandboxPatches.DiagPhysQuantDynTotal += totalDyn;
            }
            return true;
        }
    }

    /// <summary>
    /// Patches DXGuid.New(), DXRandom.Global, and DXGame.UpdateGameLogic / LoadPhysics
    /// to enforce deterministic execution when LockstepClock.IsDeterministicModeActive is true.
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicSandboxPatches
    {
        // 1. Patch DXGuid.New() for dual-track ID generation
        [HarmonyPatch(typeof(DXGuid), nameof(DXGuid.New))]
        [HarmonyPrefix]
        public static bool DXGuid_New_Prefix(ref ulong __result)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return true;
            if (DiagPerfSamplingActive)
                System.Threading.Interlocked.Increment(ref DiagGuidCalls);

            // During (async) save loading or pure-visual VFX particle creation, generated IDs belong
            // to local/transient objects and must never consume the shared synced ID counter.
            if (LockstepClock.InVisualVfxContext || LockstepClock.LoadingLevel)
            {
                __result = LockstepClock.NextLocalUiGuid();
                return false;
            }

            if (LockstepClock.InDeterministicContext
                || LockstepClock.IsLogicWorkerActive()
                || LockstepClock.IsPhysicsThreadActive())
            {
                __result = LockstepClock.IsInProjectileGuidContext
                    ? LockstepClock.NextSyncedProjectileGuid()
                    : LockstepClock.NextSyncedGuid();
                return false;
            }

            // Only isolate render-thread transient IDs into the 0x8000... UI range once a synced
            // level or deterministic replay is actually running. Offline/pre-sync map generation
            // (e.g., Host starting a New Survival Game after clicking Co-op Host in the menu) runs
            // on a background worker thread and must allocate clean low-bit deterministic IDs so
            // vanilla DXGuid.New()'s upper bits and NextLocalUiGuid() never hide gameplay entities
            // from StateHasher and CalibrateEntityIdCounterFromLoadedLevel.
            if (Sync.CoopSessionManager.IsInSyncedLevel || Testing.DeterministicReplayVerifier.IsRunning)
            {
                __result = LockstepClock.NextLocalUiGuid();
                return false;
            }

            if (Sync.CoopSessionManager.IsSessionActive
                && Sync.CoopSessionManager.Transport != null
                && Sync.CoopSessionManager.Transport.IsHost
                && !Sync.CoopSessionManager.IsHostSnapshotReloadPending
                && (LockstepClock.InMapDrawer
                    || (LockstepClock.MainThreadId != 0 && Thread.CurrentThread.ManagedThreadId != LockstepClock.MainThreadId)))
            {
                __result = LockstepClock.IsInProjectileGuidContext
                    ? LockstepClock.NextSyncedProjectileGuid()
                    : LockstepClock.NextSyncedGuid();
                return false;
            }

            return true;
        }

        // 2. Patch DXRandom.Global getter to return deterministic PRNG in deterministic context
        [HarmonyPatch(typeof(DXRandom), nameof(DXRandom.Global), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool DXRandom_Global_Prefix(ref DXRandom __result)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return true;

            // DXRandom.Global is [ThreadStatic] and seeded from new Random()/real ticks, so it must
            // never be handed out while the sandbox is active. Only the logic worker and the level-
            // loading worker may draw from the shared synced stream: the main/render thread runs
            // menu animations and loader fades whose frame counts differ between processes, and
            // feeding those into the synced stream shifted every later draw (the A/B attractor).
            int main = LockstepClock.MainThreadId;
            bool onMain = main != 0 && Thread.CurrentThread.ManagedThreadId == main;

            if (LockstepClock.InAudioContext || LockstepClock.InVisualVfxContext)
                __result = LockstepClock.GetRenderGlobalRandom();
            else if (LockstepClock.IsPhysicsThreadActive())
            {
                if (DiagPerfSamplingActive && !LockstepClock.HasActiveRngScope)
                    System.Threading.Interlocked.Increment(ref DiagRngGlobalCalls);
                __result = LockstepClock.GetAuxGlobalRandom();
            }
            else if (LockstepClock.InDeterministicContext || LockstepClock.IsLogicWorkerActive()
                || (LockstepClock.LoadingLevel && !onMain))
            {
                if (DiagPerfSamplingActive && !LockstepClock.HasActiveRngScope)
                    System.Threading.Interlocked.Increment(ref DiagRngGlobalCalls);
                __result = LockstepClock.GetSyncedGlobalRandom();
            }
            else
                __result = LockstepClock.GetRenderGlobalRandom();
            return false;
        }

        private static readonly MethodInfo _mUpdateGameLogic = AccessTools.Method(typeof(DXGame), "UpdateGameLogic");
        private static readonly MethodInfo _mUpdatePhysics = AccessTools.Method(typeof(DXGame), "UpdatePhysics");
        private static readonly MethodInfo _mWaitForGameLogic = AccessTools.Method(typeof(DXGame), "WaitForGameLogic");
        private static readonly FieldInfo _fTaskLogic = AccessTools.Field(typeof(DXGame), "_TaskLogic");
        private static Action<DXGame> _fastRunLogicLambda;
        private static bool _fastRunLogicLambdaTried;
        [ThreadStatic] private static bool _inSingleTickStep;

        private static bool _asyncTickInFlight;
        internal static bool IsAsyncTickInFlight => _asyncTickInFlight;
        private static volatile bool _asyncPhysicsChained;
        private static volatile bool _asyncPhysicsStepped;
        private static bool _inFinalizeAsyncTick;
        private static long _asyncLaunchTs;
        private static long _asyncWaitStartTs;
        private static double _diagAsyncWaitMs;
        private static double _diagWorkerLogicMs;
        private static readonly object _deferredAsyncLock = new object();
        private static readonly List<Action> _deferredAsyncPostMethods = new List<Action>();

        // Persistent high-priority dedicated worker thread executing sequential deterministic logic
        // + UpdatePhysics control plane so Core 1 never competes with shared ThreadPool tasks.
        private static Thread _dedicatedSimThread;
        private static readonly ManualResetEventSlim _simWorkReady = new ManualResetEventSlim(false);
        private static readonly ManualResetEventSlim _simWorkDone = new ManualResetEventSlim(true);
        private static readonly object _simWorkerLifecycleLock = new object();
        private static volatile bool _simWorkerStopRequested;
        private static volatile bool _dedicatedAsyncActive;
        private static DXGame _dedicatedPendingGame;
        private static TaskCompletionSource<object> _dedicatedTcs;

        private static Action<DXGame> EnsureFastLogicLambdaCompiled()
        {
            if (_fastRunLogicLambdaTried) return _fastRunLogicLambda;
            _fastRunLogicLambdaTried = true;
            try
            {
                const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                MethodInfo target = null;
                foreach (var m in typeof(DXGame).GetMethods(F))
                {
                    if (m.Name.StartsWith("<UpdateGameLogic>b__", StringComparison.Ordinal)
                        && m.GetParameters().Length == 0
                        && m.ReturnType == typeof(void))
                    {
                        target = m;
                        break;
                    }
                }
                if (target != null)
                {
                    var dm = new DynamicMethod("TABCoop_FastRunLogicBody", typeof(void),
                        new[] { typeof(DXGame) }, typeof(DeterministicSandboxPatches).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Call, target);
                    il.Emit(OpCodes.Ret);
                    _fastRunLogicLambda = (Action<DXGame>)dm.CreateDelegate(typeof(Action<DXGame>));
                    ModLogger.Info($"[Perf] Compiled DynamicMethod TABCoop_FastRunLogicBody -> {target.Name}.");
                }
            }
            catch (Exception ex)
            {
                _fastRunLogicLambda = null;
                ModLogger.Warn($"[LockstepClock] Failed to bind DXGame logic body (using ThreadPool Task fallback): {ex.Message}");
            }
            return _fastRunLogicLambda;
        }

        internal static void EnsureDedicatedSimWorkerStarted()
        {
            EnsureFastLogicLambdaCompiled();
            if (_fastRunLogicLambda == null) return;
            lock (_simWorkerLifecycleLock)
            {
                if (_dedicatedSimThread != null && _dedicatedSimThread.IsAlive)
                    return;
                _simWorkerStopRequested = false;
                _simWorkReady.Reset();
                _simWorkDone.Set();
                _dedicatedSimThread = new Thread(DedicatedSimWorkerLoop)
                {
                    Name = "TABCoop.DedicatedSimWorker",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _dedicatedSimThread.Start();
            }
        }

        internal static void StopDedicatedSimWorker()
        {
            if (_dedicatedAsyncActive)
            {
                try { _simWorkDone.Wait(2000); } catch { }
            }
            Thread t;
            lock (_simWorkerLifecycleLock)
            {
                t = _dedicatedSimThread;
                if (t == null) return;
                _simWorkerStopRequested = true;
                _simWorkReady.Set();
            }
            try { t.Join(500); } catch { }
            lock (_simWorkerLifecycleLock)
            {
                if (!t.IsAlive)
                    _dedicatedSimThread = null;
                _simWorkerStopRequested = false;
            }
        }

        private static void DedicatedSimWorkerLoop()
        {
            double swMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            while (!_simWorkerStopRequested)
            {
                _simWorkReady.Wait();
                if (_simWorkerStopRequested) break;
                _simWorkReady.Reset();

                DXGame game = _dedicatedPendingGame;
                var tcs = _dedicatedTcs;
                _dedicatedPendingGame = null;
                _dedicatedTcs = null;

                try
                {
                    if (game != null && !game.Disposing && _fastRunLogicLambda != null)
                    {
                        long launchTs = _asyncLaunchTs;
                        long workerStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        try
                        {
                            _fastRunLogicLambda(game);
                        }
                        finally
                        {
                            PrecomputedPerceptionManager.EndWatchWindow();
                        }
                        double workerMs = (System.Diagnostics.Stopwatch.GetTimestamp() - launchTs) * swMs;
                        _diagWorkerLogicMs = _diagWorkerLogicMs <= 0.0 ? workerMs : _diagWorkerLogicMs * 0.9 + workerMs * 0.1;
                        DiagAvgTickLogicMs = _diagWorkerLogicMs;

                        if (DXGame.IsIADeterministic && _mUpdatePhysics != null
                            && !game.Disposing
                            && !LockstepClock.LoadingLevel && !LockstepClock.PausedBySession
                            && DXScene.Current != null && DXScene.Current.RootObject is DXIsometricGridObject)
                        {
                            double tPhysStart = System.Diagnostics.Stopwatch.GetTimestamp() * swMs;
                            _asyncPhysicsChained = true;
                            LockstepClock.AllowPhysicsThisFrame = true;
                            _mUpdatePhysics.Invoke(game, null);
                            LockstepClock.AllowPhysicsThisFrame = false;
                            double pMs = System.Diagnostics.Stopwatch.GetTimestamp() * swMs - tPhysStart;
                            if (pMs > 0.2)
                            {
                                DiagAvgPhysicsMs = DiagAvgPhysicsMs <= 0.0 ? pMs : DiagAvgPhysicsMs * 0.9 + pMs * 0.1;
                                DiagPhysStepWindowCount++;
                            }
                        }
                        Volatile.Write(ref _lastAsyncWorkerWorkTicks, System.Diagnostics.Stopwatch.GetTimestamp() - workerStartTs);
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[LockstepClock] DedicatedSimWorker error: {ex}");
                }
                finally
                {
                    LockstepClock.AllowPhysicsThisFrame = false;
                    LockstepClock.LogicTickActive = false;
                    LockstepClock.LogicThreadId = 0;
                    _simWorkDone.Set();
                    try { tcs?.TrySetResult(null); } catch { }
                }
            }
        }

        internal static void ResetAsyncTickState()
        {
            if (_dedicatedAsyncActive)
            {
                try { _simWorkDone.Wait(2000); } catch { }
            }
            _asyncTickInFlight = false;
            _dedicatedAsyncActive = false;
            _dedicatedPendingGame = null;
            _dedicatedTcs = null;
            _asyncPhysicsChained = false;
            _asyncPhysicsStepped = false;
            _inFinalizeAsyncTick = false;
            _asyncLaunchTs = 0;
            _asyncWaitStartTs = 0;
            _diagAsyncWaitMs = 0.0;
            _diagWorkerLogicMs = 0.0;
            Volatile.Write(ref _lastAsyncWorkerWorkTicks, 0L);
            ResetPerfRingBuffer();
            lock (_deferredAsyncLock) { _deferredAsyncPostMethods.Clear(); }
            LockstepClock._physPullWorkerCount = 0;
            LockstepClock._physPullLevel = null;
            CMovableSpriteProgressSyncPatch.ResetCache();
            RenderVisualInterpolationPatch.Reset();
            LockstepClock.InvalidateAllStage4Caches();
        }

        internal static void EnqueueDeferredAsyncPostMethod(Action action)
        {
            if (action == null) return;
            lock (_deferredAsyncLock) { _deferredAsyncPostMethods.Add(action); }
        }

        internal static void FlushPendingAsyncTick(DXGame game)
        {
            if (!_asyncTickInFlight || game == null) return;
            _mWaitForGameLogic?.Invoke(game, null);
        }

        internal static double DiagLastFrameDtMs { get; private set; }
        internal static double DiagAvgFrameMs { get; private set; }
        internal static double DiagAvgTickLogicMs { get; private set; }
        internal static double DiagAvgPhysicsMs { get; private set; }
        internal static int DiagLastWantedTicks { get; private set; }
        internal static int DiagLastRanTicks { get; private set; }
        private static double _diagLastEntryMs;

        // Detailed deterministic hotspot telemetry (gated by DiagPerfSamplingActive so normal play pays zero overhead;
        // automatically enables for a 60-tick burst with 30s cooldown whenever slow ticks > 45ms are detected).
        internal static volatile bool DiagPerfSamplingActive;
        private static double _diagSlowSampleCooldownUntilMs = -100000.0;
        internal static long DiagTickMovableSyncTicks;
        internal static long DiagTickMovableSyncCalls;
        internal static long DiagTickPullSyncTicks;
        internal static long DiagTickPullSyncCalls;
        internal static long DiagTickPathfindTicks;
        internal static long DiagTickPathfindCalls;
        internal static long DiagTickPathfindCacheHits;
        internal static long DiagTickHashTicks;
        internal static long DiagTickPostDrainTicks;
        internal static long DiagPhysFor1Ticks;
        internal static long DiagPhysFor2Ticks;
        internal static long DiagPhysQuantTicks;
        internal static long DiagPhysQuantModified;
        internal static long DiagPhysQuantDynTotal;
        internal static long DiagRngGlobalCalls;
        internal static long DiagRngScopedCalls;
        internal static long DiagRngGenCalls;
        internal static long DiagRngCtorCalls;
        internal static long DiagGuidCalls;
        internal static long DiagPostWrapCalls;
        internal static long DiagTickWatcherBypassed;
        internal static long DiagTickWatcherTicks;
        internal static long DiagTickWatcherCalls;
        internal static long DiagTickWatcherPrecompHits;
        internal static long DiagTickPrecompTicks;
        internal static long DiagTickPrecompBuilt;
        internal static long DiagTickPath1uBypassed;
        internal static int DiagActiveUpdateEntitiesCount;
        internal static int DiagTotalEntitiesCount;
        internal static int DiagTickWindowCount;
        internal static int DiagPhysStepWindowCount;
        private static readonly double _swTickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static long _lastAsyncWorkerWorkTicks;
        internal static long LastClientNetGateBlockTs;
        private static long _lastSimPauseOrLoadWallTs;
        private const int PerfSampleCap = 512;
        private static readonly long[] _perfSampleTs = new long[PerfSampleCap];
        private static readonly double[] _perfSampleWorkMs = new double[PerfSampleCap];
        private static int _perfSampleHead;
        private static int _perfSampleCount;

        public static double SecondsSinceLastSimPauseOrLoad =>
            _lastSimPauseOrLoadWallTs <= 0L
                ? 999.0
                : (System.Diagnostics.Stopwatch.GetTimestamp() - _lastSimPauseOrLoadWallTs) * _swTickToMs / 1000.0;

        internal static void ResetPerfRingBuffer()
        {
            _perfSampleHead = 0;
            _perfSampleCount = 0;
            LastClientNetGateBlockTs = 0L;
            _lastSimPauseOrLoadWallTs = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        internal static void RecordCompletedTickPerf(double tickWorkMs)
        {
            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            _perfSampleTs[_perfSampleHead] = nowTs;
            _perfSampleWorkMs[_perfSampleHead] = tickWorkMs > 0.0 ? tickWorkMs : 0.0;
            _perfSampleHead = (_perfSampleHead + 1) % PerfSampleCap;
            if (_perfSampleCount < PerfSampleCap) _perfSampleCount++;
        }

        public static void GetPerfMetrics(out double mspt, out double tps)
        {
            mspt = 0.0;
            tps = 0.0;
            int total = _perfSampleCount;
            if (total <= 0) return;

            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            long windowTicks = System.Diagnostics.Stopwatch.Frequency; // 1.0 second
            long cutoffTs = nowTs - windowTicks;

            int inWindow = 0;
            double sumWorkMs = 0.0;
            long oldestTs = 0L;
            long newestTs = 0L;

            for (int i = 0; i < total; i++)
            {
                int idx = (_perfSampleHead - 1 - i + PerfSampleCap) % PerfSampleCap;
                long ts = _perfSampleTs[idx];
                if (ts < cutoffTs) break;
                if (inWindow == 0) newestTs = ts;
                oldestTs = ts;
                sumWorkMs += _perfSampleWorkMs[idx];
                inWindow++;
            }

            if (inWindow == 0) return;
            mspt = sumWorkMs / inWindow;

            double ageNewestMs = (nowTs - newestTs) * _swTickToMs;
            bool isPaused = LockstepClock.PausedBySession || (DXGame.Current != null && DXGame.Current.Paused);
            if (isPaused && ageNewestMs > 60.0)
            {
                tps = 0.0;
                return;
            }
            if (ageNewestMs > 400.0)
            {
                double spanFromOldestSec = (nowTs - oldestTs) * _swTickToMs / 1000.0;
                tps = inWindow / Math.Max(0.25, spanFromOldestSec);
                return;
            }

            if (inWindow >= 2)
            {
                double spanSec = (newestTs - oldestTs) * _swTickToMs / 1000.0;
                tps = spanSec > 0.001 ? (inWindow - 1) / spanSec : 0.0;
            }
            else
            {
                tps = 1.0;
            }
        }

        // 3. Fixed-step clock: drive GameTime in exact 33.333..ms increments and only advance a
        //    tick when the active ITickGate allows it. Vanilla's UpdateGameLogic runs a Task and
        //    loops `while (GameTime >= LastGameTimeIAUpdate + c)`. When render FPS drops below 30Hz
        //    (e.g. two windows side-by-side) or GameSpeed > 1x, outer UpdateGameLogic_Prefix steps
        //    up to 8 sequential single-tick passes per frame, running physics + turn flush + hashing
        //    + PostMethods between every tick to preserve bit-identical interleaving.
        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPrefix]
        public static bool UpdateGameLogic_Prefix(DXGame __instance)
        {
            if (!_inSingleTickStep)
                RenderVisualInterpolationPatch.ExitVisualSceneUpdate(__instance);

            if (!LockstepClock.IsDeterministicModeActive) return true;

            if (_inSingleTickStep)
            {
                int tickIdx = __instance.IAIterationInt;
                if (Sync.CoopSessionManager.IsInSyncedLevel)
                {
                    Sync.CoopSessionManager.OnSimulationTickStarting(tickIdx);
                    Sync.CoopSessionManager.FlushDueTurnCommands(tickIdx);
                }

                if (LockstepClock.PausedBySession)
                {
                    LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                    LockstepClock.AllowPhysicsThisFrame = false;
                    return false;
                }

                LockstepClock.LogicTickActive = true;
                LockstepClock.InDeterministicContext = true;
                LockstepClock.AllowPhysicsThisFrame = true;
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate + LockstepClock.MsPerLogicTick);
                return true;
            }

            // Defensive flush if UpdateGameLogic is ever reached while a prior async tick is in flight.
            if (_asyncTickInFlight)
                _mWaitForGameLogic?.Invoke(__instance, null);

            // Never touch GameTime or run logic during MainMenu or async level loading:
            // MainMenu ticks would advance LastGameTimeIAUpdate to wall-clock ~15s and later
            // clobber the save's restored GameTime while ReadSaveFile/InitLevel runs on T9/T11.
            if (LockstepClock.LoadingLevel || DXScene.Current == null || !(DXScene.Current.RootObject is DXIsometricGridObject))
            {
                _lastSimPauseOrLoadWallTs = System.Diagnostics.Stopwatch.GetTimestamp();
                LockstepClock.ResetHostTickPacing();
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            // Background windows still need Visible=true so OnPreRender keeps drawing both windows
            // side-by-side, but NEVER force Focused=true: DXGame.ProcessInputEvents relies on
            // Focused to ignore global mouse/keyboard polling on the unfocused window so two
            // instances on the same PC do not mirror local camera/selection/hotkeys.
            if (!__instance.Visible) __instance.Visible = true;

            if (DXGame.LastGameTimeIAUpdate == 0.0)
                DXGame.LastGameTimeIAUpdate = __instance.GameTime;

            bool inCoop = Sync.CoopSessionManager.IsInSyncedLevel;
            if (inCoop)
            {
                if (__instance.CursorLocked && !Core.GameBridge.IsGameLoading())
                    Core.GameBridge.ClearWaitCursor();

                // Poll transport and flush outgoing Client inputs right before deciding this frame's ticks
                // so packets arriving during RenderFrame are handled without waiting another full render frame.
                Sync.CoopSessionManager.PumpNetworkBeforeLogic();
            }

            double swFreqMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double nowEntryMs = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs;
            if (_diagLastEntryMs > 0.0)
            {
                double dt = nowEntryMs - _diagLastEntryMs;
                DiagLastFrameDtMs = dt;
                DiagAvgFrameMs = DiagAvgFrameMs <= 0.0 ? dt : DiagAvgFrameMs * 0.85 + dt * 0.15;
            }
            _diagLastEntryMs = nowEntryMs;

            if (LockstepClock.PausedBySession)
            {
                _lastSimPauseOrLoadWallTs = System.Diagnostics.Stopwatch.GetTimestamp();
                if (inCoop)
                {
                    int pausedTick = __instance.IAIterationInt;
                    if (LockstepClock.TickGate == null || LockstepClock.TickGate.IsTickReady(pausedTick))
                        Sync.CoopSessionManager.FlushDueTurnCommands(pausedTick);
                }
                LockstepClock.ResetHostTickPacing();
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            if (Testing.DeterministicReplayVerifier.IsRunning
                && __instance.Paused
                && !DXVision.GUI.DXModalScreen.IsInModal
                && !Core.GameBridge.IsLevelEnded())
            {
                // EnterGameView queues an InvokeOnStartFrame action that sets DXGame.Paused=true
                // on the frame after OnLevelLoadComplete clears it; keep replay runs unpaused.
                __instance.Paused = false;
            }

            if (__instance.Paused || !Core.GameBridge.IsCurrentLevelSystemEnabled() || Core.GameBridge.IsLevelEnded())
            {
                _lastSimPauseOrLoadWallTs = System.Diagnostics.Stopwatch.GetTimestamp();
                LockstepClock.ResetHostTickPacing();
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            int maxTicksThisFrame = 1;
            bool isHost = inCoop && Sync.CoopSessionManager.Transport != null && Sync.CoopSessionManager.Transport.IsHost;
            bool isHeavySingleTick = __instance.GameSpeed <= 1.05
                && !Testing.TestHarnessConfig.DisableAsyncTick
                && !Testing.DeterministicReplayVerifier.IsRunning
                && (DiagAvgTickLogicMs >= 15.0 || (DiagAvgTickLogicMs + DiagAvgPhysicsMs * 0.4) >= 16.0);
            if (inCoop)
            {
                if (isHost)
                {
                    Sync.CoopSessionManager.GetHostPaceAdjustment(out double hostPaceMult, out int hostMaxTicks);
                    if (hostMaxTicks <= 0)
                    {
                        LockstepClock.ResetHostTickPacing();
                        maxTicksThisFrame = 0;
                    }
                    else
                    {
                        // Keep Host 1-tick async overlap alive when a single tick already takes >= 16ms
                        // at 1x speed (same protection Client uses below): otherwise maxTicksThisFrame=2
                        // disables canOverlapAsync, falls back to the 18ms sync loop (which breaks after 1 tick),
                        // and serializes ~48ms Sim + ~30ms Render on the main thread (~78ms frame).
                        int effectiveHostMax = isHeavySingleTick ? 1 : hostMaxTicks;
                        maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(__instance.GameSpeed, effectiveHostMax, hostPaceMult);
                    }
                }
                else
                {
                    int curTick = __instance.IAIterationInt;
                    int readyAhead = (LockstepClock.TickGate is TurnInputTickGate netGate)
                        ? (netGate.ReadyThrough - curTick)
                        : 1;
                    if (readyAhead < 0)
                    {
                        LastClientNetGateBlockTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        maxTicksThisFrame = 0;
                    }
                    else
                    {
                        double clientSpeed = __instance.GameSpeed;
                        int clientSpeedCeil = clientSpeed > 1.05 ? Math.Max(2, (int)Math.Ceiling(clientSpeed)) : 1;
                        if (readyAhead <= Sync.CoopSessionManager.InputDelayTicks)
                        {
                            if (clientSpeedCeil > 1)
                            {
                                int clientCap = Math.Min(16, Math.Min(readyAhead + 1, clientSpeedCeil + 2));
                                maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(clientSpeed, clientCap);
                            }
                            else
                            {
                                // Pace Client smoothly at 30Hz * GameSpeed so it doesn't burn the entire InputDelayTicks
                                // cushion in a single frame and then stall for the next 2 frames.
                                maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(clientSpeed, 1);
                            }
                        }
                        else
                        {
                            int behind = readyAhead - Sync.CoopSessionManager.InputDelayTicks;
                            if (clientSpeedCeil > 1)
                            {
                                // Always batch multiple ticks on Client when GameSpeed > 1x (even if 1 tick > 16ms),
                                // because async overlap is disabled above 1.05x; otherwise heavy 360x360 maps stay
                                // capped at want=1 (~13 TPS) while Host runs multi-tick bursts ahead.
                                int fastCap = Math.Min(16, Math.Min(readyAhead + 1, Math.Max(clientSpeedCeil + 2, behind >= 12 ? 12 : (behind >= 6 ? 8 : 4))));
                                maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(clientSpeed, fastCap, paceMultiplier: 0.72);
                                maxTicksThisFrame = Math.Max(maxTicksThisFrame, Math.Min(fastCap, behind));
                                LockstepClock.ResetHostTickPacing();
                            }
                            // Keep async 1-tick overlap alive whenever lag is mild (<=3t) OR a single tick
                            // already exceeds 16ms (while still recovering if lag grows >=12t).
                            else if (behind <= 3 || (isHeavySingleTick && behind < 12))
                            {
                                maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(clientSpeed, 1, paceMultiplier: 0.72);
                                if (maxTicksThisFrame < 1) maxTicksThisFrame = 1;
                            }
                            else
                            {
                                // Client fell behind by >=4 ticks and ticks are fast enough to batch: sync catchup.
                                int catchup = behind >= 12 ? 4 : (behind >= 7 ? 3 : 2);
                                maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(clientSpeed, catchup);
                                maxTicksThisFrame = Math.Max(maxTicksThisFrame, Math.Min(catchup, behind));
                                LockstepClock.ResetHostTickPacing();
                            }
                        }
                    }
                }
            }
            else if (Testing.TestHarnessConfig.SoloDeterministicMode && !Testing.DeterministicReplayVerifier.IsRunning)
            {
                double soloSpeed = __instance.GameSpeed;
                int soloMax = soloSpeed > 1.05 ? Math.Max(2, (int)Math.Ceiling(soloSpeed)) : 1;
                maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(soloSpeed, soloMax);
            }
            else
            {
                LockstepClock.ResetHostTickPacing();
            }

            if (maxTicksThisFrame <= 0)
            {
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            DiagLastWantedTicks = maxTicksThisFrame;

            // Pipeline 1 tick asynchronously on our dedicated high-priority secondary core while RenderFrame/Present runs on MainThread
            // ONLY when running 1 tick this frame; multi-tick catchup must execute synchronously so
            // slow late-game clients are not permanently capped to 1 tick per render frame.
            bool canOverlapAsync = (inCoop || Testing.TestHarnessConfig.SoloDeterministicMode)
                && !Testing.TestHarnessConfig.DisableAsyncTick
                && !Testing.DeterministicReplayVerifier.IsRunning
                && __instance.GameSpeed <= 1.05
                && maxTicksThisFrame == 1;

            if (canOverlapAsync)
            {
                int asyncNextTick = __instance.IAIterationInt;
                Sync.CoopSessionManager.OnSimulationTickStarting(asyncNextTick);
                if (LockstepClock.TickGate == null || LockstepClock.TickGate.IsTickReady(asyncNextTick))
                {
                    Sync.CoopSessionManager.FlushDueTurnCommands(asyncNextTick);
                    if (!LockstepClock.PausedBySession && !(__instance.IAIterationInt != asyncNextTick))
                    {
                        if (maxTicksThisFrame > 1)
                            LockstepClock.RefundHostRealTimeTicks(__instance.GameSpeed, maxTicksThisFrame - 1);

                        EnsureDedicatedSimWorkerStarted();
                        if (_fastRunLogicLambda != null && _dedicatedSimThread != null && _dedicatedSimThread.IsAlive)
                        {
                            _simWorkDone.Wait();
                            _asyncTickInFlight = true;
                            _dedicatedAsyncActive = true;
                            _asyncPhysicsChained = false;
                            _asyncPhysicsStepped = false;
                            _asyncLaunchTs = System.Diagnostics.Stopwatch.GetTimestamp();
                            LockstepClock.LogicTickActive = true;
                            LockstepClock.InDeterministicContext = false;
                            LockstepClock.AllowPhysicsThisFrame = false;
                            LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate + LockstepClock.MsPerLogicTick);
                            DiagLastRanTicks = 1;

                            var tcs = new TaskCompletionSource<object>();
                            _fTaskLogic?.SetValue(__instance, tcs.Task);
                            _dedicatedPendingGame = __instance;
                            _dedicatedTcs = tcs;
                            _simWorkDone.Reset();
                            _simWorkReady.Set();
                            return false;
                        }

                        _asyncTickInFlight = true;
                        _dedicatedAsyncActive = false;
                        _asyncPhysicsChained = false;
                        _asyncPhysicsStepped = false;
                        _asyncLaunchTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        LockstepClock.LogicTickActive = true;
                        LockstepClock.InDeterministicContext = false;
                        LockstepClock.AllowPhysicsThisFrame = false;
                        LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate + LockstepClock.MsPerLogicTick);
                        DiagLastRanTicks = 1;
                        return true;
                    }
                }

                LockstepClock.RefundHostRealTimeTicks(__instance.GameSpeed, maxTicksThisFrame);
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                LockstepClock.AllowPhysicsThisFrame = false;
                DiagLastRanTicks = 0;
                return false;
            }

            EnsureFastLogicLambdaCompiled();
            int ticksRan = 0;
            // After running at least 1 tick, allow a wider budget when catching up multiple ticks
            // (especially late-game Client catchup where 1 tick alone takes >10ms).
            double frameSimBudgetMs = __instance.GameSpeed > 1.05 && maxTicksThisFrame > 1
                ? Math.Min(180.0, 28.0 * Math.Min(6.0, __instance.GameSpeed))
                : ((!isHost && maxTicksThisFrame > 1)
                    ? 28.0
                    : (maxTicksThisFrame > 1 ? 18.0 : 10.0));
            for (int step = 0; step < maxTicksThisFrame; step++)
            {
                if (LockstepClock.PausedBySession) break;
                if (ticksRan >= 1)
                {
                    double simElapsedMs = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs - nowEntryMs;
                    if (simElapsedMs >= frameSimBudgetMs) break;
                }

                int nextTick = __instance.IAIterationInt;
                if (LockstepClock.TickGate != null && !LockstepClock.TickGate.IsTickReady(nextTick))
                    break;

                if (_mUpdateGameLogic == null && _fastRunLogicLambda == null) break;

                long tSyncTickStart = System.Diagnostics.Stopwatch.GetTimestamp();
                double tLogicStart = tSyncTickStart * swFreqMs;
                if (_fastRunLogicLambda != null)
                {
                    if (inCoop)
                    {
                        Sync.CoopSessionManager.OnSimulationTickStarting(nextTick);
                        Sync.CoopSessionManager.FlushDueTurnCommands(nextTick);
                    }
                    if (LockstepClock.PausedBySession)
                    {
                        LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                        LockstepClock.AllowPhysicsThisFrame = false;
                        break;
                    }

                    LockstepClock.LogicTickActive = true;
                    LockstepClock.InDeterministicContext = true;
                    LockstepClock.AllowPhysicsThisFrame = true;
                    LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate + LockstepClock.MsPerLogicTick);
                    try
                    {
                        _fastRunLogicLambda(__instance);
                    }
                    finally
                    {
                        PrecomputedPerceptionManager.EndWatchWindow();
                        LockstepClock.LogicTickActive = false;
                        LockstepClock.LogicThreadId = 0;
                        LockstepClock.InDeterministicContext = false;
                    }
                }
                else
                {
                    _inSingleTickStep = true;
                    try
                    {
                        _mUpdateGameLogic.Invoke(__instance, null);
                    }
                    finally
                    {
                        PrecomputedPerceptionManager.EndWatchWindow();
                        _inSingleTickStep = false;
                    }
                }
                double tLogicEnd = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs;
                double lMs = tLogicEnd - tLogicStart;
                DiagAvgTickLogicMs = DiagAvgTickLogicMs <= 0.0 ? lMs : DiagAvgTickLogicMs * 0.9 + lMs * 0.1;

                // Honor a SetPaused(true) command flushed right before/during this tick.
                if (LockstepClock.PausedBySession && ticksRan == 0 && __instance.IAIterationInt == nextTick)
                    break;

                ticksRan++;

                if (DXGame.IsIADeterministic && _mUpdatePhysics != null)
                {
                    double tPhysStart = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs;
                    LockstepClock.AllowPhysicsThisFrame = true;
                    _mUpdatePhysics.Invoke(__instance, null);
                    double pMs = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs - tPhysStart;
                    if (pMs > 0.2)
                    {
                        DiagAvgPhysicsMs = DiagAvgPhysicsMs <= 0.0 ? pMs : DiagAvgPhysicsMs * 0.9 + pMs * 0.1;
                        DiagPhysStepWindowCount++;
                    }
                }

                // Drain all tick-end PostMethods deterministically BEFORE state hashing so every
                // deferred AddToScene/RemoveFromScene/build-completion settles at the identical tick boundary
                // regardless of whether Host/Client ran this tick async-overlapped or multi-tick sync.
                if (inCoop)
                {
                    bool samplePerf = DiagPerfSamplingActive;
                    long tDrainStart = samplePerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    bool prevRendering = __instance.RenderingFrame;
                    bool prevDet = LockstepClock.InDeterministicContext;
                    __instance.RenderingFrame = false;
                    LockstepClock.InDeterministicContext = false;
                    try
                    {
                        __instance.PostMethods_OnFinishFrame?.ExecuteAll();
                        __instance.PostMethods_OnSafeLogicAndDrawing?.ExecuteAll();
                        __instance.PostMethods_OnStartFrame?.ExecuteAll();
                    }
                    finally
                    {
                        LockstepClock.InDeterministicContext = prevDet;
                        __instance.RenderingFrame = prevRendering;
                    }
                    if (samplePerf)
                        DiagTickPostDrainTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tDrainStart;

                    long tHashStart = samplePerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    Sync.CoopSessionManager.OnSimulationTickCompleted(__instance.IAIterationInt);
                    if (samplePerf)
                        DiagTickHashTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tHashStart;
                }

                RecordCompletedTickPerf((System.Diagnostics.Stopwatch.GetTimestamp() - tSyncTickStart) * swFreqMs);

                DiagTickWindowCount++;
                if (DiagTickWindowCount >= 60)
                    LogPerfWindow(__instance, ticksRan);
            }

            DiagLastRanTicks = ticksRan;
            if (ticksRan > 0)
                RenderVisualInterpolationPatch.OnLogicTickCompleted(ticksRan);
            if (isHost && !LockstepClock.PausedBySession && ticksRan < maxTicksThisFrame)
                LockstepClock.RefundHostRealTimeTicks(__instance.GameSpeed, maxTicksThisFrame - ticksRan);

            if (ticksRan == 0)
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);

            // Physics was already stepped deterministically after each individual tick above.
            LockstepClock.AllowPhysicsThisFrame = false;
            return false;
        }

        private static void LogPerfWindow(DXGame game, int ranThisFrame)
        {
            int tc = DiagTickWindowCount;
            bool wasSampling = DiagPerfSamplingActive;
            double nowMs = DXHelper_Environment.Ticks;
            if (wasSampling)
            {
                double movMs = DiagTickMovableSyncTicks * _swTickToMs / tc;
                double pullMs = DiagTickPullSyncTicks * _swTickToMs / Math.Max(1, DiagPhysStepWindowCount);
                double pathMs = DiagTickPathfindTicks * _swTickToMs / tc;
                double hashMs = DiagTickHashTicks * _swTickToMs / tc;
                double drainMs = DiagTickPostDrainTicks * _swTickToMs / tc;
                double pFor1Ms = DiagPhysFor1Ticks * _swTickToMs / Math.Max(1, DiagPhysStepWindowCount);
                double pFor2Ms = DiagPhysFor2Ticks * _swTickToMs / Math.Max(1, DiagPhysStepWindowCount);
                double pQuantMs = DiagPhysQuantTicks * _swTickToMs / Math.Max(1, DiagPhysStepWindowCount);
                long pQuantMod = DiagPhysQuantModified / Math.Max(1, DiagPhysStepWindowCount);
                long pQuantDyn = DiagPhysQuantDynTotal / Math.Max(1, DiagPhysStepWindowCount);
                double watchMs = DiagTickWatcherTicks * _swTickToMs / tc;
                double preMs = DiagTickPrecompTicks * _swTickToMs / tc;
                ModLogger.Info($"[Perf] tick={game.IAIterationInt} frame={DiagAvgFrameMs:F1}ms logic={DiagAvgTickLogicMs:F1}ms(wait={_diagAsyncWaitMs:F1}ms) phys={DiagAvgPhysicsMs:F1}ms(x{DiagPhysStepWindowCount}/60t) " +
                    $"| movSync={movMs:F2}ms({DiagTickMovableSyncCalls / tc}/t) pullSync={pullMs:F2}ms({DiagTickPullSyncCalls / Math.Max(1, DiagPhysStepWindowCount)}/p) " +
                    $"physFor1={pFor1Ms:F2}ms physFor2={pFor2Ms:F2}ms quant={pQuantMs:F2}ms({pQuantMod}/{pQuantDyn}) pIter={AdaptivePhysicsStepPatch.CurrentIterations} path={pathMs:F2}ms({DiagTickPathfindCalls / tc}/t,hit={DiagTickPathfindCacheHits / tc}/t) p1uFast={DiagTickPath1uBypassed / tc}/t " +
                    $"watch={watchMs:F2}ms({DiagTickWatcherCalls / tc}/t,pre={DiagTickWatcherPrecompHits / tc}/t,skip={DiagTickWatcherBypassed / tc}/t) precomp={preMs:F2}ms({DiagTickPrecompBuilt / tc}/t) actEnt={DiagActiveUpdateEntitiesCount}/{DiagTotalEntitiesCount} coopTick={hashMs:F2}ms drain={drainMs:F2}ms " +
                    $"| calls/t: rngG={DiagRngGlobalCalls / tc} scoped={DiagRngScopedCalls / tc} gen={DiagRngGenCalls / tc} ctor={DiagRngCtorCalls / tc} guid={DiagGuidCalls / tc} post={DiagPostWrapCalls / tc} want={DiagLastWantedTicks} ran={ranThisFrame}");
                if (!Testing.TestHarnessConfig.Enabled && !Testing.TestHarnessConfig.ForcePerfDiag)
                {
                    DiagPerfSamplingActive = false;
                    _diagSlowSampleCooldownUntilMs = nowMs + 30000.0;
                }
            }
            else if (Testing.TestHarnessConfig.Enabled || Testing.TestHarnessConfig.ForcePerfDiag)
            {
                DiagPerfSamplingActive = true;
            }
            else if (DiagAvgTickLogicMs > 20.0 && (nowMs >= _diagSlowSampleCooldownUntilMs || nowMs < _diagSlowSampleCooldownUntilMs - 60000.0))
            {
                DiagPerfSamplingActive = true;
            }
            DiagTickMovableSyncTicks = 0; DiagTickMovableSyncCalls = 0;
            DiagTickPullSyncTicks = 0; DiagTickPullSyncCalls = 0;
            DiagTickPathfindTicks = 0; DiagTickPathfindCalls = 0; DiagTickPathfindCacheHits = 0;
            DiagTickHashTicks = 0; DiagTickPostDrainTicks = 0;
            DiagPhysFor1Ticks = 0; DiagPhysFor2Ticks = 0;
            DiagPhysQuantTicks = 0; DiagPhysQuantModified = 0;
            DiagPhysQuantDynTotal = 0;
            DiagRngGlobalCalls = 0; DiagRngScopedCalls = 0; DiagRngGenCalls = 0; DiagRngCtorCalls = 0; DiagGuidCalls = 0; DiagPostWrapCalls = 0;
            DiagTickWatcherBypassed = 0; DiagTickPath1uBypassed = 0;
            DiagTickWatcherTicks = 0; DiagTickWatcherCalls = 0; DiagTickWatcherPrecompHits = 0; DiagTickPrecompTicks = 0; DiagTickPrecompBuilt = 0;
            DiagTickWindowCount = 0; DiagPhysStepWindowCount = 0;
        }

        internal static void ResetPerfWindowCounters(bool enableSampling)
        {
            DiagTickMovableSyncTicks = 0; DiagTickMovableSyncCalls = 0;
            DiagTickPullSyncTicks = 0; DiagTickPullSyncCalls = 0;
            DiagTickPathfindTicks = 0; DiagTickPathfindCalls = 0; DiagTickPathfindCacheHits = 0;
            DiagTickHashTicks = 0; DiagTickPostDrainTicks = 0;
            DiagPhysFor1Ticks = 0; DiagPhysFor2Ticks = 0;
            DiagPhysQuantTicks = 0; DiagPhysQuantModified = 0;
            DiagPhysQuantDynTotal = 0;
            DiagRngGlobalCalls = 0; DiagRngScopedCalls = 0; DiagRngGenCalls = 0; DiagRngCtorCalls = 0; DiagGuidCalls = 0; DiagPostWrapCalls = 0;
            DiagTickWatcherBypassed = 0; DiagTickPath1uBypassed = 0;
            DiagTickWatcherTicks = 0; DiagTickWatcherCalls = 0; DiagTickWatcherPrecompHits = 0; DiagTickPrecompTicks = 0; DiagTickPrecompBuilt = 0;
            DiagActiveUpdateEntitiesCount = 0; DiagTotalEntitiesCount = 0;
            DiagTickWindowCount = 0; DiagPhysStepWindowCount = 0;
            DiagPerfSamplingActive = enableSampling;
        }

        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPostfix]
        public static void UpdateGameLogic_Postfix(DXGame __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive) return;

            if (_dedicatedAsyncActive) return;

            // Async overlapped tick continues running in _TaskLogic (and chained UpdatePhysics)
            // across RenderFrame/Present and is finalized by StartFrame -> FlushPendingAsyncTick
            // before ComputeSceneAvance / OnSetCamera of the next frame.
            if (_asyncTickInFlight && !_inSingleTickStep)
            {
                var origTask = _fTaskLogic?.GetValue(__instance) as System.Threading.Tasks.Task;
                if (origTask != null && _fTaskLogic != null)
                {
                    long launchTs = _asyncLaunchTs;
                    var cont = origTask.ContinueWith(_ =>
                    {
                        double swMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        double workerMs = (System.Diagnostics.Stopwatch.GetTimestamp() - launchTs) * swMs;
                        _diagWorkerLogicMs = _diagWorkerLogicMs <= 0.0 ? workerMs : _diagWorkerLogicMs * 0.9 + workerMs * 0.1;
                        DiagAvgTickLogicMs = _diagWorkerLogicMs;

                        try
                        {
                            if (DXGame.IsIADeterministic && _mUpdatePhysics != null
                                && __instance != null && !__instance.Disposing
                                && !LockstepClock.LoadingLevel && !LockstepClock.PausedBySession
                                && DXScene.Current != null && DXScene.Current.RootObject is DXIsometricGridObject)
                            {
                                double tPhysStart = System.Diagnostics.Stopwatch.GetTimestamp() * swMs;
                                _asyncPhysicsChained = true;
                                LockstepClock.AllowPhysicsThisFrame = true;
                                _mUpdatePhysics.Invoke(__instance, null);
                                LockstepClock.AllowPhysicsThisFrame = false;
                                double pMs = System.Diagnostics.Stopwatch.GetTimestamp() * swMs - tPhysStart;
                                if (pMs > 0.2)
                                {
                                    DiagAvgPhysicsMs = DiagAvgPhysicsMs <= 0.0 ? pMs : DiagAvgPhysicsMs * 0.9 + pMs * 0.1;
                                    DiagPhysStepWindowCount++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ModLogger.Warn($"[LockstepClock] Async UpdatePhysics continuation error: {ex}");
                        }
                        finally
                        {
                            LockstepClock.AllowPhysicsThisFrame = false;
                            LockstepClock.LogicTickActive = false;
                            LockstepClock.LogicThreadId = 0;
                            Volatile.Write(ref _lastAsyncWorkerWorkTicks, System.Diagnostics.Stopwatch.GetTimestamp() - launchTs);
                        }
                    }, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
                    _fTaskLogic.SetValue(__instance, cont);
                }
                return;
            }

            _mWaitForGameLogic?.Invoke(__instance, null);
            LockstepClock.LogicTickActive = false;
            LockstepClock.LogicThreadId = 0;
            LockstepClock.InDeterministicContext = false;
        }

        [HarmonyPatch(typeof(DXGame), "WaitForGameLogic")]
        [HarmonyPrefix]
        public static void WaitForGameLogic_Prefix(DXGame __instance)
        {
            if (_asyncTickInFlight && !_inFinalizeAsyncTick)
                _asyncWaitStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_dedicatedAsyncActive && !_inFinalizeAsyncTick)
            {
                _simWorkDone.Wait();
                _fTaskLogic?.SetValue(__instance, null);
            }
        }

        [HarmonyPatch(typeof(DXGame), "WaitForGameLogic")]
        [HarmonyPostfix]
        public static void WaitForGameLogic_Postfix(DXGame __instance)
        {
            if (!_asyncTickInFlight || _inFinalizeAsyncTick) return;
            _inFinalizeAsyncTick = true;
            try
            {
                bool didChainedPhysics = _asyncPhysicsChained;
                bool didAsyncPhysStep = _asyncPhysicsStepped;
                _asyncTickInFlight = false;
                _dedicatedAsyncActive = false;
                _asyncPhysicsChained = false;
                _asyncPhysicsStepped = false;
                double swFreqMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double waitMs = _asyncWaitStartTs > 0
                    ? (System.Diagnostics.Stopwatch.GetTimestamp() - _asyncWaitStartTs) * swFreqMs
                    : 0.0;
                _asyncWaitStartTs = 0;
                _diagAsyncWaitMs = _diagAsyncWaitMs <= 0.0 ? waitMs : _diagAsyncWaitMs * 0.9 + waitMs * 0.1;

                LockstepClock.LogicTickActive = false;
                LockstepClock.LogicThreadId = 0;
                LockstepClock.InDeterministicContext = false;

                if (__instance == null || __instance.Disposing || !LockstepClock.IsDeterministicModeActive
                    || LockstepClock.LoadingLevel || DXScene.Current == null || !(DXScene.Current.RootObject is DXIsometricGridObject))
                {
                    lock (_deferredAsyncLock) { _deferredAsyncPostMethods.Clear(); }
                    return;
                }

                long mainFinalizeStartTs = System.Diagnostics.Stopwatch.GetTimestamp();

                if (!didChainedPhysics && DXGame.IsIADeterministic && _mUpdatePhysics != null)
                {
                    double tPhysStart = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs;
                    LockstepClock.AllowPhysicsThisFrame = true;
                    _mUpdatePhysics.Invoke(__instance, null);
                    LockstepClock.AllowPhysicsThisFrame = false;
                    double pMs = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs - tPhysStart;
                    if (pMs > 0.2)
                    {
                        DiagAvgPhysicsMs = DiagAvgPhysicsMs <= 0.0 ? pMs : DiagAvgPhysicsMs * 0.9 + pMs * 0.1;
                        DiagPhysStepWindowCount++;
                    }
                }

                if (didAsyncPhysStep)
                {
                    RenderVisualInterpolationPatch.OnPhysicsStepCompleted();
                    CSpritePullIsolationPatch.RunDeterministicPullUpdates(__instance);
                }

                List<Action> deferred = null;
                lock (_deferredAsyncLock)
                {
                    if (_deferredAsyncPostMethods.Count > 0)
                    {
                        deferred = new List<Action>(_deferredAsyncPostMethods);
                        _deferredAsyncPostMethods.Clear();
                    }
                }
                {
                    long tDrainStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool prevRendering = __instance.RenderingFrame;
                    bool prevDet = LockstepClock.InDeterministicContext;
                    __instance.RenderingFrame = false;
                    LockstepClock.InDeterministicContext = false;
                    try
                    {
                        if (deferred != null)
                        {
                            for (int i = 0; i < deferred.Count; i++)
                                LockstepClock.RunInDeterministicContext(deferred[i]);
                        }
                        // Flush nested/queued post-methods deterministically BEFORE StateChecksum so
                        // async ticks settle at the exact same tick boundary as sync catchup ticks.
                        __instance.PostMethods_OnFinishFrame?.ExecuteAll();
                        __instance.PostMethods_OnSafeLogicAndDrawing?.ExecuteAll();
                        __instance.PostMethods_OnStartFrame?.ExecuteAll();
                    }
                    finally
                    {
                        LockstepClock.InDeterministicContext = prevDet;
                        __instance.RenderingFrame = prevRendering;
                    }
                    DiagTickPostDrainTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tDrainStart;
                }

                if (Sync.CoopSessionManager.IsInSyncedLevel)
                {
                    long tHashStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    Sync.CoopSessionManager.OnSimulationTickCompleted(__instance.IAIterationInt);
                    DiagTickHashTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tHashStart;
                }
                long workerTicks = Volatile.Read(ref _lastAsyncWorkerWorkTicks);
                Volatile.Write(ref _lastAsyncWorkerWorkTicks, 0L);
                long mainFinalizeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - mainFinalizeStartTs;
                RecordCompletedTickPerf((workerTicks + mainFinalizeTicks) * swFreqMs);

                RenderVisualInterpolationPatch.OnLogicTickCompleted(1);

                DiagTickWindowCount++;
                if (DiagTickWindowCount >= 60)
                    LogPerfWindow(__instance, 1);
            }
            finally
            {
                _inFinalizeAsyncTick = false;
            }
        }

        // 4. Suppress the engine's background GameTime/physics threads while deterministic:
        //    they advance GameTime by real frame time and would destroy fixed-step reproducibility.
        //    UpdatePhysics() is still called synchronously from SetCamera when IsIADeterministic.
        [HarmonyPatch(typeof(DXGame), "LoadPhysics")]
        [HarmonyPrefix]
        public static bool LoadPhysics_Prefix() => !LockstepClock.IsDeterministicModeActive;

        [HarmonyPatch(typeof(DXGame), "LoadPhysicsOld")]
        [HarmonyPrefix]
        public static bool LoadPhysicsOld_Prefix() => !LockstepClock.IsDeterministicModeActive;

        // Mark the physics section so DXRandom.Global draws from physics callbacks use a stream that
        // render-phase draws cannot perturb, and so DeterministicTaskPatch only serializes the two
        // position-affecting For-loops (not scene culling / texture baking / level tables).
        [HarmonyPatch(typeof(DXGame), "UpdatePhysics")]
        [HarmonyPrefix]
        public static bool UpdatePhysics_Enter(out uint __state)
        {
            __state = 0;
            if (!LockstepClock.IsDeterministicModeActive) return true;
            if (!LockstepClock.AllowPhysicsThisFrame || LockstepClock.LoadingLevel || LockstepClock.PausedBySession)
                return false;

            PrecomputedPerceptionManager.EndWatchWindow();
            __state = DXPhysicWorld.NUpdates;
            LockstepClock.AllowPhysicsThisFrame = false;
            LockstepClock.PhysicsThreadId = Thread.CurrentThread.ManagedThreadId;
            LockstepClock.InPhysicsContext = true;
            LockstepClock.InPhysicsStep = true;
            LockstepClock.InPhysicsReadback = false;
            LockstepClock.MergedPhysicsReadbackHandled = false;
            LockstepClock._physPullWorkerCount = 0;
            LockstepClock._physPullLevel = null;
            return true;
        }

        [HarmonyPatch(typeof(DXGame), "OnFinishPhysics")]
        [HarmonyPostfix]
        public static void OnFinishPhysics_Postfix()
        {
            if (LockstepClock.InPhysicsStep)
                LockstepClock.InPhysicsReadback = true;
        }

        [HarmonyPatch(typeof(DXGame), "UpdatePhysics")]
        [HarmonyPostfix]
        public static void UpdatePhysics_Exit(uint __state)
        {
            bool didStep = LockstepClock.InPhysicsStep;
            bool mergedHandled = LockstepClock.MergedPhysicsReadbackHandled;
            LockstepClock.InPhysicsContext = false;
            LockstepClock.PhysicsThreadId = 0;
            LockstepClock.InPhysicsStep = false;
            LockstepClock.InPhysicsReadback = false;
            LockstepClock.MergedPhysicsReadbackHandled = false;

            if (LockstepClock.IsDeterministicModeActive)
            {
                DXPhysicWorld.TimeLastBodyPosition = DXPhysicWorld.TimeBodyPosition;
                if (didStep && DXPhysicWorld.NUpdates != __state && !LockstepClock.LoadingLevel)
                {
                    if (!mergedHandled)
                        LockstepClock.QuantizePhysicsBodiesPostStep();
                    RenderVisualInterpolationPatch.PublishSnapshot();
                    if (_asyncTickInFlight)
                    {
                        _asyncPhysicsStepped = true;
                    }
                    else
                    {
                        RenderVisualInterpolationPatch.OnPhysicsStepCompleted();
                        CSpritePullIsolationPatch.RunDeterministicPullUpdates(DXGame.Current);
                    }
                }
            }
        }

        // Prevent DXEntity.UpdateSceneObjectParallel from mutating authoritative Entity.Position and
        // triggering DXWorldGrid.SetNewCellFor; when inside RenderVisualInterpolationPatch's scene pass,
        // return the thread's already-projected visual position so DXEntity.Position = InterpolatedPosition
        // becomes a zero-work no-op.
        [HarmonyPatch(typeof(DXPhysicBody), nameof(DXPhysicBody.InterpolatedPosition), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool InterpolatedPosition_Prefix(DXPhysicBody __instance, ref PointF __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            if (RenderVisualInterpolationPatch.TryGetProjectedEntityPos(__instance?.GameEntity, out PointF visualPos))
            {
                __result = visualPos;
                return false;
            }
            __result = __instance.PositionComputed;
            return false;
        }

        private static long _lastSimFrameThrottleTs;

        // Remember which thread runs the frame loop; DXRandomCtorPatch uses it to keep the
        // level-loading worker's seed sequence synced while the UI thread stays off that counter.
        // Accumulate wall-clock dtMs FIRST, then flush any completed async tick so OnPhysicsStepCompleted
        // preserves exact overshoot without jumping frame 1 of the next physics interval.
        [HarmonyPatch(typeof(DXGame), "StartFrame")]
        [HarmonyPrefix]
        public static void StartFrame_CaptureMainThread()
        {
            if (LockstepClock.MainThreadId == 0) LockstepClock.CaptureMainThread();

            int minFrameMs = Testing.TestHarnessConfig.SimMinFrameMs;
            if (minFrameMs > 0 && Sync.CoopSessionManager.IsInSyncedLevel && !LockstepClock.LoadingLevel)
            {
                double swToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_lastSimFrameThrottleTs > 0)
                {
                    double elapsedMs = (nowTs - _lastSimFrameThrottleTs) * swToMs;
                    int waitMs = (int)(minFrameMs - elapsedMs);
                    if (waitMs > 0 && waitMs <= 250)
                        Thread.Sleep(waitMs);
                }
                _lastSimFrameThrottleTs = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            RenderVisualInterpolationPatch.UpdateFrameAlpha();
            FlushPendingAsyncTick(DXGame.Current);
        }

        // RenderFrame() runs in parallel with DedicatedSimWorker, but FinishFrame() immediately runs
        // DXSystem.OnFinishFrame() and ProcessInputEvents(false) (mouse clicks/moves/hotkeys, command UI
        // CanExecute/IsVisible, Structure diagonal/EntitiesOfType checks, OCellAreaCursor). Flush any in-flight
        // async tick BEFORE FinishFrame touches simulation collections, FactionContextManager, or templates.
        [HarmonyPatch(typeof(DXGame), "FinishFrame")]
        [HarmonyPrefix]
        public static void FinishFrame_FlushAsyncTick(DXGame __instance)
        {
            if (_asyncTickInFlight && !_inFinalizeAsyncTick
                && LockstepClock.MainThreadId != 0
                && Thread.CurrentThread.ManagedThreadId == LockstepClock.MainThreadId)
            {
                FlushPendingAsyncTick(__instance);
            }
        }
    }

    /// <summary>
    /// Isolates visual scene interpolation, resync snap smoothing, and visual GameTime
    /// from deterministic simulation state across the entire DXGame.SetCamera scene-update pass
    /// (covering CSprite, CSelectable, CShadow, custom components, and isometric Z-sorting).
    /// </summary>
    public static class RenderVisualInterpolationPatch
    {
        public struct RestoreState
        {
            public DXEntity Entity;
            public PointF OrigPos;
        }

        private struct VisualSnapEntry
        {
            public float OffsetX;
            public float OffsetY;
            public long StartTimestamp;
        }

        private struct VisualInterpItem
        {
            public DXEntity Entity;
            public PointF LastPos;
            public PointF CurPos;
        }

        private const int InitialInterpBufferCap = 4096;
        private static VisualInterpItem[][] _interpBuffers =
        {
            new VisualInterpItem[InitialInterpBufferCap],
            new VisualInterpItem[InitialInterpBufferCap]
        };
        private static readonly int[] _interpCounts = new int[2];
        private static int _activeReadInterpIdx;
        [ThreadStatic] private static HashSet<ulong> _projectedSnapEntityIds;

        private const double VisualSnapExpireMs = 600.0;
        private const double VisualSnapTauMs = 180.0;
        private const float MaxVisualSnapOffsetCells = 3.5f;

        private static readonly double _swToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static long _lastStartFrameWallTs;
        private static double _accumPhysicsElapsedMs;
        private static double _accumLogicElapsedMs;
        private static double _visualGameTimeRemainderMs;
        private static volatile bool _hasPhysicsStep;
        private static volatile float _frameInterpAlpha = -1f;

        private static readonly ConcurrentDictionary<ulong, VisualSnapEntry> _visualSnapOffsets =
            new ConcurrentDictionary<ulong, VisualSnapEntry>();
        private static readonly List<ulong> _staleVisualSnapIds = new List<ulong>();
        private static volatile bool _hasVisualSnapOffsets;

        private static long _currentFrameTimestamp;

        private static bool _posAccessorsCompiled;
        private static Func<DXEntity, PointF> _fastGetRawPos;
        private static Action<DXEntity, PointF> _fastSetRawPos;

        private static volatile bool _inSetCamera;
        private static volatile bool _inVisualSceneUpdate;
        private static double _savedAuthGameTime;
        private static bool _hasSavedVisualGameTime;
        private static readonly List<RestoreState> _projectedRestoreList = new List<RestoreState>(2048);

        internal static bool InSetCamera => _inSetCamera;

        internal static void BeginSetCamera() => _inSetCamera = true;
        internal static void EndSetCamera(DXGame game)
        {
            _inSetCamera = false;
            ExitVisualSceneUpdate(game);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryGetProjectedEntityPos(DXEntity ent, out PointF pos)
        {
            if (_inVisualSceneUpdate && ent != null && _fastGetRawPos != null)
            {
                pos = _fastGetRawPos(ent);
                return true;
            }
            pos = default;
            return false;
        }

        public static void RecordVisualSnapOffset(ulong entityId, float offsetX, float offsetY)
        {
            if (entityId == 0) return;
            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_visualSnapOffsets.TryGetValue(entityId, out var prev))
            {
                double prevAgeMs = (nowTs - prev.StartTimestamp) * _swToMs;
                if (prevAgeMs > 0.0 && prevAgeMs < VisualSnapExpireMs)
                {
                    float prevDecay = (float)Math.Exp(-prevAgeMs / VisualSnapTauMs);
                    offsetX += prev.OffsetX * prevDecay;
                    offsetY += prev.OffsetY * prevDecay;
                }
            }

            float magSq = offsetX * offsetX + offsetY * offsetY;
            if (magSq < 0.0025f) return;
            if (magSq > MaxVisualSnapOffsetCells * MaxVisualSnapOffsetCells)
            {
                float scale = MaxVisualSnapOffsetCells / (float)Math.Sqrt(magSq);
                offsetX *= scale;
                offsetY *= scale;
            }

            _visualSnapOffsets[entityId] = new VisualSnapEntry
            {
                OffsetX = offsetX,
                OffsetY = offsetY,
                StartTimestamp = nowTs
            };
            _hasVisualSnapOffsets = true;
        }

        private static void EnsureRawPosAccessors()
        {
            if (_posAccessorsCompiled) return;
            _posAccessorsCompiled = true;
            try
            {
                var fPos = typeof(DXEntity).GetField("_Position", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fPos == null || fPos.FieldType != typeof(PointF))
                {
                    ModLogger.Warn("[RenderInterp] DXEntity._Position field not found; staying discrete.");
                    return;
                }

                var dmGet = new DynamicMethod("TABCoop_GetRawEntityPos", typeof(PointF), new[] { typeof(DXEntity) },
                    typeof(RenderVisualInterpolationPatch).Module, skipVisibility: true);
                var ilG = dmGet.GetILGenerator();
                ilG.Emit(OpCodes.Ldarg_0);
                ilG.Emit(OpCodes.Ldfld, fPos);
                ilG.Emit(OpCodes.Ret);
                _fastGetRawPos = (Func<DXEntity, PointF>)dmGet.CreateDelegate(typeof(Func<DXEntity, PointF>));

                var dmSet = new DynamicMethod("TABCoop_SetRawEntityPos", typeof(void), new[] { typeof(DXEntity), typeof(PointF) },
                    typeof(RenderVisualInterpolationPatch).Module, skipVisibility: true);
                var ilS = dmSet.GetILGenerator();
                ilS.Emit(OpCodes.Ldarg_0);
                ilS.Emit(OpCodes.Ldarg_1);
                ilS.Emit(OpCodes.Stfld, fPos);
                ilS.Emit(OpCodes.Ret);
                _fastSetRawPos = (Action<DXEntity, PointF>)dmSet.CreateDelegate(typeof(Action<DXEntity, PointF>));

                ModLogger.Info("[Perf] Compiled DynamicMethods TABCoop_GetRawEntityPos / SetRawEntityPos.");
            }
            catch (Exception ex)
            {
                _fastGetRawPos = null;
                _fastSetRawPos = null;
                ModLogger.Warn($"[RenderInterp] Failed to compile raw position accessors: {ex.Message}");
            }
        }

        internal static void Reset()
        {
            _hasPhysicsStep = false;
            _accumPhysicsElapsedMs = 0.0;
            _accumLogicElapsedMs = 0.0;
            _visualGameTimeRemainderMs = 0.0;
            _lastStartFrameWallTs = 0;
            _frameInterpAlpha = -1f;
            _hasVisualSnapOffsets = false;
            _visualSnapOffsets.Clear();
            _currentFrameTimestamp = 0;
            _inSetCamera = false;
            _inVisualSceneUpdate = false;
            _hasSavedVisualGameTime = false;
            Volatile.Write(ref _interpCounts[0], 0);
            Volatile.Write(ref _interpCounts[1], 0);
            Volatile.Write(ref _activeReadInterpIdx, 0);
        }

        internal static void PublishSnapshot()
        {
            if (!LockstepClock.IsDeterministicModeActive
                || Testing.DeterministicReplayVerifier.IsRunning
                || LockstepClock.LoadingLevel
                || DXGame.IsInDesigner)
                return;

            bool physLockTaken = false;
            try
            {
                Monitor.TryEnter(DXPhysicWorld.Sync, ref physLockTaken);
                if (!physLockTaken) return;

                var bodies = DXPhysicWorld.Bodies;
                int writeIdx = 1 - Volatile.Read(ref _activeReadInterpIdx);
                if (bodies == null || bodies.Count == 0)
                {
                    Volatile.Write(ref _interpCounts[writeIdx], 0);
                    Volatile.Write(ref _activeReadInterpIdx, writeIdx);
                    return;
                }

                VisualInterpItem[] buf = _interpBuffers[writeIdx];
                int count = bodies.Count;
                int outCount = 0;

                for (int i = 0; i < count; i++)
                {
                    if (i >= bodies.Count) break;
                    var body = bodies[i];
                    if (body == null || body.Handle == IntPtr.Zero) continue;
                    var ent = body.GameEntity;
                    if (ent == null || ent.Disposed || ent.PhysicalType != DXBodyType.Dynamic) continue;
                    if (ent.SceneObject == null) continue;

                    PointF lastPos = body.LastPositionComputed;
                    PointF curPos = body.PositionComputed;
                    if (lastPos == curPos) continue;

                    if (outCount >= buf.Length)
                    {
                        int nextCap = Math.Max(buf.Length * 2, count);
                        var grown = new VisualInterpItem[nextCap];
                        Array.Copy(buf, grown, outCount);
                        buf = grown;
                        Volatile.Write(ref _interpBuffers[writeIdx], buf);
                    }

                    buf[outCount++] = new VisualInterpItem
                    {
                        Entity = ent,
                        LastPos = lastPos,
                        CurPos = curPos
                    };
                }

                Volatile.Write(ref _interpCounts[writeIdx], outCount);
                Volatile.Write(ref _activeReadInterpIdx, writeIdx);
            }
            finally
            {
                if (physLockTaken)
                    Monitor.Exit(DXPhysicWorld.Sync);
            }
        }

        internal static void OnLogicTickCompleted(int ticksRan)
        {
            if (ticksRan <= 0) return;
            if (ticksRan > 1)
            {
                _accumLogicElapsedMs = 0.0;
            }
            else
            {
                _accumLogicElapsedMs -= LockstepClock.MsPerLogicTick;
                if (_accumLogicElapsedMs < 0.0) _accumLogicElapsedMs = 0.0;
                if (_accumLogicElapsedMs > LockstepClock.MsPerLogicTick * 0.95)
                    _accumLogicElapsedMs = LockstepClock.MsPerLogicTick * 0.95;
            }
            _visualGameTimeRemainderMs = _accumLogicElapsedMs;
        }

        internal static void OnPhysicsStepCompleted()
        {
            if (_hasPhysicsStep)
            {
                _accumPhysicsElapsedMs -= LockstepClock.MsPerPhysicsTick;
                if (_accumPhysicsElapsedMs < 0.0) _accumPhysicsElapsedMs = 0.0;
                if (_accumPhysicsElapsedMs > LockstepClock.MsPerPhysicsTick * 0.95)
                    _accumPhysicsElapsedMs = LockstepClock.MsPerPhysicsTick * 0.95;
            }
            else
            {
                _accumPhysicsElapsedMs = 0.0;
                _hasPhysicsStep = true;
            }

            float a = (float)(_accumPhysicsElapsedMs / LockstepClock.MsPerPhysicsTick);
            _frameInterpAlpha = a < 0f ? 0f : (a > 1f ? 1f : a);
        }

        internal static void UpdateFrameAlpha()
        {
            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            double dtMs = _lastStartFrameWallTs > 0 ? (nowTs - _lastStartFrameWallTs) * _swToMs : 0.0;
            _lastStartFrameWallTs = nowTs;
            _currentFrameTimestamp = nowTs;

            var game = DXGame.Current;

            if (_hasVisualSnapOffsets)
            {
                _staleVisualSnapIds.Clear();
                foreach (var kv in _visualSnapOffsets)
                {
                    if ((nowTs - kv.Value.StartTimestamp) * _swToMs > VisualSnapExpireMs)
                        _staleVisualSnapIds.Add(kv.Key);
                }
                for (int i = 0; i < _staleVisualSnapIds.Count; i++)
                    _visualSnapOffsets.TryRemove(_staleVisualSnapIds[i], out _);
                _hasVisualSnapOffsets = !_visualSnapOffsets.IsEmpty;
            }

            if (!LockstepClock.IsDeterministicModeActive
                || Testing.DeterministicReplayVerifier.IsRunning
                || LockstepClock.LoadingLevel
                || !_hasPhysicsStep
                || DXGame.IsInDesigner
                || game == null
                || game.Disposing
                || DXScene.Current == null
                || !(DXScene.Current.RootObject is DXIsometricGridObject)
                || Core.GameBridge.IsGameLoading())
            {
                _frameInterpAlpha = -1f;
                _visualGameTimeRemainderMs = 0.0;
                return;
            }

            EnsureRawPosAccessors();
            if (_fastGetRawPos == null || _fastSetRawPos == null)
            {
                _frameInterpAlpha = -1f;
                _visualGameTimeRemainderMs = 0.0;
                return;
            }

            if (!LockstepClock.PausedBySession && game != null && !game.Paused && game.GameSpeed > 0.001)
            {
                double clampedDt = dtMs < 0.0 ? 0.0 : (dtMs > 100.0 ? 100.0 : dtMs);
                double scaledDt = clampedDt * game.GameSpeed;

                _accumPhysicsElapsedMs += scaledDt;
                double maxPhysAccum = LockstepClock.MsPerPhysicsTick * 1.35;
                if (_accumPhysicsElapsedMs > maxPhysAccum)
                    _accumPhysicsElapsedMs = maxPhysAccum;

                _accumLogicElapsedMs += scaledDt;
                double maxLogicAccum = LockstepClock.MsPerLogicTick * 1.35;
                if (_accumLogicElapsedMs > maxLogicAccum)
                    _accumLogicElapsedMs = maxLogicAccum;
                _visualGameTimeRemainderMs = Math.Min(LockstepClock.MsPerLogicTick * 0.98, _accumLogicElapsedMs);
            }
            else
            {
                _visualGameTimeRemainderMs = 0.0;
            }

            float a = (float)(_accumPhysicsElapsedMs / LockstepClock.MsPerPhysicsTick);
            _frameInterpAlpha = a < 0f ? 0f : (a > 1f ? 1f : a);
        }

        internal static void EnterVisualSceneUpdate(DXGame game)
        {
            if (!LockstepClock.IsDeterministicModeActive
                || Testing.DeterministicReplayVerifier.IsRunning
                || LockstepClock.LoadingLevel
                || DXGame.IsInDesigner
                || game == null
                || game.Disposing
                || DXScene.Current == null
                || !(DXScene.Current.RootObject is DXIsometricGridObject)
                || Core.GameBridge.IsGameLoading())
                return;

            EnsureRawPosAccessors();
            if (_fastGetRawPos == null || _fastSetRawPos == null) return;

            if (_inVisualSceneUpdate)
                ExitVisualSceneUpdate(game);

            _inVisualSceneUpdate = true;
            _hasSavedVisualGameTime = false;
            if (!LockstepClock.PausedBySession && !game.Paused && game.GameSpeed > 0.001 && _visualGameTimeRemainderMs >= 1.0)
            {
                _savedAuthGameTime = game.GameTime;
                _hasSavedVisualGameTime = true;
                LockstepClock.SetDeterministicGameTime(game, _savedAuthGameTime + _visualGameTimeRemainderMs);
            }

            float alpha = _frameInterpAlpha;
            if (alpha < 0f) return;

            int readIdx = Volatile.Read(ref _activeReadInterpIdx);
            int count = Volatile.Read(ref _interpCounts[readIdx]);
            VisualInterpItem[] snapBuf = Volatile.Read(ref _interpBuffers[readIdx]);
            bool hasSnaps = _hasVisualSnapOffsets;
            if (count <= 0 && !hasSnaps) return;

            _projectedRestoreList.Clear();
            long nowTs = _currentFrameTimestamp;
            HashSet<ulong> seenSnapIds = null;
            if (hasSnaps)
            {
                seenSnapIds = _projectedSnapEntityIds ?? (_projectedSnapEntityIds = new HashSet<ulong>());
                seenSnapIds.Clear();
            }

            if (snapBuf != null && count > 0)
            {
                int safeCount = Math.Min(count, snapBuf.Length);
                for (int i = 0; i < safeCount; i++)
                {
                    VisualInterpItem item = snapBuf[i];
                    var ent = item.Entity;
                    if (ent == null || ent.Disposed || ent.PhysicalType != DXBodyType.Dynamic || ent.SceneObject == null)
                        continue;

                    PointF lastPos = item.LastPos;
                    PointF curPos = item.CurPos;

                    float snapOffX = 0f, snapOffY = 0f;
                    if (hasSnaps && _visualSnapOffsets.TryGetValue(ent.ID, out var snap))
                    {
                        seenSnapIds.Add(ent.ID);
                        double ageMs = (nowTs - snap.StartTimestamp) * _swToMs;
                        if (ageMs < 0.0) ageMs = 0.0;
                        if (ageMs < VisualSnapExpireMs)
                        {
                            float decay = (float)Math.Exp(-ageMs / VisualSnapTauMs);
                            snapOffX = snap.OffsetX * decay;
                            snapOffY = snap.OffsetY * decay;
                        }
                    }

                    if (lastPos == curPos && snapOffX == 0f && snapOffY == 0f)
                        continue;

                    PointF origPos = _fastGetRawPos(ent);
                    float ddx = origPos.X - curPos.X;
                    float ddy = origPos.Y - curPos.Y;
                    if (ddx * ddx + ddy * ddy > 0.01f) continue;

                    PointF basePos = lastPos == curPos
                        ? curPos
                        : new PointF(
                            lastPos.X + (curPos.X - lastPos.X) * alpha,
                            lastPos.Y + (curPos.Y - lastPos.Y) * alpha);
                    PointF interpPos = new PointF(basePos.X + snapOffX, basePos.Y + snapOffY);
                    if (interpPos == origPos) continue;

                    _projectedRestoreList.Add(new RestoreState { Entity = ent, OrigPos = origPos });
                    _fastSetRawPos(ent, interpPos);
                }
            }

            if (hasSnaps)
            {
                foreach (var kv in _visualSnapOffsets)
                {
                    if (seenSnapIds.Contains(kv.Key)) continue;
                    double ageMs = (nowTs - kv.Value.StartTimestamp) * _swToMs;
                    if (ageMs < 0.0) ageMs = 0.0;
                    if (ageMs >= VisualSnapExpireMs) continue;

                    DXEntity ent = game.GetEntityByID(kv.Key);
                    if (ent == null || ent.Disposed || ent.PhysicalType != DXBodyType.Dynamic || ent.SceneObject == null)
                        continue;

                    float decay = (float)Math.Exp(-ageMs / VisualSnapTauMs);
                    float snapOffX = kv.Value.OffsetX * decay;
                    float snapOffY = kv.Value.OffsetY * decay;
                    if (snapOffX == 0f && snapOffY == 0f) continue;

                    PointF origPos = _fastGetRawPos(ent);
                    PointF interpPos = new PointF(origPos.X + snapOffX, origPos.Y + snapOffY);
                    if (interpPos == origPos) continue;

                    _projectedRestoreList.Add(new RestoreState { Entity = ent, OrigPos = origPos });
                    _fastSetRawPos(ent, interpPos);
                }
            }
        }

        internal static void ExitVisualSceneUpdate(DXGame game)
        {
            if (!_inVisualSceneUpdate) return;
            _inVisualSceneUpdate = false;

            if (_fastSetRawPos != null && _projectedRestoreList.Count > 0)
            {
                for (int i = 0; i < _projectedRestoreList.Count; i++)
                {
                    var item = _projectedRestoreList[i];
                    if (item.Entity != null)
                        _fastSetRawPos(item.Entity, item.OrigPos);
                }
                _projectedRestoreList.Clear();
            }

            if (_hasSavedVisualGameTime && game != null)
            {
                LockstepClock.SetDeterministicGameTime(game, _savedAuthGameTime);
                _hasSavedVisualGameTime = false;
            }
        }
    }

    [HarmonyPatch(typeof(DXGame), "SetCamera")]
    public static class SetCameraVisualPassPatch
    {
        [HarmonyPrefix]
        public static void Prefix() => RenderVisualInterpolationPatch.BeginSetCamera();

        [HarmonyFinalizer]
        public static void Finalizer(DXGame __instance) => RenderVisualInterpolationPatch.EndSetCamera(__instance);
    }

    [HarmonyPatch(typeof(DXWorldGrid), nameof(DXWorldGrid.UpdateVisibleCells), new[] { typeof(RectangleF), typeof(int) })]
    public static class UpdateVisibleCellsVisualPassPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (RenderVisualInterpolationPatch.InSetCamera)
                RenderVisualInterpolationPatch.EnterVisualSceneUpdate(DXGame.Current);
        }
    }

    /// <summary>
    /// Smooths camera panning frame deltas during ZXSystemGameLevel.OnSetCamera and lifts the
    /// engine's 0.7f per-frame displacement clamp (which saturates at >=16.7ms / wide zoom and
    /// causes camera pan velocity to drop/stutter on slower frames).
    /// </summary>
    [HarmonyPatch]
    public static class SmoothCameraPanPatch
    {
        public struct CamState
        {
            public bool Active;
            public double OrigElapsed;
            public RectangleF PrevCam;
            public double UsedDtMs;
        }

        private static readonly double _swToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static long _lastCamTs;
        private static double _smoothedDtMs = 16.67;
        private static MethodInfo _mApplyCamBounds;

        public static MethodBase TargetMethod()
        {
            Type tLevelSys = GameBridge.TypeZXSystemGameLevel ?? AccessTools.TypeByName(ObfuscatedSymbols.Type_ZXSystemGameLevel);
            return tLevelSys != null ? AccessTools.Method(tLevelSys, "OnSetCamera", Type.EmptyTypes) : null;
        }

        private static FieldInfo _fIntroAnim;
        private static PropertyInfo _pNewCamCenter;
        private static IntPtr _ownHwnd;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private static bool IsGameWindowForeground(DXGame game)
        {
            if (game == null || !game.Focused) return false;
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                if (_ownHwnd == IntPtr.Zero)
                    _ownHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                return _ownHwnd == IntPtr.Zero || fg == _ownHwnd;
            }
            catch { return game.Focused; }
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance, out CamState __state)
        {
            __state = default;
            if (!LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel)
                return true;
            var scene = DXScene.Current;
            if (scene == null)
                return true;

            // In FullBorderless mode, an unfocused window's ResetInput() (or a virtual-display
            // idle cursor at (0,0)) matches ViewArea.TopLeft (0,0) and causes vanilla OnSetCamera
            // to continuously edge-scroll toward the top-left corner of the map.
            var game = DXGame.Current;
            bool suppressIdleDrift = !IsGameWindowForeground(game)
                || (Testing.TestHarnessConfig.ControlPort > 0 && DXMouseState.Current.Position.IsEmpty);
            if (suppressIdleDrift && __instance != null)
            {
                if (_fIntroAnim == null)
                    _fIntroAnim = AccessTools.Field(__instance.GetType(), ObfuscatedSymbols.Field_LevelSys_CameraIntroAnim);
                if (_pNewCamCenter == null)
                    _pNewCamCenter = AccessTools.Property(__instance.GetType(), "NewCameraCenter");
                bool introActive = _fIntroAnim != null && _fIntroAnim.GetValue(null) is bool b && b;
                bool hasNewCenter = _pNewCamCenter != null && _pNewCamCenter.GetValue(__instance, null) != null;
                if (!introActive && !hasNewCenter)
                    return false;
            }

            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            double rawDtMs = _lastCamTs > 0 ? (nowTs - _lastCamTs) * _swToMs : 16.67;
            _lastCamTs = nowTs;
            if (rawDtMs < 2.0 || rawDtMs > 200.0) rawDtMs = 16.67;
            _smoothedDtMs = _smoothedDtMs * 0.65 + rawDtMs * 0.35;
            double useDtMs = _smoothedDtMs < 4.0 ? 4.0 : (_smoothedDtMs > 55.0 ? 55.0 : _smoothedDtMs);

            __state.Active = true;
            __state.OrigElapsed = scene.ElapsedTime;
            __state.PrevCam = scene.CameraArea;
            __state.UsedDtMs = useDtMs;
            scene.ElapsedTime = useDtMs;
            return true;
        }

        [HarmonyFinalizer]
        public static void Finalizer(object __instance, CamState __state)
        {
            if (!__state.Active)
                return;
            var scene = DXScene.Current;
            if (scene == null)
                return;

            scene.ElapsedTime = __state.OrigElapsed;

            RectangleF curCam = scene.CameraArea;
            RectangleF prevCam = __state.PrevCam;
            if (Math.Abs(curCam.Width - prevCam.Width) < 0.001f
                && Math.Abs(curCam.Height - prevCam.Height) < 0.001f)
            {
                float desiredStep = Math.Min(2.4f, 0.0007f * (float)__state.UsedDtMs * prevCam.Width);
                if (desiredStep > 0.705f)
                {
                    float dx = curCam.X - prevCam.X;
                    float dy = curCam.Y - prevCam.Y;
                    bool pannedX = Math.Abs(Math.Abs(dx) - 0.7f) < 0.02f;
                    bool pannedY = Math.Abs(Math.Abs(dy) - 0.7f) < 0.02f;
                    if (pannedX || pannedY)
                    {
                        float scale = desiredStep / 0.7f;
                        var adjusted = new RectangleF(
                            prevCam.X + (pannedX ? dx * scale : dx),
                            prevCam.Y + (pannedY ? dy * scale : dy),
                            curCam.Width,
                            curCam.Height);
                        if (_mApplyCamBounds == null && __instance != null)
                            _mApplyCamBounds = AccessTools.Method(__instance.GetType(), "#=zTPyBK8tWy$hm", new[] { typeof(RectangleF) });
                        _mApplyCamBounds?.Invoke(__instance, new object[] { adjusted });
                    }
                }
            }
        }
    }

    /// <summary>
    /// Replaces background-thread pathfinding (DXWorldGrid.FindPathConcurrent / HighPriority) with
    /// deterministic synchronous execution on the active logic thread when deterministic mode is active.
    /// This eliminates background worker scheduling races and avoids vanilla's buggy IsMultiplayer branch
    /// in CMovable (which hardcodes cellSize=1 and recurses infinitely on null paths).
    /// </summary>
    [HarmonyPatch(typeof(DXWorldGrid))]
    public static class DeterministicPathfindingPatch
    {
        private struct PathCacheKey : IEquatable<PathCacheKey>
        {
            public int CellSize;
            public int X1, Y1, X2, Y2;
            public uint Mask;
            public bool NearPath;
            public int MaxNodes;

            public bool Equals(PathCacheKey o) =>
                CellSize == o.CellSize && X1 == o.X1 && Y1 == o.Y1 && X2 == o.X2 && Y2 == o.Y2
                && Mask == o.Mask && NearPath == o.NearPath && MaxNodes == o.MaxNodes;

            public override bool Equals(object obj) => obj is PathCacheKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = CellSize;
                    h = (h * 397) ^ X1;
                    h = (h * 397) ^ Y1;
                    h = (h * 397) ^ X2;
                    h = (h * 397) ^ Y2;
                    h = (h * 397) ^ (int)Mask;
                    h = (h * 397) ^ (NearPath ? 1 : 0);
                    h = (h * 397) ^ MaxNodes;
                    return h;
                }
            }
        }

        public struct PathPrewarmItem
        {
            public int CellSize;
            public Point Start;
            public Point End;
            public uint Mask;
            public int? MaxNodes;
        }

        private delegate List<Point> FindPath8Delegate(
            int cellSize,
            Algorithms.PathFinderFast pf,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            int? maxNodes,
            bool useCache);

        private static FieldInfo _movableRequestField;
        private static Func<Dictionary<int, Algorithms.PathFinderFast[]>> _fastGetPathFinders;
        private static Func<Dictionary<int, uint[,]>> _fastGetGrids;
        private static FindPath8Delegate _fastFindPath8;
        private static bool _findPath8Resolved;
        private static int _gridMutationVersion;
        private static int _cachedGridVersion = -1;
        private static readonly Dictionary<PathCacheKey, List<Point>> _exactPathCache =
            new Dictionary<PathCacheKey, List<Point>>(512);
        private const int MaxExactPathCacheEntries = 32768;

        private static readonly object _prewarmPfLock = new object();
        private static readonly Dictionary<int, uint[,]> _cachedGridRefs = new Dictionary<int, uint[,]>(4);
        private static readonly Dictionary<int, Algorithms.PathFinderFast[]> _parallelPfs = new Dictionary<int, Algorithms.PathFinderFast[]>(4);

        private struct CompCacheKey : IEquatable<CompCacheKey>
        {
            public int CellSize;
            public uint Mask;
            public bool Equals(CompCacheKey o) => CellSize == o.CellSize && Mask == o.Mask;
            public override bool Equals(object obj) => obj is CompCacheKey o && Equals(o);
            public override int GetHashCode() => (CellSize * 397) ^ (int)Mask;
        }

        private sealed class CompMapEntry
        {
            public uint[,] GridRef;
            public int Width;
            public int Height;
            public int[] CompIds;
            public int[] CompSizes;
        }

        private static readonly object _compCacheLock = new object();
        private static int _cachedCompVersion = -1;
        private static readonly Dictionary<CompCacheKey, CompMapEntry> _compCache = new Dictionary<CompCacheKey, CompMapEntry>(8);
        private static int[] _compQueueScratch;
        private static int[] _compSizesScratch;

        private struct FlowKey : IEquatable<FlowKey>
        {
            public int CellSize;
            public int TargetX;
            public int TargetY;
            public uint Mask;
            public bool NearPath;

            public bool Equals(FlowKey o) =>
                CellSize == o.CellSize && TargetX == o.TargetX && TargetY == o.TargetY
                && Mask == o.Mask && NearPath == o.NearPath;

            public override bool Equals(object obj) => obj is FlowKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = CellSize;
                    h = (h * 397) ^ TargetX;
                    h = (h * 397) ^ TargetY;
                    h = (h * 397) ^ (int)Mask;
                    h = (h * 397) ^ (NearPath ? 1 : 0);
                    return h;
                }
            }
        }

        private sealed class FlowFieldEntry
        {
            public uint[,] GridRef;
            public int Width;
            public int Height;
            public int[] NextIdx;
            public int[] Dist;
        }

        private static readonly object _flowCacheLock = new object();
        private static int _cachedFlowVersion = -1;
        private static readonly Dictionary<FlowKey, FlowFieldEntry> _flowCache = new Dictionary<FlowKey, FlowFieldEntry>(16);
        private static readonly Dictionary<FlowKey, int> _targetQueryCounts = new Dictionary<FlowKey, int>(64);
        private const int MaxFlowCacheEntries = 32;

        private static void PushHeap(ref ulong[] heap, ref int count, ulong val)
        {
            if (count == heap.Length)
                Array.Resize(ref heap, heap.Length * 2);
            int i = count++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (heap[p] <= val) break;
                heap[i] = heap[p];
                i = p;
            }
            heap[i] = val;
        }

        private static ulong PopHeap(ulong[] heap, ref int count)
        {
            ulong top = heap[0];
            int n = --count;
            if (n > 0)
            {
                ulong last = heap[n];
                int i = 0;
                int half = n >> 1;
                while (i < half)
                {
                    int child = (i << 1) + 1;
                    int right = child + 1;
                    if (right < n && heap[right] < heap[child])
                        child = right;
                    if (last <= heap[child]) break;
                    heap[i] = heap[child];
                    i = child;
                }
                heap[i] = last;
            }
            return top;
        }

        private static FlowFieldEntry BuildFlowFieldCore(uint[,] grid, uint mask, Point target, bool nearPath)
        {
            if (grid == null) return null;
            int gw = grid.GetLength(0);
            int gh = grid.GetLength(1);
            if ((uint)target.X >= (uint)gw || (uint)target.Y >= (uint)gh) return null;
            int total = gw * gh;
            if (total <= 0 || total >= (1 << 20)) return null;

            var dist = new int[total];
            var nextIdx = new int[total];
            for (int i = 0; i < total; i++)
            {
                dist[i] = int.MaxValue;
                nextIdx[i] = -1;
            }

            var heap = new ulong[Math.Min(Math.Max(1024, gw * 8), total)];
            int heapCount = 0;

            bool targetOpen = (grid[target.X, target.Y] & mask) == 0u;
            if (targetOpen)
            {
                int rootIdx = target.Y * gw + target.X;
                dist[rootIdx] = 0;
                nextIdx[rootIdx] = rootIdx;
                PushHeap(ref heap, ref heapCount, ((ulong)0 << 20) | (uint)rootIdx);
            }
            else if (!nearPath)
            {
                return new FlowFieldEntry { GridRef = grid, Width = gw, Height = gh, NextIdx = nextIdx, Dist = dist };
            }
            else
            {
                // Seed all walkable cells directly abutting the contiguous blocked footprint of target (7x7 max radius).
                var bQueue = new int[49];
                var bVisited = new bool[49];
                int bHead = 0, bTail = 0;
                bVisited[3 * 7 + 3] = true;
                bQueue[bTail++] = (target.Y << 16) | (target.X & 0xFFFF);

                while (bHead < bTail)
                {
                    int packed = bQueue[bHead++];
                    int bx = (short)(packed & 0xFFFF);
                    int by = packed >> 16;

                    for (int dir = 0; dir < 4; dir++)
                    {
                        int nx = bx + (dir == 0 ? 1 : dir == 1 ? -1 : 0);
                        int ny = by + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                        if ((uint)nx >= (uint)gw || (uint)ny >= (uint)gh) continue;

                        if ((grid[nx, ny] & mask) == 0u)
                        {
                            int sIdx = ny * gw + nx;
                            if (nextIdx[sIdx] != sIdx)
                            {
                                int dx = nx - target.X;
                                int dy = ny - target.Y;
                                int seedDist = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy) * 100.0);
                                dist[sIdx] = seedDist;
                                nextIdx[sIdx] = sIdx;
                                PushHeap(ref heap, ref heapCount, ((ulong)(uint)seedDist << 20) | (uint)sIdx);
                            }
                        }
                        else
                        {
                            int lx = nx - target.X + 3;
                            int ly = ny - target.Y + 3;
                            if ((uint)lx < 7u && (uint)ly < 7u)
                            {
                                int vi = ly * 7 + lx;
                                if (!bVisited[vi])
                                {
                                    bVisited[vi] = true;
                                    bQueue[bTail++] = (ny << 16) | (nx & 0xFFFF);
                                }
                            }
                        }
                    }
                }

                if (heapCount == 0)
                {
                    for (int r = 1; r <= 6 && heapCount == 0; r++)
                    {
                        int minY = Math.Max(0, target.Y - r);
                        int maxY = Math.Min(gh - 1, target.Y + r);
                        int minX = Math.Max(0, target.X - r);
                        int maxX = Math.Min(gw - 1, target.X + r);
                        for (int ty = minY; ty <= maxY; ty++)
                        {
                            for (int tx = minX; tx <= maxX; tx++)
                            {
                                if ((grid[tx, ty] & mask) != 0u) continue;
                                int sIdx = ty * gw + tx;
                                int dx = tx - target.X;
                                int dy = ty - target.Y;
                                int seedDist = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy) * 100.0);
                                dist[sIdx] = seedDist;
                                nextIdx[sIdx] = sIdx;
                                PushHeap(ref heap, ref heapCount, ((ulong)(uint)seedDist << 20) | (uint)sIdx);
                            }
                        }
                    }
                }

                if (heapCount == 0)
                    return null;
            }

            // Reverse Dijkstra with exact PathFinderFast octile costs (100 cardinal, 141 diagonal)
            // and symmetric non-corner-cutting diagonal walkability checks.
            while (heapCount > 0)
            {
                ulong top = PopHeap(heap, ref heapCount);
                int dCur = (int)(top >> 20);
                int cur = (int)(top & 0xFFFFFUL);
                if (dCur != dist[cur]) continue;

                int cy = cur / gw;
                int cx = cur - cy * gw;

                bool openPosX = cx + 1 < gw && (grid[cx + 1, cy] & mask) == 0u;
                bool openNegX = cx > 0 && (grid[cx - 1, cy] & mask) == 0u;
                bool openPosY = cy + 1 < gh && (grid[cx, cy + 1] & mask) == 0u;
                bool openNegY = cy > 0 && (grid[cx, cy - 1] & mask) == 0u;

                int orthoDist = dCur + 100;
                if (openPosX)
                {
                    int ni = cur + 1;
                    if (orthoDist < dist[ni]) { dist[ni] = orthoDist; nextIdx[ni] = cur; PushHeap(ref heap, ref heapCount, ((ulong)(uint)orthoDist << 20) | (uint)ni); }
                }
                if (openNegX)
                {
                    int ni = cur - 1;
                    if (orthoDist < dist[ni]) { dist[ni] = orthoDist; nextIdx[ni] = cur; PushHeap(ref heap, ref heapCount, ((ulong)(uint)orthoDist << 20) | (uint)ni); }
                }
                if (openPosY)
                {
                    int ni = cur + gw;
                    if (orthoDist < dist[ni]) { dist[ni] = orthoDist; nextIdx[ni] = cur; PushHeap(ref heap, ref heapCount, ((ulong)(uint)orthoDist << 20) | (uint)ni); }
                }
                if (openNegY)
                {
                    int ni = cur - gw;
                    if (orthoDist < dist[ni]) { dist[ni] = orthoDist; nextIdx[ni] = cur; PushHeap(ref heap, ref heapCount, ((ulong)(uint)orthoDist << 20) | (uint)ni); }
                }

                int diagDist = dCur + 141;
                if (openPosX && openPosY && (grid[cx + 1, cy + 1] & mask) == 0u)
                {
                    int ni = cur + gw + 1;
                    if (diagDist < dist[ni]) { dist[ni] = diagDist; nextIdx[ni] = cur; PushHeap(ref heap, ref heapCount, ((ulong)(uint)diagDist << 20) | (uint)ni); }
                }
                if (openPosX && openNegY && (grid[cx + 1, cy - 1] & mask) == 0u)
                {
                    int ni = cur - gw + 1;
                    if (diagDist < dist[ni]) { dist[ni] = diagDist; nextIdx[ni] = cur; PushHeap(ref heap, ref heapCount, ((ulong)(uint)diagDist << 20) | (uint)ni); }
                }
                if (openNegX && openPosY && (grid[cx - 1, cy + 1] & mask) == 0u)
                {
                    int ni = cur + gw - 1;
                    if (diagDist < dist[ni]) { dist[ni] = diagDist; nextIdx[ni] = cur; PushHeap(ref heap, ref heapCount, ((ulong)(uint)diagDist << 20) | (uint)ni); }
                }
                if (openNegX && openNegY && (grid[cx - 1, cy - 1] & mask) == 0u)
                {
                    int ni = cur - gw - 1;
                    if (diagDist < dist[ni]) { dist[ni] = diagDist; nextIdx[ni] = cur; PushHeap(ref heap, ref heapCount, ((ulong)(uint)diagDist << 20) | (uint)ni); }
                }
            }

            return new FlowFieldEntry
            {
                GridRef = grid,
                Width = gw,
                Height = gh,
                NextIdx = nextIdx,
                Dist = dist
            };
        }

        private static FlowFieldEntry GetOrBuildFlowField(int cellSize, uint mask, Point target, bool nearPath, uint[,] grid, int gridVersion)
        {
            if (grid == null) return null;
            var fk = new FlowKey { CellSize = cellSize, TargetX = target.X, TargetY = target.Y, Mask = mask, NearPath = nearPath };
            lock (_flowCacheLock)
            {
                if (_cachedFlowVersion != gridVersion)
                {
                    _flowCache.Clear();
                    _targetQueryCounts.Clear();
                    _cachedFlowVersion = gridVersion;
                }
                if (_flowCache.TryGetValue(fk, out var existing) && existing != null && ReferenceEquals(existing.GridRef, grid))
                    return existing;
            }

            var built = BuildFlowFieldCore(grid, mask, target, nearPath);
            if (built == null) return null;

            lock (_flowCacheLock)
            {
                if (_cachedFlowVersion != gridVersion)
                {
                    _flowCache.Clear();
                    _targetQueryCounts.Clear();
                    _cachedFlowVersion = gridVersion;
                }
                if (_flowCache.TryGetValue(fk, out var race) && race != null && ReferenceEquals(race.GridRef, grid))
                    return race;
                if (_flowCache.Count >= MaxFlowCacheEntries)
                    _flowCache.Clear();
                _flowCache[fk] = built;
                return built;
            }
        }

        private static bool TryExtractFlowPath(
            FlowFieldEntry flow, uint[,] grid, uint mask,
            Point p1, Point p2, bool nearPath,
            out List<Point> path)
        {
            path = null;
            if (flow == null || grid == null || !ReferenceEquals(flow.GridRef, grid))
                return false;

            int gw = flow.Width, gh = flow.Height;
            if ((uint)p1.X >= (uint)gw || (uint)p1.Y >= (uint)gh)
                return false;

            if (p1 == p2)
            {
                path = new List<Point>(1) { p2 };
                return true;
            }

            int startIdx = p1.Y * gw + p1.X;
            int stepIdx = flow.NextIdx[startIdx];

            if (stepIdx < 0 && (grid[p1.X, p1.Y] & mask) != 0u)
            {
                int bestNb = -1;
                int bestDist = int.MaxValue;
                for (int dir = 0; dir < 4; dir++)
                {
                    int nx = p1.X + (dir == 0 ? 1 : dir == 1 ? -1 : 0);
                    int ny = p1.Y + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                    if ((uint)nx >= (uint)gw || (uint)ny >= (uint)gh) continue;
                    int ni = ny * gw + nx;
                    if (flow.NextIdx[ni] >= 0 && flow.Dist[ni] < bestDist)
                    {
                        bestDist = flow.Dist[ni];
                        bestNb = ni;
                    }
                }
                stepIdx = bestNb;
            }

            if (stepIdx < 0)
            {
                if (!nearPath)
                {
                    path = null;
                    return true;
                }
                return false;
            }

            if (stepIdx == startIdx)
            {
                path = new List<Point>(1) { p1 };
                return true;
            }

            var pts = new List<Point>(48);
            int prevX = p1.X, prevY = p1.Y;
            int curIdx = stepIdx;
            int cy = curIdx / gw;
            int cx = curIdx - cy * gw;
            int guard = gw * gh;

            while (guard-- > 0)
            {
                int nxtIdx = flow.NextIdx[curIdx];
                if (nxtIdx < 0)
                {
                    path = null;
                    return false;
                }
                if (nxtIdx == curIdx)
                {
                    pts.Add(new Point(cx, cy));
                    break;
                }

                int ny = nxtIdx / gw;
                int nx = nxtIdx - ny * gw;
                if (nx - cx != cx - prevX || ny - cy != cy - prevY)
                    pts.Add(new Point(cx, cy));

                prevX = cx;
                prevY = cy;
                curIdx = nxtIdx;
                cx = nx;
                cy = ny;
            }

            path = pts;
            return true;
        }

        private static CompMapEntry GetOrBuildComponentMap(int cellSize, uint mask, uint[,] grid, int gridVersion)
        {
            if (grid == null) return null;
            int gw = grid.GetLength(0);
            int gh = grid.GetLength(1);
            if (gw <= 0 || gh <= 0) return null;

            var key = new CompCacheKey { CellSize = cellSize, Mask = mask };
            lock (_compCacheLock)
            {
                if (_cachedCompVersion != gridVersion)
                {
                    _compCache.Clear();
                    _cachedCompVersion = gridVersion;
                }

                if (_compCache.TryGetValue(key, out var existing) && existing != null && ReferenceEquals(existing.GridRef, grid))
                    return existing;

                int total = gw * gh;
                if (_compQueueScratch == null || _compQueueScratch.Length < total)
                    _compQueueScratch = new int[total];
                if (_compSizesScratch == null || _compSizesScratch.Length < 1024)
                    _compSizesScratch = new int[1024];

                var compIds = new int[total];
                int[] q = _compQueueScratch;
                int compCount = 0;

                // 4-connected BFS is mathematically exact for PathFinderFast walkability because
                // diagonal moves in PathFinderFast require both adjacent cardinal cells to be open.
                for (int y = 0; y < gh; y++)
                {
                    int rowBase = y * gw;
                    for (int x = 0; x < gw; x++)
                    {
                        int startIdx = rowBase + x;
                        if (compIds[startIdx] != 0 || (grid[x, y] & mask) != 0u)
                            continue;

                        compCount++;
                        compIds[startIdx] = compCount;
                        int qHead = 0, qTail = 0;
                        q[qTail++] = startIdx;
                        int size = 0;

                        while (qHead < qTail)
                        {
                            int cur = q[qHead++];
                            size++;
                            int cy = cur / gw;
                            int cx = cur - cy * gw;

                            if (cx > 0)
                            {
                                int n = cur - 1;
                                if (compIds[n] == 0 && (grid[cx - 1, cy] & mask) == 0u)
                                {
                                    compIds[n] = compCount;
                                    q[qTail++] = n;
                                }
                            }
                            if (cx + 1 < gw)
                            {
                                int n = cur + 1;
                                if (compIds[n] == 0 && (grid[cx + 1, cy] & mask) == 0u)
                                {
                                    compIds[n] = compCount;
                                    q[qTail++] = n;
                                }
                            }
                            if (cy > 0)
                            {
                                int n = cur - gw;
                                if (compIds[n] == 0 && (grid[cx, cy - 1] & mask) == 0u)
                                {
                                    compIds[n] = compCount;
                                    q[qTail++] = n;
                                }
                            }
                            if (cy + 1 < gh)
                            {
                                int n = cur + gw;
                                if (compIds[n] == 0 && (grid[cx, cy + 1] & mask) == 0u)
                                {
                                    compIds[n] = compCount;
                                    q[qTail++] = n;
                                }
                            }
                        }

                        if (compCount >= _compSizesScratch.Length)
                            Array.Resize(ref _compSizesScratch, _compSizesScratch.Length * 2);
                        _compSizesScratch[compCount] = size;
                    }
                }

                var finalSizes = new int[compCount + 1];
                Array.Copy(_compSizesScratch, finalSizes, compCount + 1);
                var created = new CompMapEntry
                {
                    GridRef = grid,
                    Width = gw,
                    Height = gh,
                    CompIds = compIds,
                    CompSizes = finalSizes
                };
                _compCache[key] = created;
                return created;
            }
        }

        private static bool MatchesAnyComp(int candidate, int c0, int c1, int c2, int c3) =>
            candidate != 0 && (candidate == c0 || candidate == c1 || candidate == c2 || candidate == c3);

        private static void GetStartCompIds(
            CompMapEntry comp, Point p1,
            out int c0, out int c1, out int c2, out int c3, out int maxCompSize)
        {
            c0 = c1 = c2 = c3 = 0;
            maxCompSize = 0;
            int gw = comp.Width, gh = comp.Height;
            if ((uint)p1.X >= (uint)gw || (uint)p1.Y >= (uint)gh) return;

            int direct = comp.CompIds[p1.Y * gw + p1.X];
            if (direct > 0)
            {
                c0 = direct;
                maxCompSize = comp.CompSizes[direct];
                return;
            }

            // If p1 sits on a blocked boundary cell, collect walkable cardinal entry neighbors
            // (all enterable diagonal neighbors are also reachable via their two open cardinal sides).
            int rowBase = p1.Y * gw;
            if (p1.X > 0)
            {
                int c = comp.CompIds[rowBase + p1.X - 1];
                if (c > 0) { c0 = c; if (comp.CompSizes[c] > maxCompSize) maxCompSize = comp.CompSizes[c]; }
            }
            if (p1.X + 1 < gw)
            {
                int c = comp.CompIds[rowBase + p1.X + 1];
                if (c > 0 && c != c0) { c1 = c; if (comp.CompSizes[c] > maxCompSize) maxCompSize = comp.CompSizes[c]; }
            }
            if (p1.Y > 0)
            {
                int c = comp.CompIds[(p1.Y - 1) * gw + p1.X];
                if (c > 0 && c != c0 && c != c1) { c2 = c; if (comp.CompSizes[c] > maxCompSize) maxCompSize = comp.CompSizes[c]; }
            }
            if (p1.Y + 1 < gh)
            {
                int c = comp.CompIds[(p1.Y + 1) * gw + p1.X];
                if (c > 0 && c != c0 && c != c1 && c != c2) { c3 = c; if (comp.CompSizes[c] > maxCompSize) maxCompSize = comp.CompSizes[c]; }
            }
        }

        private static bool TryResolveTargetWithComp(
            CompMapEntry comp, uint[,] grid, uint mask,
            Point p1, Point p2, bool nearPath,
            out Point resolvedEnd, out bool resolvedNearPath)
        {
            resolvedEnd = p2;
            resolvedNearPath = nearPath;
            if (comp == null || grid == null) return false;
            int gw = comp.Width, gh = comp.Height;
            if ((uint)p2.X >= (uint)gw || (uint)p2.Y >= (uint)gh) return false;
            if ((uint)p1.X >= (uint)gw || (uint)p1.Y >= (uint)gh) return false;

            GetStartCompIds(comp, p1, out int c0, out int c1, out int c2c, out int c3, out int _);
            if (c0 == 0 && p1 != p2)
                return true; // Completely boxed in with no walkable cardinal neighbor -> fast null

            bool p2Blocked = (grid[p2.X, p2.Y] & mask) != 0u;
            int targetComp = p2Blocked ? 0 : comp.CompIds[p2.Y * gw + p2.X];

            if (!nearPath)
            {
                if (p1 != p2 && (p2Blocked || !MatchesAnyComp(targetComp, c0, c1, c2c, c3)))
                    return true; // Unreachable -> fast null
                resolvedEnd = p2;
                resolvedNearPath = false;
                return false;
            }

            if (!p2Blocked && MatchesAnyComp(targetComp, c0, c1, c2c, c3))
            {
                // Directly reachable -> skip second nearPath pass cleanly
                resolvedEnd = p2;
                resolvedNearPath = false;
                return false;
            }

            // When target cell itself is blocked (e.g. CommandCenter / building / wall center)
            // or behind an open-edge building, resolve deterministically to the closest walkable
            // perimeter cell in the starter component so A* avoids a 200k-node full-map flood fill.
            if (p2Blocked)
            {
                bool found = false;
                int bestX = 0, bestY = 0;
                long bestD2End = long.MaxValue, bestD2Start = long.MaxValue;
                int[] ids = comp.CompIds;

                for (int r = 1; r <= 6; r++)
                {
                    if (found && (long)r * r > bestD2End)
                        break;
                    int minY = Math.Max(0, p2.Y - r);
                    int maxY = Math.Min(gh - 1, p2.Y + r);
                    int minX = Math.Max(0, p2.X - r);
                    int maxX = Math.Min(gw - 1, p2.X + r);
                    for (int ty = minY; ty <= maxY; ty++)
                    {
                        bool onEdgeY = (ty == p2.Y - r || ty == p2.Y + r);
                        int rowOff = ty * gw;
                        for (int tx = minX; tx <= maxX; tx++)
                        {
                            if (!onEdgeY && tx != p2.X - r && tx != p2.X + r)
                                continue;
                            int cc = ids[rowOff + tx];
                            if (!MatchesAnyComp(cc, c0, c1, c2c, c3))
                                continue;
                            long dxE = tx - p2.X, dyE = ty - p2.Y;
                            long d2End = dxE * dxE + dyE * dyE;
                            long dxS = tx - p1.X, dyS = ty - p1.Y;
                            long d2Start = dxS * dxS + dyS * dyS;
                            if (!found || d2End < bestD2End || (d2End == bestD2End && d2Start < bestD2Start))
                            {
                                found = true;
                                bestX = tx; bestY = ty;
                                bestD2End = d2End; bestD2Start = d2Start;
                            }
                        }
                    }
                }

                if (found)
                {
                    resolvedEnd = new Point(bestX, bestY);
                    resolvedNearPath = false;
                    return false;
                }
            }

            return false;
        }

        private static void EnsureFindPath8Bound()
        {
            if (_findPath8Resolved) return;
            _findPath8Resolved = true;
            try
            {
                var fPathFinders = AccessTools.Field(typeof(DXWorldGrid), "PathFinders");
                var fGrids = AccessTools.Field(typeof(DXWorldGrid), "Grids");
                var mFindPath8 = AccessTools.Method(
                    typeof(DXWorldGrid),
                    "FindPath",
                    new[]
                    {
                        typeof(int),
                        typeof(Algorithms.PathFinderFast),
                        typeof(Point),
                        typeof(Point),
                        typeof(uint),
                        typeof(bool),
                        typeof(int?),
                        typeof(bool)
                    });

                if (fPathFinders != null && mFindPath8 != null)
                {
                    var dmDict = new System.Reflection.Emit.DynamicMethod(
                        "TABCoop_GetDXWorldGridPathFinders",
                        typeof(Dictionary<int, Algorithms.PathFinderFast[]>),
                        Type.EmptyTypes,
                        typeof(DeterministicPathfindingPatch).Module,
                        skipVisibility: true);
                    var il = dmDict.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, fPathFinders);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    _fastGetPathFinders = (Func<Dictionary<int, Algorithms.PathFinderFast[]>>)dmDict.CreateDelegate(
                        typeof(Func<Dictionary<int, Algorithms.PathFinderFast[]>>));

                    _fastFindPath8 = (FindPath8Delegate)Delegate.CreateDelegate(typeof(FindPath8Delegate), mFindPath8);
                }

                if (fGrids != null)
                {
                    var dmGrids = new System.Reflection.Emit.DynamicMethod(
                        "TABCoop_GetDXWorldGridGrids",
                        typeof(Dictionary<int, uint[,]>),
                        Type.EmptyTypes,
                        typeof(DeterministicPathfindingPatch).Module,
                        skipVisibility: true);
                    var ilG = dmGrids.GetILGenerator();
                    ilG.Emit(System.Reflection.Emit.OpCodes.Ldsfld, fGrids);
                    ilG.Emit(System.Reflection.Emit.OpCodes.Ret);
                    _fastGetGrids = (Func<Dictionary<int, uint[,]>>)dmGrids.CreateDelegate(
                        typeof(Func<Dictionary<int, uint[,]>>));
                }
            }
            catch (Exception ex)
            {
                _fastGetPathFinders = null;
                _fastGetGrids = null;
                _fastFindPath8 = null;
                ModLogger.Warn($"[Pathfinding] Failed to bind 8-arg FindPath (falling back to 6-arg): {ex.Message}");
            }
        }

        private static List<Point> SolveSimplifiedPathPure(
            Algorithms.PathFinderFast pf,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            int? maxNodes)
        {
            int searchLimit = pf.SearchLimit;
            if (maxNodes.HasValue)
                pf.SearchLimit = maxNodes.Value;
            List<Algorithms.PathFinderNode> nodes = pf.FindPath(p1, p2, mask, nearPath);
            pf.SearchLimit = searchLimit;

            if (nodes == null || nodes.Count == 0)
                return null;

            if (nodes[0].X != p2.X || nodes[0].Y != p2.Y)
            {
                p2.X = nodes[0].X;
                p2.Y = nodes[0].Y;
            }

            var path = new List<Point>(nodes.Count);
            if (nodes.Count == 1)
            {
                path.Add(new Point(nodes[0].X, nodes[0].Y));
            }
            else
            {
                Point prev = new Point(nodes[nodes.Count - 1].X, nodes[nodes.Count - 1].Y);
                Point cur = new Point(nodes[nodes.Count - 2].X, nodes[nodes.Count - 2].Y);
                for (int i = nodes.Count - 2; i >= 0; i--)
                {
                    if (i == 0)
                    {
                        path.Add(new Point(nodes[i].X, nodes[i].Y));
                        break;
                    }
                    Point next = cur;
                    cur.X = nodes[i - 1].X;
                    cur.Y = nodes[i - 1].Y;
                    if (cur.X - next.X != next.X - prev.X || cur.Y - next.Y != next.Y - prev.Y)
                        path.Add(new Point(nodes[i].X, nodes[i].Y));
                    prev = next;
                }
            }

            if (path.Count > 0 && path[path.Count - 1] != p2)
                path.Add(p2);

            return path;
        }

        private static Point TryClusterOrigin(int safeCellSize, Point p1, Point p2, uint mask, Dictionary<int, uint[,]> grids)
        {
            if (safeCellSize != 1 || grids == null)
                return p1;
            int dist = p1.DistanceMaxAxis(p2);
            if (dist < 24)
                return p1;
            if (!grids.TryGetValue(1, out uint[,] grid) || grid == null)
                return p1;

            int gw = grid.GetLength(0);
            int gh = grid.GetLength(1);

            if (dist >= 64)
            {
                int b4x = p1.X & ~3;
                int b4y = p1.Y & ~3;
                if (b4x >= 0 && b4y >= 0 && b4x + 3 < gw && b4y + 3 < gh)
                {
                    bool open4 = true;
                    for (int dx = 0; dx < 4 && open4; dx++)
                    {
                        for (int dy = 0; dy < 4; dy++)
                        {
                            if ((grid[b4x + dx, b4y + dy] & mask) != 0u)
                            {
                                open4 = false;
                                break;
                            }
                        }
                    }
                    if (open4)
                        return new Point(b4x, b4y);
                }
            }

            int bx = p1.X & ~1;
            int by = p1.Y & ~1;
            if (bx < 0 || by < 0 || bx + 1 >= gw || by + 1 >= gh)
                return p1;

            if ((grid[bx, by] & mask) == 0u
                && (grid[bx + 1, by] & mask) == 0u
                && (grid[bx, by + 1] & mask) == 0u
                && (grid[bx + 1, by + 1] & mask) == 0u)
            {
                return new Point(bx, by);
            }
            return p1;
        }

        public static void PrewarmUniquePathsParallel(List<PathPrewarmItem> items)
        {
            if (!LockstepClock.IsDeterministicModeActive || items == null || items.Count < 4)
                return;

            int workers = Testing.TestHarnessConfig.ParallelWorkers;
            if (workers < 1) workers = 1;

            EnsureFindPath8Bound();
            var grids = _fastGetGrids?.Invoke();
            if (grids == null) return;

            int curVer = System.Threading.Volatile.Read(ref _gridMutationVersion);
            if (_cachedGridVersion != curVer || _exactPathCache.Count >= MaxExactPathCacheEntries)
            {
                _exactPathCache.Clear();
                _cachedGridVersion = curVer;
            }

            var seenKeys = new HashSet<PathCacheKey>();
            var toCompute = new List<PathCacheKey>(items.Count);

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                int safeCellSize = it.CellSize < 1 ? 1 : (it.CellSize > 3 ? 3 : it.CellSize);
                if (it.Start == it.End || it.Start.DistanceMaxAxis(it.End) <= 1)
                    continue;
                if (!grids.ContainsKey(safeCellSize))
                    continue;

                Point startPt = TryClusterOrigin(safeCellSize, it.Start, it.End, it.Mask, grids);
                var key = new PathCacheKey
                {
                    CellSize = safeCellSize,
                    X1 = startPt.X,
                    Y1 = startPt.Y,
                    X2 = it.End.X,
                    Y2 = it.End.Y,
                    Mask = it.Mask,
                    NearPath = true,
                    MaxNodes = it.MaxNodes ?? -1
                };
                if (!_exactPathCache.ContainsKey(key) && seenKeys.Add(key))
                    toCompute.Add(key);
            }

            if (toCompute.Count < 4)
                return;

            int activeWorkers = Math.Min(workers, toCompute.Count);
            var pfByCellSize = new Dictionary<int, Algorithms.PathFinderFast[]>(3);

            lock (_prewarmPfLock)
            {
                for (int i = 0; i < toCompute.Count; i++)
                {
                    int cs = toCompute[i].CellSize;
                    if (pfByCellSize.ContainsKey(cs)) continue;
                    if (!grids.TryGetValue(cs, out uint[,] grid) || grid == null) continue;

                    if (!_cachedGridRefs.TryGetValue(cs, out var cachedGrid)
                        || !ReferenceEquals(cachedGrid, grid)
                        || !_parallelPfs.TryGetValue(cs, out var pfArr)
                        || pfArr == null
                        || pfArr.Length < activeWorkers)
                    {
                        pfArr = new Algorithms.PathFinderFast[activeWorkers];
                        for (int w = 0; w < activeWorkers; w++)
                            pfArr[w] = DXWorldGrid.CreatePathFinder(grid);
                        _cachedGridRefs[cs] = grid;
                        _parallelPfs[cs] = pfArr;
                    }
                    pfByCellSize[cs] = pfArr;
                }
            }

            var results = new List<Point>[toCompute.Count];
            int total = toCompute.Count;

            // Collect (CellSize, Mask, Target) FlowKeys shared by >= 3 unique queries (or already cached)
            // so reverse Dijkstra FlowFields match RunSyncPathfinding's exact deterministic >= 3 threshold
            // regardless of worker count (1 vs 4).
            var flowKeyCounts = new Dictionary<FlowKey, int>(8);
            var flowKeysList = new List<FlowKey>(8);
            var flowTargets = new Dictionary<FlowKey, FlowFieldEntry>(8);
            for (int i = 0; i < total; i++)
            {
                var k = toCompute[i];
                if (k.MaxNodes >= 0) continue; // Default unbounded searches use exact reverse flow fields
                var fk = new FlowKey { CellSize = k.CellSize, TargetX = k.X2, TargetY = k.Y2, Mask = k.Mask, NearPath = true };
                flowKeyCounts.TryGetValue(fk, out int cnt);
                flowKeyCounts[fk] = cnt + 1;
            }
            foreach (var kv in flowKeyCounts)
            {
                bool alreadyCached = false;
                lock (_flowCacheLock)
                {
                    if (_cachedFlowVersion == curVer && _flowCache.ContainsKey(kv.Key))
                        alreadyCached = true;
                }
                if (kv.Value >= 3 || alreadyCached)
                    flowKeysList.Add(kv.Key);
            }

            var flowSw = System.Diagnostics.Stopwatch.StartNew();
            if (flowKeysList.Count > 0)
            {
                var builtFlows = new FlowFieldEntry[flowKeysList.Count];
                int flowWorkers = Math.Min(activeWorkers, flowKeysList.Count);
                Parallel.For(0, flowWorkers, w =>
                {
                    for (int fi = w; fi < flowKeysList.Count; fi += flowWorkers)
                    {
                        var fk = flowKeysList[fi];
                        if (grids.TryGetValue(fk.CellSize, out var g) && g != null)
                            builtFlows[fi] = GetOrBuildFlowField(fk.CellSize, fk.Mask, new Point(fk.TargetX, fk.TargetY), fk.NearPath, g, curVer);
                    }
                });
                for (int fi = 0; fi < flowKeysList.Count; fi++)
                {
                    if (builtFlows[fi] != null)
                        flowTargets[flowKeysList[fi]] = builtFlows[fi];
                }
            }
            long flowMs = flowSw.ElapsedMilliseconds;

            var compByKey = new Dictionary<CompCacheKey, CompMapEntry>(4);
            int flowHits = 0;
            int fallbackCount = 0;

            Parallel.For(0, activeWorkers, w =>
            {
                int localFlowHits = 0;
                int localFallback = 0;
                int s = (total * w) / activeWorkers;
                int e = (total * (w + 1)) / activeWorkers;
                for (int i = s; i < e; i++)
                {
                    var k = toCompute[i];
                    Point p1 = new Point(k.X1, k.Y1);
                    Point p2 = new Point(k.X2, k.Y2);
                    grids.TryGetValue(k.CellSize, out var g);

                    if (k.MaxNodes < 0 && flowTargets.TryGetValue(
                        new FlowKey { CellSize = k.CellSize, TargetX = k.X2, TargetY = k.Y2, Mask = k.Mask, NearPath = true },
                        out var flow)
                        && TryExtractFlowPath(flow, g, k.Mask, p1, p2, true, out var flowSolved))
                    {
                        results[i] = flowSolved;
                        localFlowHits++;
                        continue;
                    }

                    if (!pfByCellSize.TryGetValue(k.CellSize, out var pfArr)) continue;
                    var pf = pfArr[w];
                    int? maxNodesOpt = k.MaxNodes >= 0 ? k.MaxNodes : (int?)null;
                    CompMapEntry comp = null;
                    if (g != null)
                        comp = GetOrBuildComponentMap(k.CellSize, k.Mask, g, curVer);

                    if (TryResolveTargetWithComp(comp, g, k.Mask, p1, p2, true, out Point resEnd, out bool resNear))
                        results[i] = null;
                    else
                        results[i] = SolveSimplifiedPathPure(pf, p1, resEnd, k.Mask, resNear, maxNodesOpt);
                    localFallback++;
                }
                System.Threading.Interlocked.Add(ref flowHits, localFlowHits);
                System.Threading.Interlocked.Add(ref fallbackCount, localFallback);
            });

            if (total >= 64)
                ModLogger.Info($"[Pathfinding] PrewarmUniquePathsParallel solved uniqueKeys={total} (rawItems={items.Count}, flowHits={flowHits}, fallback={fallbackCount}, flowFields={flowTargets.Count}, flowMs={flowMs}, workers={activeWorkers}).");

            if (_exactPathCache.Count + total >= MaxExactPathCacheEntries)
                _exactPathCache.Clear();

            int limit = Math.Min(total, MaxExactPathCacheEntries - _exactPathCache.Count);
            for (int i = 0; i < limit; i++)
            {
                var p = results[i];
                _exactPathCache[toCompute[i]] = p != null ? new List<Point>(p) : null;
            }
        }

        internal static void InvalidatePathCache()
        {
            System.Threading.Interlocked.Increment(ref _gridMutationVersion);
        }

        [HarmonyPatch(nameof(DXWorldGrid.SetCell), new[] { typeof(int), typeof(int), typeof(uint), typeof(bool) })]
        [HarmonyPostfix]
        public static void SetCell_Postfix()
        {
            if (LockstepClock.IsDeterministicModeActive)
                System.Threading.Interlocked.Increment(ref _gridMutationVersion);
        }

        [HarmonyPatch(nameof(DXWorldGrid.FindPathConcurrent))]
        [HarmonyPrefix]
        public static bool FindPathConcurrent_Prefix(
            out DXWorldGrid.PathFindingRequest pfr,
            int cellSize,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            DXWorldGrid.PathFindingRequest.OnPathFoundDelegate onPathFound,
            object userData,
            int? maxNodes,
            bool useCache)
        {
            if (!LockstepClock.IsDeterministicModeActive)
            {
                pfr = null;
                return true;
            }

            RunSyncPathfinding(out pfr, cellSize, p1, p2, mask, nearPath, onPathFound, userData, maxNodes);
            return false;
        }

        [HarmonyPatch(nameof(DXWorldGrid.FindPathConcurrentWithHighPriority))]
        [HarmonyPrefix]
        public static bool FindPathConcurrentWithHighPriority_Prefix(
            out DXWorldGrid.PathFindingRequest pfr,
            int cellSize,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            DXWorldGrid.PathFindingRequest.OnPathFoundDelegate onPathFound,
            object userData,
            int? maxNodes,
            bool useCache)
        {
            if (!LockstepClock.IsDeterministicModeActive)
            {
                pfr = null;
                return true;
            }

            RunSyncPathfinding(out pfr, cellSize, p1, p2, mask, nearPath, onPathFound, userData, maxNodes);
            return false;
        }

        private static void RunSyncPathfinding(
            out DXWorldGrid.PathFindingRequest pfr,
            int cellSize,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            DXWorldGrid.PathFindingRequest.OnPathFoundDelegate onPathFound,
            object userData,
            int? maxNodes)
        {
            bool samplePerf = DeterministicSandboxPatches.DiagPerfSamplingActive;
            long t0 = samplePerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            int safeCellSize = cellSize < 1 ? 1 : (cellSize > 3 ? 3 : cellSize);
            var req = new DXWorldGrid.PathFindingRequest(safeCellSize, p1, p2, mask, nearPath, onPathFound, userData, maxNodes, false);
            pfr = req;

            // Mirror CMovable.#=ziMavkfK9ZJji before invoking onPathFound so #=zxg3EOEU9RJ22's
            // reference check (`request == #=ziMavkfK9ZJji`) passes even if Harmony stages `out pfr`.
            if (userData != null)
            {
                if (_movableRequestField == null)
                    _movableRequestField = AccessTools.Field(userData.GetType(), ObfuscatedSymbols.Field_CMovable_PendingPathRequest);
                _movableRequestField?.SetValue(userData, req);
            }

            req.Status = DXWorldGrid.PathFindingRequest.TStatus.Computing;
            int curVer = System.Threading.Volatile.Read(ref _gridMutationVersion);
            if (_cachedGridVersion != curVer || _exactPathCache.Count >= MaxExactPathCacheEntries)
            {
                _exactPathCache.Clear();
                _cachedGridVersion = curVer;
            }

            EnsureFindPath8Bound();
            var grids = _fastGetGrids?.Invoke();
            Point clusteredP1 = TryClusterOrigin(safeCellSize, p1, p2, mask, grids);

            var key = new PathCacheKey
            {
                CellSize = safeCellSize,
                X1 = clusteredP1.X, Y1 = clusteredP1.Y,
                X2 = p2.X, Y2 = p2.Y,
                Mask = mask,
                NearPath = nearPath,
                MaxNodes = maxNodes ?? -1
            };

            if (_exactPathCache.TryGetValue(key, out List<Point> cachedPath))
            {
                req.Path = cachedPath != null ? new List<Point>(cachedPath) : null;
                if (samplePerf)
                    System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagTickPathfindCacheHits);
            }
            else
            {
                List<Point> solved = null;
                bool solvedByFlow = false;
                Point queryEnd = p2;
                bool queryNear = nearPath;
                bool fastUnreachable = false;

                uint[,] grid = null;
                bool hasGrid = grids != null && grids.TryGetValue(safeCellSize, out grid) && grid != null;
                if (hasGrid && (uint)p2.X < (uint)grid.GetLength(0) && (uint)p2.Y < (uint)grid.GetLength(1))
                {
                    bool p2Blocked = (grid[p2.X, p2.Y] & mask) != 0u;

                    if (!maxNodes.HasValue)
                    {
                        var fk = new FlowKey { CellSize = safeCellSize, TargetX = p2.X, TargetY = p2.Y, Mask = mask, NearPath = nearPath };
                        FlowFieldEntry flow = null;
                        lock (_flowCacheLock)
                        {
                            if (_cachedFlowVersion != curVer)
                            {
                                _flowCache.Clear();
                                _targetQueryCounts.Clear();
                                _cachedFlowVersion = curVer;
                            }
                            if (!_flowCache.TryGetValue(fk, out flow) || flow == null || !ReferenceEquals(flow.GridRef, grid))
                            {
                                flow = null;
                                if (p2Blocked || clusteredP1.DistanceMaxAxis(p2) >= 24)
                                {
                                    _targetQueryCounts.TryGetValue(fk, out int qc);
                                    _targetQueryCounts[fk] = qc + 1;
                                }
                            }
                        }

                        if (flow == null && (p2Blocked || clusteredP1.DistanceMaxAxis(p2) >= 24))
                        {
                            int qc;
                            lock (_flowCacheLock) { _targetQueryCounts.TryGetValue(fk, out qc); }
                            if (qc >= 3)
                                flow = GetOrBuildFlowField(safeCellSize, mask, p2, nearPath, grid, curVer);
                        }

                        if (flow != null && TryExtractFlowPath(flow, grid, mask, clusteredP1, p2, nearPath, out var flowPath))
                        {
                            solved = flowPath;
                            solvedByFlow = true;
                        }
                    }

                    if (!solvedByFlow)
                    {
                        if (!nearPath && p2Blocked && clusteredP1 != p2)
                        {
                            fastUnreachable = true;
                        }
                        else if (p2Blocked || clusteredP1.DistanceMaxAxis(p2) >= 24)
                        {
                            var comp = GetOrBuildComponentMap(safeCellSize, mask, grid, curVer);
                            fastUnreachable = TryResolveTargetWithComp(comp, grid, mask, clusteredP1, p2, nearPath, out queryEnd, out queryNear);
                        }
                    }
                }

                if (!solvedByFlow)
                {
                    if (fastUnreachable)
                    {
                        solved = null;
                    }
                    else
                    {
                        var pfDict = _fastGetPathFinders?.Invoke();
                        if (_fastFindPath8 != null &&
                            pfDict != null &&
                            pfDict.TryGetValue(safeCellSize, out var pfArr) &&
                            pfArr != null &&
                            pfArr.Length > 0 &&
                            pfArr[0] != null)
                        {
                            // Use the 8-arg internal DXWorldGrid.FindPath overload so maxNodes (pf.SearchLimit)
                            // is honored instead of silently dropped by the 6-arg overload.
                            solved = _fastFindPath8(safeCellSize, pfArr[0], clusteredP1, queryEnd, mask, queryNear, maxNodes, false);
                        }
                        else
                        {
                            solved = DXWorldGrid.FindPath(safeCellSize, clusteredP1, queryEnd, mask, queryNear, maxNodes);
                        }
                    }
                }
                _exactPathCache[key] = solved != null ? new List<Point>(solved) : null;
                req.Path = solved;
            }

            req.Status = DXWorldGrid.PathFindingRequest.TStatus.Computed;
            onPathFound?.Invoke(req);
            req.Status = DXWorldGrid.PathFindingRequest.TStatus.Finished;

            // If no path was found, #=zxg3EOEU9RJ22 clears #=ziMavkfK9ZJji; keep `out pfr` null as well.
            if (req.Path == null)
                pfr = null;

            if (samplePerf)
            {
                System.Threading.Interlocked.Add(ref DeterministicSandboxPatches.DiagTickPathfindTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagTickPathfindCalls);
            }
        }
    }

    /// <summary>
    /// Immediately consumes synchronous pathfinding results inside CMovable.#=zUPt0E_k= instead
    /// of leaving #=zdzxO_Le$3wwY unapplied until the next IsIAFrameModule(4) tick (+66.7ms).
    /// Mirrors vanilla IsMultiplayer immediate re-entry right after storing #=zdzxO_Le$3wwY.
    /// </summary>
    [HarmonyPatch]
    public static class CMovableImmediatePathConsumePatch
    {
        [ThreadStatic]
        private static bool _inRecursiveConsume;
        private static Type _tMovable;
        private static FieldInfo _fNewPath;
        private static MethodInfo _mUpdatePath;
        private static Func<object, object> _fastGetNewPath;
        private static Action<object, PointF, bool> _fastUpdatePath;
        private static bool _compiled;

        public static MethodBase TargetMethod()
        {
            _tMovable = GameBridge.GameAssembly?.GetType("ZX.Components.CMovable");
            if (_tMovable == null) return null;
            _fNewPath = AccessTools.Field(_tMovable, "#=zdzxO_Le$3wwY");
            _mUpdatePath = AccessTools.Method(_tMovable, "#=zUPt0E_k=", new[] { typeof(PointF), typeof(bool) });
            return _mUpdatePath;
        }

        private static void EnsureFastDelegates()
        {
            if (_compiled) return;
            _compiled = true;
            try
            {
                if (_tMovable == null || _fNewPath == null || _mUpdatePath == null) return;

                var dmGet = new DynamicMethod("TABCoop_GetMovableNewPath", typeof(object), new[] { typeof(object) },
                    typeof(CMovableImmediatePathConsumePatch).Module, skipVisibility: true);
                var ilG = dmGet.GetILGenerator();
                ilG.Emit(OpCodes.Ldarg_0);
                ilG.Emit(OpCodes.Castclass, _tMovable);
                ilG.Emit(OpCodes.Ldfld, _fNewPath);
                ilG.Emit(OpCodes.Ret);
                _fastGetNewPath = (Func<object, object>)dmGet.CreateDelegate(typeof(Func<object, object>));

                var dmCall = new DynamicMethod("TABCoop_CallMovableUpdatePath", typeof(void),
                    new[] { typeof(object), typeof(PointF), typeof(bool) },
                    typeof(CMovableImmediatePathConsumePatch).Module, skipVisibility: true);
                var ilC = dmCall.GetILGenerator();
                ilC.Emit(OpCodes.Ldarg_0);
                ilC.Emit(OpCodes.Castclass, _tMovable);
                ilC.Emit(OpCodes.Ldarg_1);
                ilC.Emit(OpCodes.Ldarg_2);
                ilC.Emit(OpCodes.Call, _mUpdatePath);
                ilC.Emit(OpCodes.Ret);
                _fastUpdatePath = (Action<object, PointF, bool>)dmCall.CreateDelegate(typeof(Action<object, PointF, bool>));
            }
            catch (Exception ex)
            {
                _fastGetNewPath = null;
                _fastUpdatePath = null;
                ModLogger.Warn($"[Perf] CMovableImmediatePathConsume compile failed (using reflection): {ex.Message}");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, PointF __0, bool __1)
        {
            if (!LockstepClock.IsDeterministicModeActive || _inRecursiveConsume || __instance == null)
                return;

            EnsureFastDelegates();
            object pending = _fastGetNewPath != null ? _fastGetNewPath(__instance) : _fNewPath?.GetValue(__instance);
            if (pending == null) return;

            _inRecursiveConsume = true;
            try
            {
                if (_fastUpdatePath != null)
                    _fastUpdatePath(__instance, __0, __1);
                else
                    _mUpdatePath?.Invoke(__instance, new object[] { __0, __1 });
            }
            finally
            {
                _inRecursiveConsume = false;
            }
        }
    }

    /// <summary>
    /// Runs after ZXSystemGameLevel.#=zwK_pUIeRBpbp (the main-thread finish action of the async
    /// loader). This is the first point where all saved entities and GameTime are restored, so it is
    /// the only safe place to calibrate the synced ID counter and align the simulation clocks.
    /// </summary>
    [HarmonyPatch]
    public static class LevelLoadCompletePatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_OnLevelLoadComplete, Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            DeterministicSandboxPatches.ResetPerfWindowCounters(
                Testing.TestHarnessConfig.Enabled || Testing.TestHarnessConfig.ForcePerfDiag);
            Testing.CommandCenterInvulnerability.OnNewLevelLoaded();
            if (!Sync.CoopSessionManager.IsSessionActive
                && Testing.TestHarnessConfig.SoloDeterministicMode
                && !LockstepClock.IsDeterministicModeActive)
            {
                Sync.FactionOwnershipTracker.TryLoadFromLevelScriptVariables();
                LockstepClock.CalibrateEntityIdCounterFromLoadedLevel(20261109);
            }
            LockstepClock.OnLevelLoadComplete();
            LockstepClock.InvalidateAllStage4Caches();
            Testing.DeterministicReplayVerifier.OnLevelLoadComplete();
            Sync.CoopSessionManager.OnLevelLoadComplete();
        }
    }

    /// <summary>
    /// ZXSystemGameLevel.OnStartUpdate runs inside DXGame._TaskLogic right before the deterministic
    /// entity update loop. Rebind that worker thread's [ThreadStatic] behaviour RNG once per tick
    /// instead of patching DXEntity.Update (which would destroy AggressiveInlining across 50k+ entities).
    /// </summary>
    [HarmonyPatch]
    public static class EntityUpdateBehaviourRandomPatch
    {
        public static MethodBase TargetMethod()
        {
            var t = Core.GameBridge.TypeZXSystemGameLevel ?? AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXSystemGameLevel);
            return t != null ? AccessTools.Method(t, "OnStartUpdate", Type.EmptyTypes) : null;
        }

        [HarmonyPrefix]
        public static void Prefix()
        {
            if (LockstepClock.IsDeterministicModeActive)
            {
                LockstepClock.CaptureLogicThread();
                int extraTickMs = Testing.TestHarnessConfig.SimExtraTickMs;
                if (extraTickMs > 0 && Sync.CoopSessionManager.IsInSyncedLevel && !LockstepClock.LoadingLevel)
                    Thread.Sleep(Math.Min(extraTickMs, 200));
                LockstepClock.EnsureBehaviourRandomOnCurrentThread();
                var game = DXGame.Current;
                if (game != null)
                    LockstepClock.ReanchorSimulationRngForTick(game.IAIterationInt);
            }
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (LockstepClock.IsDeterministicModeActive && !LockstepClock.LoadingLevel)
            {
                var game = DXGame.Current;
                if (game != null)
                {
                    ZombieWatcherCoarseGrid.RebuildForTick(game);
                    Physics1uGrid.EnsureSynced(game);
                    ActiveUpdateEntityManager.PrepareForTick(game);
                    PrecomputedPerceptionManager.PrecomputeForTick(game);
                }
            }
        }
    }

    /// <summary>
    /// Logs any engine message-box alert raised while deterministic mode is active. The save loader
    /// reports deserialization failures this way (no exception escapes), so a silent Client-side
    /// load failure shows up here.
    /// </summary>
    [HarmonyPatch]
    public static class MessageBoxAlertDiagPatch
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowAlert,
                new[] { typeof(string), typeof(string), typeof(Action) });

        [HarmonyPrefix]
        public static void Prefix(string __0, string __1)
        {
            if (LockstepClock.IsDeterministicModeActive)
                ModLogger.Warn($"[MsgBox] {__0}: {__1}");
        }
    }

    /// <summary>
    /// DXGame.IsFrameSceneModule(n) is true when the RENDER frame counter is a multiple of n. Two
    /// processes render at different rates (focused vs background window), so any logic or UI action
    /// gated on it drifts apart. In deterministic mode, gate on the logic tick counter instead.
    /// </summary>
    [HarmonyPatch(typeof(DXGame), "IsFrameSceneModule")]
    public static class FrameSceneModuleDeterminismPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int iteration, ref bool __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            var game = DXGame.Current;
            __result = iteration > 0 && game != null && (game.IAIterationInt % iteration) == 0;
            return false;
        }
    }

    /// <summary>
    /// Preserves deserialized in-flight projectile kinematics across ZX.Entities.Proyectile.OnSceneAdded.
    /// On load, OnSceneAdded advances Arrow Origin by +0.3f, overwrites Position = Origin, and
    /// recomputes DTime/DX/DY. A Host-to-Client snapshot load runs OnSceneAdded a second time unless
    /// preserved, mutating in-flight projectiles before tick 0. Newly spawned projectiles enter
    /// OnSceneAdded with DTime == 0f; save-restored in-flight projectiles already have DTime > 0f.
    /// </summary>
    [HarmonyPatch]
    public static class ProyectileLoadPreservePatch
    {
        public sealed class ProyectileSavedState
        {
            public PointF Origin;
            public PointF Position;
            public PointF LastPosition;
            public float H;
            public float LastH;
            public float OriginH;
            public float Distance;
            public float DistanceH;
            public float DX;
            public float DY;
            public float DH;
            public float DTime;
            public float FactorTime;
            public float HFactor;
        }

        private static bool _metaInit;
        private static PropertyInfo _pOrigin, _pPosition, _pLastPosition;
        private static PropertyInfo _pH, _pLastH, _pOriginH, _pDistance, _pDistanceH;
        private static PropertyInfo _pDX, _pDY, _pDH, _pDTime, _pFactorTime, _pHFactor;

        private static void EnsureMeta(Type t)
        {
            if (_metaInit || t == null) return;
            _metaInit = true;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            _pOrigin = t.GetProperty("Origin", F);
            _pPosition = t.GetProperty("Position", F);
            _pLastPosition = t.GetProperty("LastPosition", F);
            _pH = t.GetProperty("H", F);
            _pLastH = t.GetProperty("LastH", F);
            _pOriginH = t.GetProperty("OriginH", F);
            _pDistance = t.GetProperty("Distance", F);
            _pDistanceH = t.GetProperty("DistanceH", F);
            _pDX = t.GetProperty("DX", F);
            _pDY = t.GetProperty("DY", F);
            _pDH = t.GetProperty("DH", F);
            _pDTime = t.GetProperty("DTime", F);
            _pFactorTime = t.GetProperty("FactorTime", F);
            _pHFactor = t.GetProperty("HFactor", F);
        }

        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.GameAssembly?.GetType("ZX.Entities.Proyectile");
            EnsureMeta(t);
            return t != null ? AccessTools.Method(t, "OnSceneAdded", Type.EmptyTypes) : null;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, ref ProyectileSavedState __state)
        {
            __state = null;
            if (!LockstepClock.IsDeterministicModeActive || __instance == null) return;
            EnsureMeta(__instance.GetType());
            if (_pDTime == null) return;

            float dTime = (float)_pDTime.GetValue(__instance);
            if (dTime <= 0f) return;

            __state = new ProyectileSavedState
            {
                Origin = _pOrigin != null ? (PointF)_pOrigin.GetValue(__instance) : PointF.Empty,
                Position = _pPosition != null ? (PointF)_pPosition.GetValue(__instance) : PointF.Empty,
                LastPosition = _pLastPosition != null ? (PointF)_pLastPosition.GetValue(__instance) : PointF.Empty,
                H = _pH != null ? (float)_pH.GetValue(__instance) : 0f,
                LastH = _pLastH != null ? (float)_pLastH.GetValue(__instance) : 0f,
                OriginH = _pOriginH != null ? (float)_pOriginH.GetValue(__instance) : 0f,
                Distance = _pDistance != null ? (float)_pDistance.GetValue(__instance) : 0f,
                DistanceH = _pDistanceH != null ? (float)_pDistanceH.GetValue(__instance) : 0f,
                DX = _pDX != null ? (float)_pDX.GetValue(__instance) : 0f,
                DY = _pDY != null ? (float)_pDY.GetValue(__instance) : 0f,
                DH = _pDH != null ? (float)_pDH.GetValue(__instance) : 0f,
                DTime = dTime,
                FactorTime = _pFactorTime != null ? (float)_pFactorTime.GetValue(__instance) : 0f,
                HFactor = _pHFactor != null ? (float)_pHFactor.GetValue(__instance) : 0f,
            };
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, ProyectileSavedState __state)
        {
            if (__state == null || __instance == null) return;

            _pOrigin?.SetValue(__instance, __state.Origin);
            _pLastPosition?.SetValue(__instance, __state.LastPosition);
            _pPosition?.SetValue(__instance, __state.Position);
            _pH?.SetValue(__instance, __state.H);
            _pLastH?.SetValue(__instance, __state.LastH);
            _pOriginH?.SetValue(__instance, __state.OriginH);
            _pDistance?.SetValue(__instance, __state.Distance);
            _pDistanceH?.SetValue(__instance, __state.DistanceH);
            _pDX?.SetValue(__instance, __state.DX);
            _pDY?.SetValue(__instance, __state.DY);
            _pDH?.SetValue(__instance, __state.DH);
            _pDTime?.SetValue(__instance, __state.DTime);
            _pFactorTime?.SetValue(__instance, __state.FactorTime);
            _pHFactor?.SetValue(__instance, __state.HFactor);
        }
    }

    /// <summary>
    /// Deterministically refreshes CSprite animation progress (#=zbBN4HvXVWuDv) right before
    /// CMovable logic/physics force computation. Vanilla only updates #=zbBN4HvXVWuDv inside
    /// UpdateSceneObjectParallel for entities visible in the local camera viewport, yet CMovable
    /// reads it for zombie gait acceleration and harpy jump timing on all entities.
    ///
    /// Uses a compiled DynamicMethod with skipVisibility so all 20k+ movables run with zero
    /// reflection/boxing overhead and immediately early-return when neither jumping nor gait-moving.
    /// </summary>
    [HarmonyPatch]
    public static class CMovableSpriteProgressSyncPatch
    {
        private static volatile bool _metaInit;
        private static readonly object _metaLock = new object();
        private static MethodInfo _mGetSprite;
        private static FieldInfo _fCurAnim;
        private static PropertyInfo _pTimeAnimStart;
        private static FieldInfo _fAnimProgress;
        private static MethodInfo _mAnimProgress;
        private static Action<object, int> _fastSync;

        internal static void ResetCache()
        {
            EnsureMeta();
        }

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            lock (_metaLock)
            {
                if (_metaInit) return;
                var asm = GameBridge.GameAssembly;
                if (asm == null) return;

                Type tEntity = GameBridge.TypeZXEntity;
                Type tSprite = asm.GetType("ZX.Components.CSprite");
                Type tSpriteAnim = asm.GetType("ZX.Components.SpriteAnimation");
                Type tMovable = asm.GetType("ZX.Components.CMovable");

                if (tEntity != null)
                    _mGetSprite = AccessTools.Method(tEntity, "#=zbkLIvkCtqwewx0xa2A==", Type.EmptyTypes);
                if (tSprite != null)
                {
                    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    _fCurAnim = tSprite.GetField("#=za7pUzvqbWjrs", F);
                    _pTimeAnimStart = tSprite.GetProperty("TimeAnimationStart", F);
                    _fAnimProgress = tSprite.GetField("#=zbBN4HvXVWuDv", F);
                }
                if (tSpriteAnim != null)
                    _mAnimProgress = AccessTools.Method(tSpriteAnim, "#=z_y5dBgkM5Q4t", new[] { typeof(int) });

                _fastSync = TryCompileFastSync(tMovable, tEntity, tSprite, tSpriteAnim);
                _metaInit = true;
            }
        }

        private static Action<object, int> TryCompileFastSync(Type tMovable, Type tEntity, Type tSprite, Type tSpriteAnim)
        {
            try
            {
                if (tMovable == null || tEntity == null || tSprite == null || tSpriteAnim == null) return null;
                if (_mGetSprite == null || _fCurAnim == null || _pTimeAnimStart == null || _fAnimProgress == null || _mAnimProgress == null) return null;

                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var fTargetPos = tMovable.GetField("#=zx1L3mWRqDcT7", F);
                var fGaitFactor = tMovable.GetField("#=z0l82Y9z76YJp58yPhQ==", F);
                var mGetJumpDestiny = tMovable.GetProperty("JumpDestiny", F)?.GetGetMethod(true);
                var mGetEntity = typeof(DXComponent).GetProperty("Entity", F)?.GetGetMethod(true);
                var mGetTimeAnimStart = _pTimeAnimStart.GetGetMethod(true);
                var mPointFNullableHasValue = typeof(PointF?).GetProperty("HasValue")?.GetGetMethod();

                if (fTargetPos == null || fGaitFactor == null || mGetJumpDestiny == null || mGetEntity == null ||
                    mGetTimeAnimStart == null || mPointFNullableHasValue == null)
                    return null;

                var dm = new DynamicMethod("TABCoop_FastMovableSpriteSync", typeof(void), new[] { typeof(object), typeof(int) },
                    typeof(CMovableSpriteProgressSyncPatch).Module, skipVisibility: true);
                var il = dm.GetILGenerator();

                var locMov = il.DeclareLocal(tMovable);
                var locTargetOpt = il.DeclareLocal(typeof(PointF?));
                var locJumpOpt = il.DeclareLocal(typeof(PointF?));
                var locEnt = il.DeclareLocal(tEntity);
                var locSprite = il.DeclareLocal(tSprite);

                var lblCheckJump = il.DefineLabel();
                var lblProceed = il.DefineLabel();
                var lblRet = il.DefineLabel();

                // mov = (CMovable)arg0;
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, tMovable);
                il.Emit(OpCodes.Stloc, locMov);

                // if (mov.#=z0l82Y9z76YJp58yPhQ== > 0f && mov.#=zx1L3mWRqDcT7.HasValue) goto proceed;
                il.Emit(OpCodes.Ldloc, locMov);
                il.Emit(OpCodes.Ldfld, fGaitFactor);
                il.Emit(OpCodes.Ldc_R4, 0f);
                il.Emit(OpCodes.Ble_Un_S, lblCheckJump);

                il.Emit(OpCodes.Ldloc, locMov);
                il.Emit(OpCodes.Ldflda, fTargetPos);
                il.Emit(OpCodes.Call, mPointFNullableHasValue);
                il.Emit(OpCodes.Brtrue_S, lblProceed);

                // if (!mov.JumpDestiny.HasValue) return;
                il.MarkLabel(lblCheckJump);
                il.Emit(OpCodes.Ldloc, locMov);
                il.Emit(OpCodes.Callvirt, mGetJumpDestiny);
                il.Emit(OpCodes.Stloc, locJumpOpt);
                il.Emit(OpCodes.Ldloca, locJumpOpt);
                il.Emit(OpCodes.Call, mPointFNullableHasValue);
                il.Emit(OpCodes.Brfalse_S, lblRet);

                // ent = mov.Entity as ZXEntity; if (ent == null) return;
                il.MarkLabel(lblProceed);
                il.Emit(OpCodes.Ldloc, locMov);
                il.Emit(OpCodes.Callvirt, mGetEntity);
                il.Emit(OpCodes.Isinst, tEntity);
                il.Emit(OpCodes.Stloc, locEnt);
                il.Emit(OpCodes.Ldloc, locEnt);
                il.Emit(OpCodes.Brfalse_S, lblRet);

                // sprite = ent.#=zbkLIvkCtqwewx0xa2A==(); if (sprite == null) return;
                il.Emit(OpCodes.Ldloc, locEnt);
                il.Emit(OpCodes.Callvirt, _mGetSprite);
                il.Emit(OpCodes.Stloc, locSprite);
                il.Emit(OpCodes.Ldloc, locSprite);
                il.Emit(OpCodes.Brfalse_S, lblRet);

                // sprite.#=zbBN4HvXVWuDv = sprite.#=za7pUzvqbWjrs.#=z_y5dBgkM5Q4t(gameTimeInt - sprite.TimeAnimationStart);
                il.Emit(OpCodes.Ldloc, locSprite);
                il.Emit(OpCodes.Ldloc, locSprite);
                il.Emit(OpCodes.Ldflda, _fCurAnim);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloc, locSprite);
                il.Emit(OpCodes.Callvirt, mGetTimeAnimStart);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Call, _mAnimProgress);
                il.Emit(OpCodes.Stfld, _fAnimProgress);

                il.MarkLabel(lblRet);
                il.Emit(OpCodes.Ret);

                var del = (Action<object, int>)dm.CreateDelegate(typeof(Action<object, int>));
                ModLogger.Info("[Perf] Compiled DynamicMethod TABCoop_FastMovableSpriteSync.");
                return del;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[Perf] TryCompileFastMovableSpriteSync failed (falling back to reflection): {ex.Message}");
                return null;
            }
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            EnsureMeta();
            Type tMovable = GameBridge.GameAssembly?.GetType("ZX.Components.CMovable");
            if (tMovable == null) yield break;

            // Only #=zMgpsCOScDEumTw2atg== (jumper height check inside #=z5GPRaBw=) and
            // #=zi4HgedM=(PointF) (active gait speed modulation inside #=zkgQp3Dx6LolarfpKKQ==)
            // actually read CSprite.#=zbBN4HvXVWuDv. Targeting them avoids ~21k Harmony
            // trampolines/tick on stationary units.
            var mJumpCheck = AccessTools.Method(tMovable, "#=zMgpsCOScDEumTw2atg==", Type.EmptyTypes);
            if (mJumpCheck != null) yield return mJumpCheck;

            var mSteerTo = AccessTools.Method(tMovable, "#=zi4HgedM=", new[] { typeof(PointF) });
            if (mSteerTo != null) yield return mSteerTo;
        }

        [HarmonyPrefix]
        public static void Prefix(DXComponent __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive || __instance == null) return;
            bool samplePerf = DeterministicSandboxPatches.DiagPerfSamplingActive;
            long t0 = samplePerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            var game = DXGame.Current;
            if (game == null) return;

            int simTimeInt = DXGame.LastGameTimeIAUpdate > 0.0
                ? (int)DXGame.LastGameTimeIAUpdate
                : game.GameTimeInt;

            // Always recompute sprite progress without any cross-thread/process-dependent cache:
            // Loop 1 (StartUpdatePhysics) invokes this concurrently across workers, and CSprite.#=zHT0zbX8=
            // may switch Walk->Run animation duration within the same simTimeInt tick between IA and Physics.
            EnsureMeta();
            if (_fastSync != null)
            {
                _fastSync(__instance, simTimeInt);
            }
            else
            {
                var entity = __instance.Entity;
                if (entity == null || _mGetSprite == null || _fCurAnim == null || _pTimeAnimStart == null || _fAnimProgress == null || _mAnimProgress == null)
                    return;
                if (!_mGetSprite.DeclaringType.IsInstanceOfType(entity))
                    return;

                object csprite = _mGetSprite.Invoke(entity, null);
                if (csprite == null) return;

                object animBox = _fCurAnim.GetValue(csprite);
                if (animBox == null) return;

                int animStart = (int)_pTimeAnimStart.GetValue(csprite);
                int elapsed = simTimeInt - animStart;
                float progress = (float)_mAnimProgress.Invoke(animBox, new object[] { elapsed });
                _fAnimProgress.SetValue(csprite, progress);
            }

            if (samplePerf)
            {
                System.Threading.Interlocked.Add(ref DeterministicSandboxPatches.DiagTickMovableSyncTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagTickMovableSyncCalls);
            }
        }
    }

    /// <summary>
    /// Redirects calls made directly on the static field DXScene.GlobalRandom (e.g. DXLinq.RandomElement,
    /// DXLinq.Shuffle, and main-thread DXHelper_Random) to a deterministic stream during simulation and
    /// loading, while routing render/UI draws to the isolated render stream.
    /// </summary>
    [HarmonyPatch(typeof(DXRandom), "genrand_uint32")]
    public static class SceneGlobalRandomInstancePatch
    {
        private static readonly DXRandom _sceneGlobalSentinel = DXScene.GlobalRandom;

        [HarmonyPrefix]
        public static bool Prefix(DXRandom __instance, ref uint __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            bool samplePerf = DeterministicSandboxPatches.DiagPerfSamplingActive;
            if (samplePerf)
                System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagRngGenCalls);

            if (LockstepClock.InAudioContext || LockstepClock.InVisualVfxContext)
            {
                var renderRng = LockstepClock.GetRenderGlobalRandom();
                if (object.ReferenceEquals(__instance, renderRng))
                    return true;
                __result = renderRng.NextUInt();
                return false;
            }

            if (!object.ReferenceEquals(__instance, LockstepClock.RenderGlobalRandomRaw)
                && LockstepClock.TryNextScopedRandomUInt(out uint scopedRand))
            {
                if (samplePerf)
                    System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagRngScopedCalls);
                __result = scopedRand;
                return false;
            }
            if (!object.ReferenceEquals(__instance, _sceneGlobalSentinel)) return true;

            __result = LockstepClock.ShouldUseSyncedSceneRandom()
                ? LockstepClock.GetSyncedSceneRandom().NextUInt()
                : LockstepClock.GetRenderGlobalRandom().NextUInt();
            return false;
        }
    }

    /// <summary>
    /// Ensures DXHelper_Random.Random follows the same deterministic vs render bifurcation as
    /// DXScene.GlobalRandom even on worker threads where [ThreadStatic] _Random is initially null.
    /// </summary>
    [HarmonyPatch(typeof(DXHelper_Random), "Random", MethodType.Getter)]
    public static class HelperRandomGetterPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref DXRandom __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;

            __result = LockstepClock.ShouldUseSyncedSceneRandom()
                ? LockstepClock.GetSyncedSceneRandom()
                : LockstepClock.GetRenderGlobalRandom();
            return false;
        }
    }

    /// <summary>
    /// Isolates synchronous ZXSystemAudio calls from the deterministic PRNG streams. Audio
    /// playback checks local camera frustum and singleton caches (sound-list shuffle dictionary),
    /// so letting it draw from DXRandom.Global or DXScene.GlobalRandom desyncs Host vs Client.
    /// </summary>
    [HarmonyPatch]
    public static class AudioRngIsolationPatch
    {
        private static readonly string[] TargetNames =
        {
            Core.ObfuscatedSymbols.Method_Audio_PickFromList,
            Core.ObfuscatedSymbols.Method_Audio_PickWeightedVoice,
            Core.ObfuscatedSymbols.Method_Audio_StartAmbient,
            Core.ObfuscatedSymbols.Method_Audio_PlayUnique,
            Core.ObfuscatedSymbols.Method_Audio_PlayEntitySound,
            Core.ObfuscatedSymbols.Method_Audio_PlayPosSound,
            Core.ObfuscatedSymbols.Method_Audio_PlayPosSound3D,
            Core.ObfuscatedSymbols.Method_Audio_Play2DSound,
            Core.ObfuscatedSymbols.Method_Audio_PlayNamed2DSound,
            Core.ObfuscatedSymbols.Method_Audio_PlayNotification,
        };

        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var audioType = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXSystemAudio);
            if (audioType == null) yield break;

            var nameSet = new System.Collections.Generic.HashSet<string>(TargetNames, StringComparer.Ordinal);
            foreach (var m in AccessTools.GetDeclaredMethods(audioType))
            {
                if (m != null && !m.IsAbstract && nameSet.Contains(m.Name))
                    yield return m;
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix() => LockstepClock.EnterAudioContext();

        [HarmonyFinalizer]
        public static void Finalizer() => LockstepClock.ExitAudioContext();
    }

    /// <summary>
    /// Replaces BHFarmer's farm-cell picker (#=zK2CGzIZdW6uI$Q9jPNxMDPs=) in deterministic mode so
    /// it selects grass cells around the owner Farm from deterministic terrain + building occupancy
    /// (TerrainResourceCells == Grass and !AnyEntityAtCell(..., 2048u)) rather than querying
    /// CFarmer's visual Wheat_A DXEntities (which spawn via InvokeOnStartFrame and fade out over
    /// 1000ms of render-frame animation time before removal from DXWorldGrid).
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicFarmerTargetPatch
    {
        private static bool _metaInit;
        private static MethodInfo _mBDataOwner;
        private static PropertyInfo _pBDataTargetPos;
        private static PropertyInfo _pRefBuildingOwner;
        private static PropertyInfo _pRefEntity;
        private static MethodInfo _mGetParams;
        private static PropertyInfo _pCollectRadius;
        private static MethodInfo _mTerrainGetCurrent;
        private static PropertyInfo _pTerrainCells;

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            var asm = GameBridge.GameAssembly;
            if (asm == null) return;

            var tBData = asm.GetType("ZX.Behaviours.BData");
            if (tBData != null)
            {
                _mBDataOwner = AccessTools.Method(tBData, Core.ObfuscatedSymbols.Method_BData_GetOwner, Type.EmptyTypes);
                _pBDataTargetPos = AccessTools.Property(tBData, "TargetPosition");
            }

            var tColonist = asm.GetType("ZX.Entities.Colonist");
            if (tColonist != null)
            {
                _pRefBuildingOwner = AccessTools.Property(tColonist, "RefBuildingOwner");
                if (_pRefBuildingOwner != null)
                    _pRefEntity = AccessTools.Property(_pRefBuildingOwner.PropertyType, "Entity");
            }

            var tZxEntity = asm.GetType("ZX.Entities.ZXEntity");
            if (tZxEntity != null)
                _mGetParams = AccessTools.Method(tZxEntity, Core.ObfuscatedSymbols.Method_ZXEntity_GetParams, Type.EmptyTypes);

            var tParams = _mGetParams?.ReturnType;
            if (tParams != null)
                _pCollectRadius = AccessTools.Property(tParams, "ResourceCollectionRadius");

            var tTerrain = asm.GetType(Core.ObfuscatedSymbols.Type_TerrainSystem);
            if (tTerrain != null)
            {
                _mTerrainGetCurrent = AccessTools.Method(tTerrain, Core.ObfuscatedSymbols.Method_TerrainSystem_GetCurrent, Type.EmptyTypes);
                _pTerrainCells = AccessTools.Property(tTerrain, Core.ObfuscatedSymbols.Prop_TerrainSystem_Cells);
            }
        }

        public static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var tBeh = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXBehaviour);
            var fNode = tBeh != null
                ? tBeh.GetField(Core.ObfuscatedSymbols.Field_ZXBehaviour_PickFarmCell, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                : null;
            object node = fNode?.GetValue(null);
            if (node == null) yield break;

            var del = AccessTools.Property(node.GetType(), "OnExecute")?.GetValue(node) as Delegate;
            if (del?.Method != null) yield return del.Method;
        }

        [HarmonyPrefix]
        public static bool Prefix(object __0, ref IARes __result)
        {
            if (!LockstepClock.IsDeterministicModeActive || __0 == null) return true;
            EnsureMeta();
            if (_mBDataOwner == null || _pBDataTargetPos == null || _pRefBuildingOwner == null ||
                _pRefEntity == null || _mGetParams == null || _pCollectRadius == null ||
                _mTerrainGetCurrent == null || _pTerrainCells == null)
            {
                return true;
            }

            var colonist = _mBDataOwner.Invoke(__0, null);
            if (colonist == null) return true;

            var ownerRef = _pRefBuildingOwner.GetValue(colonist);
            var building = ownerRef != null ? _pRefEntity.GetValue(ownerRef) as DXEntity : null;
            if (building == null)
            {
                _pBDataTargetPos.SetValue(__0, null);
                __result = IARes.Fail;
                return false;
            }

            object terrainSys = _mTerrainGetCurrent.Invoke(null, null);
            var cells = terrainSys != null ? _pTerrainCells.GetValue(terrainSys) as Array : null;
            object defParams = _mGetParams.Invoke(building, null);
            if (cells == null || defParams == null) return true;

            int radius = (int)_pCollectRadius.GetValue(defParams);
            System.Drawing.Rectangle buildingArea = building.CellArea;
            System.Drawing.Rectangle searchArea = buildingArea.Enlarge(radius);
            int xMin = Math.Max(0, searchArea.X);
            int xMax = Math.Min(DXWorldGrid.NCells - 1, searchArea.Right - 1);
            int yMin = Math.Max(0, searchArea.Y);
            int yMax = Math.Min(DXWorldGrid.NCells - 1, searchArea.Bottom - 1);

            var candidates = new System.Collections.Generic.List<System.Drawing.Point>();
            for (int x = xMin; x <= xMax; x++)
            {
                for (int y = yMin; y <= yMax; y++)
                {
                    if (buildingArea.Contains(x, y)) continue;
                    // ZXTerrainResourceType.Grass == 30
                    if (Convert.ToByte(cells.GetValue(x, y)) != 30) continue;
                    // 2048u == Structure mask (same exclusion check used by CFarmer)
                    if (DXWorldGrid.AnyEntityAtCell(x, y, 2048u)) continue;
                    candidates.Add(new System.Drawing.Point(x, y));
                }
            }

            if (candidates.Count == 0)
            {
                _pBDataTargetPos.SetValue(__0, null);
                __result = IARes.Fail;
                return false;
            }

            System.Drawing.Point chosen = candidates.RandomElement();
            _pBDataTargetPos.SetValue(__0, DXWorldGrid.PointFromCell_Center(chosen));
            __result = IARes.Success;
            return false;
        }
    }

    /// <summary>
    /// Isolates camera-gated visual VFX helpers (#=zg10wm6mM9hhx: footsteps/dust puffs, blood
    /// splatters, hit sparks, explosions, screen shake, bullet tracers), CShotEffect muzzle-flash
    /// visuals (and its one-time static DXProjectLevel asset load), and auxiliary DXProjectLevel.OnLoadData
    /// deserializations from the synced PRNG and entity-ID streams.
    /// </summary>
    [HarmonyPatch]
    public static class VisualVfxIsolationPatch
    {
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var vfxType = AccessTools.TypeByName("#=zg10wm6mM9hhx");
            if (vfxType != null)
            {
                foreach (var m in AccessTools.GetDeclaredMethods(vfxType))
                {
                    if (m != null && !m.IsAbstract && !m.ContainsGenericParameters)
                        yield return m;
                }
            }

            // CShotEffect lazily deserializes a static DXProjectLevel once per process in #=zcv694jLlBMdF()
            // on first OnSceneAdded(), and allocates visual DXProjectObject_Sprite clips.
            var shotType = AccessTools.TypeByName("ZX.Components.CShotEffect");
            if (shotType != null)
            {
                foreach (var m in AccessTools.GetDeclaredMethods(shotType))
                {
                    if (m != null && !m.IsAbstract && !m.ContainsGenericParameters && !m.IsConstructor)
                        yield return m;
                }
            }

            var projLevelLoad = AccessTools.DeclaredMethod(typeof(DXProjectLevel), "OnLoadData");
            if (projLevelLoad != null)
                yield return projLevelLoad;

            // ZXMapEvent is allocated in #=z5Ns_eH8ENLF7HFU5mg== depending on local CameraArea.Contains(cell),
            // and calls DXGuid.New() inside its field initializer / OnPoolNew(). Isolate it from the
            // synced entity ID counter so camera divergence never shifts _syncedEntityIdCounter.
            var mapEventType = AccessTools.TypeByName("ZX.Systems.ZXMapEvent");
            if (mapEventType != null)
            {
                foreach (var c in AccessTools.GetDeclaredConstructors(mapEventType))
                    if (c != null) yield return c;
                var mPoolNew = AccessTools.Method(mapEventType, "OnPoolNew");
                if (mPoolNew != null) yield return mPoolNew;
            }

            var msgQueueType = AccessTools.TypeByName("ZX.GUI.OMessageQueue");
            if (msgQueueType != null)
            {
                foreach (var c in AccessTools.GetDeclaredConstructors(msgQueueType))
                    if (c != null) yield return c;
            }

            // Ambient cosmetic colonists (Worker_A / Worker_B) spawned by enabled Buildings do not
            // participate in combat or resource generation; isolate their IDs to the 0x8000... UI track.
            var buildingType = GameBridge.TypeBuilding;
            if (buildingType != null)
            {
                var mSpawnColonists = AccessTools.Method(buildingType, Core.ObfuscatedSymbols.Method_Building_SpawnColonists, Type.EmptyTypes);
                if (mSpawnColonists != null) yield return mSpawnColonists;
            }
        }

        [HarmonyPrefix]
        public static void Prefix() => LockstepClock.EnterVisualVfxContext();

        [HarmonyFinalizer]
        public static void Finalizer() => LockstepClock.ExitVisualVfxContext();
    }

    /// <summary>
    /// Propagates deterministic simulation context into deferred callbacks queued via
    /// DXPostMethodManager.Invoke (OnStartFrame, OnFinishFrame, OnSafeLogicAndDrawing,
    /// DXPhysicWorld.PostMethods) when scheduled from a simulation context, so that
    /// callbacks executed later during DXGame.OnRender (e.g. ZXEntity zombie spawns)
    /// stay on the synced RNG stream and synced DXGuid track.
    /// </summary>
    [HarmonyPatch(typeof(DXPostMethodManager), nameof(DXPostMethodManager.Invoke))]
    public static class PostMethodInvokeContextPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DXPostMethodManager __instance, ref Action method)
        {
            if (method == null || !LockstepClock.IsDeterministicModeActive)
                return true;
            if (LockstepClock.LoadingLevel || LockstepClock.InAudioContext || LockstepClock.InVisualVfxContext)
                return true;

            int main = LockstepClock.MainThreadId;
            bool onMain = main != 0 && Thread.CurrentThread.ManagedThreadId == main;
            bool isSimThread = LockstepClock.InDeterministicContext
                || LockstepClock.IsLogicWorkerActive()
                || LockstepClock.IsPhysicsThreadActive();
            if (!isSimThread)
                return true;

            Action orig = method;
            if (DeterministicSandboxPatches.DiagPerfSamplingActive)
                System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagPostWrapCalls);

            // When _TaskLogic runs concurrently with RenderFrame(), defer OnStartFrame/OnFinishFrame/
            // OnSafeLogicAndDrawing callbacks enqueued from the worker thread until WaitForGameLogic
            // finishes so render timing can never change which tick executes a simulation callback.
            var game = DXGame.Current;
            if (DeterministicSandboxPatches.IsAsyncTickInFlight && !onMain && game != null
                && (object.ReferenceEquals(__instance, game.PostMethods_OnStartFrame)
                    || object.ReferenceEquals(__instance, game.PostMethods_OnFinishFrame)
                    || object.ReferenceEquals(__instance, game.PostMethods_OnSafeLogicAndDrawing)))
            {
                DeterministicSandboxPatches.EnqueueDeferredAsyncPostMethod(orig);
                return false;
            }

            method = () => LockstepClock.RunInDeterministicContext(orig);
            return true;
        }
    }

    /// <summary>
    /// Prevents DXGame.OnRender / FinishFrame from draining PostMethods_OnFinishFrame (or
    /// StartFrame/SafeLogic) on the main thread while an async _TaskLogic tick is in flight,
    /// e.g. callbacks queued synchronously by FlushDueTurnCommands just prior to launching _TaskLogic.
    /// WaitForGameLogic_Postfix drains all three queues deterministically once the worker completes.
    /// </summary>
    [HarmonyPatch(typeof(DXPostMethodManager), nameof(DXPostMethodManager.ExecuteAll))]
    public static class PostMethodExecuteAsyncGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DXPostMethodManager __instance)
        {
            if (!DeterministicSandboxPatches.IsAsyncTickInFlight)
                return true;

            var game = DXGame.Current;
            if (game != null && (object.ReferenceEquals(__instance, game.PostMethods_OnStartFrame)
                || object.ReferenceEquals(__instance, game.PostMethods_OnFinishFrame)
                || object.ReferenceEquals(__instance, game.PostMethods_OnSafeLogicAndDrawing)))
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Marks LoadingLevel = true right when ZXGame.LoadGameAndEnter begins loading a .zxsav file,
    /// allowing menu Co-op sessions to defer LoadingLevel until an actual save load occurs (so
    /// New Survival map generation creates normal tracked entity IDs rather than 0x8000... UI IDs).
    /// </summary>
    [HarmonyPatch]
    public static class LoadGameLoadingFlagPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeZXGame, ObfuscatedSymbols.Method_ZXGame_LoadGameAndEnter, new[] { typeof(string) });

        [HarmonyPrefix]
        public static void Prefix()
        {
            if (LockstepClock.IsDeterministicModeActive)
                LockstepClock.LoadingLevel = true;
        }
    }

    /// <summary>
    /// Isolates CSprite.#=zykGuoNdyOv10tsMpy8FzNkE=() (physics-push animation/movement mode sync)
    /// from render-time UpdateSceneObjectParallel, which only runs for entities inside the local
    /// camera viewport when their sprite atlas frame advances. Instead, we run it deterministically
    /// once per physics step for all entities with CanBePulled == true.
    /// </summary>
    [HarmonyPatch]
    public static class CSpritePullIsolationPatch
    {
        private static Type _cspriteType;
        private static int _cspriteTypeId;
        private static MethodInfo _pullMethod;
        private static MethodInfo _isPulledGetter;
        private static PropertyInfo _paramsProp;
        private static PropertyInfo _canBePulledProp;
        private static Action<object> _fastPull;
        private static Func<object, bool> _fastIsPulled;
        private static bool _fastCompiled;
        private static readonly HashSet<DXEntity> _activePullEntities = new HashSet<DXEntity>();
        private static DXLevel _lastColdScanLevel;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<RuntimeTypeHandle, bool> _canPullByTypeHandle =
            new System.Collections.Concurrent.ConcurrentDictionary<RuntimeTypeHandle, bool>();
        private static readonly object _canPullMetaLock = new object();

        private static MethodBase TargetMethod()
        {
            _cspriteType = AccessTools.TypeByName("ZX.Components.CSprite");
            if (_cspriteType == null) return null;
            _cspriteTypeId = DXComponent.GetIDFromType(_cspriteType);
            _paramsProp = AccessTools.Property(_cspriteType, "Params");
            _pullMethod = AccessTools.Method(_cspriteType, "#=zykGuoNdyOv10tsMpy8FzNkE=");
            _isPulledGetter = AccessTools.Property(_cspriteType, "IsPulled")?.GetGetMethod(true);
            return _pullMethod;
        }

        private static void EnsureFastDelegates()
        {
            if (_fastCompiled) return;
            _fastCompiled = true;
            try
            {
                if (_cspriteType == null || _pullMethod == null || _isPulledGetter == null) return;

                var dmPull = new DynamicMethod("TABCoop_FastSpritePull", typeof(void), new[] { typeof(object) },
                    typeof(CSpritePullIsolationPatch).Module, skipVisibility: true);
                var ilP = dmPull.GetILGenerator();
                ilP.Emit(OpCodes.Ldarg_0);
                ilP.Emit(OpCodes.Castclass, _cspriteType);
                ilP.Emit(OpCodes.Callvirt, _pullMethod);
                ilP.Emit(OpCodes.Ret);
                _fastPull = (Action<object>)dmPull.CreateDelegate(typeof(Action<object>));

                var dmIs = new DynamicMethod("TABCoop_FastSpriteIsPulled", typeof(bool), new[] { typeof(object) },
                    typeof(CSpritePullIsolationPatch).Module, skipVisibility: true);
                var ilI = dmIs.GetILGenerator();
                ilI.Emit(OpCodes.Ldarg_0);
                ilI.Emit(OpCodes.Castclass, _cspriteType);
                ilI.Emit(OpCodes.Callvirt, _isPulledGetter);
                ilI.Emit(OpCodes.Ret);
                _fastIsPulled = (Func<object, bool>)dmIs.CreateDelegate(typeof(Func<object, bool>));

                ModLogger.Info("[Perf] Compiled DynamicMethods TABCoop_FastSpritePull / IsPulled.");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[Perf] FastSpritePull compile failed (falling back to reflection): {ex.Message}");
                _fastPull = null;
                _fastIsPulled = null;
            }
        }

        internal static bool TryBeginMergedPullCollection(out bool coldScan, out HashSet<DXEntity> activeSet)
        {
            coldScan = false;
            activeSet = null;
            if (_pullMethod == null || _cspriteType == null)
                return false;

            EnsureFastDelegates();
            var curLevel = DXLevel.Current;
            coldScan = !object.ReferenceEquals(_lastColdScanLevel, curLevel);
            if (coldScan)
                _activePullEntities.Clear();
            else if (_activePullEntities.Count > 0)
                _activePullEntities.RemoveWhere(ent => ent == null || ent.Disposed);

            activeSet = _activePullEntities;
            return true;
        }

        internal static bool TryPreparePullCandidate(DXEntity e, out object csprite)
        {
            csprite = null;
            if (e == null) return false;
            RuntimeTypeHandle handle = e.GetType().TypeHandle;
            if (_canPullByTypeHandle.TryGetValue(handle, out bool knownCanPull))
            {
                if (!knownCanPull) return false;
                csprite = e.GetComponent(_cspriteTypeId);
                return csprite != null;
            }

            csprite = e.GetComponent(_cspriteTypeId);
            if (csprite == null)
            {
                _canPullByTypeHandle.TryAdd(handle, false);
                return false;
            }

            lock (_canPullMetaLock)
            {
                if (_canPullByTypeHandle.TryGetValue(handle, out knownCanPull))
                    return knownCanPull;
                var p = _paramsProp?.GetValue(csprite);
                if (p == null) return false;
                if (_canBePulledProp == null)
                    _canBePulledProp = AccessTools.Property(p.GetType(), "CanBePulled");
                knownCanPull = (bool)(_canBePulledProp?.GetValue(p) ?? false);
                _canPullByTypeHandle.TryAdd(handle, knownCanPull);
                return knownCanPull;
            }
        }

        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !LockstepClock.IsDeterministicModeActive || LockstepClock.AllowCSpritePullUpdate;
        }

        public static void RunDeterministicPullUpdates(DXGame game)
        {
            if (_pullMethod == null || _cspriteType == null)
                return;

            EnsureFastDelegates();
            bool samplePerf = DeterministicSandboxPatches.DiagPerfSamplingActive;
            long t0 = samplePerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            int calls = 0;

            var curLevel = DXLevel.Current;
            LockstepClock.AllowCSpritePullUpdate = true;
            try
            {
                if (LockstepClock.TryConsumeMergedPullCandidates(out var mergedQueues, out int workerCount))
                {
                    _lastColdScanLevel = curLevel;
                    for (int w = 0; w < workerCount; w++)
                    {
                        var q = mergedQueues[w];
                        for (int i = 0; i < q.Count; i++)
                        {
                            var cand = q[i];
                            if (cand.Ent == null || cand.Ent.Disposed || cand.CSprite == null) continue;

                            if (_fastPull != null)
                                _fastPull(cand.CSprite);
                            else
                                _pullMethod.Invoke(cand.CSprite, null);

                            bool nowPulled = _fastIsPulled != null
                                ? _fastIsPulled(cand.CSprite)
                                : (_isPulledGetter != null && (bool)_isPulledGetter.Invoke(cand.CSprite, null));
                            if (nowPulled)
                                _activePullEntities.Add(cand.Ent);
                            else
                                _activePullEntities.Remove(cand.Ent);

                            calls++;
                        }
                        q.Clear();
                    }
                    return;
                }

                var bodies = DXPhysicWorld.Bodies;
                if (bodies == null) return;

                bool coldScan = !object.ReferenceEquals(_lastColdScanLevel, curLevel);
                if (coldScan)
                {
                    _lastColdScanLevel = curLevel;
                    _activePullEntities.Clear();
                }
                else if (_activePullEntities.Count > 0)
                {
                    _activePullEntities.RemoveWhere(ent => ent == null || ent.Disposed);
                }

                int bodyCount = bodies.Count;
                for (int i = 0; i < bodyCount; i++)
                {
                    var body = bodies[i];
                    var e = body?.GameEntity;
                    if (e == null || e.Disposed || e.PhysicalType == DXBodyType.Static)
                        continue;

                    if (!coldScan)
                    {
                        var v = body.Velocity;
                        if (v.X * v.X + v.Y * v.Y <= 0.04f && !_activePullEntities.Contains(e))
                            continue;
                    }

                    if (!TryPreparePullCandidate(e, out object csprite))
                        continue;

                    if (_fastPull != null)
                        _fastPull(csprite);
                    else
                        _pullMethod.Invoke(csprite, null);

                    bool nowPulled = _fastIsPulled != null
                        ? _fastIsPulled(csprite)
                        : (_isPulledGetter != null && (bool)_isPulledGetter.Invoke(csprite, null));
                    if (nowPulled)
                        _activePullEntities.Add(e);
                    else
                        _activePullEntities.Remove(e);

                    calls++;
                }
            }
            finally
            {
                LockstepClock.AllowCSpritePullUpdate = false;
                if (samplePerf)
                {
                    DeterministicSandboxPatches.DiagTickPullSyncTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    DeterministicSandboxPatches.DiagTickPullSyncCalls += calls;
                }
            }
        }
    }

    /// <summary>
    /// Prevents DXGame._TaskGameTime background thread from mutating DXGame._GameTime and
    /// DXGame.GameTimeInt asynchronously while deterministic mode is active. Since .NET JIT
    /// inlines DXGame.get_GameTimeInt() across assemblies, freezing the backing field itself
    /// ensures CMovable, CSprite, and ZXEntity read identical tick timestamps across peers.
    /// </summary>
    [HarmonyPatch(typeof(DXGame), nameof(DXGame.GameTime), MethodType.Setter)]
    public static class GameTimeIsolationPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel)
                return true;
            return LockstepClock.AllowRawGameTimeSet;
        }
    }

    /// <summary>
    /// Allows explicit engine clock resets via DXGame.SetGameTime(double) — e.g. when starting a
    /// New Survival or Custom Map after returning from a previous match — so GameTime/IAIterationInt
    /// do not inherit the previous match's colony days while GameTimeIsolationPatch is active.
    /// </summary>
    [HarmonyPatch(typeof(DXGame), nameof(DXGame.SetGameTime), new[] { typeof(double) })]
    public static class GameSetGameTimePatch
    {
        [HarmonyPrefix]
        public static void Prefix(out bool __state)
        {
            __state = LockstepClock.AllowRawGameTimeSet;
            LockstepClock.AllowRawGameTimeSet = true;
        }

        [HarmonyPostfix]
        public static void Postfix(double gameTime, bool __state)
        {
            LockstepClock.AllowRawGameTimeSet = __state;
            if (LockstepClock.IsDeterministicModeActive)
            {
                DXGame.LastGameTimeIAUpdate = gameTime;
                DXPhysicWorld.LastGameTimeUpdatePhysics = gameTime;
            }
        }
    }

    /// <summary>
    /// Recreates the native Chipmunk2D cpSpace inside DXPhysicWorld.Clear() when deterministic mode
    /// is active. DXPhysicWorld.Init() normally runs only once per process, so cpSpace->shapeIDCounter,
    /// cpBBTree leaf/node pools, and cached arbiters carry across reloads/Auto-resync and alter
    /// cpBBTree query traversal and Gauss-Seidel arbiter ordering between Host and Client.
    /// </summary>
    [HarmonyPatch(typeof(DXPhysicWorld), nameof(DXPhysicWorld.Clear))]
    public static class DXPhysicWorldClearPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!LockstepClock.IsDeterministicModeActive || !DXPhysicWorld.Initialized)
                return;

            lock (DXPhysicWorld.Sync)
            {
                long prevIdCnt = DXPhysicWorld.Handle != IntPtr.Zero
                    ? System.Runtime.InteropServices.Marshal.ReadInt64(DXPhysicWorld.Handle, 0x58)
                    : -1;
                DXPhysicWorld.Init(10);
                DXPool.Clear();
                LockstepClock.SyncedDynamicBodyCount = 0;
                AdaptivePhysicsStepPatch.Reset();
                ModLogger.Info($"[LockstepClock] DXPhysicWorld.Clear -> reset cpSpace and DXPool (prevPhysIdCnt={prevIdCnt}, newSpace=0x{DXPhysicWorld.Handle.ToInt64():X}).");
            }
        }
    }

    /// <summary>
    /// Stage 4D-1: Opt-in deterministic Chipmunk2D solver iteration reduction (TABCOOP_ADAPTIVE_PHYS_ITER=1)
    /// driven strictly by SyncedDynamicBodyCount from the prior completed physics step.
    /// </summary>
    [HarmonyPatch(typeof(DXPhysicWorld), nameof(DXPhysicWorld.Step))]
    public static class AdaptivePhysicsStepPatch
    {
        private const int HighBodyThresholdEnter = 22000;
        private const int HighBodyThresholdExit = 20500;
        private const int MedBodyThresholdEnter = 16500;
        private const int MedBodyThresholdExit = 15500;

        private static IntPtr _lastSpaceHandle = IntPtr.Zero;
        internal static int CurrentIterations { get; private set; } = 10;

        internal static void Reset()
        {
            _lastSpaceHandle = IntPtr.Zero;
            CurrentIterations = 10;
        }

        [HarmonyPrefix]
        public static void Prefix()
        {
            IntPtr h = DXPhysicWorld.Handle;
            if (h == IntPtr.Zero) return;

            if (_lastSpaceHandle != h)
            {
                _lastSpaceHandle = h;
                CurrentIterations = 10;
            }

            bool allowAdaptive = Sync.CoopSessionManager.IsInSyncedLevel
                ? Sync.CoopSessionManager.SyncedAdaptivePhysicsIterations
                : Testing.TestHarnessConfig.AdaptivePhysicsIterations;
            if (!LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel || !allowAdaptive)
            {
                if (CurrentIterations != 10 && LockstepClock.NativePhysicsDirectAccessor.TrySetSpaceIterations(h, 10))
                    CurrentIterations = 10;
                return;
            }

            int dyn = LockstepClock.SyncedDynamicBodyCount;
            int target = CurrentIterations;
            if (CurrentIterations == 10)
            {
                if (dyn >= HighBodyThresholdEnter) target = 5;
                else if (dyn >= MedBodyThresholdEnter) target = 7;
            }
            else if (CurrentIterations == 7)
            {
                if (dyn >= HighBodyThresholdEnter) target = 5;
                else if (dyn < MedBodyThresholdExit) target = 10;
            }
            else
            {
                if (dyn < MedBodyThresholdExit) target = 10;
                else if (dyn < HighBodyThresholdExit) target = 7;
            }

            if (target != CurrentIterations && LockstepClock.NativePhysicsDirectAccessor.TrySetSpaceIterations(h, target))
            {
                ModLogger.Info($"[AdaptivePhys] cpSpace iterations {CurrentIterations} -> {target} (dynBodies={dyn}, tick={LockstepClock.CurrentSimTick}).");
                CurrentIterations = target;
            }
        }
    }

    /// <summary>
    /// Ensures pooled DXEntity instances (such as Proyectile) acquire a fresh deterministic
    /// DXGuid.New() on pool reuse just like newly constructed instances, eliminating
    /// render-frame pool hit/miss divergence across reloads and differing client framerates.
    /// </summary>
    [HarmonyPatch(typeof(DXEntity), nameof(DXEntity.OnPoolNew))]
    public static class DXEntityOnPoolNewPatch
    {
        private static bool IsProjectileEntity(DXEntity ent)
        {
            return ent != null && string.Equals(ent.GetType().Name, "Proyectile", StringComparison.Ordinal);
        }

        [HarmonyPrefix]
        public static void Prefix(DXEntity __instance, out bool __state)
        {
            __state = false;
            if (LockstepClock.IsDeterministicModeActive && __instance != null)
            {
                __instance.ID = 0L;
                if (IsProjectileEntity(__instance))
                {
                    LockstepClock.EnterProjectileGuidContext();
                    __state = true;
                }
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state)
                LockstepClock.ExitProjectileGuidContext();
        }
    }

    /// <summary>
    /// Isolates Proyectile constructor DXGuid.New() calls into the dedicated Bit 62 namespace
    /// (0x4000...) so DXPool&lt;Proyectile&gt; cache misses can never shift _syncedEntityIdCounter.
    /// </summary>
    [HarmonyPatch]
    public static class ProjectileCtorGuidIsolationPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var projType = AccessTools.TypeByName("ZX.Entities.Proyectile");
            if (projType != null)
            {
                foreach (var c in AccessTools.GetDeclaredConstructors(projType))
                    if (c != null) yield return c;
            }
        }

        [HarmonyPrefix]
        public static void Prefix() => LockstepClock.EnterProjectileGuidContext();

        [HarmonyFinalizer]
        public static void Finalizer() => LockstepClock.ExitProjectileGuidContext();
    }

    /// <summary>
    /// Scopes behaviour RNG draws inside ZXBehaviour.Run() to a stateless per-entity SplitMix64
    /// sequence keyed by (SyncedRandomSeed, SimTick, Entity.ID, drawIndex). Because every entity
    /// receives an independent RNG stream, an extra draw by one unit or zombie can never shift
    /// the behaviour random numbers of any other entity within the same simulation tick.
    /// </summary>
    [HarmonyPatch]
    public static class ZXBehaviourRunRngScopePatch
    {
        private static bool _metaInit;
        private static Type _tBehaviour;
        private static Type _tBData;
        private static MethodInfo _mGetData;
        private static MethodInfo _mGetOwner;
        private static Func<object, ulong> _fastGetEntityId;

        private static MethodBase TargetMethod()
        {
            var tBehav = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXBehaviour);
            if (tBehav == null) return null;
            EnsureMeta();
            return AccessTools.Method(tBehav, "Run", Type.EmptyTypes);
        }

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            try
            {
                var asm = Core.GameBridge.GameAssembly;
                if (asm == null) return;

                _tBehaviour = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXBehaviour);
                _tBData = asm.GetType("ZX.Behaviours.BData");
                if (_tBehaviour == null || _tBData == null) return;

                _mGetData = _tBehaviour.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)?.GetGetMethod(true);
                FieldInfo fOwner = _tBData.GetField("#=zIuCqHApzmkOGXXyxMg==", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _mGetOwner = AccessTools.Method(_tBData, Core.ObfuscatedSymbols.Method_BData_GetOwner, Type.EmptyTypes);
                MethodInfo mGetEntityId = typeof(DXEntity).GetProperty("ID", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);

                if (_mGetData != null && fOwner != null && mGetEntityId != null)
                {
                    var dm = new DynamicMethod("TABCoop_GetBehavOwnerEntityId", typeof(ulong), new[] { typeof(object) },
                        typeof(ZXBehaviourRunRngScopePatch).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    var locData = il.DeclareLocal(_tBData);
                    var locOwner = il.DeclareLocal(typeof(DXEntity));
                    var lblZero = il.DefineLabel();

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Brfalse_S, lblZero);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _tBehaviour);
                    il.Emit(OpCodes.Callvirt, _mGetData);
                    il.Emit(OpCodes.Stloc, locData);
                    il.Emit(OpCodes.Ldloc, locData);
                    il.Emit(OpCodes.Brfalse_S, lblZero);

                    il.Emit(OpCodes.Ldloc, locData);
                    il.Emit(OpCodes.Ldfld, fOwner);
                    il.Emit(OpCodes.Stloc, locOwner);
                    il.Emit(OpCodes.Ldloc, locOwner);
                    il.Emit(OpCodes.Brfalse_S, lblZero);

                    il.Emit(OpCodes.Ldloc, locOwner);
                    il.Emit(OpCodes.Callvirt, mGetEntityId);
                    il.Emit(OpCodes.Ret);

                    il.MarkLabel(lblZero);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Conv_I8);
                    il.Emit(OpCodes.Ret);

                    _fastGetEntityId = (Func<object, ulong>)dm.CreateDelegate(typeof(Func<object, ulong>));
                    ModLogger.Info("[Perf] Compiled DynamicMethod TABCoop_GetBehavOwnerEntityId.");
                }
            }
            catch (Exception ex)
            {
                _fastGetEntityId = null;
                ModLogger.Warn($"[LockstepClock] ZXBehaviourRunRngScopePatch meta init failed (using reflection): {ex.Message}");
            }
        }

        private static ulong ResolveEntityId(object behaviour)
        {
            if (behaviour == null) return 0UL;
            if (!_metaInit) EnsureMeta();
            if (_fastGetEntityId != null)
                return _fastGetEntityId(behaviour);

            object data = _mGetData?.Invoke(behaviour, null);
            if (data == null) return 0UL;
            var owner = _mGetOwner?.Invoke(data, null) as DXEntity;
            return owner != null ? owner.ID : 0UL;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, out bool __state)
        {
            if (!LockstepClock.IsDeterministicModeActive)
            {
                __state = false;
                return;
            }
            ulong id = ResolveEntityId(__instance);
            if (id == 0UL || (id & 0x8000000000000000UL) != 0UL)
            {
                __state = false;
                return;
            }
            LockstepClock.PushRngScope(id, LockstepClock.RngDomainTag.BehaviourRun);
            __state = true;
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state)
                LockstepClock.PopRngScope();
        }
    }

    /// <summary>
    /// Scopes remaining deterministic entity and component RNG draws (unit generators,
    /// infection nests, rotating engines, birds, buildings, projectiles, dwellings, trains)
    /// to stateless SplitMix64 streams keyed by owner Entity.ID.
    /// </summary>
    [HarmonyPatch]
    public static class ScopedEntityAndComponentRngPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var asm = Core.GameBridge.GameAssembly;
            if (asm == null) yield break;

            var seen = new HashSet<MethodBase>();
            void TryAdd(string typeName, string methodName, Type[] paramTypes, List<MethodBase> targetList)
            {
                var t = asm.GetType(typeName);
                if (t == null) return;
                var m = AccessTools.Method(t, methodName, paramTypes);
                if (m != null && seen.Add(m))
                    targetList.Add(m);
            }

            var list = new List<MethodBase>(16);
            TryAdd("ZX.Components.CUnitGenerator", "#=zzHXxhbjNa0Zf", Type.EmptyTypes, list);
            TryAdd("ZX.Components.CInfectionNest", "#=z5GPRaBw=", Type.EmptyTypes, list);
            TryAdd("ZX.Components.CInfectionNest", "#=zhLBqOyoki5Mmzj47tAxLxu8=", new[] { typeof(float) }, list);
            TryAdd("ZX.Components.CRotatingEngine", "#=z5GPRaBw=", Type.EmptyTypes, list);
            TryAdd("ZX.Components.CBird", "#=zxisWwAA=", Type.EmptyTypes, list);
            TryAdd("ZX.Components.CBird", "#=z4ZFInv$6iTpo", Type.EmptyTypes, list);
            TryAdd("ZX.Entities.TheInn", "OnUpdate", Type.EmptyTypes, list);
            TryAdd("ZX.Entities.Proyectile", "#=zxgVr00U=", Type.EmptyTypes, list);
            TryAdd("ZX.Entities.InfectedHole", "#=zU_6ExxgtTzKNDVbh$g==", Type.EmptyTypes, list);
            TryAdd("ZX.Entities.Locomotive", "#=zLy9fAtQPdqXfWKLk$RnIUUk=", Type.EmptyTypes, list);
            TryAdd("ZX.Entities.Dwelling", "#=zc79$InrAZEEj6AnLM9kicU8=", Type.EmptyTypes, list);
            TryAdd("ZX.Entities.DoomBuildingLarge", "get_EntitiesToCreateOnDestroy", Type.EmptyTypes, list);
            TryAdd("ZX.Entities.DoomBuildingMedium", "get_EntitiesToCreateOnDestroy", Type.EmptyTypes, list);
            TryAdd("ZX.Entities.DoomBuildingSmall", "get_EntitiesToCreateOnDestroy", Type.EmptyTypes, list);

            foreach (var m in list)
                yield return m;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            if (!LockstepClock.IsDeterministicModeActive || __instance == null)
                return;

            ulong id = 0UL;
            if (__instance is DXEntity ent)
                id = ent.ID;
            else if (__instance is DXComponent comp)
                id = comp.Entity != null ? comp.Entity.ID : 0UL;

            if (id == 0UL || (id & 0x8000000000000000UL) != 0UL)
                return;

            LockstepClock.PushRngScope(id, LockstepClock.RngDomainTag.EntityUpdate);
            __state = true;
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state)
                LockstepClock.PopRngScope();
        }
    }

    /// <summary>
    /// Scopes command effect execution (ZXCommand.#=zPZOGjUTtkdy$ -> #=zEJihvZ1Lcs9a) RNG
    /// draws to the executor entity's deterministic SplitMix64 stream. Targets the public
    /// non-virtual wrapper #=zPZOGjUTtkdy$ because protected virtual #=zEJihvZ1Lcs9a contains
    /// Eazfuscator exception blocks that MonoMod DMD cannot re-emit.
    /// </summary>
    [HarmonyPatch]
    public static class ScopedCommandEffectRngPatch
    {
        public static MethodBase TargetMethod()
        {
            var tCmd = Core.GameBridge.GameAssembly?.GetType("ZX.Commands.ZXCommand");
            return tCmd != null
                ? AccessTools.Method(tCmd, "#=zPZOGjUTtkdy$")
                : null;
        }

        [HarmonyPrefix]
        public static void Prefix(object __0, out bool __state)
        {
            __state = false;
            var executor = __0 as DXEntity;
            if (!LockstepClock.IsDeterministicModeActive || executor == null)
                return;

            ulong id = executor.ID;
            if (id == 0UL || (id & 0x8000000000000000UL) != 0UL)
                return;

            LockstepClock.PushRngScope(id, LockstepClock.RngDomainTag.CommandReplay);
            __state = true;
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state)
                LockstepClock.PopRngScope();
        }
    }

    /// <summary>
    /// Scopes ZXLevelEvent updates and wave triggers to a deterministic SplitMix64 stream
    /// keyed by ZXLevelEvent.ID so map events never perturb the shared global PRNG.
    /// </summary>
    [HarmonyPatch]
    public static class ScopedLevelEventRngPatch
    {
        private static bool _metaInit;
        private static Func<object, int> _fastGetEventId;
        private static PropertyInfo _propEventId;

        private static void EnsureMeta(Type tEvent)
        {
            if (_metaInit) return;
            _metaInit = true;
            try
            {
                _propEventId = AccessTools.Property(tEvent, "ID");
                var getter = _propEventId?.GetGetMethod(true);
                if (getter != null)
                {
                    var dm = new DynamicMethod("TABCoop_GetLevelEventId", typeof(int), new[] { typeof(object) },
                        typeof(ScopedLevelEventRngPatch).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    var lblZero = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Brfalse_S, lblZero);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, tEvent);
                    il.Emit(OpCodes.Callvirt, getter);
                    il.Emit(OpCodes.Ret);
                    il.MarkLabel(lblZero);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ret);
                    _fastGetEventId = (Func<object, int>)dm.CreateDelegate(typeof(Func<object, int>));
                }
            }
            catch (Exception ex)
            {
                _fastGetEventId = null;
                ModLogger.Warn($"[LockstepClock] ScopedLevelEventRngPatch meta init failed (using reflection): {ex.Message}");
            }
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            var t = Core.GameBridge.GameAssembly?.GetType("ZX.GameSystems.ZXLevelEvent");
            if (t == null) yield break;
            EnsureMeta(t);

            string[] names =
            {
                "#=zUMCW0sQ=",
                "#=zHI6D76EGh68R",
                "#=zEzgd90E=",
                "#=zrITPs$A=",
                "#=z6sbTOMQ="
            };
            foreach (var name in names)
            {
                var m = AccessTools.Method(t, name);
                if (m != null) yield return m;
            }
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            if (!LockstepClock.IsDeterministicModeActive || __instance == null)
                return;

            int evtId = 0;
            if (_fastGetEventId != null)
                evtId = _fastGetEventId(__instance);
            else if (_propEventId != null)
                evtId = (int)_propEventId.GetValue(__instance);

            ulong scopeId = 0x4C45565400000000UL | unchecked((uint)evtId);
            LockstepClock.PushRngScope(scopeId, LockstepClock.RngDomainTag.SystemUpdate);
            __state = true;
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state)
                LockstepClock.PopRngScope();
        }
    }

    /// <summary>
    /// Prevents the vanilla 6s New Game intro cinematic (5x camera zoom-out, RootObject blur, and
    /// +300/-300*Unit HUD slide animation) from triggering during active Co-op or deterministic replay
    /// sessions, including during the brief pre-ShowScene window when InitLevel enables CurrentGameSystem.
    /// </summary>
    [HarmonyPatch]
    public static class DisableIntroCinematicInCoopPatch
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(Core.GameBridge.TypeZXSystemGameLevel, "OnSetCamera");

        [HarmonyPrefix]
        public static void Prefix(object __instance)
        {
            if (!Sync.CoopSessionManager.IsSessionActive && !LockstepClock.IsDeterministicModeActive)
                return;
            Core.GameBridge.EnforceDisabledCameraIntroForLevel(__instance);
        }
    }

    /// <summary>
    /// Suppresses Structure.HasNearEnemies during authorized lockstep command replay in Co-op
    /// sessions so a 0.1-cell dynamic zombie drift on one peer never causes single-sided
    /// rejection of an already-committed Upgrade or PlaceBuilding command.
    /// </summary>
    [HarmonyPatch]
    public static class StructureNearEnemiesReplayPatch
    {
        public static MethodBase TargetMethod() =>
            GameBridge.TypeStructure != null
                ? AccessTools.PropertyGetter(GameBridge.TypeStructure, "HasNearEnemies")
                : null;

        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (Commands.CommandExecutor.IsReplayingCommand && Sync.CoopSessionManager.IsSessionActive)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Stage 4A (S1): Exact mathematical short-circuit for far-field zombie CEntityWatcher scans.
    /// Maintains an 8x8-cell dilated coarse presence bitmap (4 KB, L1-resident) of all live entities
    /// carrying HasPeople (0x8000u) or DeffensiveStructure (0x2000u) masks. When a non-attacked
    /// zombie with no existing cached target is strictly outside the dilated coarse footprint,
    /// its CEntityWatcher scan is mathematically guaranteed to yield null with zero RNG/state writes.
    /// </summary>
    internal static class ZombieWatcherCoarseGrid
    {
        private const int CoarseShift = 3; // 8x8 cells per coarse tile
        internal const uint WatchInterestMask = 0x8000u | 0x2000u; // HasPeople | DeffensiveStructure

        private static int[] _presenceStamp = new int[64 * 64];
        private static int[] _dilatedR2Stamp = new int[64 * 64];
        private static int _coarseDim = 64;
        private static int _nCells;
        private static int _currentStamp = 1;
        private static int _builtSimTick = -1;
        private static DXLevel _builtLevel;

        private static bool _typesInit;
        private static Type _tStructure;
        private static Type _tHuman;
        private static Func<DXEntity, Point> _getRawCell;
        private static readonly HashSet<DXEntity> _extraInterestEntities = new HashSet<DXEntity>();

        private static void EnsureTypesAndAccessors()
        {
            if (_typesInit) return;
            _typesInit = true;
            var asm = GameBridge.GameAssembly;
            _tStructure = GameBridge.TypeStructure ?? asm?.GetType("ZX.Entities.Structure");
            _tHuman = asm?.GetType("ZX.Entities.Human");
            try
            {
                var fCell = typeof(DXEntity).GetField("_Cell", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (fCell != null && fCell.FieldType == typeof(Point))
                {
                    var dm = new DynamicMethod("TABCoop_CoarseGridGetRawCell", typeof(Point), new[] { typeof(DXEntity) },
                        typeof(ZombieWatcherCoarseGrid).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, fCell);
                    il.Emit(OpCodes.Ret);
                    _getRawCell = (Func<DXEntity, Point>)dm.CreateDelegate(typeof(Func<DXEntity, Point>));
                }
            }
            catch { _getRawCell = null; }
        }

        internal static void Invalidate()
        {
            _builtSimTick = -1;
            _builtLevel = null;
            _extraInterestEntities.Clear();
        }

        internal static bool IsStructureOrHuman(DXEntity e)
        {
            EnsureTypesAndAccessors();
            if (e == null) return false;
            return (_tStructure != null && _tStructure.IsInstanceOfType(e))
                || (_tHuman != null && _tHuman.IsInstanceOfType(e));
        }

        internal static void OnEntityBodyMaskRefreshed(DXEntity e)
        {
            if (!LockstepClock.IsDeterministicModeActive || e == null || e.Disposed) return;
            EnsureTypesAndAccessors();
            uint mask = e._BodyCategoryMask;
            if ((mask & WatchInterestMask) != 0u)
            {
                if (!IsStructureOrHuman(e))
                    _extraInterestEntities.Add(e);

                var game = DXGame.Current;
                var curLevel = DXLevel.Current;
                if (game != null && curLevel != null && ReferenceEquals(_builtLevel, curLevel) && _builtSimTick == game.IAIterationInt)
                    StampEntity(e, _currentStamp);
            }
            else if (_extraInterestEntities.Count > 0)
            {
                _extraInterestEntities.Remove(e);
            }
        }

        internal static void OnEntityPositionChanging(DXEntity e, PointF newPos)
        {
            if (e == null || e.Disposed || (e._BodyCategoryMask & WatchInterestMask) == 0u || !IsReadyForCurrentTick())
                return;
            if (!IsStructureOrHuman(e))
                _extraInterestEntities.Add(e);

            if (e.IsOneCellSize)
            {
                Point cell = DXWorldGrid.CellFromPoint(newPos);
                StampCoarseRange(cell.X >> CoarseShift, cell.Y >> CoarseShift, cell.X >> CoarseShift, cell.Y >> CoarseShift, _currentStamp);
            }
            else
            {
                Rectangle area = DXWorldGrid.CellAreaFromArea(new RectangleF(newPos.X - e.Width * 0.5f, newPos.Y - e.Height * 0.5f, e.Width, e.Height));
                StampCoarseRange(area.Left >> CoarseShift, area.Top >> CoarseShift, (area.Right - 1) >> CoarseShift, (area.Bottom - 1) >> CoarseShift, _currentStamp);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StampEntity(DXEntity e, int stamp)
        {
            int cx0, cy0, cx1, cy1;
            if (e.IsOneCellSize)
            {
                Point cell = _getRawCell != null ? _getRawCell(e) : e.Cell;
                cx0 = cx1 = cell.X >> CoarseShift;
                cy0 = cy1 = cell.Y >> CoarseShift;
            }
            else
            {
                Rectangle area = e.CellArea;
                cx0 = area.Left >> CoarseShift;
                cy0 = area.Top >> CoarseShift;
                cx1 = (area.Right - 1) >> CoarseShift;
                cy1 = (area.Bottom - 1) >> CoarseShift;
            }
            StampCoarseRange(cx0, cy0, cx1, cy1, stamp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StampCoarseRange(int cx0, int cy0, int cx1, int cy1, int stamp)
        {
            int dim = _coarseDim;
            if (cx1 < 0 || cy1 < 0 || cx0 >= dim || cy0 >= dim) return;
            if (cx0 < 0) cx0 = 0;
            if (cy0 < 0) cy0 = 0;
            if (cx1 >= dim) cx1 = dim - 1;
            if (cy1 >= dim) cy1 = dim - 1;

            for (int cy = cy0; cy <= cy1; cy++)
            {
                int row = cy * dim;
                for (int cx = cx0; cx <= cx1; cx++)
                    _presenceStamp[row + cx] = stamp;
            }

            int dx0 = cx0 - 2; if (dx0 < 0) dx0 = 0;
            int dy0 = cy0 - 2; if (dy0 < 0) dy0 = 0;
            int dx1 = cx1 + 2; if (dx1 >= dim) dx1 = dim - 1;
            int dy1 = cy1 + 2; if (dy1 >= dim) dy1 = dim - 1;

            for (int cy = dy0; cy <= dy1; cy++)
            {
                int row = cy * dim;
                for (int cx = dx0; cx <= dx1; cx++)
                    _dilatedR2Stamp[row + cx] = stamp;
            }
        }

        internal static void RebuildForTick(DXGame game)
        {
            if (game == null || LockstepClock.LoadingLevel)
            {
                _builtSimTick = -1;
                return;
            }

            var level = DXLevel.Current;
            int nCells = DXWorldGrid.NCells;
            if (level == null || nCells <= 0)
            {
                _builtSimTick = -1;
                return;
            }

            EnsureTypesAndAccessors();
            if (_tStructure == null && _tHuman == null)
            {
                _builtSimTick = -1;
                return;
            }

            int dim = (nCells + ((1 << CoarseShift) - 1)) >> CoarseShift;
            int needed = dim * dim;
            if (_presenceStamp.Length < needed)
            {
                _presenceStamp = new int[needed];
                _dilatedR2Stamp = new int[needed];
                _currentStamp = 1;
            }
            _coarseDim = dim;
            _nCells = nCells;

            unchecked { _currentStamp++; }
            if (_currentStamp == 0)
            {
                Array.Clear(_presenceStamp, 0, _presenceStamp.Length);
                Array.Clear(_dilatedR2Stamp, 0, _dilatedR2Stamp.Length);
                _currentStamp = 1;
            }

            int stamp = _currentStamp;

            if (_tStructure != null && game.EntitiesOfType(_tStructure) is HashSet<DXEntity> structs)
            {
                foreach (DXEntity e in structs)
                {
                    if (e != null && !e.Disposed && (e._BodyCategoryMask & WatchInterestMask) != 0u)
                        StampEntity(e, stamp);
                }
            }

            if (_tHuman != null && game.EntitiesOfType(_tHuman) is HashSet<DXEntity> humans)
            {
                foreach (DXEntity e in humans)
                {
                    if (e != null && !e.Disposed && (e._BodyCategoryMask & WatchInterestMask) != 0u)
                        StampEntity(e, stamp);
                }
            }

            if (_extraInterestEntities.Count > 0)
            {
                _extraInterestEntities.RemoveWhere(e => e == null || e.Disposed || (e._BodyCategoryMask & WatchInterestMask) == 0u);
                foreach (DXEntity e in _extraInterestEntities)
                    StampEntity(e, stamp);
            }

            _builtLevel = level;
            _builtSimTick = game.IAIterationInt;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsReadyForCurrentTick()
        {
            var game = DXGame.Current;
            var level = DXLevel.Current;
            return game != null && level != null
                && ReferenceEquals(_builtLevel, level)
                && _builtSimTick == game.IAIterationInt;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAreaEmptyAroundZombie(int cellX, int cellY, float watchRange)
        {
            int nCells = _nCells;
            if ((uint)cellX >= (uint)nCells || (uint)cellY >= (uint)nCells)
                return false;

            int dim = _coarseDim;
            int stamp = _currentStamp;
            int zx = cellX >> CoarseShift;
            int zy = cellY >> CoarseShift;

            // With +-2 coarse-tile dilation (8 cells/tile), any unstamped coarse tile has
            // Chebyshev distance >= 17 cells to every registered cell of any 0x8000/0x2000 entity.
            // Since GetNearestVisibleEntityAtPoint stops at num5 = Ceiling(watchRange) + 1 <= 16 when
            // watchRange <= 15.0f, a miss in _dilatedR2Stamp guarantees zero targets in range.
            if (watchRange <= 15.0f)
                return _dilatedR2Stamp[zy * dim + zx] != stamp;

            if (_dilatedR2Stamp[zy * dim + zx] == stamp)
                return false;

            int rPad = (int)Math.Ceiling(watchRange) + 2;
            int cx0 = (cellX - rPad) >> CoarseShift; if (cx0 < 0) cx0 = 0;
            int cy0 = (cellY - rPad) >> CoarseShift; if (cy0 < 0) cy0 = 0;
            int cx1 = (cellX + rPad) >> CoarseShift; if (cx1 >= dim) cx1 = dim - 1;
            int cy1 = (cellY + rPad) >> CoarseShift; if (cy1 >= dim) cy1 = dim - 1;

            for (int cy = cy0; cy <= cy1; cy++)
            {
                int row = cy * dim;
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (_presenceStamp[row + cx] == stamp)
                        return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class EntityRefreshBodyMaskPatch
    {
        public static MethodBase TargetMethod()
        {
            var t = GameBridge.TypeZXEntity ?? GameBridge.GameAssembly?.GetType("ZX.Entities.ZXEntity");
            return t != null ? AccessTools.Method(t, ObfuscatedSymbols.Method_ZXEntity_RefreshBodyMask, Type.EmptyTypes) : null;
        }

        [HarmonyPostfix]
        public static void Postfix(DXEntity __instance)
        {
            if (LockstepClock.IsDeterministicModeActive)
            {
                ZombieWatcherCoarseGrid.OnEntityBodyMaskRefreshed(__instance);
                ActiveUpdateEntityManager.OnEntityMaskOrTeamRefreshed(__instance);
                if (PrecomputedPerceptionManager.InWatchWindow)
                    PrecomputedPerceptionManager.NotifyEntityGridOrMaskMutated(__instance);
            }
        }
    }

    [HarmonyPatch]
    public static class ZombieWatcherFastPathPatch
    {
        private delegate bool FastWatcherCheckDelegate(object watcher, out float watchRange, out int cellX, out int cellY);

        private static bool _metaInit;
        private static FastWatcherCheckDelegate _fastCheck;

        public static MethodBase TargetMethod()
        {
            var tWatcher = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_CEntityWatcher);
            if (tWatcher == null) return null;
            EnsureMeta();
            return AccessTools.Method(tWatcher, ObfuscatedSymbols.Method_CEntityWatcher_UpdateWatch, new[] { typeof(bool) });
        }

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            try
            {
                var asm = GameBridge.GameAssembly;
                if (asm == null) return;

                Type tWatcher = asm.GetType(ObfuscatedSymbols.Type_CEntityWatcher);
                Type tZombie = asm.GetType(ObfuscatedSymbols.Type_Zombie);
                Type tZxEnt = GameBridge.TypeZXEntity ?? asm.GetType("ZX.Entities.ZXEntity");
                if (tWatcher == null || tZombie == null || tZxEnt == null) return;

                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var fTargetMask = tWatcher.GetField(ObfuscatedSymbols.Field_CEntityWatcher_TargetMask, F);
                var fTargetEnt = tWatcher.GetField(ObfuscatedSymbols.Field_CEntityWatcher_TargetEntity, F);
                var mGetWasAttacked = tWatcher.GetProperty("WasAttacked", F)?.GetGetMethod(true);
                var mGetCompEntity = typeof(DXComponent).GetProperty("Entity", F)?.GetGetMethod(true);
                var mGetDisposed = typeof(DXEntity).GetProperty("Disposed", F)?.GetGetMethod(true);
                var fRawCell = typeof(DXEntity).GetField("_Cell", F);
                var mGetParams = AccessTools.Method(tZxEnt, ObfuscatedSymbols.Method_ZXEntity_GetParams, Type.EmptyTypes);
                var mGetRangeBonus = tZxEnt.GetProperty("RangeBonus", F)?.GetGetMethod(true);
                var tParams = mGetParams?.ReturnType;
                var mGetWatchRange = tParams != null ? tParams.GetProperty("WatchRange", F)?.GetGetMethod(true) : null;
                var mPointX = typeof(Point).GetProperty("X")?.GetGetMethod();
                var mPointY = typeof(Point).GetProperty("Y")?.GetGetMethod();

                if (fTargetMask == null || fTargetEnt == null || mGetWasAttacked == null || mGetCompEntity == null ||
                    mGetDisposed == null || fRawCell == null || mGetParams == null || mGetRangeBonus == null ||
                    mGetWatchRange == null || mPointX == null || mPointY == null)
                    return;

                var dm = new DynamicMethod("TABCoop_TryGetZombieWatcherFastCheck", typeof(bool),
                    new[] { typeof(object), typeof(float).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() },
                    typeof(ZombieWatcherFastPathPatch).Module, skipVisibility: true);
                var il = dm.GetILGenerator();

                var locW = il.DeclareLocal(tWatcher);
                var locZ = il.DeclareLocal(tZombie);
                var locParams = il.DeclareLocal(tParams);
                var locCell = il.DeclareLocal(typeof(Point));
                var lblRetFalse = il.DefineLabel();

                // if (watcher == null) goto lblRetFalse;
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Brfalse, lblRetFalse);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, tWatcher);
                il.Emit(OpCodes.Stloc, locW);

                // if (w._targetMask != 0x8000u) goto lblRetFalse;
                il.Emit(OpCodes.Ldloc, locW);
                il.Emit(OpCodes.Ldfld, fTargetMask);
                il.Emit(OpCodes.Ldc_I4, 0x8000);
                il.Emit(OpCodes.Bne_Un, lblRetFalse);

                // if (w._targetEntity != null) goto lblRetFalse;
                il.Emit(OpCodes.Ldloc, locW);
                il.Emit(OpCodes.Ldfld, fTargetEnt);
                il.Emit(OpCodes.Brtrue, lblRetFalse);

                // if (w.WasAttacked) goto lblRetFalse;
                il.Emit(OpCodes.Ldloc, locW);
                il.Emit(OpCodes.Callvirt, mGetWasAttacked);
                il.Emit(OpCodes.Brtrue, lblRetFalse);

                // z = w.Entity as Zombie; if (z == null || z.Disposed) goto lblRetFalse;
                il.Emit(OpCodes.Ldloc, locW);
                il.Emit(OpCodes.Callvirt, mGetCompEntity);
                il.Emit(OpCodes.Isinst, tZombie);
                il.Emit(OpCodes.Stloc, locZ);
                il.Emit(OpCodes.Ldloc, locZ);
                il.Emit(OpCodes.Brfalse, lblRetFalse);
                il.Emit(OpCodes.Ldloc, locZ);
                il.Emit(OpCodes.Callvirt, mGetDisposed);
                il.Emit(OpCodes.Brtrue, lblRetFalse);

                // p = z.Params; if (p == null) goto lblRetFalse;
                il.Emit(OpCodes.Ldloc, locZ);
                il.Emit(OpCodes.Callvirt, mGetParams);
                il.Emit(OpCodes.Stloc, locParams);
                il.Emit(OpCodes.Ldloc, locParams);
                il.Emit(OpCodes.Brfalse, lblRetFalse);

                // watchRange = (float)p.WatchRange + (float)z.RangeBonus;
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloc, locParams);
                il.Emit(OpCodes.Callvirt, mGetWatchRange);
                il.Emit(OpCodes.Conv_R4);
                il.Emit(OpCodes.Ldloc, locZ);
                il.Emit(OpCodes.Callvirt, mGetRangeBonus);
                il.Emit(OpCodes.Conv_R4);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stind_R4);

                // cell = z._Cell; cellX = cell.X; cellY = cell.Y;
                il.Emit(OpCodes.Ldloc, locZ);
                il.Emit(OpCodes.Ldfld, fRawCell);
                il.Emit(OpCodes.Stloc, locCell);

                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldloca, locCell);
                il.Emit(OpCodes.Call, mPointX);
                il.Emit(OpCodes.Stind_I4);

                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Ldloca, locCell);
                il.Emit(OpCodes.Call, mPointY);
                il.Emit(OpCodes.Stind_I4);

                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(lblRetFalse);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_R4, 0f);
                il.Emit(OpCodes.Stind_R4);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stind_I4);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stind_I4);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);

                _fastCheck = (FastWatcherCheckDelegate)dm.CreateDelegate(typeof(FastWatcherCheckDelegate));
                ModLogger.Info("[Perf] Compiled DynamicMethod TABCoop_TryGetZombieWatcherFastCheck.");
            }
            catch (Exception ex)
            {
                _fastCheck = null;
                ModLogger.Warn($"[Perf] TryGetZombieWatcherFastCheck compile failed: {ex.Message}");
            }
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel || _fastCheck == null)
                return true;
            if (!ZombieWatcherCoarseGrid.IsReadyForCurrentTick())
                return true;

            if (_fastCheck(__instance, out float watchRange, out int cellX, out int cellY)
                && ZombieWatcherCoarseGrid.IsAreaEmptyAroundZombie(cellX, cellY, watchRange))
            {
                if (DeterministicSandboxPatches.DiagPerfSamplingActive)
                    Interlocked.Increment(ref DeterministicSandboxPatches.DiagTickWatcherBypassed);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class LevelStateShowFullMapSyncPatch
    {
        public static MethodBase TargetMethod()
        {
            var t = Core.GameBridge.GameAssembly?.GetType("ZX.ZXLevelState");
            return t?.GetProperty("ShowFullMap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetSetMethod(true);
        }

        [HarmonyPostfix]
        public static void Postfix([HarmonyArgument(0)] bool value)
        {
            Sync.FactionContextManager.OnNativeShowFullMapSet(value);
        }
    }

    // =========================================================================
    // Stage 4B-1: Conservative 2-tier bitmap + supercover DDA fast-pass for
    // DXEntity.IsFreePhysicPath(destiny, 1u) (Geography / static 1u colliders).
    // Any cell within 1.05 units of any Chipmunk body with (CategoryMask & 1u) != 0
    // is ref-counted so open-field string-pull segments skip native Space_RayCast
    // with 0 false negatives, while near-obstacle segments fall back to Chipmunk.
    // =========================================================================
    internal static class Physics1uGrid
    {
        private struct CellRect { public int X0, Y0, X1, Y1; public bool Dynamic1u; }

        private const int CoarseShift = 3; // 8x8 cells per coarse tile
        private static readonly object _sync = new object();
        private static readonly Dictionary<DXPhysicBody, CellRect> _tracked = new Dictionary<DXPhysicBody, CellRect>(4096);
        private static ushort[] _cellRefCount = Array.Empty<ushort>();
        private static ushort[] _coarseRefCount = Array.Empty<ushort>();
        private static int _nCells;
        private static int _coarseDim;
        private static int _dynamic1uCount;
        private static DXLevel _builtLevel;
        private static readonly bool _disabledByEnv = string.Equals(Environment.GetEnvironmentVariable("TABCOOP_DISABLE_PHYS1U"), "1", StringComparison.Ordinal);
        private static bool _needsRebuild = true;
        private static volatile bool _ready;

        internal static void Invalidate()
        {
            lock (_sync)
            {
                _tracked.Clear();
                if (_cellRefCount.Length > 0) Array.Clear(_cellRefCount, 0, _cellRefCount.Length);
                if (_coarseRefCount.Length > 0) Array.Clear(_coarseRefCount, 0, _coarseRefCount.Length);
                _dynamic1uCount = 0;
                _builtLevel = null;
                _needsRebuild = true;
                _ready = false;
            }
        }

        internal static bool IsReadyForFastQuery()
        {
            return !_disabledByEnv && _ready && !_needsRebuild && _dynamic1uCount == 0 && _nCells > 0;
        }

        private static bool TryComputePaddedBounds(DXPhysicBody body, int nCells, out CellRect rect, out bool hasEntity)
        {
            rect = default;
            hasEntity = false;
            if (body == null || body.Handle == IntPtr.Zero || nCells <= 0) return false;
            if (System.Runtime.InteropServices.Marshal.ReadIntPtr(body.Handle, 0x88) == IntPtr.Zero) return false;

            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            // Read native body center (cpBody->p) and extents; DXPhysicsNative.Body_GetSize returns
            // negative Height (-h) for cpPolyShape rectangles (including all CAccesibleLayer terrain colliders).
            PointF center = body.PhysicPosition;
            SizeF ps = body.PhysicSize;
            float pw = Math.Abs(ps.Width);
            float ph = Math.Abs(ps.Height);
            if (pw > 0f && ph > 0f)
            {
                float hx = pw * 0.5f;
                float hy = ph * 0.5f;
                left = center.X - hx;
                right = center.X + hx;
                top = center.Y - hy;
                bottom = center.Y + hy;
            }

            var e = body.GameEntity;
            hasEntity = e != null;
            bool isDyn = e != null ? e.PhysicalType == DXBodyType.Dynamic : body.BodyType == DXBodyType.Dynamic;
            if (e != null)
            {
                Rectangle cellRect = e.CellArea;
                if (e.PhysicalType == DXBodyType.Static && e.Template != null)
                {
                    if (e.Template.CellAreaMode == DXTypeOfCellAreaMode.MaxArea) cellRect = e.MaxCellArea;
                    else if (e.Template.CellAreaMode == DXTypeOfCellAreaMode.MinArea) cellRect = e.MinCellArea;
                }
                if (cellRect.Width > 0 && cellRect.Height > 0)
                {
                    if (cellRect.Left < left) left = cellRect.Left;
                    if (cellRect.Top < top) top = cellRect.Top;
                    if (cellRect.Right > right) right = cellRect.Right;
                    if (cellRect.Bottom > bottom) bottom = cellRect.Bottom;
                }
                if (e.PhysicalShape == DXPhysicalShape.Circle || e.PhysicalShape == DXPhysicalShape.None)
                {
                    float r = cellRect.Width > 0 ? cellRect.Width * 0.5f : e.Size.Width * 0.5f;
                    PointF ep = e.Position;
                    if (ep.X - r < left) left = ep.X - r;
                    if (ep.Y - r < top) top = ep.Y - r;
                    if (ep.X + r > right) right = ep.X + r;
                    if (ep.Y + r > bottom) bottom = ep.Y + r;
                }
            }

            if (left > right || top > bottom || float.IsNaN(left) || float.IsInfinity(left))
                return false;

            // Pad by 1.05 units: guarantees any centerline in unmarked cells stays > 0.90 units away
            // (covering all entity radii <= 0.90f passed to IsFreePhysicPath).
            const float pad = 1.05f;
            int x0 = (int)Math.Floor(left - pad); if (x0 < 0) x0 = 0;
            int y0 = (int)Math.Floor(top - pad); if (y0 < 0) y0 = 0;
            int x1 = (int)Math.Floor(right + pad); if (x1 >= nCells) x1 = nCells - 1;
            int y1 = (int)Math.Floor(bottom + pad); if (y1 >= nCells) y1 = nCells - 1;
            if (x0 > x1 || y0 > y1) return false;

            rect = new CellRect { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Dynamic1u = isDyn };
            return true;
        }

        private static void ApplyDelta(in CellRect r, int delta)
        {
            int nCells = _nCells;
            int dim = _coarseDim;
            for (int y = r.Y0; y <= r.Y1; y++)
            {
                int row = y * nCells;
                int cyRow = (y >> CoarseShift) * dim;
                for (int x = r.X0; x <= r.X1; x++)
                {
                    int idx = row + x;
                    int next = _cellRefCount[idx] + delta;
                    _cellRefCount[idx] = (ushort)(next > 0 ? (next <= ushort.MaxValue ? next : ushort.MaxValue) : 0);

                    int cIdx = cyRow + (x >> CoarseShift);
                    int cNext = _coarseRefCount[cIdx] + delta;
                    _coarseRefCount[cIdx] = (ushort)(cNext > 0 ? (cNext <= ushort.MaxValue ? cNext : ushort.MaxValue) : 0);
                }
            }
        }

        internal static void EnsureSynced(DXGame game)
        {
            if (game == null || LockstepClock.LoadingLevel)
            {
                _ready = false;
                return;
            }
            var level = DXLevel.Current;
            int nCells = DXWorldGrid.NCells;
            if (level == null || nCells <= 0)
            {
                _ready = false;
                return;
            }
            if (_ready && !_needsRebuild && ReferenceEquals(_builtLevel, level) && _nCells == nCells)
                return;

            lock (_sync)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _ready = false;
                _needsRebuild = true;
                _tracked.Clear();
                _dynamic1uCount = 0;
                _nCells = nCells;
                _coarseDim = (nCells + ((1 << CoarseShift) - 1)) >> CoarseShift;
                int cellLen = nCells * nCells;
                int coarseLen = _coarseDim * _coarseDim;
                if (_cellRefCount.Length < cellLen) _cellRefCount = new ushort[cellLen];
                else Array.Clear(_cellRefCount, 0, cellLen);
                if (_coarseRefCount.Length < coarseLen) _coarseRefCount = new ushort[coarseLen];
                else Array.Clear(_coarseRefCount, 0, coarseLen);

                int noEntityCount = 0;
                int entityCount = 0;
                int skippedCount = 0;
                var bodies = DXPhysicWorld.Bodies;
                if (bodies != null)
                {
                    for (int i = 0; i < bodies.Count; i++)
                    {
                        DXPhysicBody b = bodies[i];
                        if (b == null || b.Handle == IntPtr.Zero) continue;
                        if (System.Runtime.InteropServices.Marshal.ReadIntPtr(b.Handle, 0x88) == IntPtr.Zero) continue;
                        if ((b.CategoryMask & 1u) == 0u) continue;
                        if (TryComputePaddedBounds(b, nCells, out CellRect r, out bool hasEntity))
                        {
                            _tracked[b] = r;
                            if (r.Dynamic1u) _dynamic1uCount++;
                            if (hasEntity) entityCount++;
                            else noEntityCount++;
                            ApplyDelta(in r, +1);
                        }
                        else
                        {
                            skippedCount++;
                        }
                    }
                }

                int blockedCells = 0;
                for (int i = 0; i < cellLen; i++)
                    if (_cellRefCount[i] != 0) blockedCells++;
                int blockedCoarse = 0;
                for (int i = 0; i < coarseLen; i++)
                    if (_coarseRefCount[i] != 0) blockedCoarse++;

                _builtLevel = level;
                _needsRebuild = false;
                _ready = true;
                sw.Stop();
                ModLogger.Info($"[Perf] Physics1uGrid built: tracked={_tracked.Count} (terrain/noEntity={noEntityCount}, entity={entityCount}, dyn1u={_dynamic1uCount}, skipped={skippedCount}), blockedCells={blockedCells}/{cellLen}, blockedCoarse={blockedCoarse}/{coarseLen}, nCells={nCells}, {sw.Elapsed.TotalMilliseconds:F2}ms");
            }
        }

        internal static void OnBodyCategoryMaskSet(DXPhysicBody body, uint newMask)
        {
            if (!_ready || _needsRebuild || body == null || LockstepClock.LoadingLevel) return;
            bool has1u = (newMask & 1u) != 0u;
            lock (_sync)
            {
                if (!_ready || _needsRebuild) return;
                bool wasTracked = _tracked.TryGetValue(body, out CellRect oldRect);
                if (!has1u && !wasTracked) return;

                CellRect newRect = default;
                bool hasNew = has1u
                    && body.Handle != IntPtr.Zero
                    && System.Runtime.InteropServices.Marshal.ReadIntPtr(body.Handle, 0x88) != IntPtr.Zero
                    && TryComputePaddedBounds(body, _nCells, out newRect, out _);

                if (wasTracked && hasNew
                    && oldRect.X0 == newRect.X0 && oldRect.Y0 == newRect.Y0
                    && oldRect.X1 == newRect.X1 && oldRect.Y1 == newRect.Y1
                    && oldRect.Dynamic1u == newRect.Dynamic1u)
                {
                    return;
                }

                if (wasTracked)
                {
                    ApplyDelta(in oldRect, -1);
                    if (oldRect.Dynamic1u && _dynamic1uCount > 0) _dynamic1uCount--;
                    _tracked.Remove(body);
                }
                if (hasNew)
                {
                    _tracked[body] = newRect;
                    if (newRect.Dynamic1u) _dynamic1uCount++;
                    ApplyDelta(in newRect, +1);
                }
            }
        }

        internal static void OnBodyGeometryChanged(DXPhysicBody body)
        {
            if (!_ready || _needsRebuild || body == null || LockstepClock.LoadingLevel) return;
            // DXPhysicsNative.Body_GetCategoryMask dereferences [Handle+0x88] (cpShape) without a null
            // check, unlike Body_SetCategoryMask / Body_GetSize. Shape-less bodies (DXPhysicalShape.None
            // via DXPhysicBody.Create) leave [Handle+0x88] == IntPtr.Zero and would AV if queried.
            IntPtr handle = body.Handle;
            uint mask = (handle != IntPtr.Zero && System.Runtime.InteropServices.Marshal.ReadIntPtr(handle, 0x88) != IntPtr.Zero)
                ? body.CategoryMask
                : 0u;
            if ((mask & 1u) == 0u && _tracked.Count == 0) return;
            OnBodyCategoryMaskSet(body, mask);
        }

        internal static void OnBodyDisposing(DXPhysicBody body)
        {
            if (body == null || _tracked.Count == 0) return;
            lock (_sync)
            {
                if (_tracked.TryGetValue(body, out CellRect oldRect))
                {
                    if (_ready && !_needsRebuild)
                        ApplyDelta(in oldRect, -1);
                    if (oldRect.Dynamic1u && _dynamic1uCount > 0) _dynamic1uCount--;
                    _tracked.Remove(body);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsSegmentClearOf1u(PointF a, PointF b, float radius)
        {
            if (!IsReadyForFastQuery() || radius > 0.90f)
                return false;

            float ax = a.X, ay = a.Y, bx = b.X, by = b.Y;
            int nCells = _nCells;
            if (float.IsNaN(ax) || float.IsNaN(ay) || float.IsNaN(bx) || float.IsNaN(by))
                return false;
            if (ax < 0f || ay < 0f || bx < 0f || by < 0f || ax >= nCells || ay >= nCells || bx >= nCells || by >= nCells)
                return false;

            int minX = (int)Math.Floor(ax < bx ? ax : bx);
            int minY = (int)Math.Floor(ay < by ? ay : by);
            int maxX = (int)Math.Floor(ax > bx ? ax : bx);
            int maxY = (int)Math.Floor(ay > by ? ay : by);
            if (minX < 0 || minY < 0 || maxX >= nCells || maxY >= nCells)
                return false;

            // Tier 1: 8x8 coarse empty check when segment AABB touches at most 2x2 coarse tiles
            int cx0 = minX >> CoarseShift;
            int cy0 = minY >> CoarseShift;
            int cx1 = maxX >> CoarseShift;
            int cy1 = maxY >> CoarseShift;
            if ((cx1 - cx0) <= 1 && (cy1 - cy0) <= 1)
            {
                int dim = _coarseDim;
                ushort[] coarse = _coarseRefCount;
                bool anyCoarse = false;
                for (int cy = cy0; cy <= cy1 && !anyCoarse; cy++)
                {
                    int cRow = cy * dim;
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        if (coarse[cRow + cx] != 0)
                        {
                            anyCoarse = true;
                            break;
                        }
                    }
                }
                if (!anyCoarse)
                    return true;
            }

            // Tier 2: Supercover Amanatides-Woo DDA across conservative _cellRefCount
            int x = (int)Math.Floor(ax);
            int y = (int)Math.Floor(ay);
            int ex = (int)Math.Floor(bx);
            int ey = (int)Math.Floor(by);
            ushort[] cells = _cellRefCount;
            if (cells[y * nCells + x] != 0 || cells[ey * nCells + ex] != 0)
                return false;
            if (x == ex && y == ey)
                return true;

            double dx = (double)bx - ax;
            double dy = (double)by - ay;
            int stepX = dx > 0.0 ? 1 : (dx < 0.0 ? -1 : 0);
            int stepY = dy > 0.0 ? 1 : (dy < 0.0 ? -1 : 0);
            double tDeltaX = stepX != 0 ? Math.Abs(1.0 / dx) : 1e30;
            double tDeltaY = stepY != 0 ? Math.Abs(1.0 / dy) : 1e30;
            double tMaxX = stepX > 0 ? ((x + 1.0 - ax) * tDeltaX) : (stepX < 0 ? ((ax - x) * tDeltaX) : 1e30);
            double tMaxY = stepY > 0 ? ((y + 1.0 - ay) * tDeltaY) : (stepY < 0 ? ((ay - y) * tDeltaY) : 1e30);

            int maxSteps = Math.Abs(ex - x) + Math.Abs(ey - y) + 4;
            while ((x != ex || y != ey) && maxSteps-- > 0)
            {
                if (x != ex && y != ey)
                {
                    double diff = tMaxX - tMaxY;
                    if (Math.Abs(diff) < 1e-5)
                    {
                        int nx = x + stepX;
                        int ny = y + stepY;
                        if ((uint)nx >= (uint)nCells || cells[y * nCells + nx] != 0) return false;
                        if ((uint)ny >= (uint)nCells || cells[ny * nCells + x] != 0) return false;
                        x = nx;
                        y = ny;
                        tMaxX += tDeltaX;
                        tMaxY += tDeltaY;
                    }
                    else if (diff < 0.0)
                    {
                        x += stepX;
                        tMaxX += tDeltaX;
                    }
                    else
                    {
                        y += stepY;
                        tMaxY += tDeltaY;
                    }
                }
                else if (x != ex)
                {
                    x += stepX;
                    tMaxX += tDeltaX;
                }
                else
                {
                    y += stepY;
                    tMaxY += tDeltaY;
                }
                if ((uint)x >= (uint)nCells || (uint)y >= (uint)nCells || cells[y * nCells + x] != 0)
                    return false;
            }
            return x == ex && y == ey;
        }
    }

    [HarmonyPatch(typeof(DXPhysicBody), "CategoryMask", MethodType.Setter)]
    public static class PhysicBodyCategoryMaskPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DXPhysicBody __instance, uint value)
        {
            if (LockstepClock.IsDeterministicModeActive)
                Physics1uGrid.OnBodyCategoryMaskSet(__instance, value);
        }
    }

    [HarmonyPatch(typeof(DXPhysicBody), "PhysicSize", MethodType.Setter)]
    public static class PhysicBodySize1uPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DXPhysicBody __instance)
        {
            if (LockstepClock.IsDeterministicModeActive)
                Physics1uGrid.OnBodyGeometryChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(DXPhysicBody), nameof(DXPhysicBody.TeleportTo), new[] { typeof(PointF) })]
    public static class PhysicBodyTeleport1uPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DXPhysicBody __instance)
        {
            if (LockstepClock.IsDeterministicModeActive)
                Physics1uGrid.OnBodyGeometryChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(DXPhysicBody), "Dispose")]
    public static class PhysicBodyDispose1uPatch
    {
        [HarmonyPrefix]
        public static void Prefix(DXPhysicBody __instance)
        {
            if (LockstepClock.IsDeterministicModeActive)
                Physics1uGrid.OnBodyDisposing(__instance);
        }
    }

    [HarmonyPatch(typeof(DXPhysicWorld), "Clear")]
    public static class PhysicWorldClear1uPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            Physics1uGrid.Invalidate();
        }
    }

    /// <summary>
    /// Defers patching static DXPhysicBody.Create* factory methods until after DXPhysicWorld.Init()
    /// has allocated DXPhysicWorld.Bodies. Patching them at startup before ZXGame initializes physics
    /// triggers DXPhysicBody..cctor() (Empty = new DXPhysicBody()) while Bodies == null, permanently
    /// poisoning DXPhysicBody with a cached TypeInitializationException for the whole AppDomain.
    /// </summary>
    [HarmonyPatch(typeof(DXPhysicWorld), nameof(DXPhysicWorld.Init), new[] { typeof(int) })]
    public static class PhysicWorldInitCreate1uPatches
    {
        private static int _applied;

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (Interlocked.Exchange(ref _applied, 1) != 0)
                return;
            var harmony = TABCoopMod.Bootstrap.ModEntry.HarmonyInstance;
            if (harmony == null || DXPhysicWorld.Bodies == null)
                return;

            try
            {
                var postfix = new HarmonyMethod(typeof(PhysicWorldInitCreate1uPatches), nameof(CreateBodyPostfix));
                var targets = new MethodInfo[]
                {
                    AccessTools.Method(typeof(DXPhysicBody), nameof(DXPhysicBody.Create),
                        new[] { typeof(float), typeof(PointF), typeof(float), typeof(DXBodyType), typeof(object) }),
                    AccessTools.Method(typeof(DXPhysicBody), nameof(DXPhysicBody.CreateCircle),
                        new[] { typeof(float), typeof(PointF), typeof(float), typeof(DXBodyType), typeof(object) }),
                    AccessTools.Method(typeof(DXPhysicBody), nameof(DXPhysicBody.CreateRectangle),
                        new[] { typeof(float), typeof(RectangleF), typeof(DXBodyType), typeof(object) })
                };
                int patched = 0;
                for (int i = 0; i < targets.Length; i++)
                {
                    if (targets[i] != null)
                    {
                        harmony.Patch(targets[i], postfix: postfix);
                        patched++;
                    }
                }
                ModLogger.Info($"[Perf] Applied deferred DXPhysicBody.Create* Physics1uGrid patches ({patched}/3).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[Perf] Failed to apply deferred DXPhysicBody.Create* patches: {ex.Message}");
            }
        }

        public static void CreateBodyPostfix(DXPhysicBody __result)
        {
            if (LockstepClock.IsDeterministicModeActive)
                Physics1uGrid.OnBodyGeometryChanged(__result);
        }
    }

    [HarmonyPatch(typeof(DXEntity), "IsFreePhysicPath", new[] { typeof(PointF), typeof(uint) })]
    public static class EntityFree1uPathFastPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DXEntity __instance, PointF destiny, uint mask, ref bool __result)
        {
            if (mask != 1u || !LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel || __instance == null)
                return true;

            float radius = __instance.Size.Width / 3f;
            if (Physics1uGrid.IsSegmentClearOf1u(__instance.Position, destiny, radius))
            {
                if (DeterministicSandboxPatches.DiagPerfSamplingActive)
                    Interlocked.Increment(ref DeterministicSandboxPatches.DiagTickPath1uBypassed);
                __result = true;
                return false;
            }
            return true;
        }
    }

    // =========================================================================
    // Stage 4B-2: Order-preserving ActiveUpdateEntities compact list
    // Skips ~80k inert static terrain/resource entities inside DXGame._Entities
    // while maintaining exact relative order and handling add/remove/pooling.
    // =========================================================================
    internal static class ActiveUpdateEntityManager
    {
        private static readonly object _typeLock = new object();
        private static readonly Dictionary<Type, byte> _typeKindCache = new Dictionary<Type, byte>(128);
        private static readonly List<DXEntity> _activeEntities = new List<DXEntity>(32768);
        private static readonly Dictionary<DXEntity, int> _slotByEntity = new Dictionary<DXEntity, int>(32768);
        private static int _tombstoneCount;
        private static DXEntity _disposedTombstone;

        private static bool _metaInit;
        private static Type _tZXEntity;
        private static MethodInfo _mBaseDxOnUpdate;
        private static MethodInfo _mBaseZxOnUpdate;
        private static Func<DXGame, List<DXEntity>> _getRawEntities;
        private static Func<DXEntity, int> _getRawIndex;
        private static Func<DXEntity, bool> _hasEventOnUpdate;
        private static Func<DXEntity, bool> _hasNonNullMinimapColor;

        private static readonly bool _disabledByEnv = string.Equals(Environment.GetEnvironmentVariable("TABCOOP_DISABLE_ACTIVE_UPDATE"), "1", StringComparison.Ordinal);
        private static DXLevel _builtLevel;
        private static volatile bool _needsRebuildNextTick = true;
        private static volatile bool _usingActiveList;

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            try
            {
                _tZXEntity = Core.GameBridge.TypeZXEntity ?? Core.GameBridge.GameAssembly?.GetType("ZX.Entities.ZXEntity");
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                _mBaseDxOnUpdate = typeof(DXEntity).GetMethod("OnUpdate", F);
                _mBaseZxOnUpdate = _tZXEntity?.GetMethod("OnUpdate", F);

                var tomb = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(DXEntity)) as DXEntity;
                if (tomb != null)
                {
                    tomb.Flags = DXEntity.DXEntityFlags.Disposed | DXEntity.DXEntityFlags.FreeingToPool;
                    _disposedTombstone = tomb;
                }

                var fEntities = typeof(DXGame).GetField("_Entities", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fEntities != null)
                {
                    var dm = new DynamicMethod("TABCoop_GetGameEntitiesList", typeof(List<DXEntity>), new[] { typeof(DXGame) },
                        typeof(ActiveUpdateEntityManager).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, fEntities);
                    il.Emit(OpCodes.Ret);
                    _getRawEntities = (Func<DXGame, List<DXEntity>>)dm.CreateDelegate(typeof(Func<DXGame, List<DXEntity>>));
                }

                var fIndex = typeof(DXEntity).GetField("_Index", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fIndex != null)
                {
                    var dm = new DynamicMethod("TABCoop_GetEntityIndex", typeof(int), new[] { typeof(DXEntity) },
                        typeof(ActiveUpdateEntityManager).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, fIndex);
                    il.Emit(OpCodes.Ret);
                    _getRawIndex = (Func<DXEntity, int>)dm.CreateDelegate(typeof(Func<DXEntity, int>));
                }

                FieldInfo fEvtUpd = null;
                foreach (var fi in typeof(DXEntity).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (fi.FieldType == typeof(Action) && fi.Name.IndexOf("EventOnUpdate", StringComparison.Ordinal) >= 0)
                    {
                        fEvtUpd = fi;
                        break;
                    }
                }
                if (fEvtUpd != null)
                {
                    var dm = new DynamicMethod("TABCoop_HasEventOnUpdate", typeof(bool), new[] { typeof(DXEntity) },
                        typeof(ActiveUpdateEntityManager).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, fEvtUpd);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Cgt_Un);
                    il.Emit(OpCodes.Ret);
                    _hasEventOnUpdate = (Func<DXEntity, bool>)dm.CreateDelegate(typeof(Func<DXEntity, bool>));
                }

                var mGetMm = _tZXEntity?.GetProperty("MinimapColor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true);
                if (mGetMm != null && mGetMm.ReturnType == typeof(Color?))
                {
                    var mHasVal = typeof(Color?).GetProperty("HasValue")?.GetGetMethod();
                    if (mHasVal != null)
                    {
                        var dm = new DynamicMethod("TABCoop_HasMinimapColor", typeof(bool), new[] { typeof(DXEntity) },
                            typeof(ActiveUpdateEntityManager).Module, skipVisibility: true);
                        var il = dm.GetILGenerator();
                        var loc = il.DeclareLocal(typeof(Color?));
                        var lblFalse = il.DefineLabel();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Isinst, _tZXEntity);
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Brfalse, lblFalse);
                        il.Emit(OpCodes.Callvirt, mGetMm);
                        il.Emit(OpCodes.Stloc, loc);
                        il.Emit(OpCodes.Ldloca, loc);
                        il.Emit(OpCodes.Call, mHasVal);
                        il.Emit(OpCodes.Ret);
                        il.MarkLabel(lblFalse);
                        il.Emit(OpCodes.Pop);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ret);
                        _hasNonNullMinimapColor = (Func<DXEntity, bool>)dm.CreateDelegate(typeof(Func<DXEntity, bool>));
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[ActiveUpdate] EnsureMeta failed (using full _Entities): {ex.Message}");
                _getRawEntities = null;
            }
        }

        private static byte ClassifyType(Type t)
        {
            lock (_typeLock)
            {
                if (_typeKindCache.TryGetValue(t, out byte cached))
                    return cached;

                byte kind = 0;
                var mOnUp = t.GetMethod("OnUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mOnUp != null)
                {
                    Type decl = mOnUp.DeclaringType;
                    if (decl != typeof(DXEntity) && decl != _tZXEntity)
                        kind = 1; // Subclass overrides OnUpdate beyond ZXEntity
                    else if (_tZXEntity != null && _tZXEntity.IsAssignableFrom(t))
                        kind = 2; // ZXEntity.OnUpdate: only active when MinimapColor.HasValue or EventOnUpdate != null
                }
                _typeKindCache[t] = kind;
                return kind;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldEntityBeUpdated(DXEntity e)
        {
            if (e == null) return false;
            byte kind = ClassifyType(e.GetType());
            if (kind == 1) return true;
            if (_hasEventOnUpdate != null && _hasEventOnUpdate(e)) return true;
            if (kind == 2 && (_hasNonNullMinimapColor == null || _hasNonNullMinimapColor(e))) return true;
            return false;
        }

        internal static void Invalidate()
        {
            _usingActiveList = false;
            _needsRebuildNextTick = true;
            _builtLevel = null;
            _activeEntities.Clear();
            _slotByEntity.Clear();
            _tombstoneCount = 0;
        }

        internal static void PrepareForTick(DXGame game)
        {
            EnsureMeta();
            if (_disabledByEnv)
            {
                _usingActiveList = false;
                return;
            }
            if (game == null || LockstepClock.LoadingLevel || _getRawEntities == null || _disposedTombstone == null || _hasEventOnUpdate == null)
            {
                _usingActiveList = false;
                return;
            }
            var level = DXLevel.Current;
            if (level == null)
            {
                _usingActiveList = false;
                return;
            }

            List<DXEntity> raw = _getRawEntities(game);
            if (raw == null)
            {
                _usingActiveList = false;
                return;
            }

            if (!_usingActiveList || _needsRebuildNextTick || !ReferenceEquals(_builtLevel, level))
            {
                _activeEntities.Clear();
                _slotByEntity.Clear();
                _tombstoneCount = 0;
                int count = raw.Count;
                for (int i = 0; i < count; i++)
                {
                    DXEntity e = raw[i];
                    if (e != null && !_slotByEntity.ContainsKey(e) && ShouldEntityBeUpdated(e))
                    {
                        _slotByEntity[e] = _activeEntities.Count;
                        _activeEntities.Add(e);
                    }
                }
                _builtLevel = level;
                _needsRebuildNextTick = false;
                _usingActiveList = true;
            }
            else if (_tombstoneCount >= 1024)
            {
                int write = 0;
                int n = _activeEntities.Count;
                DXEntity tomb = _disposedTombstone;
                for (int i = 0; i < n; i++)
                {
                    DXEntity e = _activeEntities[i];
                    if (ReferenceEquals(e, tomb)) continue;
                    _activeEntities[write] = e;
                    _slotByEntity[e] = write;
                    write++;
                }
                if (write < n)
                    _activeEntities.RemoveRange(write, n - write);
                _tombstoneCount = 0;
            }

            if (DeterministicSandboxPatches.DiagPerfSamplingActive)
            {
                DeterministicSandboxPatches.DiagActiveUpdateEntitiesCount = _slotByEntity.Count;
                DeterministicSandboxPatches.DiagTotalEntitiesCount = raw.Count;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryGetActiveSnapshot(out List<DXEntity> entities, out int count)
        {
            if (_usingActiveList && !_needsRebuildNextTick && LockstepClock.IsDeterministicModeActive && !LockstepClock.LoadingLevel)
            {
                entities = _activeEntities;
                count = _activeEntities.Count;
                return true;
            }
            entities = null;
            count = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryGetSlot(DXEntity entity, out int slot)
        {
            if (_usingActiveList && entity != null)
                return _slotByEntity.TryGetValue(entity, out slot);
            slot = -1;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static List<DXEntity> GetEntitiesForTick(DXGame game)
        {
            // Never switch backing list mid-tick if _needsRebuildNextTick becomes true inside the loop;
            // keep returning _activeEntities for the whole tick and rebuild cleanly in PrepareForTick.
            if (_usingActiveList && LockstepClock.IsDeterministicModeActive && !LockstepClock.LoadingLevel)
                return _activeEntities;
            if (_getRawEntities == null) EnsureMeta();
            return _getRawEntities != null ? _getRawEntities(game) : null;
        }

        internal static void OnEntityAdded(DXEntity entity)
        {
            if (!_usingActiveList || entity == null || !LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel)
                return;
            if (_getRawIndex != null && _getRawIndex(entity) < 0)
                return;
            if (!_slotByEntity.ContainsKey(entity) && ShouldEntityBeUpdated(entity))
            {
                _slotByEntity[entity] = _activeEntities.Count;
                _activeEntities.Add(entity);
            }
        }

        internal static void OnEntityRemoved(DXEntity entity)
        {
            if (!_usingActiveList || entity == null || _disposedTombstone == null)
                return;
            if (_slotByEntity.TryGetValue(entity, out int slot))
            {
                if ((uint)slot < (uint)_activeEntities.Count)
                    _activeEntities[slot] = _disposedTombstone;
                _slotByEntity.Remove(entity);
                _tombstoneCount++;
            }
        }

        internal static void OnEntityEventOnUpdateSubscribed(DXEntity entity)
        {
            if (!_usingActiveList || _needsRebuildNextTick || entity == null || !LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel)
                return;
            if ((_getRawIndex == null || _getRawIndex(entity) >= 0) && !_slotByEntity.ContainsKey(entity))
                _needsRebuildNextTick = true;
        }

        internal static void OnEntityMaskOrTeamRefreshed(DXEntity entity)
        {
            if (!_usingActiveList || _needsRebuildNextTick || entity == null || !LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel)
                return;
            if ((_getRawIndex == null || _getRawIndex(entity) >= 0) && !_slotByEntity.ContainsKey(entity) && ShouldEntityBeUpdated(entity))
                _needsRebuildNextTick = true;
        }
    }

    [HarmonyPatch]
    public static class GameUpdateLogicActiveEntitiesTranspiler
    {
        public static MethodBase TargetMethod()
        {
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var m in typeof(DXGame).GetMethods(F))
            {
                if (m.Name.StartsWith("<UpdateGameLogic>b__", StringComparison.Ordinal)
                    && m.GetParameters().Length == 0
                    && m.ReturnType == typeof(void))
                    return m;
            }
            return null;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var fDet = AccessTools.Field(typeof(DXGame), "IsIADeterministic");
            var fEntities = AccessTools.Field(typeof(DXGame), "_Entities");
            var mHelper = AccessTools.Method(typeof(ActiveUpdateEntityManager), nameof(ActiveUpdateEntityManager.GetEntitiesForTick), new[] { typeof(DXGame) });
            if (fDet == null || fEntities == null || mHelper == null)
                return list;

            bool inDetBranch = false;
            int replaced = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];
                if (!inDetBranch)
                {
                    if (ins.opcode == OpCodes.Ldsfld && Equals(ins.operand, fDet))
                        inDetBranch = true;
                }
                else
                {
                    if (ins.opcode == OpCodes.Br_S || ins.opcode == OpCodes.Br)
                    {
                        if (replaced >= 2) break;
                    }
                    if (ins.opcode == OpCodes.Ldfld && Equals(ins.operand, fEntities))
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = mHelper;
                        replaced++;
                        if (replaced >= 2) break;
                    }
                }
            }
            ModLogger.Info($"[ActiveUpdate] Transpiled UpdateGameLogic lambda: replaced={replaced} DXGame._Entities loads.");
            return list;
        }
    }

    [HarmonyPatch(typeof(DXGame), "AddEntityToList", new[] { typeof(DXEntity) })]
    public static class GameAddEntityToListPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DXEntity entity)
        {
            ActiveUpdateEntityManager.OnEntityAdded(entity);
        }
    }

    [HarmonyPatch(typeof(DXGame), "RemoveEntityFromList", new[] { typeof(DXEntity) })]
    public static class GameRemoveEntityFromListPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DXEntity entity)
        {
            ActiveUpdateEntityManager.OnEntityRemoved(entity);
        }
    }

    [HarmonyPatch(typeof(DXGame), "ClearEntities", new Type[0])]
    public static class GameClearEntitiesPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            ActiveUpdateEntityManager.Invalidate();
        }
    }

    [HarmonyPatch]
    public static class EntityAddEventOnUpdatePatch
    {
        public static MethodBase TargetMethod()
        {
            return typeof(DXEntity).GetEvent("EventOnUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetAddMethod(true);
        }

        [HarmonyPostfix]
        public static void Postfix(DXEntity __instance)
        {
            ActiveUpdateEntityManager.OnEntityEventOnUpdateSubscribed(__instance);
        }
    }

    // =========================================================================
    // Stage 4D-2: Two-Phase Parallel CEntityWatcher Perception Split
    // Phase 0 (after OnStartUpdate): parallel read-only DXWorldGrid queries across
    // fixed ParallelWorkers chunks for active Zombies not pruned by coarse grid.
    // Phase 1 (sequential entity loop): O(1) validation against mid-tick Human/Wall
    // grid mutations + exact CEntityWatcher outcome adoption.
    // =========================================================================
    internal static class PrecomputedPerceptionManager
    {
        private delegate bool GetWatcherConfigDel(DXEntity e, out float watchRange, out bool canJump, out bool canAvoidOverkill, out int priority, out object atkCmd, out int rangeBonus);
        private delegate bool GetAtkCmdParamsDel(object atkCmd, out float actionRange, out float minDist, out bool usableThruWalls);
        private delegate bool TryGetMovableTargetDel(DXEntity e, out PointF targetPt);

        private struct PrecomputedEntry
        {
            public int SimTick;
            public object Watcher;
            public PointF SnapPos;
            public Point SnapCell;
            public DXEntity SnapPrevTarget;
            public object SnapAttackCmd;
            public int SnapRangeBonus;
            public float WatchRange;
            public bool CanJump;
            public bool EarlyNullReturnFalse;
            public bool EarlyNullReturnTrue;
            public DXEntity TargetFalse;
            public DXEntity TargetTrue;
        }

        private struct DirtyCellBox
        {
            public int MinX;
            public int MinY;
            public int MaxX;
            public int MaxY;
        }

        private const int MaxExactDirtyBoxes = 16;

        private static readonly object _metaLock = new object();
        private static volatile bool _metaInit;
        private static Type _tZXEntity;
        private static Type _tZombie;
        private static Type _tWall;

        private static Func<DXEntity, Point> _getRawCell;
        private static Func<DXEntity, object> _getWatcher;
        private static Func<object, uint> _getWatcherMask;
        private static Func<object, DXEntity> _getWatcherTarget;
        private static Action<object, DXEntity> _setWatcherTarget;
        private static Func<object, bool> _getWatcherWasAttacked;
        private static Action<object, bool> _setWatcherWasAttacked;
        private static Func<object, DXEntity> _getCompEntity;
        private static Func<DXEntity, bool> _isLifeAlive;
        private static GetWatcherConfigDel _getWatcherConfig;
        private static GetAtkCmdParamsDel _getAtkCmdParams;
        private static Func<object, PointF, int, DXEntity> _callGetBlockerWall;
        private static Func<DXEntity, bool> _hasZombieWallCheckCond;
        private static TryGetMovableTargetDel _tryGetMovableTargetPointF;

        private static PrecomputedEntry[] _entries = new PrecomputedEntry[4096];
        private static int _snapCount;
        private static int _builtSimTick = -1;
        private static DXLevel _builtLevel;

        private static volatile bool _watchWindowActive;
        private static int _midTickDirtyCount;
        private static bool _allMidTickDirty;
        private static readonly DirtyCellBox[] _midTickDirtyBoxes = new DirtyCellBox[MaxExactDirtyBoxes];
        private static int _curTickNonSkippedWatchCalls;
        private static int _prevTickNonSkippedWatchCalls;
        private static bool _demandPrecomputeActive;
        private const int DemandActivateThreshold = 128;
        private const int DemandDeactivateThreshold = 64;

        internal static bool InWatchWindow
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _watchWindowActive;
        }

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            lock (_metaLock)
            {
                if (_metaInit) return;
                try
                {
                    var asm = Core.GameBridge.GameAssembly;
                    if (asm == null)
                    {
                        _metaInit = true;
                        return;
                    }

                    _tZXEntity = Core.GameBridge.TypeZXEntity ?? asm.GetType("ZX.Entities.ZXEntity");
                    _tZombie = asm.GetType(Core.ObfuscatedSymbols.Type_Zombie);
                    _tWall = asm.GetType("ZX.Entities.Wall");
                    Type tWatcher = asm.GetType(Core.ObfuscatedSymbols.Type_CEntityWatcher);
                    Type tCmd = asm.GetType("ZX.Commands.ZXCommand");
                    Type tMov = asm.GetType("ZX.Components.CMovable");

                    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    var fRawCell = typeof(DXEntity).GetField("_Cell", F);
                    var mCompEnt = typeof(DXComponent).GetProperty("Entity", F)?.GetGetMethod(true);
                    var mGetWatcher = _tZXEntity != null ? AccessTools.Method(_tZXEntity, "#=zFSiAQxYFx7B9", Type.EmptyTypes) : null;
                    var fMask = tWatcher?.GetField(Core.ObfuscatedSymbols.Field_CEntityWatcher_TargetMask, F);
                    var fTargetEnt = tWatcher?.GetField(Core.ObfuscatedSymbols.Field_CEntityWatcher_TargetEntity, F);
                    var pWasAtk = tWatcher?.GetProperty("WasAttacked", F);
                    var mGetWasAtk = pWasAtk?.GetGetMethod(true);
                    var mSetWasAtk = pWasAtk?.GetSetMethod(true);
                    var mBlocker = tWatcher?.GetMethod("#=zqyVWs5Nb64VULfbWHg==", F, null, new[] { typeof(PointF), typeof(int) }, null);

                    var mGetLife = _tZXEntity != null ? AccessTools.Method(_tZXEntity, Core.ObfuscatedSymbols.Method_ZXEntity_GetLife, Type.EmptyTypes) : null;
                    var tLife = mGetLife?.ReturnType;
                    var mIsAlive = tLife?.GetProperty("IsAlive", F)?.GetGetMethod(true);

                    var mGetParams = _tZXEntity != null ? AccessTools.Method(_tZXEntity, Core.ObfuscatedSymbols.Method_ZXEntity_GetParams, Type.EmptyTypes) : null;
                    var tParams = mGetParams?.ReturnType;
                    var mWatchRange = tParams?.GetProperty("WatchRange", F)?.GetGetMethod(true);
                    var mCanJump = tParams?.GetProperty("CanJump", F)?.GetGetMethod(true);
                    var mCanAvoid = tParams?.GetProperty("CanAvoidOverkill", F)?.GetGetMethod(true);
                    var mPriority = _tZXEntity?.GetProperty("TargetPriority", F)?.GetGetMethod(true);
                    var mAtkCmd = _tZXEntity?.GetProperty("CurrentAttackCommand", F)?.GetGetMethod(true);
                    var mRangeBonus = _tZXEntity?.GetProperty("RangeBonus", F)?.GetGetMethod(true);

                    var mCmdParams = tCmd != null ? AccessTools.Method(tCmd, "#=zcODCID3ur9P9", Type.EmptyTypes) : null;
                    var tCmdP = mCmdParams?.ReturnType;
                    var mActRange = tCmdP?.GetProperty("ActionRange", F)?.GetGetMethod(true);
                    var mMinDist = tCmdP?.GetProperty("MinTargetDistance", F)?.GetGetMethod(true);
                    var mThruWalls = tCmdP?.GetProperty("UsabeThroughWalls", F)?.GetGetMethod(true);

                    var mAtkCC = _tZombie?.GetProperty("AttackCommandCenter", F)?.GetGetMethod(true);
                    var mGetBehComp = _tZXEntity != null ? AccessTools.Method(_tZXEntity, "#=zrPViR3ZjTZsjVndIuu8$9io=", Type.EmptyTypes) : null;
                    var mGetBeh = mGetBehComp?.ReturnType?.GetProperty("Behaviour", F)?.GetGetMethod(true);
                    var mGetData = mGetBeh?.ReturnType?.GetProperty("Data", F)?.GetGetMethod(true);
                    var mGetCellAct = mGetData?.ReturnType?.GetProperty("CellActivity", F)?.GetGetMethod(true);

                    var mGetMov = _tZXEntity != null ? AccessTools.Method(_tZXEntity, Core.ObfuscatedSymbols.Method_ZXEntity_GetMovable, Type.EmptyTypes) : null;
                    var mGetTargetPos = tMov?.GetProperty("TargetPosition", F)?.GetGetMethod(true);
                    var mNextCell = tMov != null ? AccessTools.Method(tMov, "#=zyRp4Y9IqSiCU", Type.EmptyTypes) : null;

                    if (_tZXEntity == null || _tZombie == null || _tWall == null || tWatcher == null || tCmd == null || tMov == null ||
                        fRawCell == null || mCompEnt == null || mGetWatcher == null || fMask == null || fTargetEnt == null ||
                        mGetWasAtk == null || mSetWasAtk == null || mBlocker == null || mGetLife == null || mIsAlive == null ||
                        mGetParams == null || mWatchRange == null || mCanJump == null || mCanAvoid == null || mPriority == null ||
                        mAtkCmd == null || mRangeBonus == null || mCmdParams == null || mActRange == null || mMinDist == null ||
                        mThruWalls == null || mAtkCC == null || mGetBehComp == null || mGetBeh == null || mGetData == null ||
                        mGetCellAct == null || mGetMov == null || mGetTargetPos == null || mNextCell == null)
                    {
                        ModLogger.Warn("[Perception] Missing reflected member(s); Stage 4D precomputation disabled.");
                        _metaInit = true;
                        return;
                    }

                    // 1. DXEntity._Cell
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_GetCell", typeof(Point), new[] { typeof(DXEntity) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, fRawCell);
                        il.Emit(OpCodes.Ret);
                        _getRawCell = (Func<DXEntity, Point>)dm.CreateDelegate(typeof(Func<DXEntity, Point>));
                    }
                    // 2. GetWatcher(DXEntity) -> object
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_GetWatcher", typeof(object), new[] { typeof(DXEntity) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        var lblNull = il.DefineLabel();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Isinst, _tZXEntity);
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Brfalse, lblNull);
                        il.Emit(OpCodes.Callvirt, mGetWatcher);
                        il.Emit(OpCodes.Ret);
                        il.MarkLabel(lblNull);
                        il.Emit(OpCodes.Pop);
                        il.Emit(OpCodes.Ldnull);
                        il.Emit(OpCodes.Ret);
                        _getWatcher = (Func<DXEntity, object>)dm.CreateDelegate(typeof(Func<DXEntity, object>));
                    }
                    // 3. GetWatcherMask(object) -> uint
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_GetMask", typeof(uint), new[] { typeof(object) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Castclass, tWatcher);
                        il.Emit(OpCodes.Ldfld, fMask);
                        il.Emit(OpCodes.Ret);
                        _getWatcherMask = (Func<object, uint>)dm.CreateDelegate(typeof(Func<object, uint>));
                    }
                    // 4. GetWatcherTarget(object) -> DXEntity
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_GetTarget", typeof(DXEntity), new[] { typeof(object) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Castclass, tWatcher);
                        il.Emit(OpCodes.Ldfld, fTargetEnt);
                        il.Emit(OpCodes.Ret);
                        _getWatcherTarget = (Func<object, DXEntity>)dm.CreateDelegate(typeof(Func<object, DXEntity>));
                    }
                    // 5. SetWatcherTarget(object, DXEntity)
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_SetTarget", typeof(void), new[] { typeof(object), typeof(DXEntity) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Castclass, tWatcher);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Isinst, _tZXEntity);
                        il.Emit(OpCodes.Stfld, fTargetEnt);
                        il.Emit(OpCodes.Ret);
                        _setWatcherTarget = (Action<object, DXEntity>)dm.CreateDelegate(typeof(Action<object, DXEntity>));
                    }
                    // 6. GetWatcherWasAttacked / SetWatcherWasAttacked
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_GetWasAtk", typeof(bool), new[] { typeof(object) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Castclass, tWatcher);
                        il.Emit(OpCodes.Callvirt, mGetWasAtk);
                        il.Emit(OpCodes.Ret);
                        _getWatcherWasAttacked = (Func<object, bool>)dm.CreateDelegate(typeof(Func<object, bool>));

                        var dm2 = new DynamicMethod("TABCoop_Perc_SetWasAtk", typeof(void), new[] { typeof(object), typeof(bool) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il2 = dm2.GetILGenerator();
                        il2.Emit(OpCodes.Ldarg_0);
                        il2.Emit(OpCodes.Castclass, tWatcher);
                        il2.Emit(OpCodes.Ldarg_1);
                        il2.Emit(OpCodes.Callvirt, mSetWasAtk);
                        il2.Emit(OpCodes.Ret);
                        _setWatcherWasAttacked = (Action<object, bool>)dm2.CreateDelegate(typeof(Action<object, bool>));
                    }
                    // 7. GetCompEntity(object) -> DXEntity
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_CompEnt", typeof(DXEntity), new[] { typeof(object) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Castclass, typeof(DXComponent));
                        il.Emit(OpCodes.Callvirt, mCompEnt);
                        il.Emit(OpCodes.Ret);
                        _getCompEntity = (Func<object, DXEntity>)dm.CreateDelegate(typeof(Func<object, DXEntity>));
                    }
                    // 8. IsLifeAlive(DXEntity) -> bool
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_IsAlive", typeof(bool), new[] { typeof(DXEntity) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        var lblPopFalse = il.DefineLabel();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Isinst, _tZXEntity);
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Brfalse, lblPopFalse);
                        il.Emit(OpCodes.Callvirt, mGetLife);
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Brfalse, lblPopFalse);
                        il.Emit(OpCodes.Callvirt, mIsAlive);
                        il.Emit(OpCodes.Ret);
                        il.MarkLabel(lblPopFalse);
                        il.Emit(OpCodes.Pop);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ret);
                        _isLifeAlive = (Func<DXEntity, bool>)dm.CreateDelegate(typeof(Func<DXEntity, bool>));
                    }
                    // 9. GetWatcherConfig
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_Cfg", typeof(bool),
                            new[] { typeof(DXEntity), typeof(float).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(int).MakeByRefType(), typeof(object).MakeByRefType(), typeof(int).MakeByRefType() },
                            typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        var locZx = il.DeclareLocal(_tZXEntity);
                        var locP = il.DeclareLocal(tParams);
                        var lblFail = il.DefineLabel();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Isinst, _tZXEntity);
                        il.Emit(OpCodes.Stloc, locZx);
                        il.Emit(OpCodes.Ldloc, locZx);
                        il.Emit(OpCodes.Brfalse, lblFail);
                        il.Emit(OpCodes.Ldloc, locZx);
                        il.Emit(OpCodes.Callvirt, mGetParams);
                        il.Emit(OpCodes.Stloc, locP);
                        il.Emit(OpCodes.Ldloc, locP);
                        il.Emit(OpCodes.Brfalse, lblFail);

                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloc, locP);
                        il.Emit(OpCodes.Callvirt, mWatchRange);
                        il.Emit(OpCodes.Conv_R4);
                        il.Emit(OpCodes.Stind_R4);

                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldloc, locP);
                        il.Emit(OpCodes.Callvirt, mCanJump);
                        il.Emit(OpCodes.Stind_I1);

                        il.Emit(OpCodes.Ldarg_3);
                        il.Emit(OpCodes.Ldloc, locP);
                        il.Emit(OpCodes.Callvirt, mCanAvoid);
                        il.Emit(OpCodes.Stind_I1);

                        il.Emit(OpCodes.Ldarg_S, (byte)4);
                        il.Emit(OpCodes.Ldloc, locZx);
                        il.Emit(OpCodes.Callvirt, mPriority);
                        il.Emit(OpCodes.Stind_I4);

                        il.Emit(OpCodes.Ldarg_S, (byte)5);
                        il.Emit(OpCodes.Ldloc, locZx);
                        il.Emit(OpCodes.Callvirt, mAtkCmd);
                        il.Emit(OpCodes.Stind_Ref);

                        il.Emit(OpCodes.Ldarg_S, (byte)6);
                        il.Emit(OpCodes.Ldloc, locZx);
                        il.Emit(OpCodes.Callvirt, mRangeBonus);
                        il.Emit(OpCodes.Stind_I4);

                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ret);
                        il.MarkLabel(lblFail);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ret);
                        _getWatcherConfig = (GetWatcherConfigDel)dm.CreateDelegate(typeof(GetWatcherConfigDel));
                    }
                    // 10. GetAtkCmdParams
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_AtkCmd", typeof(bool),
                            new[] { typeof(object), typeof(float).MakeByRefType(), typeof(float).MakeByRefType(), typeof(bool).MakeByRefType() },
                            typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        var locP = il.DeclareLocal(tCmdP);
                        var lblNullPop = il.DefineLabel();
                        var lblNull = il.DefineLabel();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Isinst, tCmd);
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Brfalse, lblNullPop);
                        il.Emit(OpCodes.Callvirt, mCmdParams);
                        il.Emit(OpCodes.Stloc, locP);
                        il.Emit(OpCodes.Ldloc, locP);
                        il.Emit(OpCodes.Brfalse, lblNull);

                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloc, locP);
                        il.Emit(OpCodes.Callvirt, mActRange);
                        il.Emit(OpCodes.Stind_R4);

                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldloc, locP);
                        il.Emit(OpCodes.Callvirt, mMinDist);
                        il.Emit(OpCodes.Stind_R4);

                        il.Emit(OpCodes.Ldarg_3);
                        il.Emit(OpCodes.Ldloc, locP);
                        il.Emit(OpCodes.Callvirt, mThruWalls);
                        il.Emit(OpCodes.Stind_I1);

                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ret);
                        il.MarkLabel(lblNullPop);
                        il.Emit(OpCodes.Pop);
                        il.MarkLabel(lblNull);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ret);
                        _getAtkCmdParams = (GetAtkCmdParamsDel)dm.CreateDelegate(typeof(GetAtkCmdParamsDel));
                    }
                    // 11. CallGetBlockerWall
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_Blocker", typeof(DXEntity),
                            new[] { typeof(object), typeof(PointF), typeof(int) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Castclass, tWatcher);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Callvirt, mBlocker);
                        il.Emit(OpCodes.Ret);
                        _callGetBlockerWall = (Func<object, PointF, int, DXEntity>)dm.CreateDelegate(typeof(Func<object, PointF, int, DXEntity>));
                    }
                    // 12. HasZombieWallCheckCond
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_ZWallCond", typeof(bool), new[] { typeof(DXEntity) }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        var locZ = il.DeclareLocal(_tZombie);
                        var locCa = il.DeclareLocal(typeof(Point?));
                        var mHasVal = typeof(Point?).GetProperty("HasValue").GetGetMethod();
                        var lblTrue = il.DefineLabel();
                        var lblPopFalse = il.DefineLabel();
                        var lblFalse = il.DefineLabel();

                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Isinst, _tZombie);
                        il.Emit(OpCodes.Stloc, locZ);
                        il.Emit(OpCodes.Ldloc, locZ);
                        il.Emit(OpCodes.Brfalse, lblFalse);
                        il.Emit(OpCodes.Ldloc, locZ);
                        il.Emit(OpCodes.Callvirt, mAtkCC);
                        il.Emit(OpCodes.Brtrue, lblTrue);
                        il.Emit(OpCodes.Ldloc, locZ);
                        il.Emit(OpCodes.Callvirt, mGetBehComp);
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Brfalse, lblPopFalse);
                        il.Emit(OpCodes.Callvirt, mGetBeh);
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Brfalse, lblPopFalse);
                        il.Emit(OpCodes.Callvirt, mGetData);
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Brfalse, lblPopFalse);
                        il.Emit(OpCodes.Callvirt, mGetCellAct);
                        il.Emit(OpCodes.Stloc, locCa);
                        il.Emit(OpCodes.Ldloca, locCa);
                        il.Emit(OpCodes.Call, mHasVal);
                        il.Emit(OpCodes.Ret);
                        il.MarkLabel(lblTrue);
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ret);
                        il.MarkLabel(lblPopFalse);
                        il.Emit(OpCodes.Pop);
                        il.MarkLabel(lblFalse);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ret);
                        _hasZombieWallCheckCond = (Func<DXEntity, bool>)dm.CreateDelegate(typeof(Func<DXEntity, bool>));
                    }
                    // 13. TryGetMovableTargetPointF
                    {
                        var dm = new DynamicMethod("TABCoop_Perc_MovTarget", typeof(bool),
                            new[] { typeof(DXEntity), typeof(PointF).MakeByRefType() }, typeof(PrecomputedPerceptionManager).Module, true);
                        var il = dm.GetILGenerator();
                        var locMov = il.DeclareLocal(tMov);
                        var locTp = il.DeclareLocal(typeof(PointF?));
                        var locNc = il.DeclareLocal(typeof(Point?));
                        var mPfHasVal = typeof(PointF?).GetProperty("HasValue").GetGetMethod();
                        var mPfVal = typeof(PointF?).GetProperty("Value").GetGetMethod();
                        var mPtHasVal = typeof(Point?).GetProperty("HasValue").GetGetMethod();
                        var mPtVal = typeof(Point?).GetProperty("Value").GetGetMethod();
                        var mCellCenter = typeof(DXWorldGrid).GetMethod("PointFromCell_Center", new[] { typeof(Point) });
                        var lblPopFalse = il.DefineLabel();
                        var lblFalse = il.DefineLabel();
                        var lblUseCell = il.DefineLabel();

                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Isinst, _tZXEntity);
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Brfalse, lblPopFalse);
                        il.Emit(OpCodes.Callvirt, mGetMov);
                        il.Emit(OpCodes.Stloc, locMov);
                        il.Emit(OpCodes.Ldloc, locMov);
                        il.Emit(OpCodes.Brfalse, lblFalse);
                        il.Emit(OpCodes.Ldloc, locMov);
                        il.Emit(OpCodes.Callvirt, mGetTargetPos);
                        il.Emit(OpCodes.Stloc, locTp);
                        il.Emit(OpCodes.Ldloca, locTp);
                        il.Emit(OpCodes.Call, mPfHasVal);
                        il.Emit(OpCodes.Brfalse, lblFalse);
                        il.Emit(OpCodes.Ldloc, locMov);
                        il.Emit(OpCodes.Callvirt, mNextCell);
                        il.Emit(OpCodes.Stloc, locNc);
                        il.Emit(OpCodes.Ldloca, locNc);
                        il.Emit(OpCodes.Call, mPtHasVal);
                        il.Emit(OpCodes.Brtrue, lblUseCell);

                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloca, locTp);
                        il.Emit(OpCodes.Call, mPfVal);
                        il.Emit(OpCodes.Stobj, typeof(PointF));
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ret);

                        il.MarkLabel(lblUseCell);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloca, locNc);
                        il.Emit(OpCodes.Call, mPtVal);
                        il.Emit(OpCodes.Call, mCellCenter);
                        il.Emit(OpCodes.Stobj, typeof(PointF));
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ret);

                        il.MarkLabel(lblPopFalse);
                        il.Emit(OpCodes.Pop);
                        il.MarkLabel(lblFalse);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ret);
                        _tryGetMovableTargetPointF = (TryGetMovableTargetDel)dm.CreateDelegate(typeof(TryGetMovableTargetDel));
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[Perception] EnsureMeta failed (Stage 4D fallback to vanilla): {ex.Message}");
                    _getWatcher = null;
                }
                finally
                {
                    _metaInit = true;
                }
            }
        }

        internal static void Invalidate()
        {
            _watchWindowActive = false;
            _builtSimTick = -1;
            _builtLevel = null;
            _snapCount = 0;
            _midTickDirtyCount = 0;
            _allMidTickDirty = false;
            _curTickNonSkippedWatchCalls = 0;
            _prevTickNonSkippedWatchCalls = 0;
            _demandPrecomputeActive = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void RecordNonSkippedWatchCall()
        {
            _curTickNonSkippedWatchCalls++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void EndWatchWindow()
        {
            _watchWindowActive = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void NotifyEntityGridOrMaskMutated(DXEntity e)
        {
            if (!_watchWindowActive || e == null || _tZombie == null || _tZXEntity == null)
                return;
            if (_tZombie.IsInstanceOfType(e) || !_tZXEntity.IsInstanceOfType(e))
                return;
            if (_allMidTickDirty) return;

            int minX, minY, maxX, maxY;
            if (e.IsOneCellSize)
            {
                Point cell = _getRawCell != null ? _getRawCell(e) : e.Cell;
                minX = maxX = cell.X;
                minY = maxY = cell.Y;
            }
            else
            {
                Rectangle area = e.CellArea;
                minX = area.Left;
                minY = area.Top;
                maxX = area.Right - 1;
                maxY = area.Bottom - 1;
            }
            AddDirtyBox(minX, minY, maxX, maxY);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void NotifyPointMutated(DXEntity e, PointF newPos)
        {
            if (!_watchWindowActive || e == null || _tZombie == null || _tZXEntity == null)
                return;
            if (_tZombie.IsInstanceOfType(e) || !_tZXEntity.IsInstanceOfType(e))
                return;
            if (_allMidTickDirty) return;

            Point newCell = DXWorldGrid.CellFromPoint(newPos);
            if (e.IsOneCellSize)
            {
                AddDirtyBox(newCell.X, newCell.Y, newCell.X, newCell.Y);
            }
            else
            {
                int w = Math.Max(1, (int)Math.Ceiling(e.Width));
                int h = Math.Max(1, (int)Math.Ceiling(e.Height));
                AddDirtyBox(newCell.X - w, newCell.Y - h, newCell.X + w, newCell.Y + h);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddDirtyBox(int minX, int minY, int maxX, int maxY)
        {
            int idx = _midTickDirtyCount;
            if (idx >= MaxExactDirtyBoxes)
            {
                _allMidTickDirty = true;
                return;
            }
            _midTickDirtyBoxes[idx] = new DirtyCellBox { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
            _midTickDirtyCount = idx + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsMidTickDirtyNear(int cellX, int cellY, float watchRange)
        {
            if (_allMidTickDirty) return true;
            int count = _midTickDirtyCount;
            if (count == 0) return false;
            int rPad = (int)Math.Ceiling(watchRange) + 4;
            for (int i = 0; i < count; i++)
            {
                DirtyCellBox b = _midTickDirtyBoxes[i];
                if (cellX >= b.MinX - rPad && cellX <= b.MaxX + rPad && cellY >= b.MinY - rPad && cellY <= b.MaxY + rPad)
                    return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static DXEntity ResolveWatcherOutcome(
            object watcher, DXEntity entity, DXEntity cand, bool canJump, out bool earlyNullReturn)
        {
            if (cand != null)
            {
                DXObject so = cand.SceneObject;
                if (so == null || so.Scene == null || !_isLifeAlive(cand))
                {
                    earlyNullReturn = true;
                    return null;
                }
                earlyNullReturn = false;
                if (!canJump && !entity.Position.InRange(cand.Position, 0.5f))
                {
                    // Initialize and clear [ThreadStatic] _CurrentEntitiesAtCellList before FirstEntityAtCellArea
                    // (vanilla DXWorldGrid.FirstEntityAtCellArea omits InitThreadStaticEntitiesAtCellList).
                    DXWorldGrid.EntitiesAtCell(-1, -1);
                    DXEntity blocker = _callGetBlockerWall(watcher, cand.Position, (int)entity.Width + 3);
                    if (blocker != null)
                        return blocker;
                }
                return cand;
            }
            earlyNullReturn = false;
            return null;
        }

        internal static void PrecomputeForTick(DXGame game)
        {
            _prevTickNonSkippedWatchCalls = _curTickNonSkippedWatchCalls;
            _curTickNonSkippedWatchCalls = 0;
            if (_demandPrecomputeActive)
            {
                if (_prevTickNonSkippedWatchCalls < DemandDeactivateThreshold)
                    _demandPrecomputeActive = false;
            }
            else if (_prevTickNonSkippedWatchCalls >= DemandActivateThreshold)
            {
                _demandPrecomputeActive = true;
            }

            _midTickDirtyCount = 0;
            _allMidTickDirty = false;
            _watchWindowActive = false;

            if (!_demandPrecomputeActive || Testing.TestHarnessConfig.DisablePerceptionPrecompute || game == null || LockstepClock.LoadingLevel)
            {
                _builtSimTick = -1;
                return;
            }

            EnsureMeta();

            int workers = Testing.TestHarnessConfig.ParallelWorkers;
            if (workers <= 1 || _getWatcher == null || !ActiveUpdateEntityManager.TryGetActiveSnapshot(out List<DXEntity> active, out int count))
            {
                _builtSimTick = -1;
                return;
            }

            var level = DXLevel.Current;
            if (level == null || !ZombieWatcherCoarseGrid.IsReadyForCurrentTick() || count <= 0)
            {
                _builtSimTick = -1;
                return;
            }

            if (_entries.Length < count)
            {
                int newCap = Math.Max(count, _entries.Length * 2);
                _entries = new PrecomputedEntry[newCap];
            }

            int simTick = game.IAIterationInt;
            bool samplePerf = DeterministicSandboxPatches.DiagPerfSamplingActive;
            long t0 = samplePerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            long builtCount = 0;

            if (workers > 8) workers = 8;
            int chunkCount = count < 512 ? 1 : Math.Min(workers * 4, 32);

            if (chunkCount <= 1)
            {
                builtCount = PrecomputeRange(active, 0, count, simTick);
            }
            else
            {
                Parallel.For(0, chunkCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, chunk =>
                {
                    int start = (int)(((long)count * chunk) / chunkCount);
                    int end = (int)(((long)count * (chunk + 1)) / chunkCount);
                    if (start < end)
                    {
                        long c = PrecomputeRange(active, start, end, simTick);
                        if (samplePerf && c != 0)
                            Interlocked.Add(ref builtCount, c);
                    }
                });
            }

            _snapCount = count;
            _builtSimTick = simTick;
            _builtLevel = level;
            _watchWindowActive = true;

            if (samplePerf)
            {
                DeterministicSandboxPatches.DiagTickPrecompTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                DeterministicSandboxPatches.DiagTickPrecompBuilt += builtCount;
            }
        }

        private static long PrecomputeRange(List<DXEntity> active, int start, int end, int simTick)
        {
            long built = 0;
            PrecomputedEntry[] entries = _entries;
            Type tZombie = _tZombie;

            for (int i = start; i < end; i++)
            {
                DXEntity e = active[i];
                if (e == null || e.Disposed || (e.Flags & DXEntity.DXEntityFlags.FreeingToPool) != 0 || !tZombie.IsInstanceOfType(e))
                    continue;

                object watcher = _getWatcher(e);
                if (watcher == null || _getWatcherMask(watcher) != 0x8000u)
                    continue;

                if (_getWatcherWasAttacked(watcher))
                    continue;

                if (!_getWatcherConfig(e, out float watchRangeBase, out bool canJump, out bool canAvoidOverkill, out int priority, out object atkCmd, out int rangeBonus))
                    continue;
                if (canAvoidOverkill || priority != 0)
                    continue;

                DXEntity prevTarget = _getWatcherTarget(watcher);
                Point rawCell = _getRawCell(e);
                float watchRange = watchRangeBase + rangeBonus;
                if (prevTarget == null && ZombieWatcherCoarseGrid.IsAreaEmptyAroundZombie(rawCell.X, rawCell.Y, watchRange))
                    continue;

                if (!_isLifeAlive(e))
                    continue;

                float actionRange = watchRange;
                float minDist = 0f;
                uint opaqueMask = 2u;
                if (atkCmd != null && _getAtkCmdParams(atkCmd, out float actRangeBase, out float cmdMinDist, out bool thruWalls))
                {
                    float r = actRangeBase + rangeBonus;
                    if (r < actionRange) actionRange = r;
                    minDist = cmdMinDist > 0f ? cmdMinDist : 0f;
                    if (!thruWalls) opaqueMask = 6146u;
                }

                PointF pos = e.Position;
                DXEntity candFalse = DXWorldGrid.GetNearestVisibleEntityAtPoint(pos, watchRange, 0x8000u, opaqueMask, minDist);
                DXEntity resFalse = ResolveWatcherOutcome(watcher, e, candFalse, canJump, out bool earlyNullFalse);

                DXEntity resTrue;
                bool earlyNullTrue;
                if (actionRange < watchRange && candFalse != null)
                {
                    DXEntity candTrue = DXWorldGrid.GetNearestVisibleEntityAtPoint(pos, actionRange, 0x8000u, opaqueMask, minDist);
                    resTrue = ResolveWatcherOutcome(watcher, e, candTrue, canJump, out earlyNullTrue);
                }
                else
                {
                    resTrue = resFalse;
                    earlyNullTrue = earlyNullFalse;
                }

                entries[i] = new PrecomputedEntry
                {
                    SimTick = simTick,
                    Watcher = watcher,
                    SnapPos = pos,
                    SnapCell = rawCell,
                    SnapPrevTarget = prevTarget,
                    SnapAttackCmd = atkCmd,
                    SnapRangeBonus = rangeBonus,
                    WatchRange = watchRange,
                    CanJump = canJump,
                    EarlyNullReturnFalse = earlyNullFalse,
                    EarlyNullReturnTrue = earlyNullTrue,
                    TargetFalse = resFalse,
                    TargetTrue = resTrue
                };
                built++;
            }
            return built;
        }

        internal static bool TryApplyPrecomputed(object watcher, bool onlyActionRange)
        {
            if (!_watchWindowActive || _getCompEntity == null || _tZombie == null)
                return false;

            var game = DXGame.Current;
            var level = DXLevel.Current;
            if (game == null || level == null || !ReferenceEquals(_builtLevel, level) || _builtSimTick != game.IAIterationInt)
                return false;

            DXEntity entity = _getCompEntity(watcher);
            if (entity == null || entity.Disposed || (entity.Flags & DXEntity.DXEntityFlags.FreeingToPool) != 0 || !_tZombie.IsInstanceOfType(entity))
                return false;

            if (!ActiveUpdateEntityManager.TryGetSlot(entity, out int slot) || (uint)slot >= (uint)_snapCount)
                return false;

            ref PrecomputedEntry entry = ref _entries[slot];
            if (entry.SimTick != game.IAIterationInt || !ReferenceEquals(entry.Watcher, watcher))
                return false;

            if (_getWatcherMask(watcher) != 0x8000u || _getWatcherWasAttacked(watcher))
                return false;

            Point rawCell = _getRawCell(entity);
            PointF curPos = entity.Position;
            if (curPos.X != entry.SnapPos.X || curPos.Y != entry.SnapPos.Y || rawCell.X != entry.SnapCell.X || rawCell.Y != entry.SnapCell.Y)
                return false;

            if (!_getWatcherConfig(entity, out _, out bool curCanJump, out bool curCanAvoid, out int curPrio, out object curAtkCmd, out int curRangeBonus))
                return false;
            if (curCanAvoid || curPrio != 0 || curCanJump != entry.CanJump || curRangeBonus != entry.SnapRangeBonus || !ReferenceEquals(curAtkCmd, entry.SnapAttackCmd))
                return false;

            DXEntity curPrev = _getWatcherTarget(watcher);
            if (!ReferenceEquals(curPrev, entry.SnapPrevTarget))
                return false;

            if (IsMidTickDirtyNear(rawCell.X, rawCell.Y, entry.WatchRange))
                return false;

            bool earlyNull = onlyActionRange ? entry.EarlyNullReturnTrue : entry.EarlyNullReturnFalse;
            if (earlyNull)
            {
                _setWatcherTarget(watcher, null);
                return true;
            }

            DXEntity target = onlyActionRange ? entry.TargetTrue : entry.TargetFalse;
            if (target != null)
            {
                DXObject so = target.SceneObject;
                if (target.Disposed || so == null || so.Scene == null || !_isLifeAlive(target))
                    return false;

                _setWatcherTarget(watcher, target);
                _setWatcherWasAttacked(watcher, false);
                return true;
            }

            _setWatcherTarget(watcher, null);
            _setWatcherWasAttacked(watcher, false);
            if (!entry.CanJump)
            {
                if (curPrev != null && _tWall.IsInstanceOfType(curPrev) && _isLifeAlive(curPrev))
                {
                    _setWatcherTarget(watcher, curPrev);
                    return true;
                }
                if (_hasZombieWallCheckCond(entity) && _tryGetMovableTargetPointF(entity, out PointF targetPt))
                {
                    DXWorldGrid.EntitiesAtCell(-1, -1);
                    DXEntity blocker = _callGetBlockerWall(watcher, targetPt, 1);
                    if (blocker != null)
                        _setWatcherTarget(watcher, blocker);
                }
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class PrecomputedWatcherPatch
    {
        public static MethodBase TargetMethod()
        {
            var t = Core.GameBridge.GameAssembly?.GetType(Core.ObfuscatedSymbols.Type_CEntityWatcher);
            return t != null ? AccessTools.Method(t, Core.ObfuscatedSymbols.Method_CEntityWatcher_UpdateWatch, new[] { typeof(bool) }) : null;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        public static bool Prefix(object __instance, bool __0, out long __state)
        {
            bool detActive = LockstepClock.IsDeterministicModeActive && !LockstepClock.LoadingLevel;
            if (detActive)
                PrecomputedPerceptionManager.RecordNonSkippedWatchCall();

            bool samplePerf = detActive && DeterministicSandboxPatches.DiagPerfSamplingActive;
            __state = samplePerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            if (!detActive)
                return true;

            if (PrecomputedPerceptionManager.TryApplyPrecomputed(__instance, __0))
            {
                if (samplePerf)
                    DeterministicSandboxPatches.DiagTickWatcherPrecompHits++;
                return false;
            }
            // Ensure [ThreadStatic] _CurrentEntitiesAtCellList is non-null and cleared before vanilla UpdateWatch -> FirstEntityAtCellArea.
            DXWorldGrid.EntitiesAtCell(-1, -1);
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(long __state)
        {
            if (__state != 0L && DeterministicSandboxPatches.DiagPerfSamplingActive)
            {
                DeterministicSandboxPatches.DiagTickWatcherTicks += System.Diagnostics.Stopwatch.GetTimestamp() - __state;
                DeterministicSandboxPatches.DiagTickWatcherCalls++;
            }
        }
    }

    [HarmonyPatch(typeof(DXEntity), nameof(DXEntity.OnPoolFree))]
    public static class PerceptionOnPoolFreePatch
    {
        [HarmonyPrefix]
        public static void Prefix(DXEntity __instance)
        {
            if (PrecomputedPerceptionManager.InWatchWindow)
                PrecomputedPerceptionManager.NotifyEntityGridOrMaskMutated(__instance);
        }
    }

    [HarmonyPatch]
    public static class PerceptionLevelEntityDiedPatch
    {
        public static MethodBase TargetMethod()
        {
            var t = Core.GameBridge.GameAssembly?.GetType("ZX.ZXLevelState");
            return t != null ? AccessTools.Method(t, "#=zmGjBHwC$KPGs", new[] { typeof(DXEntity) }) : null;
        }

        [HarmonyPrefix]
        public static void Prefix(DXEntity __0)
        {
            if (PrecomputedPerceptionManager.InWatchWindow)
                PrecomputedPerceptionManager.NotifyEntityGridOrMaskMutated(__0);
        }
    }

    [HarmonyPatch(typeof(DXWorldGrid), "SetNewCellFor", new[] { typeof(DXEntity) })]
    public static class PerceptionSetNewCellPatch
    {
        [HarmonyPrefix]
        public static void Prefix(DXEntity __0)
        {
            if (PrecomputedPerceptionManager.InWatchWindow)
                PrecomputedPerceptionManager.NotifyEntityGridOrMaskMutated(__0);
        }
    }

    [HarmonyPatch(typeof(DXEntity), nameof(DXEntity.Position), MethodType.Setter)]
    public static class PerceptionEntitySetPositionPatch
    {
        [HarmonyPrefix]
        public static void Prefix(DXEntity __instance, PointF __0)
        {
            if (LockstepClock.IsDeterministicModeActive && __instance != null && __instance.Position != __0)
            {
                if (LockstepClock.LogicTickActive && (__instance._BodyCategoryMask & ZombieWatcherCoarseGrid.WatchInterestMask) != 0u)
                    ZombieWatcherCoarseGrid.OnEntityPositionChanging(__instance, __0);
                if (PrecomputedPerceptionManager.InWatchWindow)
                {
                    PrecomputedPerceptionManager.NotifyEntityGridOrMaskMutated(__instance);
                    PrecomputedPerceptionManager.NotifyPointMutated(__instance, __0);
                }
            }
        }
    }
}
