# PROJECT KNOWLEDGE BASE

**生成时间:** 2026-06-24
**项目:** 高难狩猎修复版 (Difficult Hunting - Fixed)
**GitHub:** https://github.com/High-cla/DifficultHunting-Fixed
**Steam Workshop ID:** 2750789266
**游戏版本:** 1.1.5.0 | **修复版本:** 1.0.145-fixed

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
├── alienitems.xml               # 所有武器/弹药/物品（单体，~2791行，235KB）
├── AfflictionsDH.xml            # 自定义Buff（DHpowerUP）
├── Missions.xml                 # 20+巢穴/Boss任务
├── OutpostEvents.xml            # 猎人电台脚本事件
├── SimplifiedChinese.xml        # 简体中文本地化
├── Typhon2_WreckedDH.sub        # 潜艇沉船
├── AGENTS.md                    # 本文件
└── README.md                    # 中英双语说明
```

## 入口点

`filelist.xml` — 模组清单，使用 `<contentpackage>` 根元素，注册71项资产：
- **57个** `<Character>` → `Characters/<Name>/<Name>.xml`（新增Alienbot, Carrierex3）
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

## 已修复问题

| 问题 | 位置 | 状态 |
|------|------|------|
| 损坏路径 `%ModDir:高难狩猎%/` | `alienitems.xml:2275,2342,2409` | ✅ 已修复 |
| 错误根元素 `<Randomevents>` | `OutpostEvents.xml:2` | ✅ 已修复 |
| 自引用EventSet | `OutpostEvents.xml:256-258` | ✅ 已修复 |
| 废弃角色未注册 | `Alienbot`, `Carrierex3` | ✅ 已注册 |
| 无 `Animations/` 目录 | `HuskSubspecies` | ✅ 已创建 |
| 孤主动画 | `Animation/` 目录 (4个XML) | ✅ 已删除 |
| 空本地化占位符 | `SimplifiedChinese.xml` (5处 `<!---->`) | ✅ 已填充 |
| 测试物品遗留 | `GuardShieldedpistol22`（名称含"测试"） | ✅ 已移除 |
| 死代码 | `alienitems.xml:580-582` 注释SpawnItem | ✅ 已清除 |
| XMLSpy残留注释 | 所有XML文件 (43处) | ✅ 已清理 |
| 空EventSprites | `OutpostEvents.xml` | ✅ 已清理 |
| 中文文件名 | 3个OGG文件 | ❌ 待处理 |
| NPC重复物品 | `rageGatling2`等4个武器副本 | ❌ 待处理 |
| 任务名称重复 | `Husk_prowler10DH` / `Husk_chimera10DH` | ❌ 待处理 |
| 目录结构问题 | `Guardianrepairbot` / `Portalguardian` | ❌ 待处理 |
| 命名后缀混乱 | 全局 (4种后缀模式) | ❌ 待处理 |

## 文件统计

| 类型 | 数量 | 用途 |
|------|------|------|
| `.xml` | 278 | 内容定义（删除了4个孤主动画XML + 清理后） |
| `.png` | 38 | 贴图精灵 |
| `.ogg` | 24 | 音效（含3个中文名未处理） |
| `.sub` | 1 | 沉船 |
| `.md` | 2 | AGENTS.md + README.md |
| **总计** | **378** | **（原383 - 5删除 + 2新增 = 380文件 + 2个.git隐藏 = 378统计）** |

## 命令

```bash
git status          # 查看当前修改
git diff            # 查看修改内容
git add . && git commit -m "feat: ..."  # 提交
git push            # 推送到 GitHub
```

**模组安装:** 复制 `D:\steam\steamapps\common\Barotrauma\LocalMods\高难狩猎修复版` 到游戏LocalMods目录
**Steam创意工坊原始版:** ID `2750789266`
**GitHub仓库:** https://github.com/High-cla/DifficultHunting-Fixed
