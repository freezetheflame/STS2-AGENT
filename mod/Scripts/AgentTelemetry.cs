using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;

namespace STS2Agent.Scripts;

/// <summary>
/// 核心遥测模块 — 订阅游戏事件，提取状态，POST 到本地 Python 桥接服务器。
/// </summary>
public static class AgentTelemetry
{
    private const string BridgeUrl = "http://localhost:8765";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ---- 当前跑局的元数据 ----
    private static string _characterId = "unknown";
    private static int _ascension = 0;

    public static void Register()
    {
        // 跑局开始
        RitsuLibFramework.SubscribeLifecycle<RunStartedEvent>(OnRunStarted);
        // 跑局结束
        RitsuLibFramework.SubscribeLifecycle<RunEndedEvent>(OnRunEnded);

        // 回合开始 — 核心决策点
        RitsuLibFramework.SubscribeLifecycle<SideTurnStartedEvent>(OnSideTurnStarted);
        // 战斗开始
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(OnCombatStarting);
        // 战斗结束
        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(OnCombatEnded);

        // 房间进入（商店/火堆/宝箱等非战斗决策）
        RitsuLibFramework.SubscribeLifecycle<RoomEnteredEvent>(OnRoomEntered);
        // 奖励选取
        RitsuLibFramework.SubscribeLifecycle<RewardTakenEvent>(OnRewardTaken);

        Entry.Logger.Info("Telemetry hooks registered.");
    }

    // ======== 事件处理器 ========

    private static void OnRunStarted(RunStartedEvent evt)
    {
        _characterId = evt.RunState.CharacterId?.ToString() ?? "unknown";
        _ascension = evt.RunState.AscensionLevel;

        PostEvent("run_started", new Dictionary<string, object?>
        {
            ["character"] = _characterId,
            ["ascension"] = _ascension,
            ["is_daily"] = evt.IsDaily,
            ["is_multiplayer"] = evt.IsMultiplayer,
            ["seed"] = evt.RunState.Seed,
        });
    }

    private static void OnRunEnded(RunEndedEvent evt)
    {
        PostEvent("run_ended", new Dictionary<string, object?>
        {
            ["is_victory"] = evt.IsVictory,
            ["is_abandoned"] = evt.IsAbandoned,
        });

        // 也发送完整的 SerializableRun 数据供离线分析
        try
        {
            Http.PostAsJsonAsync($"{BridgeUrl}/event",
                new { type = "run_history", payload = evt.Run },
                JsonOpts).FireAndForget();
        }
        catch { /* 不影响游戏 */ }
    }

    private static void OnCombatStarting(CombatStartingEvent evt)
    {
        PostEvent("combat_starting", new Dictionary<string, object?>
        {
            ["room_type"] = "combat",
        });
    }

    private static void OnCombatEnded(CombatEndedEvent evt)
    {
        // CombatEndedEvent 触发时无法直接区分胜利/失败，
        // CombatVictoryEvent 是独立事件，已由 OnCombatVictory 处理
        PostEvent("combat_ended", new Dictionary<string, object?>
        {
            ["is_victory"] = false,  // 胜利时 CombatVictoryEvent 先于 CombatEndedEvent 触发
        });
    }

    /// <summary>
    /// ★ 核心决策点：每次回合开始时，提取完整战斗状态发送给 AI。
    /// 只在玩家回合时发送（敌方回合的决策由 AI 在玩家回合预判）。
    /// </summary>
    private static void OnSideTurnStarted(SideTurnStartedEvent evt)
    {
        // 只处理玩家回合
        if (evt.Side != CombatSide.Player)
            return;

        var combatState = evt.CombatState;

        if (combatState == null)
            return;

        var state = GameStateExtractor.ExtractCombatState(combatState);
        state["character"] = _characterId;
        state["ascension"] = _ascension;
        state["event_type"] = "turn_decision";

        PostEvent("turn_decision", state);
    }

    private static void OnRoomEntered(RoomEnteredEvent evt)
    {
        var room = evt.Room;
        var roomType = room?.GetType().Name ?? "unknown";

        // 非战斗房间也可能需要决策
        PostEvent("room_entered", new Dictionary<string, object?>
        {
            ["room_type"] = roomType,
            ["run_state_available"] = evt.RunState != null,
        });
    }

    private static void OnRewardTaken(RewardTakenEvent evt)
    {
        PostEvent("reward_taken", new Dictionary<string, object?>
        {
            ["reward_type"] = evt.Reward?.GetType().Name ?? "unknown",
        });
    }

    // ======== HTTP 发送 ========

    private static void PostEvent(string eventType, Dictionary<string, object?> payload)
    {
        try
        {
            Http.PostAsJsonAsync($"{BridgeUrl}/event",
                new { type = eventType, payload },
                JsonOpts).FireAndForget();
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"Failed to post event '{eventType}': {ex.Message}");
        }
    }
}

/// <summary>
/// 让异步 Task 不产生 compiler warning 的辅助扩展。
/// </summary>
internal static class TaskExtensions
{
    public static async void FireAndForget(this Task task)
    {
        try { await task; }
        catch { /* intentional fire-and-forget */ }
    }
}
