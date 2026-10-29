using System.Globalization;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary>Process exit codes of rtxaco.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int NotElevated = 2;
    public const int Usage = 3;
    public const int Cancelled = 130;
}

/// <summary>An expected error: printed as one line, no stack trace.</summary>
internal sealed class CliException(string message, int exitCode = ExitCodes.Failure) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Parsed command line of one command: positional arguments, <c>--name value</c> options and flags.</summary>
internal sealed class CommandArgs
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);

    private CommandArgs(List<string> positionals) => Positionals = positionals;

    public IReadOnlyList<string> Positionals { get; }

    /// <summary>Parses the tokens against the allowed option names; unknown options are usage errors.</summary>
    public static CommandArgs Parse(
        IReadOnlyList<string> tokens, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flagOptions,
        int minPositionals, int maxPositionals)
    {
        var result = new CommandArgs([]);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                ((List<string>)result.Positionals).Add(token);
                continue;
            }

            var name = token[2..];
            string? inline = null;
            var eq = name.IndexOf('=');
            if (eq >= 0) (name, inline) = (name[..eq], name[(eq + 1)..]);

            if (flagOptions.Contains(name, StringComparer.OrdinalIgnoreCase) && inline is null)
            {
                result.flags.Add(name);
            }
            else if (valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (inline is null)
                {
                    if (i + 1 >= tokens.Count) throw Usage($"Option --{name} needs a value.");
                    inline = tokens[++i];
                }
                result.values[name] = inline;
            }
            else
            {
                throw Usage($"Unknown option --{name}.");
            }
        }

        if (result.Positionals.Count < minPositionals) throw Usage("A required argument is missing.");
        if (result.Positionals.Count > maxPositionals) throw Usage($"Unexpected argument '{result.Positionals[maxPositionals]}'.");
        return result;
    }

    public bool Has(string flag) => flags.Contains(flag);

    public string? Get(string name) => values.GetValueOrDefault(name);

    /// <summary>Integer option within <paramref name="min"/>..<paramref name="max"/>, or null if absent.</summary>
    public int? GetInt(string name, int min, int max)
    {
        if (Get(name) is not { } text) return null;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
            throw Usage($"--{name} must be an integer from {min} to {max}.");
        return v;
    }

    /// <summary>Decimal option within <paramref name="min"/>..<paramref name="max"/>, or null if absent.</summary>
    public double? GetDouble(string name, double min, double max)
    {
        if (Get(name) is not { } text) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
            throw Usage($"--{name} must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
        return v;
    }

    public static CliException Usage(string message) => new(message, ExitCodes.Usage);
}
