# Characters — 生物亚种定义

**生成时间:** 2026-06-24
**文件数:** ~351 XML + 38 PNG | **注册:** 55种在 `filelist.xml`

## 概述

模组核心内容目录。~60种生物亚种，55种已注册，~5种废弃/未引用。纯XML定义（hp/AI/肢体/物品栏/战利品/动画/布娃娃物理）。

## 标准目录结构

```
Characters/<Name>/
├── <Name>.xml          # 角色定义（入口，唯一必须文件）
├── Animations/         # 动画覆写XML（每个动画一个文件）
├── Ragdolls/           # 布娃娃物理XML + 肢体贴图PNG
└── *.png               # 角色/肢体贴图
```

## 去哪找

| 文件/位置 | 内容 |
|-----------|------|
| `<Species>.xml` | 生命值(hp)、肢体(limbs)、AI行为、物品栏(item inventory)、音效、战利品(loot) |
| `Animations/*.xml` | 动画覆写（行走、攻击、闲置等） |
| `Ragdolls/*.xml` | 布娃娃物理参数（质量、关节刚度、碰撞组） |
| `Ragdolls/*.png` | 肢体贴图（由角色主PNG引用或独立） |
| `*.png`（目录根） | 角色主贴图 |

## Conventions（Characters特有）

- **目录即物种**: 每个物种必须有自己的 `<Name>/` 子目录，名称与 `<Name>.xml` 和 `filelist.xml` 注册名一致
- **filelist注册**: 新增物种必须追加条目到 `filelist.xml` `<contentpackage>` 下，类型 `<Character>`
- **动画目录**: 每个物种至少应有 `Animations/` 子目录（除非完全复用父级动画）
- **贴图路径**: 角色XML内贴图引用使用 `%ModDir%/Characters/<Name>/<file>.png`

## Anti-Patterns（仅限本目录）

- **缺失Animations目录**: `HuskSubspecies` 有 `Ragdolls/` 但无 `Animations/`
- **根目录角色文件**: `GuardianrepairbotSubspecies.xml` 和 `PortalguardianSubspecies.xml` 无自己的子目录，文件散落于 `Characters/` 根
- **废弃但完整**: `Alienbot/` 和 `Carrierex3/` 完整定义（xml/png/animations/ragdolls全齐），但未注册 `filelist.xml`
- **命名后缀混乱**: 至少4种后缀模式混用 — `DH`（crawlernest10DH）、`aa`（shellexplosionaa）、`Subspecies`（GuardianrepairbotSubspecies）、纯数字/无后缀
