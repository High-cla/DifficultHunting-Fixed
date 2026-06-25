# 画皮增强 高难狩猎 — 模组知识库

**模组名**: 画皮增强 高难狩猎 (Husk Enhancement Hard Hunt)  
**Steam Workshop ID**: 2914921497  
**版本**: 1.0.15 | **游戏版本**: 1.4.4.1  

## 概述

Barotrauma 高难度狩猎模组。纯 XML 内容包（非 Lua 模组），为游戏添加腐化（Husk）系 Boss 生物、高难狩猎任务群、事件密度覆写、前哨站任务 NPC 及防守模式。

## 目录结构

```
画皮增强 高难狩猎/
├── filelist.xml                    # 模组清单（入口）
├── Missions.xml                    # 任务定义（巢穴/狩猎/深渊/防守）
├── EventManagerSettings.xml        # 事件难度覆写（6 档阈值）
├── OutpostEvents.xml               # 前哨站剧本事件（NPC 对话/发放任务）
├── OutpostGenerationNpcs.xml       # 任务 NPC 定义
├── randomevents_campaign.xml       # 战役随机遭遇覆写
├── SimplifiedChinese.xml           # 简体中文本地化
└── Characters/
    ├── Endwormhusk2/               # 腐化末日蠕虫（7000 HP, Boss 血条）
    └── Charybdishusk2/             # 腐化噬海女妖（8500 HP, 吸附+召唤机制）
```

## 查询指引

| 需求 | 文件 | 备注 |
|------|------|------|
| 模组元信息/依赖 | `filelist.xml` | 同文件可添加新内容引用 |
| 任务种类/奖励 | `Missions.xml` | Nest/Monster/OutpostNest 三类 |
| 怪物生成率 | `EventManagerSettings.xml` | EventCooldown：30s→5s(地狱) |
| 全境遭遇覆写 | `randomevents_campaign.xml` | commonness=1000000 确保高频刷怪 |
| NPC 对话/任务链 | `OutpostEvents.xml` | 4 个分支对话事件 |
| 生物属性/AI | `Characters/*/*.xml` | CombatStrength=10000 |
| 文本/翻译 | `SimplifiedChinese.xml` | 任务描述/事件对话 |

## 约定

- **无 Lua 脚本**：纯 XML 覆写，不依赖 Luatrauma
- **跨模组声音依赖**：`%ModDir:Improved Husks%` — 需安装 Improved Husks 模组才有音效
- **事件 commonness**：模组使用极值（1000000）确保事件必然触发
- **怪物难度**：Boss 类 CombatStrength=10000，Vitality=7000~8500
- **NPC 全技能 100**：任务发放 NPC 配置为全能船长

## 禁止模式

- **勿编辑 MinLevelDifficulty/MaxLevelDifficulty**：EventManagerSettings.xml 中的区域边界值（L16），修改会破坏生物生成区域
- **勿降低 DefaultEventThreshold/EventThresholdIncrease**：会导致无事件生成

## 已知问题

- `OutpostEvents.xml` 第 485 行待办：计划添加更多前哨站专属事件集
- 军/研/矿/城市四种前哨站事件仅 50% 概率触发（TODO）
- 4 个任务 NPC 事件结构高度重复（DifficultHuntinghusk1~4），有待抽象
