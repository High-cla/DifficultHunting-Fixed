# PROJECT KNOWLEDGE BASE

**生成时间:** 2026-06-24
**项目:** 高难狩猎 (Difficult Hunting) — Barotrauma 纯XML内容模组
**Steam Workshop ID:** 2750789266
**游戏版本:** 1.1.5.0 | **模组版本:** 1.0.145

## 概述

Barotrauma 高难度生物狩猎模组。纯XML内容包，零编译代码、零脚本。新增~60种生物亚种、自定义武器/物品、20+任务、猎人电台NPC事件系统。

## 目录结构

```
./
├── filelist.xml                 # 模组清单（入口点）
├── Characters/                  # ~60种生物亚种定义（核心内容，351文件）
├── Content/                     # 贴图、音效、UI资源
│   ├── Items/Weapons/           # 武器贴图+OGG音效
│   ├── Items/Diving/            # 潜水服贴图
│   ├── Items/Jobgear/Mechanic/  # 机械师外骨骼贴图
│   └── UI/                     # MainIconsAtlas.png
├── Animation/                   # 4个自定义动画XML（未在filelist注册）
├── alienitems.xml               # 所有武器/弹药/物品（单体，~2791行，235KB）
├── AfflictionsDH.xml            # 自定义Buff（DHpowerUP）
├── Missions.xml                 # 20+巢穴/Boss任务
├── OutpostEvents.xml            # 猎人电台脚本事件
├── SimplifiedChinese.xml        # 简体中文本地化
├── Typhon2_WreckedDH.sub        # 潜艇沉船
└── AGENTS.md                    # 本文件
```

## 入口点

`filelist.xml` — 模组清单，使用 `<contentpackage>` 根元素，注册69项资产：
- **55个** `<Character>` → `Characters/<Name>/<Name>.xml`
- **1个** `<Item>` → `alienitems.xml`
- **1个** `<Missions>` → `Missions.xml`
- **1个** `<RandomEvents>` → `OutpostEvents.xml`
- **1个** `<Text>` → `SimplifiedChinese.xml`
- **1个** `<Afflictions>` → `AfflictionsDH.xml`
- **1个** `<Wreck>` → `Typhon2_WreckedDH.sub`
- **3个** `<Other>` → OGG音效

## 去哪找

| 任务 | 位置 | 说明 |
|------|------|------|
| 新增生物 | `Characters/<Name>/` + 注册到 `filelist.xml` | 参考现有生物XML模式 |
| 新增物品/武器 | 追加到 `alienitems.xml` | 单体文件，注意合并冲突 |
| 新增任务 | 追加到 `Missions.xml` | 使用 `tags="DifficultHunting"` 支持猎人电台 |
| 新增本地化 | 追加到 `SimplifiedChinese.xml` | 点分隔键层次 |
| 修改事件/对话 | `OutpostEvents.xml` | 内联对话树 |
| 新增Buff | `AfflictionsDH.xml` | StatusEffect定义 |
| 更新贴图 | `Content/Items/` | PNG精灵 |
| 更新音效 | `Content/Items/Weapons/` | OGG音频 |

## 约定

- **DH后缀**: 所有自定义标识符以 `DH` 结尾（`DHpowerUP`, `crawlernest10DH`）
- **XML格式**: `<?xml version="1.0" encoding="utf-8"?>` 声明，2空格缩进，属性主导风格
- **命名**: 标识符 `snake_case`（`toothspikeEX`），文件 `PascalCase`（`AfflictionsDH.xml`），目录 `PascalCase`
- **覆写模式**: `<Override>` 根元素包裹新增内容
- **资源路径**: `%ModDir%/` 前缀用于模组本地资源，裸路径用于游戏本体资产
- **自闭合标签**: 叶子元素使用自闭合（`<Item identifier="..." />`）
- **全部中文**: 简体中文，无翻译覆写
- **生物目录结构**: 通常含 `Animations/` + `Ragdolls/` 子目录 + PNG贴图

## 反模式（本项目中禁止/待修复）

- **损坏的路径**: `alienitems.xml:2275,2342,2409` 使用 `%ModDir:高难狩猎%/`（非标准，应改为 `%ModDir%/`）
- **错误的根元素**: `OutpostEvents.xml:2` 使用 `<Randomevents>`（应为 `<RandomEvents>`）
- **自引用EventSet**: `OutpostEvents.xml:256-258` 内外层EventSet使用相同标识符，造成递归
- **废弃角色**: `Characters/Alienbot/` 和 `Characters/Carrierex3/` 有完整定义但未在filelist注册
- **分散的角色定义**: `PortalguardianSubspecies.xml` 在Characters根目录，但布娃娃/动画在子目录中
- **无目录的角色**: `GuardianrepairbotSubspecies.xml` 没有自己的子目录
- **无主动画目录**: `HuskSubspecies` 缺少 `Animations/` 子目录
- **孤主动画**: `Animation/` 目录的4个XML未被任何角色或filelist引用
- **路径风格不一致**: `alienitems.xml` 混用 `Content/` 和 `%ModDir%/Content/`
- **中文文件名**: `过热音.ogg` 等，在跨平台工具中会有问题
- **未完成本地化**: `SimplifiedChinese.xml` 有5个 `<!---->` 空占位符
- **推迟的内容**: `OutpostEvents.xml:4` 有 `<!--以后再说吧-->` 空EventSprites
- **测试物品遗留**: `GuardShieldedpistol22` 带有"（测试）"名称
- **临时变通**: `Mantis01.xml:48` 自标记为"temporary concealment during spawn"
- **死代码**: `alienitems.xml:580-582` 注释掉的 SpawnItem
- **NPC重复物品**: 4个NPC-only武器副本（`rageGatling2`等），90%复制粘贴
- **任务名称重复**: `Husk_prowler10DH` 和 `Husk_chimera10DH` 任务描述相同
- **XMLSpy残留**: 所有XML文件包含XMLSpy v2006/v2013自动生成注释
- **命名不一致**: 生物使用至少4种后缀模式（`DH`, `aa`, `Subspecies`, 数字）
- **无CI/测试**: 纯内容模组，零自动化基础设施

## 文件统计

| 类型 | 数量 | 用途 |
|------|------|------|
| `.xml` | 317 | 内容定义 |
| `.png` | 38 | 贴图精灵 |
| `.ogg` | 24 | 音效 |
| `.sub` | 1 | 沉船 |
| **总计** | **383** | |

## 命令

无。纯XML模组，无构建/测试命令。通过Steam创意工坊直接加载，ID: `2750789266`。
