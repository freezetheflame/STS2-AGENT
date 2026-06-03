using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace STS2Agent.Scripts;

/// <summary>
/// 从 CombatState 提取完整的博弈状态，序列化为 LLM 可读的 JSON。
/// 
/// 注意：此代码基于 RitsuLib 源码中观察到的 API 推断。
/// 实际编译时需要根据 STS2 的 runtime 类型调整。
/// 所有访问都包裹在 try-catch 中，避免因 API 差异导致游戏崩溃。
/// </summary>
public static class GameStateExtractor
{
    /// <summary>
    /// ★ 核心方法 — 从 CombatState 提取完整状态
    /// </summary>
    public static Dictionary<string, object?> ExtractCombatState(ICombatState combat)
    {
        var result = new Dictionary<string, object?>();

        try
        {
            // --- 玩家状态 ---
            var player = GetPlayer(combat);
            if (player != null)
            {
                result["player"] = ExtractPlayerState(player);
            }

            // --- 敌人状态 ---
            result["enemies"] = ExtractEnemiesState(combat);

            // --- 战斗元信息 ---
            result["turn"] = SafeGet(() => combat.CurrentTurn, 0);
            result["floor"] = SafeGet(() => (int?)combat.Act, null);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"State extraction partial failure: {ex.Message}");
        }

