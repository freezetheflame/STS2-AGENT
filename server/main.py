"""
STS2 AI Agent — 桥接服务器

接收游戏 mod 发送的游戏状态，缓存并提供查询接口。
后续将集成 LLM 策略引擎。

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

# --- 状态缓存 ---
latest_state: dict | None = None
event_history: deque[dict] = deque(maxlen=500)  # 最近 500 条事件
run_history: list[dict] = []  # 本局所有关键事件
current_run_started: float | None = None

# 日志目录
LOG_DIR = Path(__file__).parent.parent / "logs"
LOG_DIR.mkdir(exist_ok=True)


@app.post("/event")
async def receive_event(request: Request):
    """接收游戏 mod 发来的事件"""
    global latest_state, current_run_started

    try:
        body = await request.json()
    except Exception:
        return JSONResponse({"error": "Invalid JSON"}, status_code=400)

    event_type = body.get("type", "unknown")
    payload = body.get("payload", {})

    # 打时间戳
    payload["_server_time"] = time.time()

    event = {"type": event_type, "payload": payload, "timestamp": time.time()}
    event_history.append(event)

    # 关键事件持久化
    if event_type in (
        "run_started", "run_ended", "combat_starting", "combat_ended",
        "turn_decision", "reward_taken", "room_entered",
    ):
        run_history.append(event)

    # 决策事件设为当前状态
    if event_type == "turn_decision":
        latest_state = payload

    if event_type == "run_started":
        current_run_started = time.time()
        latest_state = None  # 清空旧状态

    # 终端实时输出
    _print_event(event_type, payload)

    # 持久化到日志文件
    _append_log(event)

    return {"status": "ok"}


@app.get("/state")
async def get_state():
    """返回最新的游戏状态"""
    return {
        "state": latest_state,
        "events_total": len(event_history),
        "run_duration_sec": time.time() - current_run_started if current_run_started else None,
    }


@app.get("/history")
async def get_history(limit: int = 20, offset: int = 0):
    """返回最近的事件历史"""
    events = list(event_history)[-(offset + limit):-offset] if offset else list(event_history)[-limit:]
    return {"events": events, "total": len(event_history)}


@app.get("/health")
async def health():
    """存活检查"""
    return {"status": "ok", "events_received": len(event_history)}


# --- 终端输出 ---

def _print_event(event_type: str, payload: dict):
    """在终端以可读格式打印事件"""
    if event_type == "turn_decision":
        player = payload.get("player", {})
        enemies = payload.get("enemies", [])
        hand = player.get("hand", [])

        print(f"\n{'='*60}")
        print(f"⚔️  回合决策 — HP:{player.get('hp')}/{player.get('max_hp')} "
              f"格挡:{player.get('block')} 能量:{player.get('energy')}")

        hand_names = [c.get("name", "?") for c in (hand or [])]
        print(f"🃏 手牌({len(hand_names)}): {', '.join(hand_names)}")

        print(f"📦 抽牌堆:{player.get('draw_pile_count', '?')}张  "
              f"弃牌堆:{player.get('discard_pile_count', '?')}张")

        for e in enemies:
            intent = e.get("intent", {})
            print(f"👾 {e.get('name')}: HP{e.get('hp')}/{e.get('max_hp')} "
                  f"格挡:{e.get('block')} "
                  f"意图:{intent.get('type','?')}"
                  + (f" 伤害:{intent.get('damage')}" if intent.get('damage') else ""))

        print(f"{'='*60}")

    elif event_type == "run_started":
        print(f"\n🎮 新跑局: {payload.get('character')} "
              f"进阶{payload.get('ascension')}层 "
              f"种子:{payload.get('seed')}")

    elif event_type == "run_ended":
        result = "🏆 胜利!" if payload.get("is_victory") else "💀 失败"
        print(f"\n{result}")

    elif event_type == "combat_starting":
        print(f"\n⚡ 战斗开始!")

    elif event_type == "combat_ended":
        print(f"✅ 战斗结束 (胜利: {payload.get('is_victory', '?')})")


# --- 日志持久化 ---

def _append_log(event: dict):
    """将事件追加到日志文件"""
    try:
        log_file = LOG_DIR / f"run_{time.strftime('%Y%m%d_%H%M%S')}.jsonl"
        with open(log_file, "a", encoding="utf-8") as f:
            f.write(json.dumps(event, ensure_ascii=False) + "\n")
    except Exception:
        pass


if __name__ == "__main__":
    print("""
    ╔══════════════════════════════════════╗
    ║   STS2 AI Agent — Bridge Server     ║
    ║   Listening on http://localhost:8765 ║
    ║   等待游戏 mod 连接...               ║
    ╚══════════════════════════════════════╝
    """)
    uvicorn.run(app, host="0.0.0.0", port=8765, log_level="info")
