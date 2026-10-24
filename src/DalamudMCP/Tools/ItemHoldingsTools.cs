using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Framework-thread observation only; never requests inventory packets or opens game UI.</summary>
internal sealed unsafe class ItemHoldingsTools : IDisposable
{
    private readonly GameServices svc;
    private readonly ItemHoldings holdings = new();
    private readonly RetainerOwnership ownership = new();
    private long nextSample;
    private ulong character;

    internal static readonly (string Scope, InventoryType Type)[] Containers =
    {
        ("inventory", InventoryType.Inventory1), ("inventory", InventoryType.Inventory2),
        ("inventory", InventoryType.Inventory3), ("inventory", InventoryType.Inventory4), ("inventory", InventoryType.Crystals),
        ("equipment", InventoryType.EquippedItems),
        ("armoury", InventoryType.ArmoryMainHand), ("armoury", InventoryType.ArmoryOffHand),
        ("armoury", InventoryType.ArmoryHead), ("armoury", InventoryType.ArmoryBody),
        ("armoury", InventoryType.ArmoryHands), ("armoury", InventoryType.ArmoryLegs),
        ("armoury", InventoryType.ArmoryFeets), ("armoury", InventoryType.ArmoryEar),
        ("armoury", InventoryType.ArmoryNeck), ("armoury", InventoryType.ArmoryWrist),
        ("armoury", InventoryType.ArmoryRings), ("armoury", InventoryType.ArmorySoulCrystal),
        ("saddlebag", InventoryType.SaddleBag1), ("saddlebag", InventoryType.SaddleBag2),
        ("premiumSaddlebag", InventoryType.PremiumSaddleBag1), ("premiumSaddlebag", InventoryType.PremiumSaddleBag2),
        ("retainer", InventoryType.RetainerPage1), ("retainer", InventoryType.RetainerPage2),
        ("retainer", InventoryType.RetainerPage3), ("retainer", InventoryType.RetainerPage4),
        ("retainer", InventoryType.RetainerPage5), ("retainer", InventoryType.RetainerPage6),
        ("retainer", InventoryType.RetainerPage7), ("retainer", InventoryType.RetainerCrystals),
        ("retainer", InventoryType.RetainerEquippedItems),
    };

    public ItemHoldingsTools(ToolRegistry registry, GameServices services)
    {
        svc = services;
        var schema = Json.Schema(
            ("itemId", "integer", "Base item ID; provide exactly one of itemId or name. HQ uses the same base ID.", false),
            ("name", "string", "Localized item name, exact match by default, 1-128 characters.", false),
            ("contains", "boolean", "Match name by substring (default false).", false),
            ("isHq", "boolean", "Optional HQ/NQ filter; omit to include both.", false),
            ("scopes", "array of string", "Storage scopes; default all supported scopes.", false),
            ("includeCached", "boolean", "Include session snapshots of unloaded storage (default true); cached values may be stale.", false),
            ("offset", "integer", "Matching slot offset, 0-10000 (default 0). Re-querying is a new observation.", false),
            ("maxResults", "integer", "Location rows per page, 1-200 (default 100); totals always cover all matches.", false));
        schema["properties"]!["scopes"]!["items"]!["enum"] = new JArray(ItemHoldings.Scopes);
        schema["properties"]!["scopes"]!["minItems"] = 1;
        schema["properties"]!["scopes"]!["maxItems"] = 6;
        schema["properties"]!["name"]!["minLength"] = 1;
        schema["properties"]!["name"]!["maxLength"] = 128;
        schema["properties"]!["itemId"]!["minimum"] = 1;
        schema["properties"]!["itemId"]!["maximum"] = uint.MaxValue;
        schema["properties"]!["offset"]!["minimum"] = 0;
        schema["properties"]!["offset"]!["maximum"] = 10000;
        schema["properties"]!["maxResults"]!["minimum"] = 1;
        schema["properties"]!["maxResults"]!["maximum"] = 200;
        schema["oneOf"] = new JArray(new JObject { ["required"] = new JArray("itemId"),
            ["not"] = new JObject { ["required"] = new JArray("name") } },
            new JObject { ["required"] = new JArray("name"), ["not"] = new JObject { ["required"] = new JArray("itemId") } });
        registry.Add("search_item_holdings", "Search player item holdings",
            "Find an item across carried bags/crystals, equipped gear, all current armoury slots, normal/premium chocobo saddlebags, " +
            "and visited retainers' seven bags/crystals/equipment. Returns quantities, slot locations and per-container live/cached/unavailable coverage. " +
            "Unvisited or unloaded storage is unknown, never zero. Read-only passive snapshots once per second; session-only, cleared on logout/reload. " +
            "Visit each retainer's inventory to populate its snapshot. Does not open UI, access other characters or include market listings/dresser/armoire.",
            schema, Query);
        svc.Framework.Update += OnUpdate;
    }

