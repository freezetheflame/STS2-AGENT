# STS2 Agent Communication Protocol v1.0

## 概述

AI Agent 通过 `GET /state` 从桥接服务器获取当前游戏状态，返回统一 JSON 结构。
服务器收到每个游戏事件时更新内部缓存，Agent 拉取时返回当前快照。

## 统一响应结构

```json
{
  "decision_type": "combat_turn | room_enter | reward_select | map_navigate",
  "timestamp": 1780584790.99,
  "run": {
    "character": "CHARACTER.IRONCLAD",
    "ascension": 5,
    "floor": 8,
    "act": 1,
    "gold": 250
  },
  "player": {
    "hp": 44, "max_hp": 87,
    "block": 0,
    "energy": 3, "max_energy": 3,
    "hand": [
      {"name": "CARD.BASH", "cost": 2, "type": "Attack", "upgraded": false, "playable": true}
    ],
    "draw_pile_count": 18,
    "discard_pile_count": 0,
    "exhaust_pile_count": 0,
    "powers": [{"name": "POWER.STRENGTH_POWER", "amount": 2}],
    "relics": ["RELIC.BURNING_BLOOD"],
    "potions": ["POTION.FIRE_POTION"]
  },
  "enemies": [
    {
      "name": "MONSTER.BOWLBUG_ROCK",
      "hp": 38, "max_hp": 38, "block": 0,
      "intent": {"type": "Attack", "damage": 8, "hit_count": 1},
      "powers": []
    }
  ],
  "room": null,
  "rewards": null
}
```

## 字段语义

### decision_type
| 值 | 含义 | 需要关注的字段 |
|----|------|-------------|
| `combat_turn` | 玩家回合开始，需要打出卡牌 | player.hand, enemies, player.energy |
| `room_enter` | 进入非战斗房间 | room (商店/火堆/事件) |
| `reward_select` | 战后/宝箱奖励选择 | rewards |
| `map_navigate` | 地图选路 | run.floor, room (可选路径) |

### run — 跑局元数据
始终存在。`gold` 由 GoldGained/Lost 事件追踪。

### player — 玩家状态
`combat_turn` 时完整填充。其他决策类型时仅填充 hp/max_hp/relics/potions。

### enemies — 敌人列表
仅 `combat_turn` 时存在，其他场景为 `[]`。

### room — 房间状态
仅 `room_enter` 时存在，其他为 `null`。

```json
{
  "room_type": "MerchantRoom",
  "cards_for_sale": [
    {"name": "CARD.ANGER", "cost": 50, "rarity": "Common", "type": "Attack"}
  ],
  "relics_for_sale": [
    {"name": "RELIC.VAJRA", "cost": 150}
  ],
  "potions_for_sale": [],
  "remove_cost": 75,
  "can_rest": true,
  "can_upgrade": true,
  "heal_amount": 26,
  "event_name": null,
  "choices": null
}
```

### rewards — 奖励选项
仅 `reward_select` 时存在，其他为 `null`。

```json
{
  "card_choices": [
    {"name": "CARD.BLOODLETTING", "cost": 0, "type": "Skill", "rarity": "Common", "upgraded": false},
    {"name": "CARD.TWIN_STRIKE", "cost": 1, "type": "Attack", "rarity": "Common", "upgraded": false},
    {"name": "CARD.INFLAME", "cost": 1, "type": "Power", "rarity": "Uncommon", "upgraded": false}
  ],
  "relic_choices": [],
  "gold": 250
}
```

## 实现计划

1. 修改 `server/main.py`：`GET /state` 返回统一格式
2. 在服务器端维护 run 上下文（character/ascension/floor/gold）
3. 收到不同事件时更新对应字段，Agent 查询时返回完整快照
