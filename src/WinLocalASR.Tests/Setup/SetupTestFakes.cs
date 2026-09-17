using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using WinLocalASR.Core.Inference;

namespace WinLocalASR.Tests.Setup;

/// <summary>A request recorded by <see cref="FakeFileServer"/> (path+query and Range header).</summary>
internal sealed record FileServerRequest(string RawUrl, string? RangeHeader);

/// <summary>
/// Real localhost HTTP file server (HttpListener) serving small fake artifacts with
/// HTTP Range / 206 support — the "fresh machine" download source for QA+ and resume
/// tests. Never touches the network; ports are probed locally.
/// </summary>
internal sealed class FakeFileServer : IDisposable
{
    private static int _portCounter;

    private readonly HttpListener _listener;
    private readonly Dictionary<string, byte[]> _files;
    private readonly object _gate = new();
    private volatile bool _stopped;

    private FakeFileServer(int port, HttpListener listener, Dictionary<string, byte[]> files)
    {
        Port = port;
        _listener = listener;
        _files = files;
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public int HitCount
    {
        get
        {
            lock (_gate)
            {
                return Requests.Count;
            }
        }
    }

    private List<FileServerRequest> Requests { get; } = new();

    public IReadOnlyList<FileServerRequest> SnapshotRequests()
    {
        lock (_gate)
        {
            return Requests.ToList();
        }
    }

    public static FakeFileServer Start(Dictionary<string, byte[]> files)
    {
        // Distinct ascending bases keep parallel test servers from racing on one port.
        int basePort = 19300 + Interlocked.Increment(ref _portCounter) * 13;
        int port = PortProber.FindFreePort(basePort: basePort, reservedPort: -1);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = new FakeFileServer(port, listener, files);
        _ = server.AcceptLoopAsync();
        return server;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopped)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopped)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            try
            {
                Handle(context);
            }
            catch (IOException)
            {
                // Client went away mid-response; keep serving.
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? "";
        string? range = context.Request.Headers["Range"];
        lock (_gate)
        {
            Requests.Add(new FileServerRequest(context.Request.Url?.PathAndQuery ?? path, range));
        }

        if (!_files.TryGetValue(path, out byte[]? bytes))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        long start = 0;
        if (range is not null && TryParseRangeStart(range, out long parsed))
        {
            if (parsed >= bytes.Length)
            {
                context.Response.StatusCode = 416;
                context.Response.Close();
                return;
            }

            start = parsed;
            context.Response.StatusCode = 206;
            context.Response.Headers[HttpResponseHeader.ContentRange] =
                $"bytes {start}-{bytes.Length - 1}/{bytes.Length}";
        }

        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength64 = bytes.Length - start;
        context.Response.OutputStream.Write(bytes, (int)start, (int)(bytes.Length - start));
        context.Response.Close();
    }

    private static bool TryParseRangeStart(string range, out long start)
    {
        start = 0;
        if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string spec = range["bytes=".Length..];
        int dash = spec.IndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        return long.TryParse(spec[..dash], out start) && start > 0;
    }

    public void Dispose()
    {
        _stopped = true;
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// In-process HttpMessageHandler router: records the exact request URI of every call
/// (no http.sys path restrictions, so ghproxy's prefix form and any host can be asserted
/// verbatim) and serves stubbed responses, including Range-ignoring 200s and blocking
/// streams for cancellation tests.
/// </summary>
internal sealed class StubHttpFileHandler : HttpMessageHandler
{
    private readonly Func<string, HttpResponseMessage> _route;
    private readonly object _gate = new();

    public StubHttpFileHandler(Func<string, HttpResponseMessage> route) => _route = route;

    private List<string> RequestedUris { get; } = new();

    public IReadOnlyList<string> SnapshotUris()
    {
        lock (_gate)
        {
            return RequestedUris.ToList();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string uri = request.RequestUri!.ToString();
        lock (_gate)
        {
            RequestedUris.Add(uri);
        }

        return Task.FromResult(_route(uri));
    }

    public static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent("not found"),
    };
}

/// <summary>A stream that serves one byte, then blocks until released or cancelled.</summary>
internal sealed class BlockingStream : Stream
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _servedFirstByte;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!_servedFirstByte)
        {
            _servedFirstByte = true;
            buffer.Span[0] = 0xAA;
            return 1;
        }

