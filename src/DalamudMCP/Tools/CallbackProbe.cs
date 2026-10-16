using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Tools;

/// <summary>
/// Diagnostic surface modelled on SimpleTweaksPlugin's Addon Logging → Callbacks
/// (Debugging/AddonDebug.cs): a global hook on AtkUnitBase.FireCallback that records
/// every addon callback the game performs — which addon, which AtkValue[] arguments
/// (decoded by AtkValueType), and whether visibility was updated. This is the
/// semantic layer beneath real UI interaction: clicking a button or picking a list
/// row ultimately funnels into one of these calls, so the captured (addon, values)
/// pairs are exactly the payloads an automation agent needs to reproduce in-game
/// actions without synthetic ATK events.
/// FireCallback is a NON-virtual member function. Its entry point comes directly
/// from FCS's resolved member-function pointer, installed via IGameInteropProvider —
/// the same engine SimpleTweaks uses through its Hook&lt;T&gt; helper.
/// </summary>
internal static unsafe class CallbackProbe
{
    // FCS AtkUnitBase.FireCallback [MemberFunction] signature — matches the E8 call
    // SITE inside the game binary; the rel32 target is the function itself.
    public const string Signature =
        "E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20";

    // bool AtkUnitBase::FireCallback(uint valueCount, AtkValue* values, bool close)
    // x64 Microsoft x64 calling convention: rcx=this, edx=valueCount, r8=values,
    // r9=close; bool return widened to byte in the register.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte FireCallbackDelegate(
        AtkUnitBase* self, uint valueCount, AtkValue* values, byte close);

    public sealed record CapturedCallback(
        DateTime Time,
        string AddonName,
        uint ValueCount,
        JArray Values,
        bool Close,
        bool ReturnValue);

    private const int MaxCaptured = 128;
    private const int MaxDecodedValues = 64;

    /// <summary>Ring of recent FireCallback invocations (oldest first).</summary>
    public static readonly ConcurrentQueue<CapturedCallback> Captured = new();

    private static IGameInteropProvider? interop;
    private static Hook<FireCallbackDelegate>? hook;
    private static FireCallbackDelegate? detourDelegate;

    /// <summary>Initialize once from Plugin's constructor-injected services.</summary>
    public static void Initialize(IGameInteropProvider provider, ISigScanner scanner)
    {
        interop = provider;
    }

    public static bool IsHooked => hook?.IsEnabled == true;

    /// <summary>Install the FireCallback hook. Idempotent.</summary>
    public static object Enable()
    {
        if (interop is null)
            return Json.ToolError("callback probe not initialized (no interop provider)");

        if (hook is null)
        {
            // ScanText already resolves leading E8/E9 signatures. Decoding its
            // return value as another rel32 produced a bogus address and crashed
            // HookManager.FollowJmp. Use FCS's resolved function entry, and refuse
            // unmapped/non-executable pointers before the native hook engine.
            try
            {
                var target = (IntPtr)AtkUnitBase.MemberFunctionPointers.FireCallback;
                var region = MemoryProbe.Query((ulong)(nuint)target);
                if (region is not { Committed: true, Readable: true, Executable: true, Guarded: false })
                    throw new ToolException("FireCallback function pointer is unresolved or not executable; probe not installed");

                detourDelegate ??= FireCallbackDetour;
                hook = interop.HookFromAddress<FireCallbackDelegate>(target, FireCallbackDetour);
            }
            catch (Exception ex) when (ex is not ToolException)
            {
                return Json.ToolError($"FireCallback hook creation failed: {ex.Message}");
            }
        }

        if (!hook.IsEnabled)
            hook.Enable();

        Captured.Clear();
        return new JObject
        {
            ["hooked"] = true,
            ["method"] = "inline detour via IGameInteropProvider.HookFromAddress",
            ["target"] = hook.Address.ToString("X"),
        };
    }

    public static object Disable()
    {
        if (hook is null || !hook.IsEnabled)
            return new JObject { ["hooked"] = false, ["already"] = true };

        hook.Dispose();
        hook = null;
        return new JObject { ["hooked"] = false, ["restored"] = true };
    }

    public static object Dump()
    {
        var items = new JArray();
        foreach (var c in Captured)
        {
            items.Add(new JObject
            {
                ["time"] = c.Time.ToLocalTime().ToString("HH:mm:ss.fff"),
                ["addon"] = c.AddonName,
                ["valueCount"] = c.ValueCount,
                ["values"] = c.Values,
                ["close"] = c.Close,
                ["returnValue"] = c.ReturnValue,
            });
        }

        return new JObject
        {
            ["hooked"] = IsHooked,
            ["count"] = items.Count,
            ["callbacks"] = items,
        };
    }

    public static void Clear() => Captured.Clear();

    private static byte FireCallbackDetour(
        AtkUnitBase* self, uint valueCount, AtkValue* values, byte close)
    {
        byte ret;
        try
        {
            ret = hook!.Original(self, valueCount, values, close);
        }
        catch
        {
            // Never let the probe break the game's own callback path.
            return 0;
        }

        try
        {
            var name = "<unknown>";
            if (self is not null)
            {
                try { name = self->NameString; }
                catch { /* keep placeholder */ }
            }

            var decoded = new JArray();
            var v = values;
            var n = Math.Min(valueCount, MaxDecodedValues);
            for (uint i = 0; i < n; i++, v++)
            {
                try { decoded.Add(DecodeAtkValue(v)); }
                catch { decoded.Add(new JObject { ["type"] = "DecodeError" }); }
            }

            while (Captured.Count >= MaxCaptured && Captured.TryDequeue(out _))
            {
            }

            Captured.Enqueue(new CapturedCallback(
                DateTime.UtcNow, name, valueCount, decoded, close != 0, ret != 0));
        }
        catch
        {
            // The probe must never break the game's callback dispatch.
        }

        return ret;
    }

    /// <summary>Decode one AtkValue (Type at +0x0, value union at +0x8, size 0x10)
    /// following SimpleTweaks' CallbackDetour switch, with the full FCS
    /// AtkValueType set.</summary>
    private static JObject DecodeAtkValue(AtkValue* v)
    {
        var type = v->Type;
        var obj = new JObject { ["type"] = type.ToString() };
        switch (type)
        {
            case AtkValueType.Bool:
                obj["value"] = v->Byte != 0;
                break;
            case AtkValueType.Int:
                obj["value"] = v->Int;
                break;
            case AtkValueType.Int64:
                obj["value"] = v->Int64;
                break;
            case AtkValueType.UInt:
                obj["value"] = v->UInt;
                break;
            case AtkValueType.UInt64:
                obj["value"] = v->UInt64;
                break;
            case AtkValueType.Float:
                obj["value"] = v->Float;
                break;
            case AtkValueType.String:
            case AtkValueType.ManagedString:
            case AtkValueType.ConstString:
                obj["value"] = v->String.ToString();
                break;
            case AtkValueType.WideString:
                obj["value"] = Marshal.PtrToStringUni((IntPtr)v->WideString) ?? string.Empty;
                break;
            case AtkValueType.Vector:
            case AtkValueType.ManagedVector:
                obj["value"] = v->Vector is not null
                    ? $"vector@{(IntPtr)v->Vector:X}"
                    : "null";
                break;
            case AtkValueType.Pointer:
            case AtkValueType.AtkValues:
                obj["value"] = $"0x{(IntPtr)v->Pointer:X}";
                break;
            default:
                obj["value"] = $"unknown(0x{(uint)type:X})";
                break;
        }

        return obj;
    }
}
