namespace PlanGuard.Cli;

/// <summary>Thrown for invalid command lines; maps to exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// A deliberately small parser: <c>plan-guard &lt;command&gt; --name value --flag</c>. The tool has five commands
/// and a dozen options, which does not justify a parsing framework dependency.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    public string Command { get; }

    private CommandLine(string command) => Command = command;

    public static CommandLine Parse(IReadOnlyList<string> args, IReadOnlySet<string> flags)
    {
        if (args.Count == 0 || args[0].StartsWith('-'))
        {
            throw new UsageException("Missing command.");
        }

        var cl = new CommandLine(args[0]);
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3)
            {
                throw new UsageException($"Unexpected argument '{arg}'.");
            }

            var name = arg[2..];
            string? value = null;
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (!flags.Contains(name))
            {
                if (i + 1 >= args.Count)
                {
                    throw new UsageException($"Option --{name} needs a value.");
                }

                value = args[++i];
            }

            if (!cl._options.TryAdd(name, value))
            {
                throw new UsageException($"Option --{name} given twice.");
            }
        }

        return cl;
    }

    public string? Get(string name, string? envFallback = null)
    {
        _read.Add(name);
        if (_options.TryGetValue(name, out var v))
        {
            return v;
        }

        return envFallback is null ? null : NullIfEmpty(Environment.GetEnvironmentVariable(envFallback));
    }

    public string Require(string name, string? envFallback = null) =>
        Get(name, envFallback) ?? throw new UsageException(envFallback is null
            ? $"Option --{name} is required."
            : $"Option --{name} (or environment variable {envFallback}) is required.");

    public bool Flag(string name)
    {
        _read.Add(name);
        return _options.ContainsKey(name);
    }

    public int Int(string name, int fallback, int min, int max)
    {
        var raw = Get(name);
        if (raw is null)
        {
            return fallback;
        }

        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v
            : throw new UsageException($"Option --{name} must be an integer between {min} and {max}.");
    }

    /// <summary>Fails on options the command never asked for, so a typo does not silently change behaviour.</summary>
    public void EnsureAllUsed()
    {
        var unknown = _options.Keys.Where(k => !_read.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new UsageException($"Unknown option(s) for '{Command}': {string.Join(", ", unknown.Select(u => "--" + u))}.");
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
