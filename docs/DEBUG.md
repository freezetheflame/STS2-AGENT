# STS2 AI Agent — 调试日志

> 2026-06-04 · Phase 1: 数据导出

## 当前状态

✅ Mod 编译通过、加载成功、不崩溃  
✅ RitsuLib 生命周期事件正常触发  
✅ HTTP 桥接服务器收到回合决策事件  
✅ 玩家/敌人 HP、格挡、MaxHP 提取正常  
❌ 能量、手牌、抽牌堆、弃牌堆、敌人意图、种子 — 全部返回 null/空  

## 环境

| 项目 | 值 |
|------|-----|
| Godot | 4.6.3 .NET 版 |
| .NET SDK | 9.0 |
| RitsuLib | 0.4.8 (lib/0.106.1 variant) |
| STS2 | public-beta (Steam library: D:\SteamLibrary) |
| Mod 路径 | `mods/STS2Agent/` |

## 架构

```
STS2 游戏
  └─ STS2Agent mod (C#)
       ├─ Entry.cs           — 入口，注册 Harmony
       ├─ AgentTelemetry.cs  — 订阅 7 个 RitsuLib 生命周期事件
       └─ GameStateExtractor.cs — 反射提取游戏状态
            │
            ▼ POST /event (JSON)
       Python 桥接服务器 (FastAPI :8765)
            │
            ▼ 终端打印
```

## 踩过的坑

### 1. RitsuLib "Loader vs 完整 DLL"
Release 页面下载的 `STS2-RitsuLib.dll` 只有 24KB，是 Loader。真正的 API（`RitsuLibFramework`、`SubscribeLifecycle`、事件类型）在 `lib/<version>/STS2-RitsuLib.dll`。必须下载 `variant-pack.zip` 解压完整目录结构，然后 `.csproj` 引用 `lib/0.106.1/STS2-RitsuLib.dll`。

### 2. 编译期类型层级不匹配
猜测的属性名/类型层级几乎全错：

| 猜测 | 实际 |
|------|------|
| `RunState.Character.ModelName` | 编译失败。`Character` 不存在，`CharacterId` 也不在 `RunState` 上 |
| `ICombatState.CurrentTurn` | 不存在。有 `TurnNumber` 但可能只存在于具体类 |
| `PlayerCombatState.EnergyPerTurn` | 不存在。有 `MaxEnergy` |
| `CardPile.ToList()` | CardPile 不是 IEnumerable |
| `CardModel.ModelName` | 所有模型用 `ModelId`，没有 `ModelName` |
| `CardModel.Upgraded` | 是事件，不是属性。用 `IsUpgraded` |
| `CardModel.Description` | 是 `LocString`，不能直接当 `string` |
| `Creature.Intent` | 不存在于模型上 |
| `Player` 继承 `Creature` | 编译报 CS1503，不是父子类型 |
| `Creature` 继承 `AbstractModel` | 同上 |

### 3. 游戏安装路径
Steam 用了第二个库盘 `D:\SteamLibrary`，不是默认的 `D:\Steam`。通过 `C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf` 确认。

### 4. Godot Mono → .NET 改名
Godot 4 中 "Mono 版" 现在叫 ".NET 版"。下载页点第二个按钮（标注 C# support）。Steam/Epic/itch.io 版本不带 C#。

## 解决方案：反射 + 运行时诊断

全部改用 `System.Reflection` 访问属性，避免编译期类型依赖。同时加入 `[DUMP]` 诊断：首次遇到新类型时打印其全部 public 属性/字段名。

## 下一步

1. 运行 mod，从 `godot.log` 获取 `[DUMP]` 输出
2. 根据真实属性名修正 `GetPropValue` 调用
3. 修复: Energy, Cards, Intent, Seed, ModelName
4. Phase 1 完成 → Phase 2: LLM 集成
