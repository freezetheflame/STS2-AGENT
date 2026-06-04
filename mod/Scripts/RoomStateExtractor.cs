using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Agent.Scripts;

/// <summary>
/// 根据房间类型提取非战斗房间的状态数据。
/// 支持: 商店/火堆/事件/宝箱/地图
/// </summary>
public static class RoomStateExtractor
{
    private static readonly ConcurrentDictionary<string, bool> Diagnosed = new();

    public static Dictionary<string, object?> Extract(object room, object? runState)
    {
        var result = new Dictionary<string, object?>();
        if (room == null) return result;

        var roomType = room.GetType();
        var typeName = roomType.Name;
        result["room_type"] = typeName;

        try
        {
            // 通用: 提取 RunState 数据（floor 等）
            if (runState != null)
            {
                result["floor"] = TryGetProp(runState, "ActFloor", "TotalFloor", "FloorNum", "CurrentFloor");
            }

            // 根据房间类型分发
            if (typeName.Contains("Merchant") || typeName.Contains("Shop"))
                ExtractMerchant(room, result);
            else if (typeName.Contains("RestSite") || typeName.Contains("Campfire") || typeName.Contains("Rest"))
                ExtractRestSite(room, result);
            else if (typeName.Contains("Treasure") || typeName.Contains("Chest"))
                ExtractTreasure(room, result);
            else if (typeName.Contains("Event") || typeName.Contains("Unknown"))
                ExtractEventRoom(room, result);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"Room state extraction partial: {ex.Message}");
        }

        // 首次遇到新房间类型时 dump 属性
        var diagKey = $"room:{typeName}";
        if (Diagnosed.TryAdd(diagKey, true))
        {
            var props = roomType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                .Select(p => $"{p.Name}:{p.PropertyType.Name}").OrderBy(n => n).ToList();
            Entry.Logger.Info($"[ROOM_DIAG] {typeName} props=[{string.Join(", ", props)}]");
            result["_room_props"] = props;
        }

        return result;
    }

    // ======== Merchant ========

    private static void ExtractMerchant(object room, Dictionary<string, object?> result)
    {
        // 尝试获取金币
        result["gold"] = TryGetProp(room, "Gold", "CurrentGold", "PlayerGold");

        // 待售卡牌 — 可能存在多个属性名
        var cards = GetEnumerableProp(room, "Cards", "CardsForSale", "CardStock", "Items");
        if (cards != null)
            result["cards_for_sale"] = ExtractShopItems(cards);

        // 待售遗物
        var relics = GetEnumerableProp(room, "Relics", "RelicsForSale", "RelicStock");
        if (relics != null)
            result["relics_for_sale"] = ExtractShopItems(relics);

        // 待售药水
        var potions = GetEnumerableProp(room, "Potions", "PotionsForSale", "PotionStock");
        if (potions != null)
            result["potions_for_sale"] = ExtractShopItems(potions);

        // 删卡费用
        result["remove_cost"] = TryGetProp(room, "RemoveCost", "CardRemoveCost", "PurgeCost");

        // 当前牌组（可能在 runState 上）
        result["_note"] = "room_type=shop";
    }

    // ======== RestSite ========

    private static void ExtractRestSite(object room, Dictionary<string, object?> result)
    {
        // 回复量
        result["heal_amount"] = TryGetProp(room, "HealAmount", "RestHealAmount", "HealPercent");

        // 可选操作
        result["can_rest"] = TryGetProp(room, "CanRest", "RestAvailable");
        result["can_upgrade"] = TryGetProp(room, "CanUpgrade", "UpgradeAvailable", "CanSmith");
        result["can_smith"] = TryGetProp(room, "CanSmith", "SmithAvailable");
        result["can_toke"] = TryGetProp(room, "CanToke", "TokeAvailable");
        result["can_lift"] = TryGetProp(room, "CanLift", "LiftAvailable");
        result["can_dig"] = TryGetProp(room, "CanDig", "DigAvailable");
        result["can_recall"] = TryGetProp(room, "CanRecall", "RecallAvailable");

        result["_note"] = "room_type=rest_site";
    }

    // ======== Treasure ========

    private static void ExtractTreasure(object room, Dictionary<string, object?> result)
    {
        var relic = TryGetProp(room, "Relic", "ChestRelic", "RewardRelic");
        if (relic != null)
            result["relic"] = GetModelName(relic);

        // 也可能有金币
        result["gold"] = TryGetProp(room, "Gold", "GoldAmount");

        result["_note"] = "room_type=treasure";
    }

    // ======== Event ========

    private static void ExtractEventRoom(object room, Dictionary<string, object?> result)
    {
        // 事件名称
        result["event_name"] = GetModelName(room);

        // 事件描述
        result["description"] = TryGetPropStr(room, "Description", "Body", "Text", "EventText");

        // 可能的选择
        var choices = GetEnumerableProp(room, "Choices", "Options", "Actions", "SelectableOptions");
        if (choices != null)
            result["choices"] = choices.Cast<object>().Select(c => GetModelName(c)).ToList();

        result["_note"] = "room_type=event";
    }

    // ======== Helpers ========

    private static object? TryGetProp(object? obj, params string[] names)
    {
        if (obj == null) return null;
        foreach (var name in names)
        {
            try
            {
                var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (prop != null) return prop.GetValue(obj);
                var field = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (field != null) return field.GetValue(obj);
            }
            catch { }
        }
        return null;
    }

    private static string? TryGetPropStr(object? obj, params string[] names)
    {
        return TryGetProp(obj, names)?.ToString();
    }

    private static IEnumerable? GetEnumerableProp(object? obj, params string[] names)
    {
        var val = TryGetProp(obj, names);
        return val as IEnumerable;
    }

    private static string GetModelName(object? obj)
    {
        if (obj == null) return "null";
        try
        {
            foreach (var prop in new[] { "ModelId", "Name", "Id", "CardModelId" })
            {
                var val = TryGetProp(obj, prop);
                if (val != null) { var s = val.ToString(); if (!string.IsNullOrWhiteSpace(s) && s != obj.GetType().Name) return s; }
            }
        }
        catch { }
        return obj.GetType().Name;
    }

    private static List<Dictionary<string, object?>> ExtractShopItems(IEnumerable items)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var item in items)
        {
            if (item == null) continue;
            result.Add(new Dictionary<string, object?>
            {
                ["name"] = GetModelName(item),
                ["cost"] = TryGetProp(item, "Cost", "Price", "GoldCost"),
                ["rarity"] = TryGetPropStr(item, "Rarity", "CardRarity"),
                ["type"] = TryGetPropStr(item, "Type", "CardType", "ItemType"),
            });
        }
        return result;
    }
}
