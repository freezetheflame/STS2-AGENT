using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace STS2Agent.Scripts;

/// <summary>
/// 从 CombatState 提取完整的博弈状态。
/// 全部使用反射访问属性，首次失败时打印类型的所有属性名（用于调试）。
/// </summary>
public static class GameStateExtractor
{
    // 记录已诊断过的类型，避免重复刷日志
    private static readonly ConcurrentDictionary<string, bool> Diagnosed = new();

    public static Dictionary<string, object?> ExtractCombatState(ICombatState combat)
    {
        var result = new Dictionary<string, object?>();

        try
        {
            result["player"] = ExtractPlayerState(combat);
            result["enemies"] = ExtractEnemiesState(combat);
            result["turn"] = GetPropValue(combat, "TurnNumber") ?? 0;
            result["floor"] = GetPropValue(combat, "FloorNum");
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"State extraction partial failure: {ex.Message}");
        }

        return result;
    }

    // ======== Player ========

    private static Dictionary<string, object?> ExtractPlayerState(ICombatState combat)
    {
        var state = new Dictionary<string, object?>();

        object? playerCreature = null;
        try
        {
            var all = GetPropValue(combat, "PlayerCreatures") as IEnumerable;
            if (all != null)
            {
                foreach (var c in all)
                {
                    if (c != null && (bool?)GetPropValue(c, "IsEnemy") == false)
                    {
                        playerCreature = c;
                        break;
                    }
                }
            }
        }
        catch { }

        if (playerCreature == null) return state;

        state["hp"] = GetPropValue(playerCreature, "CurrentHp") ?? 0;
        state["max_hp"] = GetPropValue(playerCreature, "MaxHp") ?? 0;
        state["block"] = GetPropValue(playerCreature, "Block") ?? 0;

        // PlayerCombatState
        object? pcs = null;
        try { pcs = GetPropValue(playerCreature, "PlayerCombatState"); }
        catch { }

        if (pcs != null)
        {
            state["energy"] = GetPropValue(pcs, "Energy");
            state["max_energy"] = GetPropValue(pcs, "MaxEnergy");
            state["hand"] = ExtractCardsFromPile(GetCardsFromPile(pcs, "Hand"), pcs);
            state["draw_pile_count"] = CardPileCount(GetPropValue(pcs, "DrawPile"));
            state["discard_pile_count"] = CardPileCount(GetPropValue(pcs, "DiscardPile"));
            state["exhaust_pile_count"] = CardPileCount(GetPropValue(pcs, "ExhaustPile"));
        }

        // 遗物
        try
        {
            var relics = GetPropValue(playerCreature, "Relics") as IEnumerable;
            if (relics != null)
                state["relics"] = relics.Cast<object>().Select(GetModelName).ToList();
        }
        catch { }

        // 药水
        try
        {
            var potions = GetPropValue(playerCreature, "Potions") as IEnumerable;
            if (potions != null)
                state["potions"] = potions.Cast<object>().Select(GetModelName).ToList();
        }
        catch { }

        // Buff
        try { ExtractPowers(playerCreature, state); } catch { }

        return state;
    }

    // ======== Enemies ========

    private static List<Dictionary<string, object?>> ExtractEnemiesState(ICombatState combat)
    {
        var enemies = new List<Dictionary<string, object?>>();

        try
        {
            var enemyList = GetPropValue(combat, "Enemies") as IEnumerable;
            if (enemyList == null) return enemies;

            foreach (var enemy in enemyList.Cast<object>())
            {
                if (enemy == null) continue;
                if ((bool?)GetPropValue(enemy, "IsAlive") != true) continue;

                var es = new Dictionary<string, object?>
                {
                    ["name"] = GetModelName(enemy),
                    ["hp"] = GetPropValue(enemy, "CurrentHp") ?? 0,
                    ["max_hp"] = GetPropValue(enemy, "MaxHp") ?? 0,
                    ["block"] = GetPropValue(enemy, "Block") ?? 0,
                };

                // 敌人意图
                var intentObj = GetPropValue(enemy, "Intent") ?? GetPropValue(enemy, "CurrentIntent") ?? GetPropValue(enemy, "Intents");
                if (intentObj != null)
                {
                    es["intent"] = new Dictionary<string, object?>
                    {
                        ["type"] = intentObj.GetType().Name,
                        ["damage"] = GetPropValue(intentObj, "Damage"),
                        ["hit_count"] = GetPropValue(intentObj, "HitCount"),
                        ["description"] = GetPropValue(intentObj, "Description")?.ToString(),
                    };
                }

                try { ExtractPowers(enemy, es); } catch { }
                enemies.Add(es);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"Enemies extraction error: {ex.Message}");
        }

        return enemies;
    }

    // ======== Powers ========

    private static void ExtractPowers(object creature, Dictionary<string, object?> target)
    {
        var powers = GetPropValue(creature, "Powers") as IEnumerable;
        if (powers == null) return;

        var list = powers.Cast<object>()
            .Select(pow => new Dictionary<string, object?>
            {
                ["name"] = GetModelName(pow),
                ["amount"] = GetPropValue(pow, "Amount"),
            })
            .Where(d => d["name"] is string)
            .ToList();

        if (list.Count > 0)
            target["powers"] = list;
    }

    // ======== Cards ========

    private static List<Dictionary<string, object?>>? ExtractCardsFromPile(List<object?>? cards, object? pcs)
    {
        if (cards == null) return null;

        return cards.Where(c => c != null).Select(card => card!).Select(card =>
        {
            var info = new Dictionary<string, object?>
            {
                ["name"] = GetModelName(card),
                ["cost"] = GetPropValue(card, "Cost"),
                ["upgraded"] = GetPropValue(card, "IsUpgraded") ?? GetPropValue(card, "Upgraded"),
            };

            if (pcs != null)
            {
                try
                {
                    var canPlayMethod = card.GetType().GetMethod("CanPlay");
                    if (canPlayMethod != null)
                    {
                        var parameters = canPlayMethod.GetParameters();
                        var args = new List<object?>();
                        foreach (var p in parameters)
                        {
                            if (p.ParameterType.IsAssignableFrom(pcs.GetType()))
                                args.Add(pcs);
                            else
                                args.Add(null);
                        }
                        info["playable"] = canPlayMethod.Invoke(card, args.ToArray());
                    }
                }
                catch { }
            }

            try
            {
                var desc = GetPropValue(card, "Description");
                if (desc != null)
                    info["description"] = desc.ToString();
            }
            catch { }

            return info;
        }).ToList();
    }

    /// <summary>从 PlayerCombatState 获取牌堆中的卡牌列表</summary>
    private static List<object?>? GetCardsFromPile(object? pcs, string pilePropName)
    {
        var pile = GetPropValue(pcs, pilePropName);
        if (pile == null) return null;

        try
        {
            // 尝试 Cards / AllCards 属性
            foreach (var cardsPropName in new[] { "Cards", "AllCards", "CardModels" })
            {
                var val = GetPropValue(pile, cardsPropName);
                if (val is IEnumerable en)
                    return en.Cast<object?>().ToList();
            }
            // 也尝试直接当 IEnumerable 遍历
            if (pile is IEnumerable pileEnum)
                return pileEnum.Cast<object?>().ToList();
        }
        catch { }
        return null;
    }

    private static int CardPileCount(object? pile)
    {
        if (pile == null) return 0;
        try
        {
            foreach (var propName in new[] { "Count", "Length", "Size" })
            {
                var val = GetPropValue(pile, propName);
                if (val is int i) return i;
            }
            foreach (var cardsPropName in new[] { "Cards", "AllCards" })
            {
                var cards = GetPropValue(pile, cardsPropName) as IEnumerable;
                if (cards != null) return cards.Cast<object>().Count();
            }
        }
        catch { }
        return 0;
    }

    // ======== Reflection Helpers ========

    private static string GetModelName(object? obj)
    {
        if (obj == null) return "null";
        try
        {
            foreach (var prop in new[] { "ModelId", "CardModelId", "ModelName", "Name", "Id", "CardId" })
            {
                var val = GetPropValue(obj, prop);
                if (val != null)
                {
                    var s = val.ToString();
                    if (!string.IsNullOrWhiteSpace(s) && s != obj.GetType().Name)
                        return s;
                }
            }
        }
        catch { }
        return obj.GetType().Name;
    }

    /// <summary>
    /// 反射获取属性值。首次对某个类型取属性失败时，打印该类型的全部 public 属性名（调试诊断）。
    /// </summary>
    private static object? GetPropValue(object? obj, string propName)
    {
        if (obj == null) return null;
        var type = obj.GetType();
        try
        {
            var prop = type.GetProperty(propName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (prop != null)
                return prop.GetValue(obj);

            var field = type.GetField(propName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (field != null)
                return field.GetValue(obj);

            // 没找到 —— 诊断输出（每种类型只输出一次）
            DiagnoseType(type, propName);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void DiagnoseType(Type type, string missingProp)
    {
        var key = $"{type.FullName}:{missingProp}";
        if (!Diagnosed.TryAdd(key, true)) return; // 已经输出过了

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .Select(f => f.Name)
            .OrderBy(n => n)
            .ToList();

        Entry.Logger.Info(
            $"[DIAG] Type={type.FullName} missing='{missingProp}' | " +
            $"Properties=[{string.Join(", ", props)}] | " +
            $"Fields=[{string.Join(", ", fields)}]");
    }
}
