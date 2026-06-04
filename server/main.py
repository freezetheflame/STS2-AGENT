"""
STS2 AI Agent — Bridge Server v2

接收游戏 mod 发送的游戏状态，维护统一协议格式的聚合状态，
提供标准化 GET /state 接口供 AI Agent 查询。

协议: data/protocol.md
启动: python server/main.py
默认监听: http://localhost:8765
"""

from __future__ import annotations

import json
import time
from collections import deque
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
import uvicorn

app = FastAPI(title="STS2 AI Agent Bridge")

# ============================================================
# 统一游戏状态 (protocol.md 格式)
# ============================================================
_unified_state: dict = {
    "decision_type": "idle",
    "timestamp": 0.0,
    "run": {
        "character": "unknown",
        "ascension": 0,
        "floor": None,
        "act": None,
        "gold": None,
    },
    "player": {
        "hp": None, "max_hp": None, "block": 0,
        "energy": None, "max_energy": None,
        "hand": [],
        "draw_pile_count": 0, "discard_pile_count": 0, "exhaust_pile_count": 0,
        "powers": [],
        "relics": [],
        "potions": [],
    },
    "enemies": [],
    "room": None,
    "rewards": None,
}

# 原始事件历史
event_history: deque[dict] = deque(maxlen=500)
current_run_started: float | None = None

LOG_DIR = Path(__file__).parent.parent / "logs"
LOG_DIR.mkdir(exist_ok=True)


def _merge_player(payload: dict):
    """从 turn_decision payload 更新 player 状态"""
    p = payload.get("player", {})
    if not p:
        return
    state = _unified_state["player"]
    for key in ("hp", "max_hp", "block", "energy", "max_energy",
                "draw_pile_count", "discard_pile_count", "exhaust_pile_count"):
        if key in p and p[key] is not None:
            state[key] = p[key]
    if p.get("hand") is not None:
        state["hand"] = p["hand"]
    if p.get("powers"):
        state["powers"] = p["powers"]
    if p.get("relics"):
        state["relics"] = p["relics"]
    if p.get("potions"):
        state["potions"] = p["potions"]


def _merge_enemies(payload: dict):
    if "enemies" in payload and payload["enemies"] is not None:
        _unified_state["enemies"] = payload["enemies"]


def _merge_room(payload: dict):
    """从 room_decision payload 更新 room 状态"""
    room = dict(payload)
    # 清理内部字段
    room.pop("character", None)
    room.pop("ascension", None)
    room.pop("event_type", None)
    room.pop("floor", None)
    room.pop("_server_time", None)
    room.pop("_room_props", None)
    room.pop("_note", None)
    _unified_state["room"] = room if room else None


def _merge_rewards(payload: dict):
    rewards = {}
    if "card_choices" in payload:
        rewards["card_choices"] = payload["card_choices"]
    if "relic_choices" in payload:
        rewards["relic_choices"] = payload["relic_choices"]
    if "gold" in payload:
        rewards["gold"] = payload["gold"]
    _unified_state["rewards"] = rewards if rewards else None


# ============================================================
# POST /event — 接收游戏事件
# ============================================================

@app.post("/event")
async def receive_event(request: Request):
    global current_run_started

    try:
        body = await request.json()
    except Exception:
        return JSONResponse({"error": "Invalid JSON"}, status_code=400)

    event_type = body.get("type", "unknown")
    payload = body.get("payload", {})

    payload["_server_time"] = time.time()
    event = {"type": event_type, "payload": payload, "timestamp": time.time()}
    event_history.append(event)

    # ---- 状态更新 ----
    _unified_state["timestamp"] = time.time()

    if event_type == "run_started":
        current_run_started = time.time()
        _unified_state["run"]["character"] = payload.get("character", "unknown")
        _unified_state["run"]["ascension"] = payload.get("ascension", 0)
        _unified_state["run"]["gold"] = payload.get("gold")
        _unified_state["decision_type"] = "idle"
        # 重置
        _unified_state["player"] = {
            "hp": None, "max_hp": None, "block": 0,
            "energy": None, "max_energy": None,
            "hand": [],
            "draw_pile_count": 0, "discard_pile_count": 0, "exhaust_pile_count": 0,
            "powers": [], "relics": [], "potions": [],
        }
        _unified_state["enemies"] = []
        _unified_state["room"] = None
        _unified_state["rewards"] = None

    elif event_type == "turn_decision":
        _unified_state["decision_type"] = "combat_turn"
        _merge_player(payload)
        _merge_enemies(payload)
        # 同步 run 上下文
        if payload.get("character") and payload["character"] != "unknown":
            _unified_state["run"]["character"] = payload["character"]
        if payload.get("floor") is not None:
            _unified_state["run"]["floor"] = payload["floor"]

    elif event_type == "room_decision":
        _unified_state["decision_type"] = "room_enter"
        _merge_room(payload)
        _unified_state["enemies"] = []
        _unified_state["rewards"] = None
        if payload.get("floor") is not None:
            _unified_state["run"]["floor"] = payload["floor"]
        # 更新 HP（从 combat 结束后带过来）
        hp = payload.get("hp") or payload.get("player", {}).get("hp")
        if hp is not None:
            _unified_state["player"]["hp"] = hp

    elif event_type == "reward_decision":
        _unified_state["decision_type"] = "reward_select"
        _merge_rewards(payload)
        _unified_state["enemies"] = []
        _unified_state["room"] = None
        if payload.get("gold") is not None:
            _unified_state["run"]["gold"] = payload["gold"]
        if payload.get("character") and payload["character"] != "unknown":
            _unified_state["run"]["character"] = payload["character"]

    elif event_type == "combat_starting":
        _unified_state["decision_type"] = "combat_turn"
        _unified_state["room"] = None
        _unified_state["rewards"] = None

    elif event_type == "combat_ended":
        _unified_state["enemies"] = []

    elif event_type == "run_ended":
        _unified_state["decision_type"] = "idle"

    elif event_type == "act_entered":
        _unified_state["run"]["act"] = payload.get("act")

    elif event_type == "gold_changed":
        delta = payload.get("delta")
        current = _unified_state["run"]["gold"]
        if current is not None and delta is not None:
            _unified_state["run"]["gold"] = current + delta
        elif delta is not None:
            _unified_state["run"]["gold"] = delta

    elif event_type == "relic_obtained":
        relic = payload.get("relic")
        if relic:
            relics = _unified_state["player"].get("relics") or []
            if relic not in relics:
                relics.append(relic)
            _unified_state["player"]["relics"] = relics

    # ---- 终端输出 ----
    _print_event(event_type, payload)

    # ---- 持久化 ----
    _append_log(event)

    return {"status": "ok"}


