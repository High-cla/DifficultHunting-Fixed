using System;
using System.Collections.Generic;
using Barotrauma;
using ItemOptimizerMod.Patches;
using ItemOptimizerMod.SignalGraph;
using ItemOptimizerMod.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ItemOptimizerMod
{
    static class StatsOverlay
    {
        internal static bool Visible;

        internal const string Version = DiagnosticHeader.ModVersion;

        private const int Padding = 10;
        private const int LineSpacing = 4;
        private const int BarHeight = 14;
        private const int BarMaxWidth = 180;
        private const int LabelWidth = 80;
        private const float MinDisplayMs = 0.01f;

        private static readonly Color MainThreadColor = new Color(255, 165, 0);   // Orange

        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!Visible) return;

            var font = GUIStyle.SmallFont;
            string[] coreLines = BuildCoreLines();

            float maxWidth = 0;
            float totalHeight = 0;

            MeasureCoreLines(font, coreLines, ref maxWidth, ref totalHeight);
            MeasureDispatchSection(font, ref maxWidth, ref totalHeight);
            MeasureMemorySection(font, ref maxWidth, ref totalHeight);
            MeasureZoneSection(font, ref maxWidth, ref totalHeight);
            MeasureServerSection(font, ref maxWidth, ref totalHeight);
            MeasureHeldItemSection(font, ref maxWidth, ref totalHeight);

            float panelW = maxWidth + Padding * 2;
            float panelH = totalHeight + Padding * 2;
            float panelX = GameMain.GraphicsWidth - panelW - Padding;
            float panelY = Padding;

            GUI.DrawRectangle(spriteBatch,
                new Vector2(panelX, panelY),
                new Vector2(panelW, panelH),
                Color.Black * 0.6f, isFilled: true);

            float y = panelY + Padding;
            DrawCoreLines(spriteBatch, font, panelX, ref y, coreLines);
            DrawDispatchSection(spriteBatch, font, panelX, ref y);
            DrawMemorySection(spriteBatch, font, panelX, ref y);
            DrawZoneSection(spriteBatch, font, panelX, ref y);
            DrawServerSection(spriteBatch, font, panelX, ref y);
            DrawHeldItemSection(spriteBatch, font, panelX, ref y);
        }

        private static string[] BuildCoreLines()
        {
            string title = $"{Localization.T("mod_name")} {Version}";
            string sep = OverlayHelper.Separator;

            string lineCold = Localization.T("strategy_cold_storage") + ": ~" + Stats.AvgColdStorageSkips.ToString("F0") + "/frame";
            string lineGnd  = Localization.T("strategy_ground_item") + ": ~" + Stats.AvgGroundItemSkips.ToString("F0") + "/frame";
            string lineMot  = Localization.T("strategy_motion") + ": ~" + Stats.AvgMotionSensorSkips.ToString("F0") + "/frame";
            string lineRule = Localization.T("stats_item_rules") + ": ~" + Stats.AvgItemRuleSkips.ToString("F0") + "/frame";
            string lineModOpt = Localization.T("stats_mod_opt") + ": ~" + Stats.AvgModOptSkips.ToString("F0") + "/frame";
            string lineWd   = Localization.T("stats_water_det") + ": ~" + Stats.AvgWaterDetectorSkips.ToString("F0") + "/frame";
            string lineHst  = Localization.T("stats_hst_cache") + ": ~" + Stats.AvgHasStatusTagCacheHits.ToString("F0") + "/frame";
            string lineAnimLod = Localization.T("stats_anim_lod") + ": ~" + (Stats.AvgAnimLODSkipped + Stats.AvgAnimLODHalfRate).ToString("F0") + "/frame";
            string lineCharStagger = Localization.T("stats_char_stagger") + ": ~" + Stats.AvgCharStaggerSkipped.ToString("F0") + "/frame";
            string lineLadderFix = Localization.T("stats_ladder_fix") + ": ~" + Stats.AvgLadderFixCorrections.ToString("F1") + "/frame";
            string lineSave = Localization.Format("stats_saved", Stats.EstimatedSavedMs());
            string lineMiscP = Localization.T("strategy_misc_parallel") + ": " + (OptimizerConfig.EnableMiscParallel ? "ON" : "OFF");

            return new[] { title, sep, lineCold, lineGnd, lineMot, lineRule, lineModOpt, lineWd, lineHst, lineAnimLod, lineCharStagger, lineLadderFix, sep, lineSave, lineMiscP };
        }

        private static void MeasureCoreLines(GUIFont font, string[] lines, ref float maxWidth, ref float totalHeight)
        {
            foreach (var line in lines)
            {
                Vector2 size = font.MeasureString(line);
                if (size.X > maxWidth) maxWidth = size.X;
                totalHeight += size.Y + LineSpacing;
            }
            totalHeight -= LineSpacing;
        }

        private static void DrawCoreLines(SpriteBatch spriteBatch, GUIFont font, float panelX, ref float y, string[] lines)
        {
            foreach (var line in lines)
            {
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    line, Color.White, font: font);
                y += font.MeasureString(line).Y + LineSpacing;
            }
        }

        private static void MeasureDispatchSection(GUIFont font, ref float maxWidth, ref float totalHeight)
        {
            if (!UpdateAllTakeover.Enabled) return;

            string dispatchHeader = Localization.T("section_threads");

            float headerH = font.MeasureString(dispatchHeader).Y + LineSpacing;
            float barsH = BarHeight + LineSpacing;

            float overheadMs = Math.Max(0, Stats.AvgTotalDispatchMs - (Stats.AvgPhaseBMainLoopMs + Stats.AvgPhaseAMs + Stats.AvgPhaseCMs + Stats.AvgPhaseDMs + Stats.AvgProxyPhysicsMs));
            string dispatchTotalLine = string.Format(Localization.T("dispatch_total"),
                Stats.AvgTotalDispatchMs, overheadMs);
            string phaseBreakdown = "  A:" + Stats.AvgPhaseAMs.ToString("F1") + " B:" + Stats.AvgPhaseBMs.ToString("F1") + " C:" + Stats.AvgPhaseCMs.ToString("F1") + " D:" + Stats.AvgPhaseDMs.ToString("F1");
            string subPhaseB = "  B=HST:" + Stats.AvgPhaseBPreBuildMs.ToString("F2") + " SG:" + Stats.AvgSignalGraphTickMs.ToString("F2") + " NR:" + Stats.AvgPhaseBNativeRtMs.ToString("F2") + " Cls:" + Stats.AvgPhaseBClassifyMs.ToString("F1") + " Loop:" + Stats.AvgPhaseBMainLoopMs.ToString("F1");

            float summaryH = font.MeasureString(dispatchTotalLine).Y + LineSpacing;
            summaryH += font.MeasureString(phaseBreakdown).Y + LineSpacing;
            summaryH += font.MeasureString(subPhaseB).Y + LineSpacing;
            totalHeight += headerH + barsH + summaryH;

            float barSectionWidth = LabelWidth + BarMaxWidth + 100;
            if (barSectionWidth > maxWidth) maxWidth = barSectionWidth;
        }

        private static void DrawDispatchSection(SpriteBatch spriteBatch, GUIFont font, float panelX, ref float y)
        {
            if (!UpdateAllTakeover.Enabled) return;

            string dispatchHeader = Localization.T("section_threads");

            // Section header
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                dispatchHeader, Color.Cyan, font: font);
            y += font.MeasureString(dispatchHeader).Y + LineSpacing;

            // Main thread bar
            float mainMs = Stats.AvgPhaseBMainLoopMs;
            float maxMs = Math.Max(MinDisplayMs, mainMs);

            float barX = panelX + Padding + LabelWidth;
            string label = Localization.T("parallel_main");

            // Label
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y + 1),
                label, MainThreadColor, font: font);

            // Bar background
            GUI.DrawRectangle(spriteBatch,
                new Vector2(barX, y),
                new Vector2(BarMaxWidth, BarHeight),
                OverlayHelper.BarBgColor, isFilled: true);

            // Bar fill
            float barW = Math.Max(1, (mainMs / maxMs) * BarMaxWidth);
            GUI.DrawRectangle(spriteBatch,
                new Vector2(barX, y),
                new Vector2(barW, BarHeight),
                MainThreadColor * 0.8f, isFilled: true);

            // Bar outline
            GUI.DrawRectangle(spriteBatch,
                new Vector2(barX, y),
                new Vector2(BarMaxWidth, BarHeight),
                MainThreadColor * 0.4f, isFilled: false);

            // Stats text
            string statsText = mainMs.ToString("F1") + "ms";
            GUI.DrawString(spriteBatch,
                new Vector2(barX + BarMaxWidth + 6, y + 1),
                statsText, Color.White, font: font);

            y += BarHeight + LineSpacing;

            // Total dispatch + overhead
            float overheadMs = Math.Max(0, Stats.AvgTotalDispatchMs - (Stats.AvgPhaseBMainLoopMs + Stats.AvgPhaseAMs + Stats.AvgPhaseCMs + Stats.AvgPhaseDMs + Stats.AvgProxyPhysicsMs));
            string dispatchTotalLine = string.Format(Localization.T("dispatch_total"),
                Stats.AvgTotalDispatchMs, overheadMs);
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                dispatchTotalLine, Color.Gray, font: font);
            y += font.MeasureString(dispatchTotalLine).Y + LineSpacing;

            // Phase breakdown diagnostic
            string phaseBreakdown = "  A:" + Stats.AvgPhaseAMs.ToString("F1") + " B:" + Stats.AvgPhaseBMs.ToString("F1") + " C:" + Stats.AvgPhaseCMs.ToString("F1") + " D:" + Stats.AvgPhaseDMs.ToString("F1");
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                phaseBreakdown, Color.DarkGray, font: font);
            y += font.MeasureString(phaseBreakdown).Y + LineSpacing;

            // Sub-phase B breakdown
            string subPhaseB = "  B=HST:" + Stats.AvgPhaseBPreBuildMs.ToString("F2") + " SG:" + Stats.AvgSignalGraphTickMs.ToString("F2") + " NR:" + Stats.AvgPhaseBNativeRtMs.ToString("F2") + " Cls:" + Stats.AvgPhaseBClassifyMs.ToString("F1") + " Loop:" + Stats.AvgPhaseBMainLoopMs.ToString("F1");
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                subPhaseB, Color.DarkGray, font: font);
            y += font.MeasureString(subPhaseB).Y + LineSpacing;
        }

        private static void MeasureMemorySection(GUIFont font, ref float maxWidth, ref float totalHeight)
        {
            string memHeapLine = "Heap: " + Stats.TotalMemoryMB.ToString("F1") + " MB  GC: " + Stats.Gen0Count.ToString() + "/" + Stats.Gen1Count.ToString() + "/" + Stats.Gen2Count.ToString();

            float lineH = font.MeasureString(memHeapLine).Y + LineSpacing;
            totalHeight += lineH;

            float w = font.MeasureString(memHeapLine).X;
            if (w > maxWidth) maxWidth = w;

            if (SignalGraph.SignalGraphEvaluator.Mode > 0)
            {
                string memSGLine = "SignalGraph: " + Stats.SG_Nodes.ToString() + " nodes, " + Stats.SG_Registers.ToString() + " regs";
                totalHeight += font.MeasureString(memSGLine).Y + LineSpacing;
                w = font.MeasureString(memSGLine).X;
                if (w > maxWidth) maxWidth = w;
            }

            if (NativeRuntimeBridge.IsEnabled)
            {
                string memZoneLine = "Zone: " + Stats.Zone_Count.ToString() + " zones (" + Stats.Zone_ActiveZones.ToString() + " active), " + Stats.Zone_Components.ToString() + " comps";
                totalHeight += font.MeasureString(memZoneLine).Y + LineSpacing;
                w = font.MeasureString(memZoneLine).X;
                if (w > maxWidth) maxWidth = w;
            }
        }

        private static void DrawMemorySection(SpriteBatch spriteBatch, GUIFont font, float panelX, ref float y)
        {
            string memHeapLine = "Heap: " + Stats.TotalMemoryMB.ToString("F1") + " MB  GC: " + Stats.Gen0Count.ToString() + "/" + Stats.Gen1Count.ToString() + "/" + Stats.Gen2Count.ToString();

            // Heap line with color coding
            Color heapColor = Stats.TotalMemoryMB > 2000 ? Color.Red
                : Stats.TotalMemoryMB > 1000 ? Color.Yellow : Color.White;
            // Flash red on Gen2 GC
            if (Stats.Gen2FlashFrames > 0)
                heapColor = (Stats.Gen2FlashFrames & 2) != 0 ? Color.OrangeRed : Color.Red;

            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                memHeapLine, heapColor, font: font);
            y += font.MeasureString(memHeapLine).Y + LineSpacing;

            if (SignalGraph.SignalGraphEvaluator.Mode > 0)
            {
                string memSGLine = "SignalGraph: " + Stats.SG_Nodes.ToString() + " nodes, " + Stats.SG_Registers.ToString() + " regs";
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    memSGLine, new Color(155, 89, 182), font: font); // purple
                y += font.MeasureString(memSGLine).Y + LineSpacing;
            }

            if (NativeRuntimeBridge.IsEnabled)
            {
                string memZoneLine = "Zone: " + Stats.Zone_Count.ToString() + " zones (" + Stats.Zone_ActiveZones.ToString() + " active), " + Stats.Zone_Components.ToString() + " comps";
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    memZoneLine, new Color(0, 206, 209), font: font); // cyan
                y += font.MeasureString(memZoneLine).Y + LineSpacing;
            }
        }

        private static void MeasureZoneSection(GUIFont font, ref float maxWidth, ref float totalHeight)
        {
            if (!NativeRuntimeBridge.IsEnabled) return;

            string zoneInfoLine = "Zone: ~" + Stats.AvgZoneSkips.ToString("F0") + " dormant + ~" + Stats.AvgZonePassiveSkips.ToString("F0") + " passive skips/frame";
            float lineH = font.MeasureString(zoneInfoLine).Y + LineSpacing;
            totalHeight += lineH;

            float w = font.MeasureString(zoneInfoLine).X;
            if (w > maxWidth) maxWidth = w;
        }

        private static void DrawZoneSection(SpriteBatch spriteBatch, GUIFont font, float panelX, ref float y)
        {
            if (!NativeRuntimeBridge.IsEnabled) return;

            string zoneInfoLine = "Zone: ~" + Stats.AvgZoneSkips.ToString("F0") + " dormant + ~" + Stats.AvgZonePassiveSkips.ToString("F0") + " passive skips/frame";
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                zoneInfoLine, Color.Cyan, font: font);
            y += font.MeasureString(zoneInfoLine).Y + LineSpacing;
        }

        private static void MeasureServerSection(GUIFont font, ref float maxWidth, ref float totalHeight)
        {
            if (!ServerMetrics.HasServerData) return;

            string healthLabel = OverlayHelper.GetHealthLabel(ServerMetrics.Health);

            string serverHeader = Localization.T("section_server") + ": " + healthLabel + " (" + ServerMetrics.HealthScore.ToString() + ")";
            string serverTickLine = "  Tick: " + ServerMetrics.AvgTickMs.ToString("F1") + "ms (" + ServerMetrics.TickRate.ToString() + "Hz)";
            string serverClientsLine = Localization.Format("server_clients_entities",
                ServerMetrics.ClientCount, ServerMetrics.EntityCount);
            string serverQueuesLine = Localization.Format("server_queues",
                ServerMetrics.AvgPendingPos, ServerMetrics.AvgEventQueue);
            string serverSkippedLine = Localization.Format("server_skipped", ServerMetrics.SkippedItems);

            float lineH = font.MeasureString(serverHeader).Y + LineSpacing;
            totalHeight += lineH * 6; // separator + header + tick + clients + queues + skipped

            float[] serverWidths = {
                font.MeasureString(serverHeader).X,
                font.MeasureString(serverTickLine).X,
                font.MeasureString(serverClientsLine).X,
                font.MeasureString(serverQueuesLine).X,
                font.MeasureString(serverSkippedLine).X
            };
            foreach (float w in serverWidths)
                if (w > maxWidth) maxWidth = w;
        }

        private static void DrawServerSection(SpriteBatch spriteBatch, GUIFont font, float panelX, ref float y)
        {
            if (!ServerMetrics.HasServerData) return;

            string healthLabel = OverlayHelper.GetHealthLabel(ServerMetrics.Health);
            Color healthColor = OverlayHelper.GetHealthColor(ServerMetrics.Health);

            string serverHeader = Localization.T("section_server") + ": " + healthLabel + " (" + ServerMetrics.HealthScore.ToString() + ")";
            string serverTickLine = "  Tick: " + ServerMetrics.AvgTickMs.ToString("F1") + "ms (" + ServerMetrics.TickRate.ToString() + "Hz)";
            string serverClientsLine = Localization.Format("server_clients_entities",
                ServerMetrics.ClientCount, ServerMetrics.EntityCount);
            string serverQueuesLine = Localization.Format("server_queues",
                ServerMetrics.AvgPendingPos, ServerMetrics.AvgEventQueue);
            string serverSkippedLine = Localization.Format("server_skipped", ServerMetrics.SkippedItems);

            // Separator
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                OverlayHelper.Separator, Color.White, font: font);
            y += font.MeasureString(OverlayHelper.Separator).Y + LineSpacing;

            // Header with health color
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                serverHeader, healthColor, font: font);
            y += font.MeasureString(serverHeader).Y + LineSpacing;

            // Tick
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                serverTickLine, Color.White, font: font);
            y += font.MeasureString(serverTickLine).Y + LineSpacing;

            // Clients + entities
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                serverClientsLine, Color.White, font: font);
            y += font.MeasureString(serverClientsLine).Y + LineSpacing;

            // Queues
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                serverQueuesLine, Color.White, font: font);
            y += font.MeasureString(serverQueuesLine).Y + LineSpacing;

            // Skipped items
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                serverSkippedLine, Color.White, font: font);
            y += font.MeasureString(serverSkippedLine).Y + LineSpacing;
        }

        private static void MeasureHeldItemSection(GUIFont font, ref float maxWidth, ref float totalHeight)
        {
            var controlled = Character.Controlled;
            if (controlled == null) return;

            Item heldItem = controlled.SelectedItem;
            if (heldItem == null)
            {
                foreach (var item in controlled.HeldItems)
                {
                    heldItem = item;
                    break;
                }
            }

            if (heldItem?.Prefab == null) return;

            string heldId = heldItem.Prefab.Identifier.Value;
            string name = heldItem.Prefab.Name?.Value ?? heldId;
            string pkg = heldItem.Prefab.ContentPackage?.Name ?? "Vanilla";
            bool isWhitelisted = OptimizerConfig.WhitelistLookup.Contains(heldId);

            string heldHeader = Localization.T("hud_held_item");
            string heldIdLine = $"  {Localization.T("hud_item_id")}: {heldId}";
            string heldNameLine = $"  {Localization.T("hud_item_name")}: {name}";
            string heldModLine = $"  {Localization.T("hud_item_mod")}: {pkg}";

            var statusParts = new List<string>();
            if (isWhitelisted)
                statusParts.Add(Localization.T("hud_whitelisted"));
            if (ColdStorageDetector.IsInColdStorage(heldItem))
                statusParts.Add(Localization.T("hud_cold_storage"));
            if (OptimizerConfig.RuleLookup.TryGetValue(heldId, out var rule))
                statusParts.Add("Rule: " + rule.Action + "/" + rule.SkipFrames.ToString() + "f");
            if (OptimizerConfig.ModOptLookup.TryGetValue(heldId, out var modSkip))
                statusParts.Add("ModOpt: " + modSkip.ToString() + "f");
            if (statusParts.Count == 0)
                statusParts.Add(Localization.T("hud_no_opt"));

            string heldStatusLine = "  " + Localization.T("hud_item_status") + ": " + string.Join(", ", statusParts);

            float lineH = font.MeasureString(heldHeader).Y + LineSpacing;
            totalHeight += lineH * 6; // sep + header + id + name + mod + status
            if (!isWhitelisted) totalHeight += lineH; // button

            float[] heldWidths = {
                font.MeasureString(heldHeader).X,
                font.MeasureString(heldIdLine).X,
                font.MeasureString(heldNameLine).X,
                font.MeasureString(heldModLine).X,
                font.MeasureString(heldStatusLine).X
            };
            foreach (float w in heldWidths)
                if (w > maxWidth) maxWidth = w;
        }

        private static void DrawHeldItemSection(SpriteBatch spriteBatch, GUIFont font, float panelX, ref float y)
        {
            var controlled = Character.Controlled;
            if (controlled == null) return;

            Item heldItem = controlled.SelectedItem;
            if (heldItem == null)
            {
                foreach (var item in controlled.HeldItems)
                {
                    heldItem = item;
                    break;
                }
            }

            if (heldItem?.Prefab == null) return;

            string heldId = heldItem.Prefab.Identifier.Value;
            string name = heldItem.Prefab.Name?.Value ?? heldId;
            string pkg = heldItem.Prefab.ContentPackage?.Name ?? "Vanilla";
            bool isWhitelisted = OptimizerConfig.WhitelistLookup.Contains(heldId);

            string heldHeader = Localization.T("hud_held_item");
            string heldIdLine = $"  {Localization.T("hud_item_id")}: {heldId}";
            string heldNameLine = $"  {Localization.T("hud_item_name")}: {name}";
            string heldModLine = $"  {Localization.T("hud_item_mod")}: {pkg}";

            var statusParts = new List<string>();
            if (isWhitelisted)
                statusParts.Add(Localization.T("hud_whitelisted"));
            if (ColdStorageDetector.IsInColdStorage(heldItem))
                statusParts.Add(Localization.T("hud_cold_storage"));
            if (OptimizerConfig.RuleLookup.TryGetValue(heldId, out var rule))
                statusParts.Add("Rule: " + rule.Action + "/" + rule.SkipFrames.ToString() + "f");
            if (OptimizerConfig.ModOptLookup.TryGetValue(heldId, out var modSkip))
                statusParts.Add("ModOpt: " + modSkip.ToString() + "f");
            if (statusParts.Count == 0)
                statusParts.Add(Localization.T("hud_no_opt"));

            string heldStatusLine = "  " + Localization.T("hud_item_status") + ": " + string.Join(", ", statusParts);
            string heldBtnText = Localization.T("btn_hud_whitelist");

            // Separator
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                OverlayHelper.Separator, Color.White, font: font);
            y += font.MeasureString(OverlayHelper.Separator).Y + LineSpacing;

            // Header
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                heldHeader, Color.Gold, font: font);
            y += font.MeasureString(heldHeader).Y + LineSpacing;

            // ID
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                heldIdLine, Color.White, font: font);
            y += font.MeasureString(heldIdLine).Y + LineSpacing;

            // Name
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                heldNameLine, Color.White, font: font);
            y += font.MeasureString(heldNameLine).Y + LineSpacing;

            // Mod
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                heldModLine, Color.Gray, font: font);
            y += font.MeasureString(heldModLine).Y + LineSpacing;

            // Status
            Color statusColor = isWhitelisted ? Color.Gold : Color.LimeGreen;
            GUI.DrawString(spriteBatch,
                new Vector2(panelX + Padding, y),
                heldStatusLine, statusColor, font: font);
            y += font.MeasureString(heldStatusLine).Y + LineSpacing;

            // Whitelist button (only if not already whitelisted)
            if (!isWhitelisted)
            {
                Vector2 btnTextSize = font.MeasureString(heldBtnText);
                int btnW = (int)btnTextSize.X + 16;
                int btnH = (int)btnTextSize.Y + 8;
                int btnX = (int)(panelX + Padding);
                int btnY = (int)y + 2;

                var btnRect = new Rectangle(btnX, btnY, btnW, btnH);
                bool hovered = btnRect.Contains(PlayerInput.MousePosition.ToPoint());

                Color btnBg = hovered ? new Color(80, 100, 60, 200) : new Color(50, 60, 40, 180);
                GUI.DrawRectangle(spriteBatch,
                    new Vector2(btnX, btnY), new Vector2(btnW, btnH),
                    btnBg, isFilled: true);

                GUI.DrawRectangle(spriteBatch,
                    new Vector2(btnX, btnY), new Vector2(btnW, btnH),
                    Color.Gold * 0.6f, isFilled: false);

                GUI.DrawString(spriteBatch,
                    new Vector2(btnX + 8, btnY + 4),
                    heldBtnText, Color.Gold, font: font);

                if (hovered && PlayerInput.PrimaryMouseButtonClicked())
                {
                    if (!string.IsNullOrEmpty(heldId) && !OptimizerConfig.Whitelist.Contains(heldId))
                    {
                        OptimizerConfig.Whitelist.Add(heldId);
                        OptimizerConfig.RebuildWhitelistLookup();
                        OptimizerConfig.AutoSave();
                    }
                }
            }
        }
    }
}
