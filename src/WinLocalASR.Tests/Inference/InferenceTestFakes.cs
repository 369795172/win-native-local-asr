using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WinLocalASR.Core.Inference;
using Xunit;

namespace WinLocalASR.Tests.Inference;

internal sealed class FakeProcessSpawner : IProcessSpawner
{
    public List<ProcessStartInfo> StartInfos { get; } = new();

    public List<FakeProcess> Processes { get; } = new();

    public int SpawnCount => StartInfos.Count;

    public Func<ProcessStartInfo, FakeProcess> Factory { get; set; } = _ => new FakeProcess();

    public IManagedProcess Spawn(ProcessStartInfo startInfo)
    {
        StartInfos.Add(startInfo);
        FakeProcess process = Factory(startInfo);
        Processes.Add(process);
        return process;
    }
}

internal sealed class FakeProcess : IManagedProcess
{
    private bool _exitRaised;

    public bool Killed { get; private set; }

    public bool HasExited { get; private set; }

    public event EventHandler? Exited;

    public event EventHandler<string>? OutputLineReceived;

    public event EventHandler<string>? ErrorLineReceived;

    public void EmitOutput(string line) => OutputLineReceived?.Invoke(this, line);

    public void EmitError(string line) => ErrorLineReceived?.Invoke(this, line);

    public void SimulateExit()
    {
        if (_exitRaised)
        {
            return;
        }

        _exitRaised = true;
        HasExited = true;
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public void Kill()
    {
        Killed = true;
        SimulateExit();
    }

    public void Dispose()
    {
    }
}

internal sealed record FakeHttpResponse(int StatusCode, string ContentType, string Body);

internal sealed record RecordedRequest(string Method, string Path, string ContentType, byte[] Body);

/// <summary>Real localhost HTTP server (HttpListener) speaking the llama-server wire contract.</summary>
internal sealed class FakeLlamaServer : IDisposable
{
    private readonly HttpListener _listener;
    private volatile bool _stopped;

    private FakeLlamaServer(int port, HttpListener listener)
    {
        Port = port;
        _listener = listener;
        Responder = DefaultResponder;
    }

    public int Port { get; }

    public List<RecordedRequest> Requests { get; } = new();

    public Func<HttpListenerRequest, Task<FakeHttpResponse>> Responder { get; set; }

    public static FakeLlamaServer Start()
    {
        int port = PortProber.FindFreePort(basePort: 19100, reservedPort: -1);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = new FakeLlamaServer(port, listener);
        _ = server.AcceptLoopAsync();
        return server;
    }

    public static async Task<FakeHttpResponse> DefaultResponder(HttpListenerRequest request) => RequestPath(request) switch
    {
        "/health" => new FakeHttpResponse(200, "application/json", "{\"status\":\"ok\"}"),
        "/v1/audio/transcriptions" => new FakeHttpResponse(
            200,
            "application/json",
            "{\"type\":\"transcript.text.done\",\"text\":\"language English<asr_text>hello\",\"usage\":{\"type\":\"tokens\",\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}"),
        _ => new FakeHttpResponse(404, "application/json", "{\"error\":\"not found\"}"),
    };

    public static string RequestPath(HttpListenerRequest request) => request.Url?.AbsolutePath ?? "";

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stopped)
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(context));
            }
        }
        catch
        {
            // listener stopped — exit the accept loop
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            byte[] body = await ReadBodyAsync(context.Request);
            lock (Requests)
            {
                Requests.Add(new RecordedRequest(
                    context.Request.HttpMethod,
                    context.Request.Url?.AbsolutePath ?? "",
                    context.Request.ContentType ?? "",
                    body));
            }

            FakeHttpResponse response = await Responder(context.Request);
            byte[] buffer = Encoding.UTF8.GetBytes(response.Body);
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
            }
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        using var buffer = new MemoryStream();
        await request.InputStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public void Stop()
    {
        _stopped = true;
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }
    }

    public void Dispose() => Stop();
}

internal sealed record MultipartPart(string Name, string? Filename, string? ContentType, byte[] Content);

/// <summary>Byte-level multipart/form-data parser so tests verify real request formation.</summary>
internal static class MultipartFormData
{
    public static List<MultipartPart> Parse(string requestContentType, byte[] body)
    {
        string boundary = Regex.Match(requestContentType, "boundary=\"?([^\";]+)\"?").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(boundary), "multipart body has no boundary in its Content-Type");
        byte[] delimiter = Encoding.UTF8.GetBytes("--" + boundary);

        var parts = new List<MultipartPart>();
        int[] boundaries = FindAll(body, delimiter);
        for (int i = 0; i < boundaries.Length - 1; i++)
        {
            int start = boundaries[i] + delimiter.Length;
            int end = boundaries[i + 1];
            if (start >= end)
            {
                continue;
            }

            byte[] segment = body[start..end];
            segment = TrimCrLf(segment);
            if (segment.Length == 0 || StartsWithDashDash(segment))
            {
                continue; // preamble or the closing "--" segment
            }

            parts.Add(ParsePart(segment));
        }

        return parts;
    }

    private static MultipartPart ParsePart(byte[] segment)
    {
        int separator = IndexOf(segment, "\r\n\r\n"u8.ToArray());
        Assert.True(separator >= 0, "multipart part has no header/body separator");
        string headerBlock = Encoding.UTF8.GetString(segment[..separator]);
        byte[] content = segment[(separator + 4)..];

        string name = "";
        string? filename = null;
        string? contentType = null;
        foreach (string headerLine in headerBlock.Split("\r\n"))
        {
            if (headerLine.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
            {
                // .NET emits both quoted (name="file") and token (name=file) forms, plus RFC 5987 filename*
                name = DispositionValue(headerLine, "name");
                string rawFilename = DispositionValue(headerLine, "filename");
                filename = rawFilename.Length == 0 ? null : rawFilename;
            }
            else if (headerLine.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            {
                contentType = headerLine["Content-Type:".Length..].Trim();
            }
        }

        return new MultipartPart(name, filename, contentType, content);
    }

    private static string DispositionValue(string headerLine, string key)
    {
        Match match = Regex.Match(headerLine, key + "=(?:\"(?<quoted>[^\"]*)\"|(?<raw>[^;]*))");
        if (!match.Success)
        {
            return "";
        }

        return match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["raw"].Value.Trim();
    }

    private static byte[] TrimCrLf(byte[] segment)
    {
        int start = 0;
        int end = segment.Length;
        if (start < end && segment[start] == '\r' && start + 1 < end && segment[start + 1] == '\n')
        {
            start += 2;
        }

        if (end > start && segment[end - 1] == '\n' && end - 1 > start && segment[end - 2] == '\r')
        {
            end -= 2;
        }

        return segment[start..end];
    }

    private static bool StartsWithDashDash(byte[] segment) =>
        segment.Length >= 2 && segment[0] == '-' && segment[1] == '-';

    private static int[] FindAll(byte[] haystack, byte[] needle)
    {
        var positions = new List<int>();
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                positions.Add(i);
                i += needle.Length - 1;
            }
        }

        return positions.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        var positions = FindAll(haystack, needle);
        return positions.Length > 0 ? positions[0] : -1;
    }
}

internal static class InferenceWavFile
{
    public static (string Path, byte[] Bytes) WriteTempWav(double seconds, int sampleRate = 24000)
    {
        int dataBytes = checked((int)(sampleRate * 2 * seconds));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        byte[] bytes = stream.ToArray();
        string path = Path.Combine(Path.GetTempPath(), $"winlocalasr-test-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, bytes);
        return (path, bytes);
    }
}
