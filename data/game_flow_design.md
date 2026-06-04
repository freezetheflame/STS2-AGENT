# STS2 Agent — Game Flow State Design

## 完整游戏流程 & 需要提取的数据

### 1. 跑局开始 (RunStartedEvent) ✅
- character, ascension, seed, is_daily

### 2. 地图选路 (RoomEnteredEvent → MapRoom / 顶层地图)
需要: 当前层数、可选路线、前方房间类型、当前 HP

### 3. 商店 (RoomEnteredEvent → MerchantRoom)
需要: 金币, 待售卡牌(name/cost/rarity), 待售遗物(name/cost), 待售药水(name/cost), 删卡费用, 当前牌组列表

### 4. 火堆 (RoomEnteredEvent → RestSiteRoom)
需要: 当前 HP/maxHP, 可升级卡牌列表, 休息回复量

### 5. 事件房间 (RoomEnteredEvent → 事件相关类型)
需要: 事件名称, 描述文本, 可选选项, 当前 HP

### 6. 宝箱 (RoomEnteredEvent → TreasureRoom)
需要: 宝箱内遗物

### 7. 战斗 (CombatStartingEvent → SideTurnStartedEvent) ✅ 已完整
需要: 全部战斗状态（已实现）

### 8. 战后奖励 (RewardsScreenContinuingEvent)
需要: 可选卡牌(name/cost/type/rarity/效果), 可选遗物, 金币数量, 可选药水

### 9. 跑局结束 (RunEndedEvent) ✅
需要: is_victory, run_statistics

## 关键 RitsuLib 事件清单

已订阅:
- RunStartedEvent, RunEndedEvent
- CombatStartingEvent, CombatEndedEvent
- SideTurnStartedEvent
- RoomEnteredEvent
- RewardTakenEvent

待订阅:
- RoomEnteringEvent (提前获取房间数据)
- RewardsScreenContinuingEvent (奖励界面)
- ActEnteredEvent (Act 切换)
- GoldGainedEvent (金币追踪)
- CardPlayedEvent (打牌追踪，可选)
- RelicObtainedEvent (遗物追踪，可选)
- GameOverScreenCreatedEvent (死亡详情)

## 实现计划

1. 创建 `RoomStateExtractor.cs` — 根据房间类型提取对应数据
2. 创建 `RewardStateExtractor.cs` — 提取奖励选项
3. 更新 `AgentTelemetry.cs` — 订阅新事件 + 调用提取器
