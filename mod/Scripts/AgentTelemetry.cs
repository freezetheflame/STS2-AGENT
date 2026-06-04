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
        // 战斗开始/结束
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(OnCombatStarting);
        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(OnCombatEnded);

        // 房间进入/离开
        RitsuLibFramework.SubscribeLifecycle<RoomEnteringEvent>(OnRoomEntering);
        RitsuLibFramework.SubscribeLifecycle<RoomEnteredEvent>(OnRoomEntered);
        RitsuLibFramework.SubscribeLifecycle<RoomExitedEvent>(OnRoomExited);

        // 奖励界面 — 战后奖励/宝箱
        RitsuLibFramework.SubscribeLifecycle<RewardsScreenContinuingEvent>(OnRewardsScreen);

        // 奖励选取
        RitsuLibFramework.SubscribeLifecycle<RewardTakenEvent>(OnRewardTaken);

        // Act 切换
        RitsuLibFramework.SubscribeLifecycle<ActEnteredEvent>(OnActEntered);

        // 关键数据追踪
        RitsuLibFramework.SubscribeLifecycle<GoldGainedEvent>(OnGoldGained);
        RitsuLibFramework.SubscribeLifecycle<GoldLostEvent>(OnGoldLost);
        RitsuLibFramework.SubscribeLifecycle<RelicObtainedEvent>(OnRelicObtained);
        RitsuLibFramework.SubscribeLifecycle<CardPlayedEvent>(OnCardPlayed);

        Entry.Logger.Info("Telemetry hooks registered (full game flow).");
    }

    // ======== 事件处理器 ========

    private static void OnRunStarted(RunStartedEvent evt)
    {
        var rs = evt.RunState;

        // 尝试多个可能的属性名获取角色 ID
        _characterId = TryGetPropStr(rs, "CharacterId", "Character", "CharacterName", "CharId", "PlayerClass", "Class") ?? "unknown";

        // 如果 RunState 取不到，从 Creature.Player.Character 尝试
        if (_characterId == "unknown")
        {
            try
            {
                var creatures = GetPropVal(rs, "PlayerCreatures") as System.Collections.IEnumerable;
                if (creatures != null)
                {
                    foreach (var c in creatures)
                    {
                        if (c != null && (bool?)GetPropVal(c, "IsEnemy") == false)
                        {
                            var pl = GetPropVal(c, "Player");
                            var ch = GetPropVal(pl, "Character");
                            if (ch != null)
                            {
                                var name = GetPropStr(ch, "ModelId") ?? GetPropStr(ch, "Name") ?? GetPropStr(ch, "Id");
                                if (!string.IsNullOrEmpty(name)) _characterId = name;
                            }
                            break;
                        }
                    }
                }
            }
            catch { }
        }
        _ascension = evt.RunState.AscensionLevel;

        PostEvent("run_started", new Dictionary<string, object?>
        {
            ["character"] = _characterId,
            ["ascension"] = _ascension,
            ["is_daily"] = evt.IsDaily,
            ["is_multiplayer"] = evt.IsMultiplayer,
            ["seed"] = GetPropVal(rs, "Seed"),
        });
    }

    /// <summary>反射获取属性值，返回 object?</summary>
    private static object? GetPropVal(object? obj, string name)
    {
        if (obj == null) return null;
        try
        {
            var prop = obj.GetType().GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return prop?.GetValue(obj);
        }
        catch { return null; }
    }

    /// <summary>尝试多个属性名获取值（用于反射探测不稳定的属性名）。</summary>
    private static object? TryGetPropVal(object? obj, params string[] propNames)
    {
        if (obj == null) return null;
        foreach (var name in propNames)
        {
            var val = GetPropVal(obj, name);
            if (val != null) return val;
        }
        return null;
    }

    /// <summary>反射获取属性值，返回 string?</summary>
    private static string? GetPropStr(object? obj, string name)
    {
        return GetPropVal(obj, name)?.ToString();
    }

    /// <summary>尝试多个属性名获取字符串值。</summary>
    private static string? TryGetPropStr(object? obj, params string[] propNames)
    {
        return TryGetPropVal(obj, propNames)?.ToString();
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

        // 从提取的状态中获取 character（如果 AgentTelemetry 记录的还是 unknown）
        if (_characterId == "unknown" && state.TryGetValue("character", out var extractedChar) && extractedChar is string ec && ec != "unknown")
        {
            _characterId = ec;
        }

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

    // ======== 新增: 全流程事件处理器 ========

    /// <summary>房间正在进入 — 此时可以提取房间内状态</summary>
    private static void OnRoomEntering(RoomEnteringEvent evt)
    {
        var state = RoomStateExtractor.Extract(evt.Room, evt.RunState);

        // 附加上下文
        state["character"] = _characterId;
        state["ascension"] = _ascension;
        state["event_type"] = "room_decision";
        state["floor"] ??= TryGetPropVal(evt.RunState, "ActFloor", "TotalFloor");

        PostEvent("room_decision", state);
    }

    private static void OnRoomExited(RoomExitedEvent evt)
    {
        PostEvent("room_exited", new Dictionary<string, object?>
        {
            ["room_type"] = evt.Room?.GetType().Name ?? "unknown",
        });
    }

    /// <summary>奖励界面显示（战后/宝箱）— 提取可选奖励</summary>
    private static void OnRewardsScreen(RewardsScreenContinuingEvent evt)
    {
        var state = new Dictionary<string, object?>
        {
            ["character"] = _characterId,
            ["ascension"] = _ascension,
            ["event_type"] = "reward_decision",
            ["gold"] = TryGetPropVal(evt, "Gold", "CurrentGold", "PlayerGold"),
        };

        // 提取可选卡牌列表
        var cards = TryGetEnumerableProp(evt, "CardRewards", "Cards", "Rewards", "CardChoices");
        if (cards != null)
        {
            var cardList = new List<Dictionary<string, object?>>();
            foreach (var c in cards)
            {
                if (c == null) continue;
                cardList.Add(new Dictionary<string, object?>
                {
                    ["name"] = TryGetPropStr(c, "ModelId", "Name", "CardId"),
                    ["cost"] = TryGetPropVal(c, "Cost", "EnergyCost"),
                    ["type"] = TryGetPropStr(c, "CardType", "Type"),
                    ["rarity"] = TryGetPropStr(c, "Rarity", "CardRarity"),
                    ["upgraded"] = TryGetPropVal(c, "IsUpgraded", "Upgraded"),
                });
            }
            state["card_choices"] = cardList;
        }

        // 可选遗物
        var relics = TryGetEnumerableProp(evt, "RelicRewards", "Relics", "BossRelics");
        if (relics != null)
        {
            var relicList = new List<string?>();
            foreach (var r in relics) { if (r != null) relicList.Add(TryGetPropStr(r, "ModelId", "Name", "Id")); }
            state["relic_choices"] = relicList;
        }

        PostEvent("reward_decision", state);
    }

    private static void OnActEntered(ActEnteredEvent evt)
    {
        PostEvent("act_entered", new Dictionary<string, object?>
        {
            ["act"] = TryGetPropVal(evt, "Act", "ActNum", "ActIndex"),
        });
    }

    private static void OnGoldGained(GoldGainedEvent evt)
    {
        PostEvent("gold_changed", new Dictionary<string, object?>
        {
            ["delta"] = GetPropVal(evt, "Amount"),
            ["character"] = _characterId,
        });
    }

    private static void OnGoldLost(GoldLostEvent evt)
    {
        var amount = GetPropVal(evt, "Amount");
        PostEvent("gold_changed", new Dictionary<string, object?>
        {
            ["delta"] = amount != null ? -(dynamic)amount : null,
            ["character"] = _characterId,
        });
    }

    private static void OnRelicObtained(RelicObtainedEvent evt)
    {
        var relic = GetPropVal(evt, "Relic");
        PostEvent("relic_obtained", new Dictionary<string, object?>
        {
            ["relic"] = TryGetPropStr(relic, "ModelId", "Name", "Id"),
            ["character"] = _characterId,
        });
    }

    private static void OnCardPlayed(CardPlayedEvent evt)
    {
        var card = GetPropVal(evt, "Card");
        PostEvent("card_played", new Dictionary<string, object?>
        {
            ["card"] = TryGetPropStr(card, "ModelId", "Name", "CardId"),
            ["cost_paid"] = TryGetPropVal(evt, "CostPaid", "EnergyCost"),
        });
    }

    // ======== 辅助反射方法 ========

    private static System.Collections.IEnumerable? TryGetEnumerableProp(object? obj, params string[] names)
    {
        if (obj == null) return null;
        foreach (var name in names)
        {
            var val = GetPropVal(obj, name);
            if (val is System.Collections.IEnumerable en) return en;
        }
        return null;
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
