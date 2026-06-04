using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace STS2Agent.Scripts;

/// <summary>
/// 从 CombatState 提取完整的博弈状态。
/// 全部使用反射访问属性，避免编译时类型层级不匹配。
/// </summary>
public static class GameStateExtractor
{
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

        // 找到玩家
        object? playerCreature = null;
        try
        {
            var all = GetPropValue(combat, "PlayerCreatures") as System.Collections.IEnumerable;
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

        // 基础属性
        state["hp"] = GetPropValue(playerCreature, "CurrentHp") ?? 0;
        state["max_hp"] = GetPropValue(playerCreature, "MaxHp") ?? 0;
        state["block"] = GetPropValue(playerCreature, "Block") ?? 0;

        // PlayerCombatState（战斗内数据）
        object? pcs = null;
        try { pcs = GetPropValue(playerCreature, "PlayerCombatState"); }
        catch { }

        if (pcs != null)
        {
            state["energy"] = GetPropValue(pcs, "Energy") ?? 0;
            state["max_energy"] = GetPropValue(pcs, "MaxEnergy") ?? 3;

            state["hand"] = ExtractCardsFromPile(GetCardsFromPileObj(GetPropValue(pcs, "Hand")), pcs);
            state["draw_pile_count"] = CardPileCount(GetPropValue(pcs, "DrawPile"));
            state["discard_pile_count"] = CardPileCount(GetPropValue(pcs, "DiscardPile"));
            state["exhaust_pile_count"] = CardPileCount(GetPropValue(pcs, "ExhaustPile"));
        }

        // 遗物
        try
        {
            var relics = GetPropValue(playerCreature, "Relics") as System.Collections.IEnumerable;
            if (relics != null)
                state["relics"] = relics.Cast<object>().Select(GetModelName).ToList();
        }
        catch { }

        // 药水
        try
        {
            var potions = GetPropValue(playerCreature, "Potions") as System.Collections.IEnumerable;
            if (potions != null)
                state["potions"] = potions.Cast<object>().Select(GetModelName).ToList();
        }
        catch { }

        // Buff
        try
        {
            ExtractPowers(playerCreature, state);
        }
        catch { }

        return state;
    }

    // ======== Enemies ========

    private static List<Dictionary<string, object?>> ExtractEnemiesState(ICombatState combat)
    {
        var enemies = new List<Dictionary<string, object?>>();

        try
        {
            var enemyList = GetPropValue(combat, "Enemies") as System.Collections.IEnumerable;
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
        var powers = GetPropValue(creature, "Powers") as System.Collections.IEnumerable;
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

            // playable
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

            // description
            try
            {
                var desc = GetPropValue(card, "Description");
                if (desc != null)
                {
                    var toString = desc.GetType().GetMethod("ToString", Type.EmptyTypes);
                    if (toString != null)
                        info["description"] = toString.Invoke(desc, null);
                }
            }
            catch { }

            return info;
        }).ToList();
    }

    private static List<object?>? GetCardsFromPileObj(object? pile)
    {
        if (pile == null) return null;
        try
        {
            // 尝试 Cards / AllCards 属性
            foreach (var propName in new[] { "Cards", "AllCards" })
            {
                var val = GetPropValue(pile, propName);
                if (val is System.Collections.IEnumerable en)
                    return en.Cast<object>().ToList();
            }
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
            // fallback: try Cards property
            foreach (var propName in new[] { "Cards", "AllCards" })
            {
                var cards = GetPropValue(pile, propName) as System.Collections.IEnumerable;
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
            // 尝试 ModelId, CardModelId, ModelName, Name
            foreach (var prop in new[] { "ModelId", "CardModelId", "ModelName", "Name", "Id" })
            {
                var val = GetPropValue(obj, prop);
                if (val != null) return val.ToString() ?? obj.GetType().Name;
            }
        }
        catch { }
        return obj.GetType().Name;
    }

    private static object? GetPropValue(object? obj, string propName)
    {
        if (obj == null) return null;
        try
        {
            var prop = obj.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (prop != null)
                return prop.GetValue(obj);

            // 尝试字段
            var field = obj.GetType().GetField(propName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (field != null)
                return field.GetValue(obj);

            return null;
        }
        catch
        {
            return null;
        }
    }
}