    private bool Ready()
    {
        var id = svc.ClientState.IsLoggedIn && svc.PlayerState.IsLoaded ? svc.PlayerState.ContentId : 0;
        if (id != character)
        {
            character = id;
            holdings.SetCharacter(id);
            ownership.Reset();
            nextSample = 0;
        }
        return id != 0;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Ready()) return;
        // Containers can stay loaded across sessions. Observe the inventory UI lifetime instead.
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return;
            var state = MemoryProbe.TryReadStruct<RetainerManager>((ulong)RetainerManager.Instance(), out _);
            ownership.Observe(state?.LastSelectedRetainerId ?? 0, IsRetainerInventoryReady());
            if (Environment.TickCount64 < nextSample) return;
            nextSample = Environment.TickCount64 + 1000;
            var snapshot = Capture(false);
            holdings.Observe(snapshot.Containers);
        }
        catch (Exception ex)
        {
            // Failed observations cannot refresh snapshots or claim empty storage.
            nextSample = Environment.TickCount64 + 5000;
            svc.LogDebug("item holdings observation failed: " + ex.Message);
        }
    }

    private object Query(JObject args)
    {
        HoldingQuery.Parse(args); // Reject invalid arguments before touching game state.
        if (!Ready()) return new JObject { ["available"] = false, ["reason"] = "not logged in", ["complete"] = false };
        var snapshot = Capture(true);
        holdings.Observe(snapshot.Containers);
        return holdings.Query(args, snapshot.Containers, snapshot.Retainers, snapshot.RosterReady,
            id => svc.Sheet<Item>().GetRowOrDefault(id)?.Name.ExtractText());
    }

    private (List<HoldingContainer> Containers, List<HoldingRetainer> Retainers, bool RosterReady) Capture(bool all)
    {
        var now = DateTimeOffset.UtcNow;
        var manager = InventoryManager.Instance();
        var state = MemoryProbe.TryReadStruct<RetainerManager>((ulong)RetainerManager.Instance(), out _);
        var roster = new List<HoldingRetainer>();
        var rosterReady = state?.IsReady == true;
        var owner = state?.LastSelectedRetainerId ?? 0;
        string? ownerName = null;
        if (rosterReady)
        {
            var copy = state!.Value;
            foreach (var r in copy.Retainers)
            {
                if (r.RetainerId == 0) continue;
                roster.Add(new HoldingRetainer(r.RetainerId.ToString(), r.NameString));
                if (r.RetainerId == owner) ownerName = r.NameString;
            }
        }
        ownership.Observe(owner, IsRetainerInventoryReady());
        var result = new List<HoldingContainer>();
        foreach (var (scope, type) in Containers)
        {
            if (!all && scope is "inventory" or "equipment" or "armoury") continue;
            if (scope == "retainer" && (owner == 0 || ownerName is null)) continue;
            var ownerId = scope == "retainer" ? owner.ToString() : "player";
            var reason = scope == "retainer" && !ownership.CanRead(owner) ? "retainer inventory UI not ready for this owner; open or reopen item management" : null;
            var snapshot = reason is null ? ReadContainer(manager, scope, type, ownerId, ownerName, now)
                : new HoldingContainer(scope, type.ToString(), ownerId, ownerName, false, reason, now, Array.Empty<HoldingItem>());
            result.Add(snapshot);
        }
        var after = MemoryProbe.TryReadStruct<RetainerManager>((ulong)RetainerManager.Instance(), out _);
        if (!after.HasValue || after.Value.LastSelectedRetainerId != owner || !IsRetainerInventoryReady())
            result.RemoveAll(c => c.Scope == "retainer");
        return (result, roster, rosterReady);
    }

    private bool IsRetainerInventoryReady()
    {
        foreach (var name in new[] { "InventoryRetainer", "InventoryRetainerLarge" })
        {
            var address = svc.GameGui.GetAddonByName(name, 1).Address;
            var addon = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)address, out _);
            if (addon.HasValue && addon.Value.IsVisible && addon.Value.IsReady) return true;
        }
        return false;
    }

    private HoldingContainer ReadContainer(InventoryManager* manager, string scope, InventoryType type,
        string ownerId, string? ownerName, DateTimeOffset now)
    {
        // Only retainers carry a separate owner name.
        if (scope != "retainer") ownerName = null;
        HoldingContainer Missing(string reason) => new(scope, type.ToString(), ownerId, ownerName, false, reason, now, Array.Empty<HoldingItem>());
        if (manager == null) return Missing("inventory manager unavailable");
        if (!InventoryContainerRead.TryRead((ulong)manager->GetInventoryContainer(type), type,
                out var container, out var bytes, out var reason)) return Missing(reason);
        var slots = svc.GameInventory.GetInventoryItems((GameInventoryType)type);
        if (slots.Length != container.Size) return Missing("SDK/container slot count mismatch");
        var items = new List<HoldingItem>();
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if ((ulong)slot.Address != (ulong)(container.Items + i)) return Missing("SDK/container slot address mismatch");
            var raw = MemoryMarshal.Read<InventoryItem>(bytes.AsSpan(i * sizeof(InventoryItem), sizeof(InventoryItem)));
            if (raw.IsSymbolic) return Missing("symbolic slots are not supported");
            if (slot.IsEmpty || slot.ItemId == 0) continue;
            if (slot.Quantity <= 0 || slot.BaseItemId == 0) return Missing("invalid item quantity or ID");
            items.Add(new HoldingItem(slot.ItemId, slot.BaseItemId, slot.Quantity, slot.IsHq, i));
        }
        return new HoldingContainer(scope, type.ToString(), ownerId, ownerName, true, null, now, items.ToArray());
    }

    public void Dispose()
    {
        svc.Framework.Update -= OnUpdate;
        holdings.SetCharacter(0);
    }
}