        return result;
    }

    // ======== Player State ========

    private static Dictionary<string, object?> ExtractPlayerState(Creature player)
    {
        var state = new Dictionary<string, object?>
        {
            ["hp"] = SafeGet(() => player.CurrentHp, 0),
            ["max_hp"] = SafeGet(() => player.MaxHp, 0),
            ["block"] = SafeGet(() => player.Block, 0),
        };

        // PlayerCombatState（包含能量、手牌等战斗内数据）
        if (player is Player p)
        {
            try
            {
                var pcs = p.PlayerCombatState;
                if (pcs != null)
                {
                    state["energy"] = SafeGet(() => pcs.Energy, 0);
                    state["energy_per_turn"] = SafeGet(() => pcs.EnergyPerTurn, 3);

                    // 手牌
                    state["hand"] = ExtractCardsFromPile(
                        SafeGet(() => pcs.Hand?.ToList(), null),
                        pcs);

                    // 抽牌堆（只发送卡名和数量）
                    var drawPile = SafeGet(() => pcs.DrawPile?.ToList(), null);
                    if (drawPile != null)
                    {
                        state["draw_pile_count"] = drawPile.Count;
                        state["draw_pile_cards"] = drawPile
                            .Select(c => SafeGet(() => c.ModelName, "unknown"))
                            .ToList();
                    }

                    // 弃牌堆
                    var discardPile = SafeGet(() => pcs.DiscardPile?.ToList(), null);
                    if (discardPile != null)
                    {
                        state["discard_pile_count"] = discardPile.Count;
                        state["discard_pile_cards"] = discardPile
                            .Select(c => SafeGet(() => c.ModelName, "unknown"))
                            .ToList();
                    }

                    // 消耗堆
                    var exhaustPile = SafeGet(() => pcs.ExhaustPile?.ToList(), null);
                    if (exhaustPile != null)
                    {
                        state["exhaust_pile_count"] = exhaustPile.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"PlayerCombatState extraction error: {ex.Message}");
            }

            // 遗物
            try
            {
                var relics = p.Relics;
                if (relics != null)
                {
                    state["relics"] = relics
                        .Select(r => SafeGet(() => r.ModelName, "unknown"))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"Relics extraction error: {ex.Message}");
            }

            // 药水
            try
            {
                var potions = p.Potions;
                if (potions != null)
                {
                    state["potions"] = potions
                        .Select(pot => SafeGet(() => pot.ModelName, "unknown"))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"Potions extraction error: {ex.Message}");
            }

            // Buff/Debuff（能力/状态）
            try
            {
                var powers = p.Powers;
                if (powers != null)
                {
                    state["powers"] = powers
                        .Select(pow => new Dictionary<string, object?>
                        {
                            ["name"] = SafeGet(() => pow.ModelName, "unknown"),
                            ["amount"] = SafeGet(() => pow.Amount, 0),
                        })
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"Powers extraction error: {ex.Message}");
            }
        }

        return state;
    }

    // ======== Enemy State ========

    private static List<Dictionary<string, object?>> ExtractEnemiesState(ICombatState combat)
    {
        var enemies = new List<Dictionary<string, object?>>();

        try
        {
            var enemyList = SafeGet(() => combat.Enemies?.ToList(), null);
            if (enemyList == null) return enemies;

            foreach (var enemy in enemyList)
            {
                if (!SafeGet(() => enemy.IsAlive, false)) continue;

                var enemyState = new Dictionary<string, object?>
                {
                    ["name"] = SafeGet(() => enemy.ModelName, "unknown"),
                    ["hp"] = SafeGet(() => enemy.CurrentHp, 0),
                    ["max_hp"] = SafeGet(() => enemy.MaxHp, 0),
                    ["block"] = SafeGet(() => enemy.Block, 0),
                };

                // 敌人意图
                try
                {
                    var intent = enemy.Intent;
                    if (intent != null)
                    {
                        enemyState["intent"] = new Dictionary<string, object?>
                        {
                            ["type"] = intent.GetType().Name,
                            ["damage"] = SafeGet(() => (int?)intent.Damage, null),
                            ["hit_count"] = SafeGet(() => (int?)intent.HitCount, null),
                            ["description"] = SafeGet(() => intent.Description, ""),
                        };
                    }
                }
                catch (Exception ex)
                {
                    Entry.Logger.Warn($"Intent extraction error for {enemy.ModelName}: {ex.Message}");
                }

                // 敌人 Buff
                try
                {
                    var powers = enemy.Powers;
                    if (powers != null && powers.Any())
                    {
                        enemyState["powers"] = powers
                            .Select(pow => new Dictionary<string, object?>
                            {
                                ["name"] = SafeGet(() => pow.ModelName, "unknown"),
                                ["amount"] = SafeGet(() => pow.Amount, 0),
                            })
                            .ToList();
                    }
                }
                catch { /* 可选 */ }

                enemies.Add(enemyState);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"Enemies extraction error: {ex.Message}");
        }

        return enemies;
    }

    // ======== Card Extraction ========

    private static List<Dictionary<string, object?>>? ExtractCardsFromPile(
        List<CardModel>? cards,
        PlayerCombatState? pcs)
    {
        if (cards == null) return null;

        return cards.Select(card =>
        {
            var info = new Dictionary<string, object?>
            {
                ["name"] = SafeGet(() => card.ModelName, "unknown"),
                ["cost"] = SafeGet(() => card.Cost, -1),
                ["upgraded"] = SafeGet(() => card.Upgraded, false),
                ["type"] = SafeGet(() => card.CardType?.ToString(), "unknown"),
            };

            // 如果卡牌可以被打出，标记是否可用
            if (pcs != null)
            {
                try
                {
                    info["playable"] = card.CanPlay(pcs);
                }
                catch { /* 可选 */ }
            }

            // 卡牌描述（如果有）
            try
            {
                var desc = card.Description;
                if (!string.IsNullOrEmpty(desc))
                    info["description"] = desc;
            }
            catch { /* 可选 */ }

            return info;
        }).ToList();
    }

    // ======== Helpers ========

    /// <summary>
    /// 从 CombatState 获取玩家 Creature（第一个非敌人）
    /// </summary>
    private static Creature? GetPlayer(ICombatState combat)
    {
        try
        {
            var all = combat.PlayerCreatures?.ToList();
            if (all != null)
            {
                return all.FirstOrDefault(c => !c.IsEnemy);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 安全地获取属性值，异常时返回默认值
    /// </summary>
    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }
}
