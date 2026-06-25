# ItemOptimizer AGENTS.md

**Barotrauma performance mod** — reduces per-frame work in large subs via Harmony patches.  
C# + LuaCsForBarotrauma (IAssemblyPlugin). Workshop ID: 3706355579, v1.0.30, game v1.12.6.2.

## Structure

```
ItemOptimizer/
├── CSharp/                    # All source code
│   ├── Shared/                # Cross-client/server logic (36 files)
│   │   ├── Core/              # Plugin lifecycle, config, localization, logging
│   │   ├── Patches/           # Harmony patches (MapEntity, Character, Item)
│   │   │   ├── MapEntity/     # UpdateAllTakeover, caches, transpilers
│   │   │   ├── Character/     # AI stagger, zone skip
│   │   │   └── Item/          # Sensor/Power rewrites, component patches
│   │   ├── World/             # Zone system, NativeRuntime, snapshots, grid
│   │   │   └── Sensors/       # MotionSensor native rewrite
│   │   ├── Signal/            # Signal graph accelerator (node compiler + eval)
│   │   ├── Diagnostics/       # SpikeDetector, Profiler, DiagLog, SyncTracker
│   │   ├── Net/               # ServerMetrics (shared DTO types)
│   │   ├── Proxy/             # ProxyRegistry (batch compute/sync)
│   │   └── Patches.cs         # Generic SubmarineZone patch
│   ├── Client/                # Client-only code (17 files)
│   │   ├── Core/              # Plugin client init
│   │   ├── UI/                # SettingsPanel (6 partials), overlays
│   │   ├── Patches/           # Interaction labels, AnimLOD, LadderFix
│   │   ├── World/             # NativeRuntimeBridge client, LightNativeComponent
│   │   └── Net/               # MetricRelayReceiver, SyncRelayReceiver
│   ├── Server/                # Server-only code (5 files)
│   │   ├── Core/              # Plugin server init, ServerOptimizer
│   │   ├── Diagnostics/       # ServerPerfTracker
│   │   └── Net/               # MetricRelaySender, SyncRelaySender
│   └── RunConfig.xml          # Mod runtime config (Client/Server = Standard)
├── CSharp_Archived/           # Old patch implementations (replaced/refactored)
├── Items/                     # Proxy item XML defs + placeholder icon
├── Localization/              # 12 JSON translation files (en, zh-CN, ja, ko, etc.)
├── Data/                      # Runtime data (empty in repo)
├── filelist.xml               # Workshop content manifest
└── README.md                  # Full feature documentation (bilingual EN/ZH)
```

## Where To Look

| Task | File |
|------|------|
| Plugin init / patch registration | `CSharp/Shared/Core/ItemOptimizerPlugin.cs` |
| Config load / save | `CSharp/Shared/Core/OptimizerConfig.cs` |
| Main update loop logic | `CSharp/Shared/Patches/MapEntity/UpdateAllTakeover.cs` |
| Item update transpiler | `CSharp/Shared/Patches/MapEntity/ItemUpdateTranspiler.cs` |
| Signal graph accelerator | `CSharp/Shared/Signal/SignalGraphEvaluator.cs` |
| Zone dispatch system | `CSharp/Shared/World/Zone.cs` + `ZoneGraph.cs` |
| Settings UI | `CSharp/Client/UI/SettingsPanel.cs` (6 partial files) |
| Server metrics relay | `CSharp/Server/Net/MetricRelaySender.cs` |
| Profiler | `CSharp/Shared/Diagnostics/PerfProfiler.cs` |
| Spike detector | `CSharp/Shared/Diagnostics/SpikeDetector.cs` |
| Localization | `CSharp/Shared/Core/Localization.cs` + `Localization/*.json` |

## Conventions

- **Partial class pattern**: `ItemOptimizerPlugin` is split across Shared/Core, Client/Core, Server/Core via `partial`. Client/Server partials implement `InitializeClient()`/`InitializeServer()`.
- **Harmony patches**: Prefix for full takeovers, Transpiler for method wrapping, Postfix for UI hooks + cross-thread safety (dedup guard via `_roundInitialized`).
- **Config**: XML-based (`ItemOptimizer_config.xml`), migrated from mod dir to user data dir for Workshop survival.
- **Config namespaces**: `ItemOptimizerMod`, `ItemOptimizerMod.Patches`, `ItemOptimizerMod.SignalGraph`, `ItemOptimizerMod.Proxy`, `ItemOptimizerMod.World`.
- **Logging**: `LuaCsLogger.Log` / `LuaCsLogger.LogError` for general, `DebugConsole.ThrowError` for user-visible errors.
- **Toggle pattern**: Each feature has an `Enable*` bool in `OptimizerConfig` + string-keyed `SetStrategyValue` switch in plugin.
- **Cached lookups**: `RuleLookup` (Dictionary), `ModOptLookup` (volatile Dictionary), `WhitelistLookup` (HashSet) — rebuilt atomically at config load/profile change.
- **LuaCs lifecycle**: Plugin implements `IAssemblyPlugin` (Initialize → OnLoadCompleted → Dispose). Round lifecycle via Harmony (GameSession.StartRound/EndRound) because LuaCs IEventRoundStarted is unreliable.

## Anti-Patterns (Archived)

Old implementations live in `CSharp_Archived/` — DO NOT use these patterns:
- Individual Harmony prefixes per-sensor (replaced by `ComponentDispatchTranspiler`)
- Direct `MotionSensor.Update`/`WaterDetector.Update` Prefix patches (replaced by native rewrites in `World/Sensors/`)
- Old SignalOptPatches (replaced by `SignalGraphEvaluator`)
- CustomInterfacePatch, DoorPatch, WearablePatch, StatusHUDPatch — replaced or no longer needed

## Commands

No build system — this is a game mod. Deploy by placing the folder in `LocalMods/` or publishing via Steam Workshop (`filelist.xml` at root).

## Notes

- Mod uses `ToggleValidator` for dependency validation (e.g., NativeRuntime requires MotionSensorRewrite).
- `PerfCommands` registers in-game console commands (`/itemopt_*`).
- `SafeLogger.HandleException` wraps all Harmony postfixes with try/catch to prevent patch crashes from killing the game.
- `UpdateAllTakeover.Enabled` is set to `true` in `OnLoadCompleted` (not `Initialize`) — all systems must be ready first.
- Signal graph compilation happens on `OnRoundStart` when items exist in `Item.ItemList`.
