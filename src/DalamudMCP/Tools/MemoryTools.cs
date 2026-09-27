using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Raw-memory tools 鈥?the escape hatch for anything the typed Dalamud / FFXIVClientStructs
/// surface does not cover yet.
///
/// The agent supplies absolute addresses, so the failure mode matters: an unchecked read at a
/// stale pointer raises an access violation and takes the whole game client down with it.
/// Every read therefore goes through <see cref="MemoryProbe"/>, which validates the address
/// against the OS region map first and returns an error instead of faulting.
/// </summary>
internal static class MemoryTools
{
    /// <summary>Hex dumps longer than this are truncated to keep tool results readable.</summary>
    private const int MaxHexDumpBytes = 4096;

    private static readonly string[] ReadFormats =
    {
        "hexdump", "bytes", "u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64",
        "f32", "f64", "bool", "string", "utf16", "pointer",
    };

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "read_memory",
            "Read memory",
            "Reads raw process memory at an absolute address. Use format=hexdump for a classic hex+ASCII view " +
            "of a struct, or a scalar format (u8/u16/u32/u64/i8..i64/f32/f64/pointer/bool/string/utf16) to decode " +
            "one value. Addresses are validated against the OS region map before reading, so an unmapped or " +
            "no-access address returns an error rather than crashing the game. Pass the address as a hex string " +
            "such as \"0x7FF6A1B2C3D4\" to avoid JSON number precision loss.",
            Json.SchemaWithEnum(
                "format", ReadFormats, "How to decode the bytes (default hexdump)", false,
                ("address", "string", "Absolute address, e.g. \"0x7FF6A1B2C3D4\"", true),
                ("length", "integer", "Bytes to read. Defaults to 256 for hexdump, 1024 for string, else the format's size.", false)),
            args => ReadMemory(svc, args));

        registry.Add(
            "query_memory_region",
            "Query memory region",
            "Describes what the OS reports for one or more addresses: whether the page is committed, readable, " +
            "writable, executable, its protection flags, and which allocation region it belongs to. Use this to " +
            "understand a struct layout before reading it, or to find out why a read failed.",
            Json.Schema(
                ("address", "string", "Absolute address to describe, e.g. \"0x7FF6A1B2C3D4\"", true),
                ("length", "integer", "Also report every region spanning this many bytes from the address", false)),
            args => QueryRegion(args));

        registry.Add(
            "read_pointer_chain",
            "Read pointer chain",
            "Follows a multi-level pointer: dereferences a pointer at the base address, adds the first offset, " +
            "dereferences again, and so on, then optionally reads a value at the final address. This is the " +
            "standard way to reach a single field from a static base. Returns every intermediate address so a " +
            "broken step is obvious.",
            Json.Schema(
                ("address", "string", "Base address to start from, e.g. \"0x7FF6A1B2C3D4\"", true),
                ("offsets", "array of integer or string", "Offsets applied after each dereference, one per level. JSON has no hex literals, so pass hex as a string: [16, 448] and [\"0x10\", \"0x1C0\"] both work.", true),
                ("valueFormat", "string", "Optional: also decode a value at the final address (same names as read_memory)", false)),
            args => ReadPointerChain(svc, args));

        registry.Add(
            "get_module_info",
            "Get module info",
            "The game module's load address and its .text / .data / .rdata section bases and sizes. Combine a " +
            "section base with an offset to get an absolute address for read_memory. Every address is returned " +
            "as a hex string.",
            Json.Schema(),
            _ => ModuleInfo(svc));

        registry.Add(
            "scan_signature",
            "Scan signature",
            "Scans the game's code or data for a byte pattern with wildcards, using Dalamud's signature scanner. " +
            "Patterns look like \"48 8B 05 ?? ?? ?? ?? 48 85 C0\" where ?? matches any byte. Use this to locate a " +
            "function or global that has no stable offset, then read_memory the address it returns.",
            Json.SchemaWithEnum(
                "section", new[] { "text", "data", "rdata", "module" }, "Which section to scan (default text)", false,
                ("signature", "string", "Byte pattern with ?? wildcards, e.g. \"48 8B 05 ?? ?? ?? ??\"", true),
                ("maxResults", "integer", "Maximum matches to return (default 1, use 0 for all)", false)),
            args => ScanSignature(svc, args));

        registry.Add(
            "read_object_memory",
            "Read object memory",
            "Reads memory at a game object's address plus an offset. Resolve the object by entityId, objectIndex, " +
            "or localPlayer, then look at a specific field. This is the bridge between the typed object tools and " +
            "raw memory: get_game_objects reports an object's address, and this reads the bytes behind it.",
            Json.SchemaWithEnum(
                "format", ReadFormats, "How to decode the bytes (default hexdump)", false,
                ("entityId", "integer", "Object entity id (from get_game_objects)", false),
                ("objectIndex", "integer", "Object table index", false),
                ("localPlayer", "boolean", "Read from the local player's object address", false),
                ("address", "string", "Read from this absolute object address instead of resolving one", false),
                ("offset", "integer", "Byte offset from the object's base address (default 0)", false),
                ("length", "integer", "Bytes to read (default 64)", false)),
            args => ReadObjectMemory(svc, args));
    }

    // ------------------------------------------------------------- handlers

    private static object ReadMemory(GameServices svc, JObject args)
    {
        if (!TryParseAddress(args["address"], out var address, out var addressError))
            throw new ToolException(addressError);

        var format = (args["format"]?.Value<string>() ?? "hexdump").Trim().ToLowerInvariant();
        if (!ReadFormats.Contains(format))
            throw new ToolException($"unknown format '{format}'. Valid values: {string.Join(", ", ReadFormats)}");

        var length = ResolveLength(format, args["length"]?.Value<int?>(), svc.Config.MaxMemoryReadBytes);

        var bytes = MemoryProbe.TryReadBytes(address, length, out var error);
        if (bytes is null)
            throw new ToolException(error ?? $"failed to read {length} bytes at 0x{address:X}");

        var result = Decode(bytes, format, address);
        result["address"] = Hex(address);
        result["bytesRead"] = bytes.Length;
        if (!string.IsNullOrEmpty(error)) result["warning"] = error;

        var region = MemoryProbe.Query(address);
        if (region is not null) result["region"] = DescribeRegion(region);

        return result;
    }

    private static object QueryRegion(JObject args)
    {
        if (!TryParseAddress(args["address"], out var address, out var addressError))
            throw new ToolException(addressError);

        var length = args["length"]?.Value<int?>();
        if (length is null or <= 0)
        {
            var single = MemoryProbe.Query(address);
            if (single is null)
                throw new ToolException($"address 0x{address:X} is not mapped in this process");

            return new JObject { ["regions"] = new JArray { DescribeRegion(single) } };
        }

        // Walk the requested span region by region so a caller inspecting a buffer sees every
        // page boundary it crosses rather than just the first region.
        const int maxRegions = 64;
        var regions = new JArray();
        var cursor = address;
        var end = address + (ulong)length;
        while (cursor < end && regions.Count < maxRegions)
        {
            var region = MemoryProbe.Query(cursor);
            if (region is null)
            {
                regions.Add(new JObject
                {
                    ["address"] = Hex(cursor),
                    ["mapped"] = false,
                    ["note"] = "not mapped",
                });
                break;
            }

            regions.Add(DescribeRegion(region));
            var next = region.RegionBase + region.RegionSize;
            if (next <= cursor) break;
            cursor = next;
        }

        if (cursor < end && regions.Count >= maxRegions)
            regions.Add(new JObject { ["note"] = $"stopped after {maxRegions} regions" });

        return new JObject
        {
            ["start"] = Hex(address),
            ["end"] = Hex(end),
            ["regions"] = regions,
        };
    }

    private static object ReadPointerChain(GameServices svc, JObject args)
    {
        if (!TryParseAddress(args["address"], out var baseAddress, out var addressError))
            throw new ToolException(addressError);

        if (args["offsets"] is not JArray offsets || offsets.Count == 0)
            throw new ToolException("offsets must be a non-empty array of integers, e.g. [0x10, 0x1C0]");

        if (offsets.Count > 16)
            throw new ToolException("offsets is limited to 16 levels");

        var steps = new JArray();
        var current = baseAddress;

        foreach (var offsetToken in offsets)
        {
            long offset;
            try
            {
                offset = offsetToken.Type == JTokenType.String
                    ? ParseOffset(offsetToken.Value<string>()!)
                    : offsetToken.Value<long>();
            }
            catch (Exception)
            {
                throw new ToolException($"offsets must be integers or numeric strings, got '{offsetToken}'");
            }

            var pointerBytes = MemoryProbe.TryReadBytes(current, 8, out var readError);
            if (pointerBytes is null)
                throw new ToolException($"failed to read pointer at 0x{current:X}: {readError}");

            var pointed = MemoryProbe.ReadPointer(pointerBytes, 0);
            var target = pointed + (ulong)offset;

            steps.Add(new JObject
            {
                ["at"] = Hex(current),
                ["pointer"] = Hex(pointed),
                ["offset"] = offset,
                ["result"] = Hex(target),
            });

            current = target;
        }

        var result = new JObject
        {
            ["base"] = Hex(baseAddress),
            ["finalAddress"] = Hex(current),
            ["steps"] = steps,
        };

        // Reading the final value is what the caller usually wants, so do it when asked and
        // surface the failure as a warning rather than throwing the whole chain away.
        var valueFormat = args["valueFormat"]?.Value<string>()?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(valueFormat))
        {
            if (!ReadFormats.Contains(valueFormat))
                throw new ToolException($"unknown valueFormat '{valueFormat}'. Valid values: {string.Join(", ", ReadFormats)}");

            var length = ResolveLength(valueFormat, null, svc.Config.MaxMemoryReadBytes);
            var bytes = MemoryProbe.TryReadBytes(current, length, out var valueError);
            if (bytes is null)
            {
                result["valueError"] = valueError;
            }
            else
            {
                foreach (var property in Decode(bytes, valueFormat, current))
                    result[property.Key] = property.Value;
            }
        }

        return result;
    }

    private static object ModuleInfo(GameServices svc)
    {
        var scanner = svc.SigScanner;
        var module = scanner.Module;

        var result = new JObject
        {
            ["moduleName"] = module?.ModuleName,
            ["moduleBase"] = Hex((ulong)(module?.BaseAddress.ToInt64() ?? 0)),
            ["moduleSize"] = module?.ModuleMemorySize,
            ["isCopy"] = scanner.IsCopy,
            ["searchBase"] = Hex((ulong)scanner.SearchBase.ToInt64()),
        };

        var sections = new JObject
        {
            ["text"] = Section(scanner.TextSectionBase, scanner.TextSectionSize),
            ["data"] = Section(scanner.DataSectionBase, scanner.DataSectionSize),
            ["rdata"] = Section(scanner.RDataSectionBase, scanner.RDataSectionSize),
        };
        result["sections"] = sections;

        // Clients differ in whether the sections are absolute or relative to the search base;
        // report the deltas so a caller can tell which convention this build is using.
        result["textSectionOffset"] = scanner.TextSectionOffset;
        result["dataSectionOffset"] = scanner.DataSectionOffset;
        result["rdataSectionOffset"] = scanner.RDataSectionOffset;

        return result;
    }

    private static object ScanSignature(GameServices svc, JObject args)
    {
        var signature = args["signature"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(signature))
            throw new ToolException("missing required parameter: signature");

        var section = (args["section"]?.Value<string>() ?? "text").Trim().ToLowerInvariant();
        var maxResults = args["maxResults"]?.Value<int?>() ?? 1;
        if (maxResults < 0) maxResults = 0;

        var scanner = svc.SigScanner;
        var matches = new List<IntPtr>();

        try
        {
            switch (section)
            {
                case "text":
                    if (maxResults == 1)
                    {
                        if (scanner.TryScanText(signature, out var one)) matches.Add(one);
                    }
                    else
                    {
                        var all = scanner.ScanAllText(signature);
                        matches.AddRange(maxResults == 0 ? all : all.Take(maxResults));
                    }

                    break;

                case "data":
                    if (scanner.TryScanData(signature, out var data)) matches.Add(data);
                    break;

                case "module":
                    if (scanner.TryScanModule(signature, out var inModule)) matches.Add(inModule);
                    break;

                case "rdata":
                    // There is no rdata-specific entry point; scan the module and keep only
                    // matches that land inside the .rdata section's range.
                    var rdataBase = scanner.RDataSectionBase.ToInt64();
                    var rdataEnd = rdataBase + scanner.RDataSectionSize;

                    if (maxResults == 1)
                    {
                        // A single-match scan may well land in .text, so scan everything and
                        // filter; correctness beats speed for an ad-hoc lookup.
                        foreach (var candidate in scanner.ScanAllText(signature))
                        {
                            var value = candidate.ToInt64();
                            if (value >= rdataBase && value < rdataEnd) matches.Add(candidate);
                        }
                    }
                    else
                    {
                        foreach (var candidate in scanner.ScanAllText(signature))
                        {
                            var value = candidate.ToInt64();
                            if (value >= rdataBase && value < rdataEnd) matches.Add(candidate);
                        }

                        if (maxResults > 0 && matches.Count > maxResults)
                            matches.RemoveRange(maxResults, matches.Count - maxResults);
                    }

                    break;

                default:
                    throw new ToolException("section must be one of: text, data, rdata, module");
            }
        }
        catch (ToolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ToolException($"signature scan failed: {ex.GetType().Name}: {ex.Message}");
        }

        var baseAddress = (ulong)svc.SigScanner.SearchBase.ToInt64();
        var array = new JArray();
        foreach (var match in matches)
        {
            var value = (ulong)match.ToInt64();
            array.Add(new JObject
            {
                ["address"] = Hex(value),
                ["offsetFromSearchBase"] = value >= baseAddress ? value - baseAddress : 0,
            });
        }

        return new JObject
        {
            ["signature"] = signature,
            ["section"] = section,
            ["count"] = matches.Count,
            ["matches"] = array,
        };
    }

    private static object ReadObjectMemory(GameServices svc, JObject args)
    {
        if (!TryParseAddress(args["address"], out var address, out _))
        {
            if (args["address"] is not null)
                throw new ToolException("address must be a hex string such as \"0x7FF6A1B2C3D4\"");

            // Resolve the object through the object table when no explicit address was given.
            if (args["localPlayer"]?.Value<bool?>() == true)
            {
                var player = svc.ObjectTable.LocalPlayer
                             ?? throw new ToolException("no local player; the client is not logged in");

                address = (ulong)player.Address.ToInt64();
            }
            else if (args["entityId"] is not null)
            {
                var entityId = args["entityId"]!.Value<uint>();
                var obj = svc.ObjectTable.SearchByEntityId(entityId)
                          ?? throw new ToolException($"no object with entityId {entityId} in the object table");

                address = (ulong)obj.Address.ToInt64();
            }
            else if (args["objectIndex"] is not null)
            {
                var index = args["objectIndex"]!.Value<int>();
                var raw = svc.ObjectTable.GetObjectAddress(index);
                if (raw == IntPtr.Zero)
                    throw new ToolException($"no object at object table index {index}");

                address = (ulong)raw.ToInt64();
            }
            else
            {
                throw new ToolException(
                    "resolve an object with one of: address, entityId, objectIndex, localPlayer");
            }
        }

        var offset = args["offset"]?.Value<long>() ?? 0;
        if (offset < 0)
        {
            // Negative offsets are legitimate for walking backwards inside a struct.
            var target = (long)address + offset;
            if (target < 0) throw new ToolException("offset moves the address below zero");
            address = (ulong)target;
        }
        else
        {
            address += (ulong)offset;
        }

        var format = (args["format"]?.Value<string>() ?? "hexdump").Trim().ToLowerInvariant();
        if (!ReadFormats.Contains(format))
            throw new ToolException($"unknown format '{format}'. Valid values: {string.Join(", ", ReadFormats)}");

        var length = ResolveLength(format, args["length"]?.Value<int?>(), svc.Config.MaxMemoryReadBytes);
        var bytes = MemoryProbe.TryReadBytes(address, length, out var error);
        if (bytes is null)
            throw new ToolException(error ?? $"failed to read {length} bytes at 0x{address:X}");

        var result = Decode(bytes, format, address);
        result["address"] = Hex(address);
        result["offset"] = offset;
        result["bytesRead"] = bytes.Length;
        if (!string.IsNullOrEmpty(error)) result["warning"] = error;
        return result;
    }

    // ------------------------------------------------------------- decoding

    private static JObject Decode(byte[] bytes, string format, ulong address)
    {
        switch (format)
        {
            case "hexdump":
                // A dump of a large read would swamp the model's context, so render only the
                // first slice and say so; the caller can re-read at an offset for more.
                var dumped = bytes.Length > MaxHexDumpBytes ? bytes[..MaxHexDumpBytes] : bytes;
                var lines = MemoryProbe.HexDump(dumped, address);
                var result = new JObject
                {
                    ["format"] = "hexdump",
                    ["hex"] = lines.Count == 0 ? string.Empty : string.Join("\n", lines),
                };

                if (dumped.Length < bytes.Length)
                {
                    result["dumpedBytes"] = dumped.Length;
                    result["note"] =
                        $"hex dump truncated to {MaxHexDumpBytes} of {bytes.Length} bytes; " +
                        "use the bytes format or re-read at an offset for the rest";
                }

                return result;

            case "bytes":
                return new JObject
                {
                    ["format"] = "bytes",
                    ["bytes"] = new JArray(bytes.Select(b => (int)b)),
                };

            case "string":
                // Decode from the bytes already read; calling TryReadCString here would repeat
                // the entire read for no benefit.
                var nul = Array.IndexOf(bytes, (byte)0);
                var span = nul >= 0 ? bytes.AsSpan(0, nul) : bytes.AsSpan();
                return new JObject
                {
                    ["format"] = "string",
                    ["text"] = Encoding.UTF8.GetString(span),
                };

            case "utf16":
                var utf16 = Encoding.Unicode.GetString(bytes);
                var terminator = utf16.IndexOf('\0');
                if (terminator >= 0) utf16 = utf16[..terminator];
                return new JObject { ["format"] = "utf16", ["text"] = utf16 };

            case "bool":
                return new JObject { ["format"] = "bool", ["value"] = bytes[0] != 0 };

            case "pointer":
                return new JObject
                {
                    ["format"] = "pointer",
                    ["value"] = Hex(MemoryProbe.ReadPointer(bytes, 0)),
                };

            default:
                // JToken.FromObject because ReadScalar returns several distinct CLR types.
                return new JObject
                {
                    ["format"] = format,
                    ["value"] = JToken.FromObject(ReadScalar(bytes, format)),
                };
        }
    }

    private static object ReadScalar(byte[] bytes, string format)
    {
        // Every scalar format below needs the same size check; the switch on format keeps the
        // conversions explicit so a wrong size returns a clear error rather than garbage.
        switch (format)
        {
            case "u8": Require(bytes, 1, format); return bytes[0];
            case "i8": Require(bytes, 1, format); return (sbyte)bytes[0];
            case "u16": Require(bytes, 2, format); return BitConverter.ToUInt16(bytes, 0);
            case "i16": Require(bytes, 2, format); return BitConverter.ToInt16(bytes, 0);
            case "u32": Require(bytes, 4, format); return BitConverter.ToUInt32(bytes, 0);
            case "i32": Require(bytes, 4, format); return BitConverter.ToInt32(bytes, 0);
            case "u64": Require(bytes, 8, format); return Hex(BitConverter.ToUInt64(bytes, 0));
            case "i64": Require(bytes, 8, format); return BitConverter.ToInt64(bytes, 0);
            case "f32": Require(bytes, 4, format); return Math.Round(BitConverter.ToSingle(bytes, 0), 4);
            case "f64": Require(bytes, 8, format); return Math.Round(BitConverter.ToDouble(bytes, 0), 6);
            default: throw new ToolException($"internal error: unhandled scalar format '{format}'");
        }
    }

    private static void Require(byte[] bytes, int size, string format)
    {
        if (bytes.Length < size)
            throw new ToolException($"format '{format}' needs {size} bytes but only {bytes.Length} were readable");
    }

    private static int ResolveLength(string format, int? requested, int maxConfigured)
    {
        // A null request means "whatever this format naturally needs" so the caller does not
        // have to know the size of a u32 before asking for one.
        var length = requested ?? format switch
        {
            "hexdump" => 256,
            "bytes" => 64,
            "string" => 1024,
            "utf16" => 512,
            "u8" or "i8" or "bool" => 1,
            "u16" or "i16" => 2,
            "u32" or "i32" or "f32" => 4,
            "u64" or "i64" or "f64" or "pointer" => 8,
            _ => 64,
        };

        if (length <= 0) throw new ToolException("length must be greater than zero");
        if (length > maxConfigured)
            throw new ToolException(
                $"length {length} exceeds the configured limit of {maxConfigured} bytes " +
                "(raise 'Max memory read bytes' in the plugin settings to read more)");

        return length;
    }

    // ------------------------------------------------------------- helpers

    private static JObject DescribeRegion(MemoryProbe.RegionInfo region) => new()
    {
        ["address"] = Hex(region.Address),
        ["regionBase"] = Hex(region.RegionBase),
        ["regionSize"] = region.RegionSize,
        ["regionEnd"] = Hex(region.RegionBase + region.RegionSize),
        ["committed"] = region.Committed,
        ["readable"] = region.Readable,
        ["writable"] = region.Writable,
        ["executable"] = region.Executable,
        ["guarded"] = region.Guarded,
        ["protection"] = region.ProtectionText,
        ["protectionFlags"] = $"0x{region.Protection:X}",
    };

    private static JObject Section(IntPtr baseAddress, int size) => new()
    {
        ["base"] = Hex((ulong)baseAddress.ToInt64()),
        ["size"] = size,
        ["end"] = Hex((ulong)baseAddress.ToInt64() + (ulong)size),
    };

    /// <summary>
    /// Parses an address from JSON. Accepts a hex string ("0x7FF6..."), a decimal string, or a
    /// JSON integer. Hex is preferred because a 64-bit address does not survive a JSON number.
    /// </summary>
    private static bool TryParseAddress(JToken? token, out ulong address, out string error)
    {
        address = 0;
        error = string.Empty;

        if (token is null || token.Type == JTokenType.Null)
        {
            error = "missing required parameter: address";
            return false;
        }

        if (token.Type == JTokenType.Integer)
        {
            try
            {
                var value = token.Value<long>();
                if (value < 0)
                {
                    error = "address must not be negative";
                    return false;
                }

                address = (ulong)value;
                return true;
            }
            catch (Exception)
            {
                // Falls through to the string path for integers too large for Int64.
            }
        }

        var text = token.ToString().Trim();
        if (text.Length == 0)
        {
            error = "address must not be empty";
            return false;
        }

        try
        {
            address = ParseAddressText(text);
            return true;
        }
        catch (Exception)
        {
            error = $"could not parse address '{text}'; use a hex string such as \"0x7FF6A1B2C3D4\"";
            return false;
        }
    }

    private static ulong ParseAddressText(string text)
    {
        // Tolerate the ways an agent may paste an address: 0x-prefixed, bare hex letters,
        // decimal, trailing 'h' (assembly style), and '_' separators.
        var cleaned = text.Replace("_", string.Empty).Replace(" ", string.Empty);

        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(cleaned[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        if (cleaned.EndsWith("h", StringComparison.OrdinalIgnoreCase) && cleaned.Length > 1)
            return ulong.Parse(cleaned[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var hasHexDigit = cleaned.Any(c => c is >= 'a' and <= 'f' or >= 'A' and <= 'F');
        return hasHexDigit
            ? ulong.Parse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static long ParseOffset(string text)
    {
        var cleaned = text.Trim().Replace("_", string.Empty);
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.Parse(cleaned[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var hasHexDigit = cleaned.Any(c => c is >= 'a' and <= 'f' or >= 'A' and <= 'F');
        return hasHexDigit
            ? long.Parse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats an address for output. Always a hex string: JSON numbers lose precision past
    /// 2^53 and game addresses routinely sit above that.
    /// </summary>
    private static string Hex(ulong address) => $"0x{address:X}";
}
