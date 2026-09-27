using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DalamudMCP;

/// <summary>
/// Guarded direct-memory access helpers.
///
/// The agent can ask for arbitrary addresses, so every read is validated with
/// <see cref="VirtualQuery"/> on the target process before touching it. Without this,
/// a bad pointer would raise an access violation that takes the whole game client
/// down — an unacceptable failure mode for a read tool.
/// </summary>
public static class MemoryProbe
{
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, IntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Describes the OS-reported state of a memory address.</summary>
    public sealed class RegionInfo
    {
        public required ulong Address { get; init; }
        public required ulong RegionBase { get; init; }
        public required ulong RegionSize { get; init; }
        public required bool Committed { get; init; }
        public required bool Readable { get; init; }
        public required bool Writable { get; init; }
        public required bool Executable { get; init; }
        public required bool Guarded { get; init; }
        public required uint Protection { get; init; }
        public required string ProtectionText { get; init; }
    }

    /// <summary>
    /// Queries the region containing <paramref name="address"/>.
    /// Returns null when the address is not mapped at all.
    /// </summary>
    public static RegionInfo? Query(ulong address)
    {
        if (address == 0) return null;
        if (VirtualQuery((IntPtr)unchecked((long)address), out var mbi, (IntPtr)Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero)
            return null;

        var protect = mbi.Protect;
        var readable = mbi.State == MemCommit && (protect & PageNoAccess) == 0 && (protect & PageGuard) == 0;

        return new RegionInfo
        {
            Address = address,
            RegionBase = (ulong)mbi.BaseAddress.ToInt64(),
            RegionSize = (ulong)mbi.RegionSize.ToInt64(),
            Committed = mbi.State == MemCommit,
            Readable = readable,
            Writable = IsWritable(protect),
            Executable = IsExecutable(protect),
            Guarded = (protect & PageGuard) != 0,
            Protection = protect,
            ProtectionText = Describe(protect),
        };
    }

    private static bool IsWritable(uint p) =>
        p is 0x04 or 0x08 or 0x40 or 0x80; // RW, WC, RWX, WCX

    private static bool IsExecutable(uint p) =>
        p is 0x10 or 0x20 or 0x40 or 0x80; // X, RX, RWX, WCX

    private static string Describe(uint p) => p switch
    {
        0x01 => "NOACCESS",
        0x02 => "R",
        0x04 => "RW",
        0x08 => "WC",
        0x10 => "X",
        0x20 => "RX",
        0x40 => "RWX",
        0x80 => "WCX",
        _ => $"0x{p:X}",
    };

    /// <summary>
    /// Reads <paramref name="length"/> bytes, refusing addresses that the OS does not
    /// report as committed and readable. Returns null instead of faulting.
    /// </summary>
    public static byte[]? TryReadBytes(ulong address, int length, out string? error)
    {
        error = null;
        if (length <= 0)
        {
            error = "length must be greater than zero";
            return null;
        }

        if (length > 1024 * 1024)
        {
            error = "length exceeds the 1 MiB per-read limit";
            return null;
        }

        var region = Query(address);
        if (region is null)
        {
            error = $"address 0x{address:X} is not mapped in this process";
            return null;
        }

        if (!region.Committed)
        {
            error = $"address 0x{address:X} is reserved but not committed";
            return null;
        }

        if (!region.Readable)
        {
            error = $"address 0x{address:X} is not readable (protection {region.ProtectionText})";
            return null;
        }

        // Clamp the read to the end of the valid region so a request that runs off
        // the edge returns the readable part rather than faulting.
        var available = region.RegionBase + region.RegionSize;
        var maxLen = available > address ? (int)Math.Min((ulong)length, available - address) : 0;
        if (maxLen <= 0)
        {
            error = $"address 0x{address:X} has no readable bytes remaining in its region";
            return null;
        }

        var buffer = new byte[maxLen];
        if (!ReadProcessMemory(GetCurrentProcess(), (IntPtr)unchecked((long)address), buffer, (IntPtr)maxLen, out var read))
        {
            error = $"ReadProcessMemory failed at 0x{address:X} (win32 error {Marshal.GetLastWin32Error()})";
            return null;
        }

        var got = (int)read.ToInt64();
        if (got != maxLen) Array.Resize(ref buffer, got);
        if (got < length) error = $"only {got} of {length} requested bytes were readable (clamped to region end)";
        return buffer;
    }

    /// <summary>Reads a typed value at an address, or null when unreadable.</summary>
    public static T? TryReadStruct<T>(ulong address, out string? error) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        var bytes = TryReadBytes(address, size, out error);
        if (bytes is null) return null;

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Reads a NUL-terminated UTF-8 string, bounded so a bad pointer cannot run away.</summary>
    public static string? TryReadCString(ulong address, int maxLength, out string? error)
    {
        error = null;
        var bytes = TryReadBytes(address, maxLength, out error);
        if (bytes is null) return null;

        var nul = Array.IndexOf(bytes, (byte)0);
        var span = nul >= 0 ? bytes.AsSpan(0, nul) : bytes.AsSpan();
        try
        {
            return Encoding.UTF8.GetString(span);
        }
        catch (ArgumentException)
        {
            // Invalid UTF-8: fall back to a lossless-ish decode so the agent still sees data.
            return Encoding.Latin1.GetString(span);
        }
    }

    /// <summary>Formats a byte block as a classic hex dump with an ASCII gutter.</summary>
    public static List<string> HexDump(byte[] bytes, ulong baseAddress, int bytesPerLine = 16)
    {
        var lines = new List<string>();
        for (var offset = 0; offset < bytes.Length; offset += bytesPerLine)
        {
            var count = Math.Min(bytesPerLine, bytes.Length - offset);
            var hex = new StringBuilder();
            var ascii = new StringBuilder();
            for (var i = 0; i < bytesPerLine; i++)
            {
                if (i < count)
                {
                    var b = bytes[offset + i];
                    hex.Append(b.ToString("X2")).Append(' ');
                    ascii.Append(b is >= 32 and < 127 ? (char)b : '.');
                }
                else
                {
                    hex.Append("   ");
                }
            }

            lines.Add($"{baseAddress + (ulong)offset:X16}  {hex.ToString().TrimEnd()}  {ascii}");
        }

        return lines;
    }

    /// <summary>Decodes a pointer-sized value using the process's pointer width.</summary>
    public static ulong ReadPointer(byte[] bytes, int offset) =>
        BitConverter.ToUInt64(bytes, offset);
}