# ============================================================
# GET /state — 统一协议格式
# ============================================================

@app.get("/state")
async def get_state():
    return {
        **_unified_state,
        "events_total": len(event_history),
        "run_duration_sec": time.time() - current_run_started if current_run_started else None,
    }


@app.get("/history")
async def get_history(limit: int = 20, offset: int = 0):
    events = list(event_history)[-(offset + limit):-offset] if offset else list(event_history)[-limit:]
    return {"events": events, "total": len(event_history)}


@app.get("/health")
async def health():
    return {"status": "ok", "events_received": len(event_history)}


# ============================================================
# 终端输出
# ============================================================

def _print_event(event_type: str, payload: dict):
    if event_type == "turn_decision":
        player = payload.get("player", {})
        enemies = payload.get("enemies", [])
        hand = player.get("hand", [])

        print(f"\n{'='*60}")
        print(f"⚔️  回合决策 T{payload.get('turn','?')} — "
              f"HP:{player.get('hp')}/{player.get('max_hp')} "
              f"格挡:{player.get('block')} 能量:{player.get('energy')}/{player.get('max_energy')}")

        hand_names = [c.get("name", "?") for c in (hand or [])]
        print(f"🃏 手牌({len(hand_names)}): {', '.join(hand_names)}")
        print(f"📦 抽牌堆:{player.get('draw_pile_count','?')}张  "
              f"弃牌堆:{player.get('discard_pile_count','?')}张  "
              f"消耗堆:{player.get('exhaust_pile_count','?')}张")

        for e in enemies:
            intent = e.get("intent", {})
            dmg = intent.get("damage")
            print(f"👾 {e.get('name')}: HP{e.get('hp')}/{e.get('max_hp')} "
                  f"格挡:{e.get('block')} "
                  f"意图:{intent.get('type','?')}"
                  + (f" 💥{dmg}×{intent.get('hit_count',1)}" if dmg else ""))

        print(f"{'='*60}")

    elif event_type == "room_decision":
        rt = payload.get("room_type", "?")
        print(f"\n🚪 进入房间: {rt}")
        if payload.get("cards_for_sale"):
            print(f"   🛒 卡牌: {len(payload['cards_for_sale'])}张")
        if payload.get("relics_for_sale"):
            print(f"   🏺 遗物: {len(payload['relics_for_sale'])}个")
        if payload.get("heal_amount"):
            print(f"   💚 可回复: {payload['heal_amount']}HP")

    elif event_type == "reward_decision":
        cards = payload.get("card_choices", [])
        print(f"\n🎁 奖励选择: {len(cards)}张卡牌可选")
        for c in cards:
            print(f"   🃏 {c.get('name','?')} ({c.get('type','?')} {c.get('rarity','?')} 费用:{c.get('cost','?')})")

    elif event_type == "run_started":
        print(f"\n🎮 新跑局: {payload.get('character')} 进阶{payload.get('ascension')}层 种子:{payload.get('seed')}")

    elif event_type == "run_ended":
        result = "🏆 胜利!" if payload.get("is_victory") else "💀 失败"
        print(f"\n{result}")

    elif event_type == "combat_starting":
        print(f"\n⚡ 战斗开始!")

    elif event_type == "combat_ended":
        print(f"✅ 战斗结束 (胜利: {payload.get('is_victory', '?')})")

    elif event_type == "card_played":
        print(f"   🎴 出牌: {payload.get('card','?')}")

    elif event_type == "gold_changed":
        delta = payload.get("delta", 0)
        sign = "+" if delta and delta > 0 else ""
        print(f"   💰 金币 {sign}{delta}")

    elif event_type == "relic_obtained":
        print(f"   🏺 获得遗物: {payload.get('relic','?')}")


# ============================================================
# 日志
# ============================================================

def _append_log(event: dict):
    try:
        log_file = LOG_DIR / f"run_{time.strftime('%Y%m%d_%H%M%S')}.jsonl"
        with open(log_file, "a", encoding="utf-8") as f:
            f.write(json.dumps(event, ensure_ascii=False) + "\n")
    except Exception:
        pass


if __name__ == "__main__":
    print("""
    ╔══════════════════════════════════════╗
    ║   STS2 AI Agent — Bridge Server v2  ║
    ║   Listening on http://localhost:8765 ║
    ║   统一协议: GET /state               ║
    ║   等待游戏 mod 连接...               ║
    ╚══════════════════════════════════════╝
    """)
    uvicorn.run(app, host="0.0.0.0", port=8765, log_level="info")
