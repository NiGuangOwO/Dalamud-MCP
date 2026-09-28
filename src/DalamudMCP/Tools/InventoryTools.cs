using System;
using System.Collections.Generic;
using Dalamud.Game.Inventory;
using DalamudMCP.Mcp;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Inventory bag and equipment views. Read-only: the GameInventory service already
/// exposes item names resolved lazily, so the tools cap results to keep responses small.
/// </summary>
internal static class InventoryTools
{
    private const int MaxItems = 200;

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_inventory",
            "Get inventory",
            "Items in the four player inventory bags (saddlebag and retainer bags are not included). " +
            "Each entry: itemId, name, quantity, isHq, slot.",
            Json.Schema(),
            _ => Inventory(svc));

        registry.Add(
            "get_equipment",
            "Get equipment",
            "Currently equipped gear with item level, condition/perfection, spiritbond, glamour id and " +
            "materia count.",
            Json.Schema(),
            _ => Equipment(svc));
    }

    private static JObject Inventory(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            return NotAvailable;

        var items = new JArray();
        foreach (var container in new[]
                 {
                     GameInventoryType.Inventory1,
                     GameInventoryType.Inventory2,
                     GameInventoryType.Inventory3,
                     GameInventoryType.Inventory4,
                 })
        {
            foreach (var slot in svc.GameInventory.GetInventoryItems(container))
            {
                if (slot.IsEmpty || slot.ItemId == 0) continue;
                items.Add(new JObject
                {
                    ["itemId"] = slot.ItemId,
                    ["name"] = ItemName(svc, slot.ItemId),
                    ["quantity"] = slot.Quantity,
                    ["isHq"] = slot.IsHq,
                    ["container"] = container.ToString(),
                    ["slot"] = slot.InventorySlot,
                });
                if (items.Count >= MaxItems) return Paged(items, MaxItems, true);
            }
        }

        return Paged(items, items.Count, false);
    }

    private static JObject Equipment(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            return NotAvailable;

        var equipment = new JArray();
        foreach (var slot in svc.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems))
        {
            if (slot.IsEmpty || slot.ItemId == 0) continue;
            equipment.Add(new JObject
            {
                ["itemId"] = slot.ItemId,
                ["name"] = ItemName(svc, slot.ItemId),
                ["quantity"] = slot.Quantity,
                ["isHq"] = slot.IsHq,
                ["slot"] = slot.InventorySlot.ToString(),
                ["condition"] = slot.Condition,
                ["spiritbondOrCollectability"] = slot.SpiritbondOrCollectability,
            });
        }

        return new JObject
        {
            ["count"] = equipment.Count,
            ["equipment"] = equipment,
        };
    }

    private static string? ItemName(GameServices svc, uint itemId)
    {
        if (itemId == 0) return null;
        try
        {
            return svc.Sheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText();
        }
        catch
        {
            return null;
        }
    }

    private static JObject Paged(JArray items, int count, bool truncated) => new()
    {
        ["count"] = count,
        ["truncated"] = truncated,
        ["items"] = items,
    };

    private static JObject NotAvailable => new()
    {
        ["available"] = false,
        ["reason"] = "not logged in",
    };
}
