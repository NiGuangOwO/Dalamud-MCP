using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudMCP.Mcp;

/// <summary>
/// A parsed HTTP/1.1 request plus the connection it arrived on.
/// The handler owning this context must either call <see cref="WriteResponseAsync"/>
/// / <see cref="WriteJsonAsync"/>, or <see cref="BeginEventStreamAsync"/> for SSE.
/// </summary>
public sealed class HttpContext
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string RawQuery { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }
    public required NetworkStream Stream { get; init; }
    public required string RemoteEndPoint { get; init; }
    public bool KeepAlive { get; set; } = true;

    /// <summary>Set once any response bytes have been written for this request.</summary>
    public bool ResponseStarted { get; private set; }

    /// <summary>
    /// Set by a handler that will close the socket after this response even though the
    /// request allowed keep-alive. The response head must then advertise the close:
    /// a client told "keep-alive" that later finds the socket gone sends its next
    /// request into a dead pooled connection and fails with WSAECONNABORTED (10053).
    /// </summary>
    public bool CloseAfterResponse { get; set; }

    private readonly SemaphoreSlim writeLock = new(1, 1);

    public string BodyText => Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Body);

    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : string.Empty;

    public string? QueryParam(string name)
    {
        if (string.IsNullOrEmpty(RawQuery)) return null;
        foreach (var pair in RawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                if (string.Equals(Uri.UnescapeDataString(pair), name, StringComparison.OrdinalIgnoreCase)) return string.Empty;
                continue;
            }

            if (string.Equals(Uri.UnescapeDataString(pair[..eq]), name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }

        return null;
    }

    public async Task WriteResponseAsync(
        int status,
        string reason,
        string contentType,
        byte[] payload,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null,
        CancellationToken ct = default)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            head.Append("Access-Control-Allow-Origin: *\r\n");
            head.Append("Access-Control-Allow-Headers: Content-Type, Authorization, Mcp-Session-Id, Mcp-Protocol-Version, Accept, Last-Event-ID\r\n");
            head.Append("Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n");
            head.Append("Access-Control-Expose-Headers: Mcp-Session-Id, Mcp-Protocol-Version\r\n");
            if (extraHeaders is not null)
                foreach (var h in extraHeaders) head.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            head.Append("Connection: ").Append(KeepAlive && !CloseAfterResponse ? "keep-alive" : "close").Append("\r\n");
            head.Append("\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            await Stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
            if (payload.Length > 0) await Stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await Stream.FlushAsync(ct).ConfigureAwait(false);
            ResponseStarted = true;
        }
        finally
        {
            writeLock.Release();
        }
    }

    public Task WriteJsonAsync(string json, int status = 200, string reason = "OK", CancellationToken ct = default) =>
        WriteResponseAsync(status, reason, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), null, ct);

    public Task WriteTextAsync(string text, int status = 200, string reason = "OK", CancellationToken ct = default) =>
        WriteResponseAsync(status, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), null, ct);

    public Task WriteEmptyAsync(int status, string reason, CancellationToken ct = default) =>
        WriteResponseAsync(status, reason, "text/plain; charset=utf-8", Array.Empty<byte>(), null, ct);

    /// <summary>
    /// Opens a <c>text/event-stream</c> response. After this returns, call
    /// <see cref="SendEventAsync"/> to push messages. The connection must stay open
    /// until the returned token source is cancelled.
    /// </summary>
    public async Task SendEventAsync(string? eventName, string data, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(eventName)) sb.Append("event: ").Append(eventName).Append('\n');
        foreach (var line in data.Split('\n')) sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');

        // An event stream is framed with chunked transfer-encoding, so every SSE
        // payload must be wrapped in a chunk or the client sees framing garbage.
        await WriteChunkAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
    }

    /// <summary>Writes the terminating zero-length chunk of a chunked response.</summary>
    public async Task EndChunkedResponseAsync(CancellationToken ct = default)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"), ct).ConfigureAwait(false);
            await Stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { /* client already gone */ }
        catch (ObjectDisposedException) { /* client already gone */ }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task BeginEventStreamAsync(int status = 200, CancellationToken ct = default)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(" OK\r\n");
            head.Append("Content-Type: text/event-stream; charset=utf-8\r\n");
            head.Append("Cache-Control: no-cache, no-store\r\n");
            head.Append("Access-Control-Allow-Origin: *\r\n");
            head.Append("Access-Control-Allow-Headers: Content-Type, Authorization, Mcp-Session-Id, Mcp-Protocol-Version, Accept\r\n");
            head.Append("X-Accel-Buffering: no\r\n");
            head.Append("Connection: ").Append(CloseAfterResponse || !KeepAlive ? "close" : "keep-alive").Append("\r\n");
            head.Append("Transfer-Encoding: chunked\r\n");
            head.Append("\r\n");
            await Stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
            await Stream.FlushAsync(ct).ConfigureAwait(false);
            ResponseStarted = true;
            EventStreamOpen = true;
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>True once <see cref="BeginEventStreamAsync"/> succeeded.</summary>
    public bool EventStreamOpen { get; private set; }

    /// <summary>Writes one chunked transfer-encoding frame (used for SSE bodies).</summary>
    public async Task WriteChunkAsync(byte[] data, CancellationToken ct = default)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var header = Encoding.ASCII.GetBytes(data.Length.ToString("x") + "\r\n");
            await Stream.WriteAsync(header, ct).ConfigureAwait(false);
            await Stream.WriteAsync(data, ct).ConfigureAwait(false);
            await Stream.WriteAsync(new byte[] { 13, 10 }, ct).ConfigureAwait(false);
            await Stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }
}

