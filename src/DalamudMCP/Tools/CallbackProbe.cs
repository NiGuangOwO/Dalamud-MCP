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
/// FireCallback is a NON-virtual member function (FCS [MemberFunction] sig
/// "E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20"), so the patch is an inline detour resolved
/// through FCS's generated address, installed via Dalamud's IGameInteropProvider —
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
    private static ISigScanner? sigScanner;
    private static Hook<FireCallbackDelegate>? hook;
    private static FireCallbackDelegate? detourDelegate;

    /// <summary>Initialize once from Plugin's constructor-injected services.</summary>
    public static void Initialize(IGameInteropProvider provider, ISigScanner scanner)
    {
        interop = provider;
        sigScanner = scanner;
    }

    public static bool IsHooked => hook?.IsEnabled == true;

    /// <summary>Install the FireCallback hook. Idempotent.</summary>
    public static object Enable()
    {
        if (interop is null || sigScanner is null)
            return Json.ToolError("callback probe not initialized (no interop provider)");

        if (hook is null)
        {
            // FCS's MemberFunction signature "E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20"
            // matches the CALL SITE of FireCallback inside the game binary, not the
            // function itself. Resolve the call site, then read the E8 rel32 to get
            // the real FireCallback entry point.
            try
            {
                if (!sigScanner.TryScanText(Signature, out var callSite))
                    return Json.ToolError($"FireCallback call-site signature not found: {Signature}");

                var target = (IntPtr)(*(int*)(callSite + 1) + callSite + 5);
                if (target == IntPtr.Zero)
                    return Json.ToolError("FireCallback call site decoded to a null target");

                detourDelegate ??= FireCallbackDetour;
                hook = interop.HookFromAddress<FireCallbackDelegate>(target, FireCallbackDetour);
            }
            catch (Exception ex)
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