        await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public void Release() => _release.TrySetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Builders for setup tests: temp manifests, fake artifacts, temp dirs.</summary>
internal static class SetupTestAssets
{
    public const string Tag = "b10964";
    public const string AssetName = "llama-b10964-bin-win-cpu-x64.zip";
    public const string MainModelFile = "Qwen3-ASR-1.7B-Q8_0.gguf";
    public const string MmprojModelFile = "mmproj-Qwen3-ASR-1.7B-Q8_0.gguf";
    public const string Repo = "ggml-org/Qwen3-ASR-1.7B-GGUF";

    public static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"winlocalasr-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] FakeBytes(int length, byte seed)
    {
        byte[] bytes = new byte[length];
        byte state = seed;
        for (int i = 0; i < length; i++)
        {
            state = (byte)(state * 31 + 17);
            bytes[i] = state;
        }

        return bytes;
    }

    /// <summary>A real zip carrying a dummy exe plus the DLL shapes the real asset ships
    /// (impl, base, dispatch variants, OpenMP) — extraction must land all of them.</summary>
    public static byte[] MakeFakeLlamaZip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, int size)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using Stream stream = entry.Open();
                byte[] payload = FakeBytes(size, (byte)(name.Length * 7 + 3));
                stream.Write(payload, 0, payload.Length);
            }

            Entry("llama-server.exe", 4096);
            Entry("llama-server-impl.dll", 16384);
            Entry("llama.dll", 8192);
            Entry("llama-common.dll", 2048);
            Entry("mtmd.dll", 2048);
            Entry("ggml.dll", 4096);
            Entry("ggml-base.dll", 8192);
            Entry("ggml-cpu-haswell.dll", 4096);
            Entry("ggml-cpu-sse42.dll", 4096);
            Entry("ggml-cpu-zen4.dll", 4096);
            Entry("ggml-rpc.dll", 2048);
            Entry("libomp.dll", 5120);
        }

        return buffer.ToArray();
    }

    public static string WriteManifest(
        string github,
        string ghProxyPrefix,
        string huggingFace,
        string hfMirror,
        string llamaSha256,
        string mainSha256,
        string mmprojSha256,
        long mainSizeBytes,
        long mmprojSizeBytes,
        string directory)
    {
        var root = new JsonObject
        {
            ["llamaCpp"] = new JsonObject
            {
                ["tag"] = Tag,
                ["assetName"] = AssetName,
                ["sha256"] = llamaSha256,
                ["sha256Policy"] = "official",
            },
            ["models"] = new JsonObject
            {
                ["main"] = ModelPin(mainSha256, mainSizeBytes, MainModelFile),
                ["mmproj"] = ModelPin(mmprojSha256, mmprojSizeBytes, MmprojModelFile),
            },
            ["urls"] = new JsonObject
            {
                ["huggingface"] = huggingFace,
                ["hfMirror"] = hfMirror,
                ["github"] = github,
                ["ghProxyPrefix"] = ghProxyPrefix,
            },
        };

        string path = Path.Combine(directory, $"versions-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static JsonObject ModelPin(string sha256, long sizeBytes, string file) => new()
    {
        ["repo"] = Repo,
        ["file"] = file,
        ["revision"] = "main",
        ["sha256"] = sha256,
        ["sizeBytes"] = sizeBytes,
    };

    public static string RepoTemplatePath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "versions.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("repo-root versions.json not found above the test bin directory");
    }
}
