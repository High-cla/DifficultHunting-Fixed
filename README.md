# 高难狩猎 (Difficult Hunting) — 修复版

> Barotrauma 高难度生物狩猎模组，纯 XML 内容增强包。~~60 种生物亚种、自定义武器、20+ 任务、猎人电台事件系统。

---

## 功能特点

- **~60 种生物亚种** — 全新 AI 行为与战斗机制，覆盖 Crawler、Husk、Mudraptor、Tigerthresher 等基础种系
- **自定义武器系统** — 外星武器、弹药、装备，全部可在游戏中制作和升级
- **20+ 巢穴/Boss 任务** — 支持猎人电台（DifficultHunting 标签）随机事件触发
- **猎人电台** — Outpost 随机事件系统，动态派发狩猎契约
- **自定义Buff** — DHpowerUP 等状态效果，增强战斗深度
- **专属沉船** — Typhon2 沉船地图，提供独特探索场景
- **全部简体中文** — 完整中文翻译与本地化

## 原模组修复内容

本修复版在原作基础上修正了以下问题：

| 修复项 | 说明 |
|--------|------|
| 路径修复 | 修复 3 处损坏的 `%ModDir:%` 引用，统一 239 处纹理路径为 `%ModDir%/` 标准格式 |
| 根元素修正 | OutpostEvents.xml 根元素 `Randomevents` → `RandomEvents` |
| 自引用递归 | 修复 EventSet 自我引用的无限循环错误 |
| 删除幽灵任务 | 移除两个不可用任务（killgiantspineling11DH/12DH） |
| 删除测试物品 | 移除 GuardShieldedpistol22（标记"测试"的遗留物品） |
| 注册废弃角色 | 将 Alienbot、Carrierex3 等已定义但未注册的角色加入 filelist |
| 填充本地化 | 补全 5 个空占位符（`<!---->`） |
| 清理注释 | 移除 43 处 XMLSpy 自动生成残留注释 |
| 清理孤主动画 | 删除未引用的 Animation 目录及 4 个孤 XML 文件 |

## 安装方式

1. **Steam 创意工坊（原版）** — 订阅 ID `2750789266`，游戏内启用
2. **修复版手动安装** — 将 `高难狩猎修复版` 文件夹复制到 `Barotrauma/LocalMods/` 目录，游戏模组菜单中启用

> 提示：修复版与原版 `2750789266` 互斥，请选择其一启用。

## 文件统计

| 类型 | 数量 | 说明 |
|------|------|------|
| `.xml` | 317 | 内容定义（生物、物品、任务、事件） |
| `.png` | 38 | 贴图精灵（武器、潜水服、UI） |
| `.ogg` | 24 | 音效 |
| `.sub` | 1 | 沉船地图 |
| **总计** | **378** | **不含 filelist.xml** |

- 游戏版本: **1.1.5.0**
- 模组版本: **1.0.145**

## Credits

- **原作者**: 高难狩猎 (Difficult Hunting) Steam Workshop，ID `2750789266`
- **修复**: 代码清理、路径标准化、bug 修正与废弃内容处理

---

# Difficult Hunting — Fixed Edition

> A pure-XML content mod for Barotrauma that adds high-difficulty creature hunting. ~60 subspecies, custom weapons, 20+ missions, and a Hunter Radio event system.

## Features

- **~60 Creature Subspecies** — New AI behaviors and combat mechanics across Crawler, Husk, Mudraptor, Tigerthresher, and more
- **Custom Weapon System** — Alien weapons, ammo, and equipment, all craftable and upgradeable in-game
- **20+ Nest/Boss Missions** — Triggered via Hunter Radio (`DifficultHunting` tag) random events
- **Hunter Radio** — Outpost random event system for dynamic hunting contracts
- **Custom Buffs** — DHpowerUP status effects for deeper combat
- **Exclusive Wreck** — Typhon2 wreck map with unique exploration content
- **Full Chinese Localization** — Complete Simplified Chinese translation

## Fixes in This Edition

| Fix | Description |
|-----|-------------|
| Broken paths | Fixed 3 `%ModDir:%` references; unified 239 texture paths to `%ModDir%/` standard |
| Root element | OutpostEvents.xml: `Randomevents` → `RandomEvents` |
| Self-recursion | Fixed EventSet infinite loop caused by self-referencing |
| Ghost missions | Removed 2 non-functional missions (killgiantspineling11DH/12DH) |
| Test items | Removed GuardShieldedpistol22 (debug item with "测试" label) |
| Unregistered NPCs | Added Alienbot, Carrierex3 and others to filelist |
| Missing localizations | Filled 5 empty placeholder entries (`<!---->`) |
| XMLSpy clutter | Removed 43 auto-generated XMLSpy comments |
| Orphan animations | Deleted unlinked Animation directory with 4 orphan XML files |

## Installation

1. **Steam Workshop (original)** — Subscribe to ID `2750789266`, enable in-game
2. **Fixed Edition manual** — Copy `高难狩猎修复版` folder to `Barotrauma/LocalMods/`, enable in mod menu

> **Note:** Fixed Edition and the original `2750789266` are mutually exclusive — enable only one.

## File Stats

| Type | Count | Purpose |
|------|-------|---------|
| `.xml` | 317 | Content definitions (creatures, items, missions, events) |
| `.png` | 38 | Textures (weapons, diving suits, UI) |
| `.ogg` | 24 | Sound effects |
| `.sub` | 1 | Wreck map |
| **Total** | **378** | **(excluding filelist.xml)** |

- Game version: **1.1.5.0**
- Mod version: **1.0.145**

## Credits

- **Original Author**: Difficult Hunting on Steam Workshop, ID `2750789266`
- **Fixes**: Code cleanup, path normalization, bug fixes, and deprecated content handling
