using System.Runtime.InteropServices;
using DalamudMCP.Tools;
using FFXIVClientStructs.FFXIV.Client.Game;

internal static unsafe class NativeContainerTests
{
    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint address, nuint size, uint allocation, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(nint address, nuint size, uint freeType);

    public static void Run(Action<bool, string> check)
    {
        var memory = VirtualAlloc(0, 8192, 0x3000, 0x04);
        if (memory == 0) throw new Exception("VirtualAlloc failed");
        try
        {
            if (!VirtualProtect(memory + 4096, 4096, 0x01, out _)) throw new Exception("VirtualProtect failed");
            var header = (InventoryContainer*)memory;
            var slot = (InventoryItem*)(memory + 256);
            *header = new InventoryContainer { Type = InventoryType.Inventory1, IsLoaded = true, Size = 1, Items = slot };
            *slot = new InventoryItem { ItemId = 10, Quantity = 5 };
            bool Read() => InventoryContainerRead.TryRead((ulong)header, InventoryType.Inventory1, out _, out _, out _);
            check(Read(), "installed SDK native header and full slot array are readable");
            check(InventoryContainerRead.TryRead((ulong)header, InventoryType.Inventory1, out _, out var bytes, out _) &&
                MemoryMarshal.Read<InventoryItem>(bytes).Quantity == 5, "native slot copy preserves quantity");
            header->IsLoaded = false; check(!Read(), "unloaded native container refused"); header->IsLoaded = true;
            header->Type = InventoryType.RetainerPage1; check(!Read(), "wrong container discriminator refused"); header->Type = InventoryType.Inventory1;
            header->Size = 201; check(!Read(), "oversized native slot count refused");
            header->Size = 0; check(!Read(), "zero-size native layout stays unknown"); header->Size = 1;
            header->Items = null; check(!Read(), "null native slot pointer refused");
            header->Items = (InventoryItem*)1; check(!Read(), "unmapped native slot pointer refused");
            header->Items = (InventoryItem*)(memory + 4096 - sizeof(InventoryItem) + 1);
            check(!Read(), "partial slot crossing unreadable page refused before unmarshalling");
            check(!InventoryContainerRead.TryRead((ulong)(memory + 4096 - sizeof(InventoryContainer) + 1), InventoryType.Inventory1,
                out _, out _, out _), "partial native header refused before reading any fields");
        }
        finally { VirtualFree(memory, 0, 0x8000); }
    }
}
