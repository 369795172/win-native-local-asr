namespace WinLocalASR.Core.Inference;

/// <summary>
/// Error taxonomy mirroring Swift ASRBridgeClient.BridgeError one-to-one so the Task 6 state
/// machine port can map cases without reinterpretation:
///   BridgeError.notReady        -> NotReadyException
///   BridgeError.processExited   -> ProcessExitedException
///   BridgeError.timeout         -> TimeoutException
///   BridgeError.runtime(String) -> RuntimeException (carries HTTP status + body summary)
///   BridgeError.restartFailed   -> RestartFailedException
///
/// Swift cases without a dedicated port: requestInProgress (the HTTP transport removes the
/// single-JSONL-pipe constraint), unexpectedResponse (mapped to RuntimeException), and
/// modelLoadFailed (startup handshake failure surfaces as ProcessExitedException on early exit
/// or TimeoutException when /health never turns healthy within StartupTimeout).
/// </summary>
public class LlamaServerException : Exception
{
    public LlamaServerException(string message)
        : base(message)
    {
    }

    public LlamaServerException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Swift: BridgeError.notReady — TranscribeAsync called before StartAsync reached ready.</summary>
public sealed class NotReadyException : LlamaServerException
{
    public NotReadyException()
        : base("The inference server is not ready.")
    {
    }
}

/// <summary>Swift: BridgeError.processExited — llama-server died, is unreachable, or could not start.</summary>
public sealed class ProcessExitedException : LlamaServerException
{
    public ProcessExitedException(string message)
        : base(message)
    {
    }

    public ProcessExitedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Swift: BridgeError.timeout. Deliberately shadows System.TimeoutException inside this
/// namespace to keep the Task 4/6 taxonomy names 1:1 with Swift. Consumer files that import
/// both this namespace and System should catch the LlamaServerException base, qualify the
/// name, or add `using TimeoutException = WinLocalASR.Core.Inference.TimeoutException;`.
/// </summary>
public sealed class TimeoutException : LlamaServerException
{
    public TimeoutException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Swift: BridgeError.runtime(String) — non-success HTTP reply (StatusCode + BodySummary) or an
/// unexpected response shape (StatusCode = 0).
/// </summary>
public sealed class RuntimeException : LlamaServerException
{
    public RuntimeException(int statusCode, string? bodySummary)
        : base($"llama-server returned HTTP {statusCode}: {bodySummary}")
    {
        StatusCode = statusCode;
        BodySummary = bodySummary;
    }

    public RuntimeException(string message)
        : base(message)
    {
        StatusCode = 0;
    }

    /// <summary>HTTP status code of the failed reply; 0 when the response shape was invalid.</summary>
    public int StatusCode { get; }

    public string? BodySummary { get; }
}

/// <summary>Swift: BridgeError.restartFailed — the crash auto-restart loop exhausted all attempts.</summary>
public sealed class RestartFailedException : LlamaServerException
{
    public RestartFailedException(string message)
        : base(message)
    {
    }
}