/// <summary>
/// Minimal, dependency-free HTTP/1.1 server built directly on <see cref="TcpListener"/>.
///
/// This deliberately avoids <c>HttpListener</c> (HTTP.SYS): binding a raw socket to
/// 127.0.0.1 requires no URL ACL / elevation, works identically inside the game
/// process, and keeps the listener off every non-loopback interface.
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private readonly Func<HttpContext, Task<bool>> handler;
    private readonly Action<string> log;
    private readonly CancellationTokenSource cts = new();
    private TcpListener? listener;
    private Task? acceptLoop;

    public MiniHttpServer(Func<HttpContext, Task<bool>> handler, Action<string> log)
    {
        this.handler = handler;
        this.log = log;
    }

    public int Port { get; private set; }

    public bool IsRunning => listener is not null;

    public void Start(int port)
    {
        if (listener is not null) return;

        var l = new TcpListener(IPAddress.Loopback, port);
        l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        l.Start(64);
        listener = l;
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        var owner = l;
        acceptLoop = Task.Run(() => AcceptLoopAsync(owner, cts.Token));
        log($"listening on http://127.0.0.1:{Port}/");
    }

    public void Stop()
    {
        var l = listener;
        listener = null;

        // TcpListener.Stop() closes the listening socket; the blocked accept in
        // AcceptLoopAsync then faults out (ObjectDisposedException / SocketException)
        // and the loop observes that its listener is no longer the current one.
        try { l?.Stop(); } catch { /* shutdown race */ }
    }

    private async Task AcceptLoopAsync(TcpListener owner, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                // Bound to the listener this loop was started for, not the mutable
                // field: Stop() nulls the field, and a restart installs a different
                // listener, so re-reading it would either null-deref or steal accepts
                // from the new listener (leaking the old socket's binds).
                client = await owner.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; } // listener stopped
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                if (!ReferenceEquals(listener, owner)) break; // this loop's listener is gone
                log($"accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => ServeClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = "unknown";
        try
        {
            remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            client.NoDelay = true;
            using var stream = client.GetStream();

            while (!ct.IsCancellationRequested)
            {
                var ctx = await ReadRequestAsync(stream, remote, ct).ConfigureAwait(false);
                if (ctx is null) break; // clean EOF or unparseable request

                bool keepAlive;
                try
                {
                    keepAlive = await handler(ctx).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log($"handler error: {ex.GetType().Name}: {ex.Message}");
                    ctx.CloseAfterResponse = true;
                    if (!ctx.ResponseStarted)
                    {
                        try
                        {
                            await ctx.WriteJsonAsync(
                                $"{{\"error\":\"internal\",\"message\":{JsonString(ex.Message)}}}",
                                500, "Internal Server Error", ct).ConfigureAwait(false);
                        }
                        catch { /* client gone */ }
                    }

                    keepAlive = false;
                }

                if (!keepAlive || !ctx.KeepAlive) break;
            }
        }
        catch (IOException) { /* client disconnected mid-request */ }
        catch (SocketException) { /* client disconnected */ }
        catch (ObjectDisposedException) { /* server shutting down */ }
        catch (Exception ex)
        {
            log($"connection {remote} failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { client.Dispose(); } catch { /* already gone */ }
        }
    }

    private static string JsonString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    private static async Task<HttpContext?> ReadRequestAsync(NetworkStream stream, string remote, CancellationToken ct)
    {
        // ---- request line + headers (terminated by CRLFCRLF) ----
        var buffer = new byte[8192];
        var head = new MemoryStream();
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) return null; // connection closed
            head.Write(buffer, 0, read);
            if (head.Length > MaxHeaderBytes) return null;

            var arr = head.GetBuffer();
            headerEnd = FindHeaderEnd(arr, (int)head.Length);
        }

        var headBytes = head.GetBuffer();
        var headText = Encoding.ASCII.GetString(headBytes, 0, headerEnd);
        var lines = headText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ', 3);
        if (parts.Length < 2) return null;
        var method = parts[0];
        var target = parts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var path = target;
        var rawQuery = string.Empty;
        var q = target.IndexOf('?');
        if (q >= 0)
        {
            path = target[..q];
            rawQuery = target[(q + 1)..];
        }

        // ---- optional body ----
        var body = Array.Empty<byte>();
        var contentLength = headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) ? cl : 0;
        var chunked = headers.TryGetValue("Transfer-Encoding", out var te)
                      && te.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        // Bytes already buffered past the header terminator belong to the body.
        var alreadyRead = (int)head.Length - (headerEnd + 4);

        if (chunked)
        {
            body = await ReadChunkedBodyAsync(stream, headBytes, headerEnd + 4, alreadyRead, ct).ConfigureAwait(false);
        }
        else if (contentLength > 0)
        {
            if (contentLength > MaxBodyBytes) return null;
            body = new byte[contentLength];
            var copied = Math.Min(alreadyRead, contentLength);
            if (copied > 0) Buffer.BlockCopy(headBytes, headerEnd + 4, body, 0, copied);

            var offset = copied;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
                if (read <= 0) break;
                offset += read;
            }
        }

        var keepAlive = true;
        if (headers.TryGetValue("Connection", out var conn))
        {
            if (conn.Contains("close", StringComparison.OrdinalIgnoreCase)) keepAlive = false;
        }
        else if (!string.Equals(parts.Length > 2 ? parts[2] : "HTTP/1.1", "HTTP/1.1", StringComparison.OrdinalIgnoreCase))
        {
            keepAlive = false; // HTTP/1.0 defaults to close
        }

        return new HttpContext
        {
            Method = method,
            Path = path,
            RawQuery = rawQuery,
            Headers = headers,
            Body = body,
            Stream = stream,
            RemoteEndPoint = remote,
            KeepAlive = keepAlive,
        };
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        NetworkStream stream, byte[] buffered, int start, int available, CancellationToken ct)
    {
        var sink = new MemoryStream();
        var staging = new MemoryStream();
        if (available > 0) staging.Write(buffered, start, available);

        async Task EnsureAsync(int needed)
        {
            while (staging.Length < needed)
            {
                var tmp = new byte[8192];
                var n = await stream.ReadAsync(tmp, ct).ConfigureAwait(false);
                if (n <= 0) break;
                var pos = staging.Position;
                staging.Position = staging.Length;
                staging.Write(tmp, 0, n);
                staging.Position = pos;
            }
        }

        string ReadLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                var b = staging.ReadByte();
                if (b < 0) break;
                if (b == '\n')
                {
                    if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                    break;
                }

                sb.Append((char)b);
            }

            return sb.ToString();
        }

        while (true)
        {
            await EnsureAsync((int)staging.Length + 1).ConfigureAwait(false);
            var sizeLine = ReadLine();
            var semi = sizeLine.IndexOf(';');
            if (semi >= 0) sizeLine = sizeLine[..semi];
            if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size)) break;
            if (size == 0)
            {
                // consume trailer + final CRLF
                await EnsureAsync((int)staging.Length + 1).ConfigureAwait(false);
                ReadLine();
                break;
            }

            await EnsureAsync((int)staging.Length + size + 1).ConfigureAwait(false);
            var chunk = new byte[size];
            for (var i = 0; i < size; i++)
            {
                var b = staging.ReadByte();
                if (b < 0) break; // EnsureAsync above guarantees availability
                chunk[i] = (byte)b;
            }

            sink.Write(chunk, 0, size);
            await EnsureAsync((int)staging.Length + 1).ConfigureAwait(false);
            ReadLine(); // trailing CRLF of the chunk
        }

        return sink.ToArray();
    }

    private static int FindHeaderEnd(byte[] buf, int len)
    {
        for (var i = 0; i + 3 < len; i++)
        {
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                return i;
        }

        return -1;
    }

    public void Dispose()
    {
        try { cts.Cancel(); } catch { /* already disposed */ }
        Stop();
        try { cts.Dispose(); } catch { /* already disposed */ }
        _ = acceptLoop;
    }
}
