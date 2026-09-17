namespace WinLocalASR.Core.Shell;

/// <summary>
/// Startup flag bag (Task 7's forward-looking arg structure). Boolean flags are
/// <c>--name</c>; key/value pairs are <c>--name=value</c> (first '=' splits, empty values
/// allowed, last value wins). Anything else — bare words, a lone <c>--</c> — is ignored so
/// unknown future flags never break startup. Task 12's ControlServer consumes
/// <see cref="EnableControlServerFlag"/> / <see cref="FakeConfiguredFlag"/> from here.
/// </summary>
public sealed class CommandLineOptions
{
    public const string EnableControlServerFlag = "enable-control-server";
    public const string FakeConfiguredFlag = "fake-configured";

    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static CommandLineOptions Empty { get; } = new();

    public static CommandLineOptions Parse(IEnumerable<string> args)
    {
        CommandLineOptions options = new();
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                continue;
            }

            string body = arg[2..];
            int separator = body.IndexOf('=');
            if (separator < 0)
            {
                options._flags.Add(body);
            }
            else
            {
                options._values[body[..separator]] = body[(separator + 1)..];
            }
        }

        return options;
    }

    public bool HasFlag(string name) => _flags.Contains(name);

    public bool TryGetValue(string name, out string value)
    {
        if (_values.TryGetValue(name, out string? v))
        {
            value = v;
            return true;
        }

        value = "";
        return false;
    }
}
