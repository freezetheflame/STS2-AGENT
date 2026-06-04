using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace STS2Agent.Scripts;

/// <summary>
/// 从 CombatState 提取完整的博弈状态，序列化为 LLM 可读的 JSON。
/// 所有访问都包裹在 try-catch / SafeGet 中，避免因 API 差异导致游戏崩溃。
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
            result["turn"] = SafeGet(() => combat.TurnNumber, 0);
            result["floor"] = SafeGet(() => (int?)combat.FloorNum, null);
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
                    state["max_energy"] = SafeGet(() => pcs.MaxEnergy, 3);

                    // 手牌 — CardPile 可能不直接支持 LINQ，用 foreach 安全遍历
                    state["hand"] = ExtractCardsFromPile(
                        SafeGetCards(pcs.Hand),
                        pcs);

                    // 抽牌堆（只发送卡名和数量）
                    var drawPileCards = SafeGetCards(pcs.DrawPile);
                    if (drawPileCards != null)
                    {
                        state["draw_pile_count"] = drawPileCards.Count;
                        state["draw_pile_cards"] = drawPileCards
                            .Select(c => SafeGetCardName(c))
                            .ToList();
                    }

                    // 弃牌堆
                    var discardPileCards = SafeGetCards(pcs.DiscardPile);
                    if (discardPileCards != null)
                    {
                        state["discard_pile_count"] = discardPileCards.Count;
                        state["discard_pile_cards"] = discardPileCards
                            .Select(c => SafeGetCardName(c))
                            .ToList();
                    }

                    // 消耗堆
                    var exhaustPileCards = SafeGetCards(pcs.ExhaustPile);
                    if (exhaustPileCards != null)
                    {
                        state["exhaust_pile_count"] = exhaustPileCards.Count;
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
                var relics = SafeGet(() => p.Relics, null);
                if (relics != null)
                {
                    state["relics"] = relics
                        .Select(r => SafeGetModelId(r))
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
                var potions = SafeGet(() => p.Potions, null);
                if (potions != null)
                {
                    state["potions"] = potions
                        .Select(pot => SafeGetModelId(pot))
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
                ExtractPowers(p, state);
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
            var enemyList = SafeGet(() => combat.Enemies, null);
            if (enemyList == null) return enemies;

            foreach (var enemy in enemyList)
            {
                if (!SafeGet(() => enemy.IsAlive, false)) continue;

                var enemyState = new Dictionary<string, object?>
                {
                    ["name"] = SafeGetModelId(enemy),
                    ["hp"] = SafeGet(() => enemy.CurrentHp, 0),
                    ["max_hp"] = SafeGet(() => enemy.MaxHp, 0),
                    ["block"] = SafeGet(() => enemy.Block, 0),
                };

                // 敌人 Buff
                try
                {
                    ExtractPowers(enemy, enemyState);
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

    // ======== Powers Extraction ========

    private static void ExtractPowers(Creature creature, Dictionary<string, object?> target)
    {
        var powers = SafeGet(() => creature.Powers, null);
        if (powers == null || !powers.Any()) return;

        target["powers"] = powers
            .Select(pow => new Dictionary<string, object?>
            {
                ["name"] = SafeGetModelId(pow),
                ["amount"] = SafeGet(() => (int?)pow.Amount, null),
            })
            .Where(d => d["name"] is string)
            .ToList();
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
                ["name"] = SafeGetCardName(card),
                ["cost"] = SafeGet(() => (int?)card.Cost, null),
                ["upgraded"] = SafeGet(() => card.Upgraded, false),
            };

            // 如果卡牌可以被打出，标记是否可用
            if (pcs != null)
            {
                try
                {
                    // CanPlay(out UnplayableReason reason, out AbstractModel? preventer)
                    info["playable"] = card.CanPlay(out var _, out var _);
                }
                catch { /* 可选 */ }
            }

            // 卡牌描述
            try
            {
                var desc = card.Description;
                if (desc is LocString loc)
                    info["description"] = loc.ToString();
                else if (desc != null)
                    info["description"] = desc.ToString();
            }
            catch { /* 可选 */ }

            return info;
        }).ToList();
    }

    // ======== CardPile Helpers ========

    /// <summary>
    /// CardPile 不是标准 IEnumerable，用反射提取内部 Card 列表。
    /// 尝试通过 Cards 属性（IReadOnlyList&lt;CardModel&gt;）获取。
    /// </summary>
    private static List<CardModel>? SafeGetCards(CardPile? pile)
    {
        if (pile == null) return null;
        try
        {
            // 尝试通过反射获取 Cards 或 AllCards 属性
            foreach (var propName in new[] { "Cards", "AllCards", "GetCards" })
            {
                var prop = pile.GetType().GetProperty(propName);
                if (prop != null)
                {
                    var val = prop.GetValue(pile);
                    if (val is IEnumerable<CardModel> cardEnum)
                        return cardEnum.ToList();
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 安全获取卡牌名称（通过 ModelId 或反射）
    /// </summary>
    private static string SafeGetCardName(CardModel card)
    {
        try
        {
            var id = card.ModelId;
            if (id != null) return id.ToString() ?? "unknown";
        }
        catch { }

        // 回退：反射获取 CardModelId
        try
        {
            var prop = card.GetType().GetProperty("CardModelId");
            if (prop != null)
            {
                var val = prop.GetValue(card);
                return val?.ToString() ?? "unknown";
            }
        }
        catch { }

        return card.GetType().Name;
    }

    /// <summary>
    /// 安全获取任意模型的 ModelId 字符串
    /// </summary>
    private static string SafeGetModelId(AbstractModel model)
    {
        try
        {
            var id = model.ModelId;
            return id?.ToString() ?? model.GetType().Name;
        }
        catch
        {
            return model.GetType().Name;
        }
    }

    // ======== General Helpers ========

    /// <summary>
    /// 从 CombatState 获取玩家 Creature（第一个非敌人）
    /// </summary>
    private static Creature? GetPlayer(ICombatState combat)
    {
        try
        {
            var all = combat.PlayerCreatures;
            if (all != null)
            {
                foreach (var c in all)
                {
                    if (!c.IsEnemy) return c;
                }
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
