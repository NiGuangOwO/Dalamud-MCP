using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Captures the game window as a PNG for the model to look at. The pixels are taken from the
/// window's own GDI surface, so this works whether the client is focused, occluded or behind
/// another window, and it never touches game state.
/// </summary>
internal static class ScreenshotTools
{
    private const int DefaultMaxDimension = 1920;
    private const int DefaultTtlSeconds = 600;

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "capture_game_screenshot",
            "Capture game screenshot",
            "Captures the game as a PNG and returns it base64-encoded. 'client' grabs only the 3D " +
            "scene, 'window' includes the title bar and borders. The client may be in the background; " +
            "the capture is taken from its own surface rather than the screen. Set save=true to also " +
            "write the file into the plugin's captures folder.",
            Json.SchemaWithEnum(
                "area",
                new[] { "client", "window" },
                "Capture only the 3D client area, or the whole window with its frame (default: client)",
                false,
                ("save", "boolean", "Also write the PNG into the plugin's captures folder (default: false)", false),
                ("ttlSeconds", "integer", "Lifetime of a saved file before the next capture prunes it, 60-86400 (default: 600)", false),
                ("maxDimension", "integer", "Long-edge pixel cap; larger captures are downscaled, 256-3840 (default: 1920)", false)),
            args => Capture(svc, args));
    }

    private static JObject Capture(GameServices svc, JObject args)
    {
        var area = (args.Value<string>("area") ?? "client").Trim().ToLowerInvariant();
        if (area.Length == 0) area = "client";
        if (area != "client" && area != "window")
            throw new ToolException("area must be 'client' or 'window'");

        var maxDimension = ClampInt(args, "maxDimension", 256, 3840, DefaultMaxDimension);
        var ttlSeconds = ClampInt(args, "ttlSeconds", 60, 86400, DefaultTtlSeconds);
        var save = args.Value<bool?>("save") ?? false;

        var hwnd = FindMainWindow();
        if (hwnd == IntPtr.Zero)
            throw new ToolException("could not locate the game window");

        var clientOnly = area == "client";
        Rect rect;
        if (clientOnly)
        {
            if (!GetClientRect(hwnd, out var client))
                throw new ToolException("could not measure the game client area");

            var origin = new Point { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin))
                throw new ToolException("could not map the game client area to the screen");

            rect = new Rect
            {
                Left = origin.X,
                Top = origin.Y,
                Right = origin.X + client.Right,
                Bottom = origin.Y + client.Bottom,
            };
        }
        else if (!GetWindowRect(hwnd, out rect))
        {
            throw new ToolException("could not measure the game window");
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new ToolException("the game window has no drawable area");

        byte[] pixels;
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new ToolException("could not acquire a device context for the screen");

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = memoryDc != IntPtr.Zero ? CreateCompatibleBitmap(screenDc, width, height) : IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new ToolException("could not allocate a drawing surface for the capture");

            previous = SelectObject(memoryDc, bitmap);

            // Let the compositor finish the frame it is currently building; without this the
            // captured surface can be one frame behind (or blank on first call after a resize).
            _ = DwmFlush();

            var flags = clientOnly ? PwClientOnly | PwRenderFullContent : PwRenderFullContent;
            if (!PrintWindow(hwnd, memoryDc, flags))
            {
                // Some drivers refuse PrintWindow on DirectX surfaces; fall back to the screen
                // itself. This can pick up an overlapping window, which is why it is only a
                // fallback.
                if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SrcCopy | CaptureBlt))
                    throw new ToolException("could not read the game window surface");
            }

            pixels = ReadPixels(memoryDc, bitmap, width, height);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        var longEdge = Math.Max(width, height);
        var scale = longEdge > maxDimension ? (double)maxDimension / longEdge : 1.0;
        var outWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outHeight = Math.Max(1, (int)Math.Round(height * scale));

        var rgba = ToRgba(pixels, width, height, outWidth, outHeight);
        var png = EncodePng(rgba, outWidth, outHeight);

        var payload = new JObject
        {
            ["format"] = "png",
            ["area"] = area,
            ["width"] = outWidth,
            ["height"] = outHeight,
            ["sourceWidth"] = width,
            ["sourceHeight"] = height,
            ["bytes"] = png.Length,
            ["imageBase64"] = Convert.ToBase64String(png),
        };

        if (save)
        {
            payload["savedPath"] = SaveCapture(svc, png, ttlSeconds);
            payload["expiresInSeconds"] = ttlSeconds;
        }

        return payload;
    }

    // ------------------------------------------------------------------
    // Window discovery
    // ------------------------------------------------------------------

    private static IntPtr FindMainWindow()
    {
        // For a plugin the "current process" is the game, so this is the game's own window.
        var handle = Process.GetCurrentProcess().MainWindowHandle;
        if (handle != IntPtr.Zero) return handle;

        // MainWindowHandle is empty when the process has no window the shell recognises as its
        // main window; pick the largest visible, owner-less, same-process window instead.
        var pid = Environment.ProcessId;
        var best = IntPtr.Zero;
        long bestArea = 0;

        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out var windowPid);
            if (windowPid != pid) return true;
            if (!IsWindowVisible(candidate)) return true;
            if (GetWindow(candidate, GwOwner) != IntPtr.Zero) return true;
            if (!GetClientRect(candidate, out var client)) return true;

            var area = (long)client.Right * client.Bottom;
            if (area > bestArea)
            {
                bestArea = area;
                best = candidate;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    // ------------------------------------------------------------------
    // Pixel plumbing
    // ------------------------------------------------------------------

    private static byte[] ReadPixels(IntPtr memoryDc, IntPtr bitmap, int width, int height)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                // A negative height asks for a top-down scan, so row 0 is the top of the image.
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
            },
        };

        var buffer = new byte[width * 4 * height];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var scanned = GetDIBits(memoryDc, bitmap, 0, (uint)height, pinned.AddrOfPinnedObject(), ref info, DibRgbColors);
            if (scanned == 0)
                throw new ToolException("could not decode the captured pixels");
        }
        finally
        {
            pinned.Free();
        }

        return buffer;
    }

    /// <summary>
    /// Converts the 32-bit BGRA surface into tightly packed RGBA, dropping to
    /// <paramref name="dstWidth"/>/<paramref name="dstHeight"/> with nearest-neighbour sampling.
    /// </summary>
    private static byte[] ToRgba(byte[] bgra, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var dst = new byte[dstWidth * dstHeight * 4];
        for (var y = 0; y < dstHeight; y++)
        {
            var sourceY = (int)((long)y * srcHeight / dstHeight);
            if (sourceY >= srcHeight) sourceY = srcHeight - 1;

            var sourceRow = sourceY * srcWidth;
            var destRow = y * dstWidth;

            for (var x = 0; x < dstWidth; x++)
            {
                var sourceX = (int)((long)x * srcWidth / dstWidth);
                if (sourceX >= srcWidth) sourceX = srcWidth - 1;

                var s = (sourceRow + sourceX) * 4;
                var d = (destRow + x) * 4;

                dst[d] = bgra[s + 2];
                dst[d + 1] = bgra[s + 1];
                dst[d + 2] = bgra[s];
                // GDI leaves alpha at zero for a plain window surface, which would make the PNG
                // fully transparent; the capture is opaque by definition.
                dst[d + 3] = 0xFF;
            }
        }

        return dst;
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        var stride = width * 4;

        byte[] compressed;
        using (var raw = new MemoryStream())
        {
            for (var y = 0; y < height; y++)
            {
                raw.WriteByte(0); // filter type 0 = None
                raw.Write(rgba, y * stride, stride);
            }

            using var output = new MemoryStream();
            using (var deflate = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                deflate.Write(raw.ToArray(), 0, (int)raw.Length);
            compressed = output.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteUInt32BigEndian(header, 0, (uint)width);
        WriteUInt32BigEndian(header, 4, (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: truecolour with alpha
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace

        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", Array.Empty<byte>());

        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteUInt32BigEndian(length, 0, (uint)data.Length);
        stream.Write(length, 0, 4);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, typeBytes.Length);
        stream.Write(data, 0, data.Length);

        var crc = Crc32(typeBytes, data);
        var checksum = new byte[4];
        WriteUInt32BigEndian(checksum, 0, crc);
        stream.Write(checksum, 0, 4);
    }

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var c = 0xFFFFFFFFu;
        foreach (var value in first) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
        foreach (var value in second) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // ------------------------------------------------------------------
    // Saved captures
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes the PNG under the plugin's captures folder. The file name carries its own creation
    /// time and lifetime, so pruning needs no sidecar metadata: any capture whose deadline has
    /// passed is removed on the next save.
    /// </summary>
    private static string SaveCapture(GameServices svc, byte[] png, int ttlSeconds)
    {
        var directory = CaptureDirectory(svc);
        Directory.CreateDirectory(directory);
        PruneCaptures(directory);

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var path = Path.Combine(directory, $"capture-{stamp}-{ttlSeconds}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    private static string CaptureDirectory(GameServices svc)
    {
        var root = svc.PluginInterface?.ConfigDirectory?.FullName;
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Path.GetTempPath(), "DalamudMCP");
        return Path.Combine(root, "captures");
    }

    private static void PruneCaptures(string directory)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var file in Directory.EnumerateFiles(directory, "capture-*.png"))
            {
                var parts = Path.GetFileNameWithoutExtension(file).Split('-');
                if (parts.Length != 3) continue;
                if (!long.TryParse(parts[1], out var created)) continue;
                if (!long.TryParse(parts[2], out var ttl)) continue;
                if (created + ttl * 1000 >= now) continue;

                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Still open elsewhere; it will be pruned by a later capture.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception)
        {
            // Pruning is housekeeping and must never fail a capture.
        }
    }

    private static int ClampInt(JObject args, string name, int min, int max, int fallback)
    {
        var token = args[name];
        if (token is null || token.Type == JTokenType.Null) return fallback;
        var value = token.Value<int?>();
        return value is null ? fallback : Math.Clamp(value.Value, min, max);
    }

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------

    private const uint PwClientOnly = 0x00000001;
    private const uint PwRenderFullContent = 0x00000002;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfo info, uint usage);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
