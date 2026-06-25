using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Complete replacement for RelayComponent.Update() via Harmony Prefix.
    /// Supersedes the Client-only RelayOpt in SignalOptPatches.
    ///
    /// Key optimizations vs vanilla:
    /// 1. FieldRefAccess for isBroken — no FieldInfo boxing
    /// 2. Cached Connection references — skip 3x per-frame string dictionary lookups
    /// 3. Cached ToString for power_value_out / load_value_out
    /// 4. .Count > 0 instead of .Any() — no LINQ enumerator alloc
    /// 5. Full UpdateOvervoltage logic (matches vanilla exactly)
    /// 6. Flat struct array — no ConditionalWeakTable / GC pressure
    /// </summary>
    static class RelayRewrite
    {
        /// <summary>Maximum item ID value (ushort.MaxValue + 1). Flat arrays indexed by item.ID.</summary>
        private const int MaxItemId = 1 << 16;

        // ── FieldRef accessors ──
        private static readonly AccessTools.FieldRef<PowerTransfer, bool> Ref_isBroken =
            AccessTools.FieldRefAccess<PowerTransfer, bool>("isBroken");
        private static readonly AccessTools.FieldRef<PowerTransfer, float> Ref_overloadCooldownTimer =
            AccessTools.FieldRefAccess<PowerTransfer, float>("overloadCooldownTimer");

        // ── Delegates for protected methods ──
        private static Action<RelayComponent> _refreshConnections;
        private static Action<RelayComponent> _setAllConnectionsDirty;

        // ── FieldRef for Item.hasStatusEffectsOfType (skip no-op ApplyStatusEffects) ──
        private static readonly AccessTools.FieldRef<Item, bool[]> Ref_hasStatusEffects =
            AccessTools.FieldRefAccess<Item, bool[]>("hasStatusEffectsOfType");

        // ── Cached connections per instance (flat array indexed by item.ID) ──
        private struct ConnCache
        {
            public Connection StateOut;
            public Connection PowerValueOut;
            public Connection LoadValueOut;
            public bool HasOnActiveEffects;
            public bool Resolved;
        }
        private static readonly ConnCache[] CachedConns = new ConnCache[MaxItemId];
        private const float DegradationChance = 0.01f;
        private const float FireProbabilityMultiplier = 0.1f;
        private const float DefaultIntensity = 0.5f;
        private const float MinParticleScale = 0.5f;

        // ── Per-instance cached ToString state ──
        private struct SignalCache
        {
            public int PrevPowerValue;
            public string PowerValueStr;
            public int PrevLoadValue;
            public string LoadValueStr;
            public bool Initialized;
        }
        private static readonly SignalCache[] CachedSignals = new SignalCache[MaxItemId];

        internal static bool IsRegistered => true; // dispatched via ComponentDispatchTranspiler

        /// <summary>
        /// Initialize delegate accessors for protected methods. Called once at plugin init.
        /// </summary>
        internal static void Init()
        {
            var refreshMethod = AccessTools.Method(typeof(PowerTransfer), "RefreshConnections");
            var dirtyMethod = AccessTools.Method(typeof(PowerTransfer), "SetAllConnectionsDirty");

            if (refreshMethod == null || dirtyMethod == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] RelayRewrite: failed to resolve PowerTransfer protected methods");
                return;
            }

            _refreshConnections = (Action<RelayComponent>)Delegate.CreateDelegate(
                typeof(Action<RelayComponent>), refreshMethod);
            _setAllConnectionsDirty = (Action<RelayComponent>)Delegate.CreateDelegate(
                typeof(Action<RelayComponent>), dirtyMethod);
            LuaCsLogger.Log("[ItemOptimizer] RelayRewrite initialized");
        }

        internal static void Reset()
        {
            Array.Clear(CachedConns, 0, CachedConns.Length);
            Array.Clear(CachedSignals, 0, CachedSignals.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref ConnCache ResolveConnections(RelayComponent rc)
        {
            int id = rc.item.ID;
            ref var cc = ref CachedConns[id];
            if (cc.Resolved) return ref cc;

            var connections = rc.item.Connections;
            if (connections != null)
            {
                foreach (var conn in connections)
                {
                    switch (conn.Name)
                    {
                        case "state_out": cc.StateOut = conn; break;
                        case "power_value_out": cc.PowerValueOut = conn; break;
                        case "load_value_out": cc.LoadValueOut = conn; break;
                    }
                }
            }
            cc.Resolved = true;
            cc.HasOnActiveEffects = Ref_hasStatusEffects != null
                && Ref_hasStatusEffects(rc.item)[(int)ActionType.OnActive];
            return ref cc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SendViaConn(Item item, string signal, Connection conn, string fallbackName)
        {
            if (conn != null)
            {
                if (!conn.IsConnectedToSomething()) return;
                item.SendSignal(new Signal(signal, source: item), conn);
            }
            else
            {
                item.SendSignal(signal, fallbackName);
            }
        }

        internal static void Execute(RelayComponent __instance, float deltaTime)
        {
            if (!OptimizerConfig.EnableRelayRewrite)
            {
                __instance.Update(deltaTime, null);
                return;
            }

            var item = __instance.item;
            int id = item.ID;

            // RefreshConnections — protected, called via fast delegate
            _refreshConnections(__instance);

            ref var cc = ref ResolveConnections(__instance);

            // 1. state_out: string literal, no alloc
            SendViaConn(item, __instance.IsOn ? "1" : "0", cc.StateOut, "state_out");

            // 2. power_value_out: cached ToString
            ref var sc = ref CachedSignals[id];
            int powerVal = (int)Math.Round(-__instance.PowerLoad);
            if (!sc.Initialized || powerVal != sc.PrevPowerValue)
            {
                sc.PrevPowerValue = powerVal;
                sc.PowerValueStr = powerVal.ToString();
            }
            SendViaConn(item, sc.PowerValueStr, cc.PowerValueOut, "power_value_out");

            // 3. load_value_out: cached ToString
            int loadVal = (int)Math.Round(__instance.DisplayLoad);
            if (!sc.Initialized || loadVal != sc.PrevLoadValue)
            {
                sc.PrevLoadValue = loadVal;
                sc.LoadValueStr = loadVal.ToString();
                sc.Initialized = true;
            }
            SendViaConn(item, sc.LoadValueStr, cc.LoadValueOut, "load_value_out");

            // isBroken check via FieldRef (zero-boxing)
            ref bool isBroken = ref Ref_isBroken(__instance);
            if (isBroken)
            {
                _setAllConnectionsDirty(__instance);
                isBroken = false;
            }

            // StatusEffects — skip if no OnActive effects defined (most relays)
            if (cc.HasOnActiveEffects)
                __instance.ApplyStatusEffects(ActionType.OnActive, deltaTime);

            // Full UpdateOvervoltage — shared helper with PowerTransferRewrite
            UpdateOvervoltage(__instance, deltaTime, item, ref Ref_overloadCooldownTimer(__instance));

            return;
        }

        internal static void UpdateOvervoltage(PowerTransfer pt, float deltaTime, Item item, ref float overloadCooldownRef)
        {
            // Full UpdateOvervoltage — matches vanilla exactly
            if (item.Repairables.Count > 0 && pt.CanBeOverloaded)
            {
                float maxOverVoltage = Math.Max(pt.OverloadVoltage, 1.0f);
                bool overload = pt.Voltage > maxOverVoltage
                    && GameMain.GameSession is not { RoundDuration: < 5 };
                pt.Overload = overload;

                if (overload && GameMain.NetworkMember is not { IsClient: true })
                {
                    if (overloadCooldownRef > 0.0f)
                    {
                        overloadCooldownRef -= deltaTime;
                    }
                    else
                    {
                        float prevCondition = item.Condition;
                        if (Rand.Range(0.0f, 1.0f) < DegradationChance)
                        {
                            float conditionFactor = MathHelper.Lerp(5.0f, 1.0f, item.Condition / item.MaxCondition);
                            item.Condition -= deltaTime * Rand.Range(10.0f, 500.0f) * conditionFactor;
                        }

                        if (item.Condition <= 0.0f && prevCondition > 0.0f)
                        {
                            overloadCooldownRef = 5.0f; // OverloadCooldown
#if CLIENT
                            SoundPlayer.PlaySound("zap", item.WorldPosition, hullGuess: item.CurrentHull);
                            Vector2 baseVel = Rand.Vector(300.0f);
                            for (int i = 0; i < 10; i++)
                            {
                                var particle = GameMain.ParticleManager.CreateParticle("spark", item.WorldPosition,
                                    baseVel + Rand.Vector(100.0f), 0.0f, item.CurrentHull);
                                if (particle != null) particle.Size *= Rand.Range(MinParticleScale, 1.0f);
                            }
#endif
                            float currentIntensity = GameMain.GameSession?.EventManager != null
                                ? GameMain.GameSession.EventManager.CurrentIntensity : DefaultIntensity;

                            if (pt.FireProbability > 0.0f &&
                                Rand.Range(0.0f, 1.0f) < MathHelper.Lerp(
                                    pt.FireProbability, pt.FireProbability * FireProbabilityMultiplier, currentIntensity))
                            {
                                new FireSource(item.WorldPosition);
                            }
                        }
                    }
                }
            }
            else
            {
                pt.Overload = false;
            }
        }
    }
}
