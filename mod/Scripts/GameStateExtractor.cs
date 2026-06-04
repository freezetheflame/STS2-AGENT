using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace STS2Agent.Scripts;

/// <summary>
/// 从 CombatState 提取完整的博弈状态。
/// 全部使用反射访问属性，首次遇到新类型时打印其全部 public 属性/字段名（调试用）。
/// </summary>
public static class GameStateExtractor
{
    private static readonly ConcurrentDictionary<Type, bool> DumpedTypes = new();

    public static Dictionary<string, object?> ExtractCombatState(ICombatState combat)
    {
        DumpTypeOnce(combat);
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

        DumpTypeOnce(playerCreature);
        state["hp"] = GetPropValue(playerCreature, "CurrentHp") ?? 0;
        state["max_hp"] = GetPropValue(playerCreature, "MaxHp") ?? 0;
        state["block"] = GetPropValue(playerCreature, "Block") ?? 0;

        object? pcs = null;
        try { pcs = GetPropValue(playerCreature, "PlayerCombatState"); }
        catch { }

        if (pcs != null)
        {
            DumpTypeOnce(pcs);
            state["energy"] = GetPropValue(pcs, "Energy");
            state["max_energy"] = GetPropValue(pcs, "MaxEnergy");
            state["hand"] = ExtractCardsFromPile(GetCardsFromPile(pcs, "Hand"), pcs);

            var dp = GetPropValue(pcs, "DrawPile");
            DumpTypeOnce(dp);
            state["draw_pile_count"] = CardPileCount(dp);

            var discP = GetPropValue(pcs, "DiscardPile");
            state["discard_pile_count"] = CardPileCount(discP);

            var exP = GetPropValue(pcs, "ExhaustPile");
            state["exhaust_pile_count"] = CardPileCount(exP);
        }

        try
        {
            var relics = GetPropValue(playerCreature, "Relics") as IEnumerable;
            if (relics != null)
                state["relics"] = relics.Cast<object>().Select(GetModelName).ToList();
        }
        catch { }

        try
        {
            var potions = GetPropValue(playerCreature, "Potions") as IEnumerable;
            if (potions != null)
                state["potions"] = potions.Cast<object>().Select(GetModelName).ToList();
        }
        catch { }

        try { ExtractPowers(playerCreature, state); } catch { }

        return state;
    }

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
                DumpTypeOnce(enemy);
                if ((bool?)GetPropValue(enemy, "IsAlive") != true) continue;

                var es = new Dictionary<string, object?>
                {
                    ["name"] = GetModelName(enemy),
                    ["hp"] = GetPropValue(enemy, "CurrentHp") ?? 0,
                    ["max_hp"] = GetPropValue(enemy, "MaxHp") ?? 0,
                    ["block"] = GetPropValue(enemy, "Block") ?? 0,
                };

                var intentObj = GetPropValue(enemy, "Intent") ?? GetPropValue(enemy, "CurrentIntent");
                if (intentObj != null)
                {
                    DumpTypeOnce(intentObj);
                    es["intent"] = new Dictionary<string, object?>
                    {
                        ["type"] = intentObj.GetType().Name,
                        ["damage"] = GetPropValue(intentObj, "Damage"),
                        ["hit_count"] = GetPropValue(intentObj, "HitCount"),
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

    private static void ExtractPowers(object creature, Dictionary<string, object?> target)
    {
        var powers = GetPropValue(creature, "Powers") as IEnumerable;
        if (powers == null) return;

        var list = new List<Dictionary<string, object?>>();
        foreach (var pow in powers)
        {
            if (pow == null) continue;
            DumpTypeOnce(pow);
            list.Add(new Dictionary<string, object?>
            {
                ["name"] = GetModelName(pow),
                ["amount"] = GetPropValue(pow, "Amount"),
            });
        }
        if (list.Count > 0) target["powers"] = list;
    }

    private static List<Dictionary<string, object?>>? ExtractCardsFromPile(List<object?>? cards, object? pcs)
    {
        if (cards == null) return null;

        return cards.Where(c => c != null).Select(card => card!).Select(card =>
        {
            DumpTypeOnce(card);
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
                if (desc != null) info["description"] = desc.ToString();
            }
            catch { }

            return info;
        }).ToList();
    }

    private static List<object?>? GetCardsFromPile(object? pcs, string pilePropName)
    {
        var pile = GetPropValue(pcs, pilePropName);
        if (pile == null) return null;
        DumpTypeOnce(pile);

        try
        {
            foreach (var cp in new[] { "Cards", "AllCards", "CardModels" })
            {
                var val = GetPropValue(pile, cp);
                if (val is IEnumerable en) return en.Cast<object?>().ToList();
            }
            if (pile is IEnumerable pileEnum) return pileEnum.Cast<object?>().ToList();
        }
        catch { }
        return null;
    }

    private static int CardPileCount(object? pile)
    {
        if (pile == null) return 0;
        try
        {
            foreach (var pn in new[] { "Count", "Length", "Size" })
            {
                var val = GetPropValue(pile, pn);
                if (val is int i) return i;
            }
            foreach (var cp in new[] { "Cards", "AllCards" })
            {
                var cards = GetPropValue(pile, cp) as IEnumerable;
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
            foreach (var prop in new[] { "ModelId", "CardModelId", "ModelName", "Name", "Id", "CardId", "RelicId", "PotionId" })
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

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>首次遇到某类型时，打印其全部 public 属性/字段</summary>
    private static void DumpTypeOnce(object? obj)
    {
        if (obj == null) return;
        var type = obj.GetType();
        if (!DumpedTypes.TryAdd(type, true)) return;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .Select(p => p.Name).OrderBy(n => n).ToList();
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .Select(f => f.Name).OrderBy(n => n).ToList();

        Entry.Logger.Info(
            $"[DUMP] {type.FullName} | Props=[{string.Join(", ", props)}] | Fields=[{string.Join(", ", fields)}]");
    }
}
