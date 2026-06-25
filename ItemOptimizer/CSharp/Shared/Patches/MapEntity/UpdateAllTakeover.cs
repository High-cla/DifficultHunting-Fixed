using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using ItemOptimizerMod.Proxy;
using ItemOptimizerMod.SignalGraph;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Complete takeover of MapEntity.UpdateAll via Harmony prefix (return false).
    /// Handles item freeze/throttle with direct item.Update() calls — zero per-item Harmony overhead.
    /// </summary>
    static class UpdateAllTakeover
    {
        /// <summary>Maximum item ID value (ushort.MaxValue + 1). Flat arrays indexed by item.ID.</summary>
        private const int MaxItemId = 1 << 16;
        private const float VelocityThreshold = 0.01f;

        // ── Public state (read by PerfProfiler, StatsOverlay) ──
        internal static volatile bool Enabled;
        internal static volatile bool DispatchActive;
        internal static readonly int MainThreadId = Environment.CurrentManagedThreadId;

        // ── Reflection caches ──
        private static FieldInfo _tickField;
        private static AccessTools.FieldRef<int> _tickFieldRef;
        private static MethodInfo _updateProjSpecific;
        private static MethodInfo _hullUpdateCheats;  // CLIENT-only, may be null
        private static Action<float> _updateProjSpecificDel;       // MapEntity.UpdateAllProjSpecific(float) — fast delegate
        private static Action<float, Camera> _hullUpdateCheatsDel; // Hull.UpdateCheats(float, Camera) — fast delegate
        private static Action<Item, float> _itemUpdateNetPos;   // Item.UpdateNetPosition(float) — fast delegate
        private static Action<Item> _itemApplyWaterForces;      // Item.ApplyWaterForces() — fast delegate

        // ── Per-frame parameters for cached Parallel.Invoke delegates ──
        // Storing frame data in static fields (instead of capturing locals in lambdas)
        // eliminates closure + delegate allocations (6 allocs/frame saved).
        private static float _piScaledDt;
        private static float _piDeltaTime;
        private static float _piPoweredDt;
        private static Camera _piCam;
        private static bool _piIsPowerFrame;
        private static readonly Action[] _piActions = new Action[3];
        private static readonly Action _piUpdateStructures = () =>
        {
            for (int _idx = 0; _idx < Structure.WallList.Count; _idx++)
            {
                Structure structure = Structure.WallList[_idx];
                structure.Update(_piScaledDt, _piCam);
            }
        };
        private static readonly Action _piUpdateGaps = () =>
        {
            for (int _idx = 0; _idx < _gapShuffleBuffer.Count; _idx++)
            {
                Gap gap = _gapShuffleBuffer[_idx];
                gap.Update(_piDeltaTime, _piCam);
            }
        };
        private static readonly Action _piUpdatePower = () =>
        {
            if (_piIsPowerFrame)
                Powered.UpdatePower(_piPoweredDt);
        };

        private static bool _reflectionOk;

        // ── Patch state ──
        private static bool _patchAttached;

        // ── Per-frame pre-computed flags (replaces ItemUpdatePatch.NewFrame) ──
        private static bool _hasColdStorage;
        private static bool _hasGroundItem;
        private static bool _hasRules;
        private static bool _hasModOpt;
        private static bool _miscParallelEnabled;
        private static bool _hasProxy;
        private static bool _hasSignalGraph;
        private static bool _hasWireSkip;
        internal static bool _hasZoneManaged;

        // ── Frame generation for active-use cache ──
        private static int _frameGeneration;

        // ── Item classification buffers (reused each frame) ──
        private static readonly List<Item> _mainItems = new(2048);

        // ── Gap shuffle buffer (reused each frame, Fisher-Yates) ──
        private static readonly List<Gap> _gapShuffleBuffer = new(512);

        // ── Tick conversion ──
        private static readonly double _ticksToMs = 1000.0 / Stopwatch.Frequency;

        // ── Freeze/throttle state — flat arrays indexed by item.ID (ushort 0–65535) ──
        // Replaces ConditionalWeakTable for O(1) direct-index access with zero locking/hashing.
        private static readonly int[] ThrottleCounters = new int[MaxItemId];      // GroundItem
        private static readonly int[] RuleThrottleCounters = new int[MaxItemId];  // Per-item rules
        private static readonly int[] ModOptCounters = new int[MaxItemId];        // Mod optimization
        private static readonly sbyte[] HoldableCache = new sbyte[MaxItemId];   // 0=unknown, 1=yes, -1=no
        private static readonly sbyte[] WireCache = new sbyte[MaxItemId];       // 0=unknown, 1=has wires, -1=no wires
        private static readonly sbyte[] IsWireCache = new sbyte[MaxItemId];    // 0=unknown, 1=pure wire item, -1=not wire
        // ActiveUseCache: generation in high 32 bits, result in low bit
        private static readonly int[] ActiveUseCacheGen = new int[MaxItemId];
        private static readonly bool[] ActiveUseCacheVal = new bool[MaxItemId];

        // ── Server-side callbacks (set by ItemOptimizerPlugin.Server, null on client) ──
        internal static Action OnPreUpdate;
        internal static Action<float> OnPostUpdate;

        private static bool _diagLogged;

        // ────────────────────────────────────────────────
        //  Registration
        // ────────────────────────────────────────────────

        internal static void Register(Harmony harmony)
        {
            if (_patchAttached) return;

            // Reflection lookups
            _tickField = typeof(MapEntity).GetField("mapEntityUpdateTick",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (_tickField != null)
                _tickFieldRef = AccessTools.StaticFieldRefAccess<int>(_tickField);
            _updateProjSpecific = AccessTools.Method(typeof(MapEntity), "UpdateAllProjSpecific",
                new[] { typeof(float) });
            _hullUpdateCheats = AccessTools.Method(typeof(Hull), "UpdateCheats",
                new[] { typeof(float), typeof(Camera) });

            // Create fast delegates for static utility methods (avoids MethodInfo.Invoke + object[] alloc)
            if (_updateProjSpecific != null)
                _updateProjSpecificDel = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), _updateProjSpecific);
            if (_hullUpdateCheats != null)
                _hullUpdateCheatsDel = (Action<float, Camera>)Delegate.CreateDelegate(typeof(Action<float, Camera>), _hullUpdateCheats);

            // Create fast delegates for hot-path private methods (avoids MethodInfo.Invoke boxing/overhead)
            var netPosMethod = AccessTools.Method(typeof(Item), "UpdateNetPosition", new[] { typeof(float) });
            if (netPosMethod != null)
                _itemUpdateNetPos = (Action<Item, float>)Delegate.CreateDelegate(typeof(Action<Item, float>), netPosMethod);

            var waterForcesMethod = AccessTools.Method(typeof(Item), "ApplyWaterForces");
            if (waterForcesMethod != null)
                _itemApplyWaterForces = (Action<Item>)Delegate.CreateDelegate(typeof(Action<Item>), waterForcesMethod);

            _reflectionOk = _tickField != null;
            if (!_reflectionOk)
            {
                LuaCsLogger.Log("[ItemOptimizer] UpdateAllTakeover: FAILED to find mapEntityUpdateTick — fallback to vanilla.");
                return;
            }
            if (_updateProjSpecific == null)
                LuaCsLogger.Log("[ItemOptimizer] UpdateAllTakeover: WARNING — UpdateAllProjSpecific not found (server build?).");

            // Single prefix on MapEntity.UpdateAll — this is our ONLY entry point
            var updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll));
            harmony.Patch(updateAll,
                prefix: new HarmonyMethod(typeof(UpdateAllTakeover), nameof(UpdateAllPrefix))
                    { priority = Priority.First });

            _patchAttached = true;
            Enabled = false;  // await OnLoadCompleted
            _diagLogged = false;

            LuaCsLogger.Log($"[ItemOptimizer] UpdateAllTakeover registered. " +
                $"tickField={_tickField != null}, projSpecific={_updateProjSpecific != null}, " +
                $"hullCheats={_hullUpdateCheats != null}, netPos={_itemUpdateNetPos != null}");
        }

        internal static void Unregister(Harmony harmony)
        {
            if (!_patchAttached) return;
            Enabled = false;
            DispatchActive = false;
            OnPreUpdate = null;
            OnPostUpdate = null;
            ClearItemCaches();

            var updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll));
            harmony.Unpatch(updateAll,
                AccessTools.Method(typeof(UpdateAllTakeover), nameof(UpdateAllPrefix)));

            _patchAttached = false;
        }

        /// <summary>Clear flat-array caches. Called on unregister / round transitions.</summary>
        internal static void ClearItemCaches()
        {
            Array.Clear(ThrottleCounters, 0, MaxItemId);
            Array.Clear(RuleThrottleCounters, 0, MaxItemId);
            Array.Clear(ModOptCounters, 0, MaxItemId);
            Array.Clear(HoldableCache, 0, MaxItemId);
            Array.Clear(WireCache, 0, MaxItemId);
            Array.Clear(IsWireCache, 0, MaxItemId);
            Array.Clear(ActiveUseCacheGen, 0, MaxItemId);
            Array.Clear(ActiveUseCacheVal, 0, MaxItemId);
            _diagLogged = false;
        }

        // ────────────────────────────────────────────────
        //  Frame flag refresh (replaces ItemUpdatePatch.NewFrame)
        // ────────────────────────────────────────────────

        internal static void RefreshFrameFlags()
        {
            _frameGeneration++;
            _hasColdStorage = OptimizerConfig.EnableColdStorageSkip;
            _hasGroundItem = OptimizerConfig.EnableGroundItemThrottle;
            _hasRules = OptimizerConfig.RuleLookup.Count > 0;
            _hasModOpt = OptimizerConfig.ModOptLookup.Count > 0;
            _miscParallelEnabled = OptimizerConfig.EnableMiscParallel;
            _hasProxy = ProxyRegistry.HasHandlers;
            _hasSignalGraph = OptimizerConfig.SignalGraphMode > 0 && (SignalGraphEvaluator.IsCompiled || SignalGraphEvaluator.IsDirty);
            _hasWireSkip = OptimizerConfig.EnableWireSkip;
            _hasZoneManaged = World.NativeRuntimeBridge.IsEnabled;
        }

        // ════════════════════════════════════════════════
        //  THE PREFIX — replaces entire MapEntity.UpdateAll
        // ════════════════════════════════════════════════

        public static bool UpdateAllPrefix(float deltaTime, Camera cam)
        {
            if (!Enabled || !_reflectionOk) return true;  // fallback to vanilla

            try
            {
                // ═══ Server-side: begin tick timing ═══
                OnPreUpdate?.Invoke();

                // ═══ Phase 0: Frame setup ═══
                int tick = ++_tickFieldRef();

                int mapInterval = MapEntity.MapEntityUpdateInterval;
                bool isMapFrame = (tick % mapInterval == 0);
                float scaledDt = deltaTime * mapInterval;

                int powerInterval = MapEntity.PoweredUpdateInterval;
                bool isPowerFrame = (tick % powerInterval == 0);
                float poweredDt = deltaTime * powerInterval;

                RefreshFrameFlags();
                PerfProfiler.MapEntityUpdateAllPrefix();

                long totalDispatchStart = Stopwatch.GetTimestamp();

                // ═══ Phase A: Misc (Hull / Structure / Gap / Power) ═══
                long phaseAStart = Stopwatch.GetTimestamp();

                // Gap reset MUST complete for ALL gaps before any gap.Update
                for (int _idx = 0; _idx < Gap.GapList.Count; _idx++)
                {
                    Gap gap = Gap.GapList[_idx];
                    gap.ResetWaterFlowThisFrame();
                }

                // Fisher-Yates shuffle via reused buffer (no LINQ, no allocation)
                _gapShuffleBuffer.Clear();
                for (int _idx = 0; _idx < Gap.GapList.Count; _idx++)
                {
                    Gap g = Gap.GapList[_idx];
                    _gapShuffleBuffer.Add(g);
                }
                for (int i = _gapShuffleBuffer.Count - 1; i > 0; i--)
                {
                    int j = Rand.Int(i + 1);
                    (_gapShuffleBuffer[i], _gapShuffleBuffer[j]) = (_gapShuffleBuffer[j], _gapShuffleBuffer[i]);
                }

                if (isMapFrame && _miscParallelEnabled)
                {
                    // Hulls run on main thread: Hull.UpdateProjSpecific (server) calls
                    // CreateEntityEvent which is NOT thread-safe. Moving Hull out of
                    // Parallel.Invoke prevents entity event list corruption that causes
                    // one-way client disconnects.

                    for (int _idx = 0; _idx < Hull.HullList.Count; _idx++)
                    {
                        Hull hull = Hull.HullList[_idx];
                        hull.Update(scaledDt, cam);
                    }
                    _hullUpdateCheatsDel?.Invoke(scaledDt, cam);
                    // Structure/Gap/Power are safe for parallel — they never call CreateEntityEvent.
                    // Store frame params in static fields, use cached Action delegates
                    // (zero closure allocation vs. lambdas capturing locals = 6 allocs/frame saved).
                    _piScaledDt = scaledDt;
                    _piDeltaTime = deltaTime;
                    _piPoweredDt = poweredDt;
                    _piCam = cam;
                    _piIsPowerFrame = isPowerFrame;
                    _piActions[0] = _piUpdateStructures;
                    _piActions[1] = _piUpdateGaps;
                    _piActions[2] = _piUpdatePower;
                    Parallel.Invoke(_piActions);

                    // Drain deferred physics actions from Gap parallel execution
                    GapSafetyPatch.DrainDeferred();
                }
                else
                {
                    // Sequential path (original behavior)
                    if (isMapFrame)
                    {
                        for (int _idx = 0; _idx < Hull.HullList.Count; _idx++)
                        {
                            Hull hull = Hull.HullList[_idx];
                            hull.Update(scaledDt, cam);
                        }
                        _hullUpdateCheatsDel?.Invoke(scaledDt, cam);

                        for (int _idx = 0; _idx < Structure.WallList.Count; _idx++)
                        {
                            Structure structure = Structure.WallList[_idx];
                            structure.Update(scaledDt, cam);
                        }
                    }

                    for (int _idx = 0; _idx < _gapShuffleBuffer.Count; _idx++)
                    {
                        Gap gap = _gapShuffleBuffer[_idx];
                        gap.Update(deltaTime, cam);
                    }

                    if (isPowerFrame)
                        Powered.UpdatePower(poweredDt);
                }

                // ═══ Phase B: Items ═══
                Stats.PhaseAMs = (float)((Stopwatch.GetTimestamp() - phaseAStart) * _ticksToMs);
                long phaseBStart = Stopwatch.GetTimestamp();
                Item.UpdatePendingConditionUpdates(deltaTime);  // every frame, raw dt

                // Rebuild hull→character spatial index (Character.UpdateAll already ran)
                if (isMapFrame)
                    HullCharacterTracker.Rebuild();

                // NativeRuntime: tick registered NativeComponents (sensors etc.)
                if (isMapFrame && World.NativeRuntimeBridge.IsEnabled)
                {
                    long nativeRtStart = Stopwatch.GetTimestamp();
                    World.NativeRuntimeBridge.Tick(scaledDt, cam);
                    Stats.PhaseBNativeRtMs = (float)((Stopwatch.GetTimestamp() - nativeRtStart) * _ticksToMs);
                }

                if (isMapFrame)
                    DispatchItemUpdates(scaledDt, cam);

                // ═══ Phase C: PriorityItems (LuaCs) — every frame, raw dt ═══
                Stats.PhaseBMs = (float)((Stopwatch.GetTimestamp() - phaseBStart) * _ticksToMs);
                long phaseCStart = Stopwatch.GetTimestamp();
                var priorityItemsC = LuaCsSetup.Instance?.Game?.UpdatePriorityItems;
                if (priorityItemsC != null)
                {
                    foreach (var item in priorityItemsC)
                    {
                        if (item.Removed) continue;
                        item.Update(deltaTime, cam);
                    }
                }

                // ═══ Phase D: Tail ═══
                Stats.PhaseCMs = (float)((Stopwatch.GetTimestamp() - phaseCStart) * _ticksToMs);
                long phaseDStart = Stopwatch.GetTimestamp();
                if (isMapFrame)
                {
                    _updateProjSpecificDel?.Invoke(scaledDt);
                    Entity.Spawner?.Update();
                }

                // ═══ Phase E: Frame-end hooks ═══
                Stats.PhaseDMs = (float)((Stopwatch.GetTimestamp() - phaseDStart) * _ticksToMs);
                Stats.TotalDispatchMs = (float)((Stopwatch.GetTimestamp() - totalDispatchStart) * _ticksToMs);
                PerfProfiler.MapEntityUpdateAllPostfix();
                SyncTracker.UpdateTimeout(deltaTime);
                CharacterStaggerPatch.IncrementFrame();
                Stats.EndFrame();

                // ═══ Server-side: collect + broadcast metrics ═══
                OnPostUpdate?.Invoke(deltaTime);

                return false;  // skip original method entirely
            }
            catch (Exception ex)
            {
                // Auto-fallback: disable takeover, let vanilla run next frame
                LuaCsLogger.Log($"[ItemOptimizer] UpdateAllTakeover CRASHED — disabling. {ex.GetType().Name}: {ex.Message}");
                DebugConsole.ThrowError($"[ItemOptimizer] UpdateAllTakeover disabled due to error: {ex.Message}", ex);
                Enabled = false;
                return true;
            }
        }

        // ────────────────────────────────────────────────
        //  Item dispatch: freeze/throttle + update
        // ────────────────────────────────────────────────

        private static void DispatchItemUpdates(float dt, Camera cam)
        {
            DispatchActive = true;

            // Pre-build HasStatusTag cache so reads are lock-free
            long preBuildStart = Stopwatch.GetTimestamp();
            HasStatusTagCachePatch.PreBuildAll();
            Stats.PhaseBPreBuildMs = (float)((Stopwatch.GetTimestamp() - preBuildStart) * _ticksToMs);

            // ── Signal graph accelerator: evaluate compiled circuit graph ──
            if (_hasSignalGraph)
            {
                SignalGraphEvaluator.Tick(dt);
                Stats.SignalGraphTickMs = SignalGraphEvaluator.LastTickMs;
            }

            _mainItems.Clear();

            Item lastUpdatedItem = null;

            try
            {
                ClassifyAndSkipItems(dt, cam);

                // ── Main thread: update all items ──
                long mainLoopStart = Stopwatch.GetTimestamp();
                for (int _idx = 0; _idx < _mainItems.Count; _idx++)
                {
                    var item = _mainItems[_idx];
                    lastUpdatedItem = item;
                    item.Update(dt, cam);
                }
                Stats.PhaseBMainLoopMs = (float)((Stopwatch.GetTimestamp() - mainLoopStart) * _ticksToMs);
            }
            catch (InvalidOperationException e)
            {
                GameAnalyticsManager.AddErrorEventOnce(
                    "MapEntity.UpdateAll:ItemUpdateInvalidOperation",
                    GameAnalyticsManager.ErrorSeverity.Critical,
                    $"Error while updating item {lastUpdatedItem?.Name ?? "null"}: {e.Message}");
                throw new InvalidOperationException(
                    $"Error while updating item {lastUpdatedItem?.Name ?? "null"}", innerException: e);
            }
            finally
            {
                DispatchActive = false;
            }
        }

        // ────────────────────────────────────────────────
        //  Classification: single-pass skip / throttle logic
        // ────────────────────────────────────────────────

        /// <summary>
        /// Classifies each item in Item.ItemList and populates _mainItems with items that need full Update.
        /// Applies priority, signal graph accel, wire skip, zone management, proxy, ground throttle,
        /// and ShouldSkipItem checks.
        /// </summary>
        private static void ClassifyAndSkipItems(float dt, Camera cam)
        {
            int skippedCount = 0;
            long proxyPhysicsTicks = 0;

            // ── Single-pass: skip / classify ──
            long classifyStart = Stopwatch.GetTimestamp();
            var priorityItems = LuaCsSetup.Instance?.Game?.UpdatePriorityItems;
            bool hasPriority = priorityItems != null && priorityItems.Count > 0;

            for (int _idx = 0; _idx < Item.ItemList.Count; _idx++)
            {
                Item item = Item.ItemList[_idx];
                if (hasPriority && priorityItems.Contains(item)) continue;

                // Cheapest check first: flat bool[MaxItemId] array (~5ns)
                if (_hasSignalGraph && SignalGraphEvaluator.IsAccelerated(item.ID))
                {
                    Stats.SignalGraphAccelSkips++;
                    continue;
                }

                // Wire skip: pure wire items have no meaningful Update (IsActive=false)
                if (_hasWireSkip && IsPureWireItem(item))
                {
                    Stats.WireSkips++;
                    continue;
                }

                // Zone-managed skip: items whose lifecycle is fully controlled by NativeRuntime
                if (_hasZoneManaged && World.NativeRuntimeBridge.IsZoneManaged[item.ID])
                {
                    Stats.ZoneSkips++;
                    continue;
                }

                // Zone tier-based LOD: skip/throttle items on distant submarines
                if (_hasZoneManaged && item.Submarine != null)
                {
                    byte tier = World.NativeRuntimeBridge.SubZoneTier[item.Submarine.ID & 0xFFFF];
                    if (tier >= (byte)World.ZoneTier.Dormant)
                    {
                        Stats.ZoneSkips++;
                        continue;
                    }
                    if (tier >= (byte)World.ZoneTier.Passive
                        && ((_frameGeneration + (uint)item.ID) & 1) != 0)
                    {
                        Stats.ZonePassiveSkips++;
                        continue;
                    }
                }

                // Proxy items: guarded by frame flag (skips Dict lookup when no handlers)
                if (_hasProxy && item.Prefab != null && ProxyRegistry.TryGetHandler(item.Prefab.Identifier, out var proxyHandler))
                {
                    ProxyRegistry.AttachIfNew(item);
                    Stats.ProxyItems++;

                    switch (proxyHandler.SkipLevel)
                    {
                        case ProxySkipLevel.Full:
                            continue;
                        case ProxySkipLevel.Lightweight:
                            long pStart = Stopwatch.GetTimestamp();
                            ProxyMinimalUpdate(item, dt);
                            proxyPhysicsTicks += Stopwatch.GetTimestamp() - pStart;
                            continue;
                        case ProxySkipLevel.StatusEffectOnly:
                            // Fall through to normal dispatch — add to _mainItems
                            break;
                    }
                    // StatusEffectOnly: skip throttle checks, go straight to dispatch
                    _mainItems.Add(item);
                    continue;
                }

                // Strategy 2: Ground Item Throttle
                // Items with wired connections are part of a circuit — throttling them
                // breaks signal continuity (e.g. water detector → inverted OR → auto-door disable).
                if (_hasGroundItem && item.ParentInventory == null
                    && !OptimizerConfig.WhitelistLookup.Contains(
                        item.Prefab?.Identifier.Value ?? "")
                    && !HasActiveCriticalComponent(item)
                    && !HasWireConnections(item))
                {
                    int gid = item.ID;
                    ThrottleCounters[gid]++;
                    if (ThrottleCounters[gid] % OptimizerConfig.GroundItemSkipFrames != 0)
                    {
                        if (item.body != null && item.body.Enabled)
                            ProxyMinimalUpdate(item, dt);
                        Stats.GroundItemSkips++;
                        skippedCount++;
                        continue;
                    }
                }

                if (ShouldSkipItem(item))
                {
                    skippedCount++;
                    if (_itemUpdateNetPos != null && item.body != null && item.body.Enabled)
                        _itemUpdateNetPos(item, dt);
                    continue;
                }

                _mainItems.Add(item);
            }

            // One-time classification diagnostic
            if (!_diagLogged && Item.ItemList.Count > 0)
            {
                _diagLogged = true;
                LuaCsLogger.Log($"[ItemOptimizer] Takeover dispatch: " +
                    $"total={Item.ItemList.Count}, main={_mainItems.Count}");
            }

            // Server metrics
            ServerMetrics.SkippedItems = skippedCount;
            Stats.ProxyPhysicsMs = (float)(proxyPhysicsTicks * _ticksToMs);
            Stats.PhaseBClassifyMs = (float)((Stopwatch.GetTimestamp() - classifyStart) * _ticksToMs);
        }

        // ────────────────────────────────────────────────
        //  Proxy minimal update (Lightweight skip level)
        // ────────────────────────────────────────────────

        /// <summary>
        /// Runs essential Item.Update phases for proxy items with Lightweight skip level.
        /// Skips: StatusEffects (phase 4), Component loop (phase 5).
        /// Runs: physics body sync, networking, water forces.
        /// </summary>
        private static void ProxyMinimalUpdate(Item item, float dt)
        {
            if (!item.IsActive || item.IsLayerHidden || item.IsInRemoveQueue) return;

            if (item.body != null && item.body.Enabled)
            {
                // Phase 7a: UpdateTransform — sync physics body to item rect
                if (Math.Abs(item.body.LinearVelocity.X) > VelocityThreshold ||
                    Math.Abs(item.body.LinearVelocity.Y) > VelocityThreshold)
                {
                    item.UpdateTransform();
                }

                // Phase 7b: UpdateNetPosition — network sync
                if (_itemUpdateNetPos != null)
                {
                    _itemUpdateNetPos(item, dt);
                }

                // Phase 7c: Water forces
                if (item.InWater)
                    _itemApplyWaterForces?.Invoke(item);
            }
        }

        // ────────────────────────────────────────────────
        //  Freeze / Throttle (migrated from ItemUpdatePatch)
        // ────────────────────────────────────────────────

        /// <summary>Returns true if this item should be SKIPPED this frame.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldSkipItem(Item item)
        {
            // Strategy 1: Cold Storage Skip
            if (_hasColdStorage && ColdStorageDetector.IsInColdStorage(item))
            {
                Stats.ColdStorageSkips++;
                return true;
            }

            // Strategy 2: Ground Item Throttle — handled in classify loop (physics-preserving)

            // Fast exit: no rules and no mod optimization
            if (!_hasRules && !_hasModOpt) return false;

            string identifier = item.Prefab?.Identifier.Value;
            if (identifier == null) return false;

            // Strategy 5: Per-Item Rules
            if (_hasRules && OptimizerConfig.RuleLookup.TryGetValue(identifier, out var rule))
            {
                if (CheckRuleCondition(item, rule.Condition))
                {
                    if (rule.Action == ItemRuleAction.Skip)
                    {
                        Stats.ItemRuleSkips++;
                        return true;
                    }
                    if (rule.Action == ItemRuleAction.Throttle)
                    {
                        int id2 = item.ID;
                        RuleThrottleCounters[id2]++;
                        if (RuleThrottleCounters[id2] % rule.SkipFrames != 0)
                        {
                            Stats.ItemRuleSkips++;
                            return true;
                        }
                    }
                }
                return false;  // rule matched — skip ModOpt for this item
            }

            // Strategy 6: Mod Optimization
            if (_hasModOpt && OptimizerConfig.ModOptLookup.TryGetValue(identifier, out var skipFrames))
            {
                // Whitelist: never throttle whitelisted items
                if (OptimizerConfig.WhitelistLookup.Contains(identifier))
                    return false;

                if (IsModOptEligibleCached(item))
                {
                    int id3 = item.ID;
                    ModOptCounters[id3]++;
                    if (ModOptCounters[id3] % skipFrames != 0)
                    {
                        Stats.ModOptSkips++;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsModOptEligibleCached(Item item)
        {
            int id = item.ID;
            if (ActiveUseCacheGen[id] == _frameGeneration)
                return ActiveUseCacheVal[id];
            ActiveUseCacheGen[id] = _frameGeneration;
            bool result = ColdStorageDetector.IsModOptEligible(item);
            ActiveUseCacheVal[id] = result;
            return result;
        }

        private static bool CheckRuleCondition(Item item, string condition)
        {
            switch (condition)
            {
                case "coldStorage": return ColdStorageDetector.IsInColdStorage(item);
                case "notInActiveUse": return ColdStorageDetector.IsNotInActiveUse(item);
                case "always":
                default: return true;
            }
        }

        private static bool IsHoldable(Item item)
        {
            int id = item.ID;
            sbyte cached = HoldableCache[id];
            if (cached != 0) return cached > 0;
            HoldableCache[id] = item.GetComponent<Holdable>() != null ? (sbyte)1 : (sbyte)-1;
            return HoldableCache[id] > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasWireConnections(Item item)
        {
            int id = item.ID;
            sbyte cached = WireCache[id];
            if (cached != 0) return cached > 0;
            var conns = item.Connections;
            if (conns != null)
            {
                for (int _idx = 0; _idx < conns.Count; _idx++)
                {
                    var c = conns[_idx];
                    if (c.Wires.Count > 0)
                    {
                        WireCache[id] = 1;
                        return true;
                    }
                }
            }
            WireCache[id] = -1;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPureWireItem(Item item)
        {
            int id = item.ID;
            sbyte cached = IsWireCache[id];
            if (cached != 0) return cached > 0;

            // A "pure wire" item has Wire as its only meaningful component.
            // Wire + ConnectionPanel + ItemContainer + Holdable are all inert when connected.
            // Use 'is' type checks instead of GetType().Name to avoid string allocations.
            bool hasWire = false;
            bool hasOtherActive = false;
            for (int _idx = 0; _idx < item.Components.Count; _idx++)
            {
                var ic = item.Components[_idx];
                if (ic is Wire)
                    hasWire = true;
                else if (ic is not ConnectionPanel && ic is not ItemContainer && ic is not Holdable)
                    hasOtherActive = true;
            }

            bool isPure = hasWire && !hasOtherActive;
            IsWireCache[id] = isPure ? (sbyte)1 : (sbyte)-1;
            return isPure;
        }

        // ────────────────────────────────────────────────
        //  Ground item critical component check
        // ────────────────────────────────────────────────

        /// <summary>
        /// Returns true if this item has a critical component that is currently active.
        /// Items with active critical components must NOT be ground-throttled.
        /// Idle machines (Fabricator not crafting, etc.) CAN be throttled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasActiveCriticalComponent(Item item)
        {
            for (int _idx = 0; _idx < item.Components.Count; _idx++)
            {
                var ic = item.Components[_idx];
                // Always-critical: submarine infrastructure that must never be throttled
                if (ic is Reactor || ic is Engine || ic is Steering
                    || ic is DockingPort || ic is ElectricalDischarger)
                    return true;

                // Critical when active: power, fluid, structural, interaction
                if (ic is Pump || ic is Door || ic is PowerTransfer
                    || ic is OxygenGenerator || ic is Turret
                    || ic is Controller || ic is TriggerComponent)
                {
                    if (ic.IsActive) return true;
                }

                // Machines: only critical when running (crafting/deconstructing)
                if (ic is Fabricator || ic is Deconstructor)
                {
                    if (ic.IsActive) return true;
                }
            }
            return false;
        }

    }
}
