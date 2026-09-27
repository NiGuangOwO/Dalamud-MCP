using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Tools;

/// <summary>
/// Temporary diagnostic surface: per-instance vtable-slot swap on one addon's
/// ReceiveEvent. While hooked, every ReceiveEvent the addon's class dispatches —
/// real mouse clicks and synthetic ones alike — is captured (type, param, and the
/// raw 0x40-byte event / event-data buffers) before being forwarded to the original
/// function. The capture exists to diff a real click against click_addon_element's
/// synthetic dispatch field by field.
/// </summary>
internal static unsafe class AtkEventProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReceiveEventDelegate(
        AtkEventListener* self, AtkEventType eventType, int param, AtkEvent* evt, AtkEventData* eventData);

    public sealed record CapturedEvent(
        DateTime Time, IntPtr Listener, int EventType, int Param, byte[] Evt, byte[] EvtData);

    private const int MaxCaptured = 64;

    /// <summary>Ring of recent ReceiveEvent invocations (oldest first).</summary>
    public static readonly ConcurrentQueue<CapturedEvent> Captured = new();

    // One hooked addon at a time keeps the swap auditable and restore trivial.
    private static IntPtr hookedUnit;
    private static IntPtr hookSlot;      // address of the ReceiveEvent slot inside the class vtable
    private static IntPtr original;      // original function pointer found in that slot

    // Kept in a static so the delegate cannot be collected while native code holds it.
    private static ReceiveEventDelegate? hookDelegate;

    public static bool IsHooked => hookedUnit != IntPtr.Zero;

    public static object Enable(AtkUnitBase* unit)
    {
        if (unit is null)
            return Json.ToolError("no addon resolved for probe_enable");
        if (hookedUnit == (IntPtr)unit)
            return new JObject { ["hooked"] = true, ["already"] = true, ["addon"] = unit->NameString };

        if (hookedUnit != IntPtr.Zero)
            return Json.ToolError("another addon is already hooked; disable it first");

        var vtbl = (AtkEventListener.AtkEventListenerVirtualTable*)unit->VirtualTable;
        if (vtbl is null)
            return Json.ToolError("addon vtable is null");

        // The address of the ReceiveEvent function-pointer field inside the class vtable.
        var slot = (IntPtr*)(&vtbl->ReceiveEvent);

        hookDelegate ??= ReceiveEventHook;
        var replacement = Marshal.GetFunctionPointerForDelegate(hookDelegate);
        original = *slot;
        *slot = replacement;
        ProbeTrampoline.Set(original);

        hookedUnit = (IntPtr)unit;
        hookSlot = (IntPtr)slot;

        Captured.Clear();
        return new JObject
        {
            ["hooked"] = true,
            ["addon"] = unit->NameString,
            ["addonId"] = (int)unit->Id,
            ["slot"] = hookSlot.ToString("X"),
            ["original"] = original.ToString("X"),
        };
    }

    public static object Disable()
    {
        if (hookedUnit == IntPtr.Zero)
            return new JObject { ["hooked"] = false, ["already"] = true };

        *(IntPtr*)hookSlot = original;
        var wasAddonId = ((AtkUnitBase*)hookedUnit)->Id;
        hookedUnit = IntPtr.Zero;
        hookSlot = IntPtr.Zero;
        original = IntPtr.Zero;
        return new JObject { ["hooked"] = false, ["addonId"] = (int)wasAddonId, ["restored"] = true };
    }

    public static object Dump()
    {
        var items = new JArray();
        foreach (var c in Captured)
        {
            items.Add(new JObject
            {
                ["time"] = c.Time.ToString("HH:mm:ss.fff"),
                ["listener"] = c.Listener.ToString("X"),
                ["eventType"] = c.EventType,
                ["param"] = c.Param,
                // AtkEvent fields at their FCS offsets, decoded from the captured buffer.
                ["evtNode"] = ReadPtr(c.Evt, 0x00),
                ["evtTarget"] = ReadPtr(c.Evt, 0x08),
                ["evtListener"] = ReadPtr(c.Evt, 0x10),
                ["evtParam"] = ReadU32(c.Evt, 0x18),
                ["evtNextEvent"] = ReadPtr(c.Evt, 0x20),
                ["evtStateEventType"] = c.Evt.Length > 0x28 ? c.Evt[0x28] : -1,
                ["evtStateReturnFlags"] = c.Evt.Length > 0x29 ? c.Evt[0x29] : -1,
                ["evtStateFlags"] = c.Evt.Length > 0x2A ? c.Evt[0x2A] : -1,
                ["evtHex"] = Hex(c.Evt, 0x30),
                ["evtDataHex"] = Hex(c.EvtData, 0x40),
            });
        }

        return new JObject
        {
            ["hooked"] = hookedUnit != IntPtr.Zero,
            ["count"] = items.Count,
            ["events"] = items,
        };
    }

    public static void Clear() => Captured.Clear();

    private static string ReadPtr(byte[] buf, int off) =>
        buf.Length >= off + 8
            ? BitConverter.ToUInt64(buf, off).ToString("X")
            : "n/a";

    private static long ReadU32(byte[] buf, int off) =>
        buf.Length >= off + 4 ? BitConverter.ToUInt32(buf, off) : -1;

    private static string Hex(byte[] buf, int max)
    {
        var sb = new System.Text.StringBuilder();
        var n = Math.Min(buf.Length, max);
        for (var i = 0; i < n; i++)
        {
            if (i > 0 && i % 16 == 0)
                sb.Append('\n');
            else if (i > 0)
                sb.Append(' ');
            sb.Append(buf[i].ToString("X2"));
        }

        return sb.ToString();
    }

    private static void ReceiveEventHook(
        AtkEventListener* self, AtkEventType eventType, int param, AtkEvent* evt, AtkEventData* eventData)
    {
        try
        {
            var evtBytes = new byte[0x40];
            if (evt is not null)
                Marshal.Copy((IntPtr)evt, evtBytes, 0, 0x40);
            var dataBytes = new byte[0x40];
            if (eventData is not null)
                Marshal.Copy((IntPtr)eventData, dataBytes, 0, 0x40);

            while (Captured.Count >= MaxCaptured && Captured.TryDequeue(out _))
            {
            }

            Captured.Enqueue(new CapturedEvent(
                DateTime.UtcNow, (IntPtr)self, (int)eventType, param, evtBytes, dataBytes));
        }
        catch
        {
            // The probe must never break the game's event dispatch.
        }

        var fwdSelf = hookedUnit == IntPtr.Zero ? self : (AtkEventListener*)hookedUnit;
        ProbeTrampoline.Invoke(fwdSelf, eventType, param, evt, eventData);
    }
}

/// <summary>Trampoline helper: calls the original function pointer saved at hook time.</summary>
internal static unsafe class ProbeTrampoline
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ReceiveEventDelegate(
        AtkEventListener* self, AtkEventType eventType, int param, AtkEvent* evt, AtkEventData* eventData);

    private static ReceiveEventDelegate? originalDelegate;

    public static void Set(IntPtr original) =>
        originalDelegate = Marshal.GetDelegateForFunctionPointer<ReceiveEventDelegate>(original);

    public static void Invoke(
        AtkEventListener* self, AtkEventType eventType, int param, AtkEvent* evt, AtkEventData* eventData)
    {
        if (originalDelegate is null)
            throw new InvalidOperationException("probe trampoline not set");
        originalDelegate(self, eventType, param, evt, eventData);
    }
}
