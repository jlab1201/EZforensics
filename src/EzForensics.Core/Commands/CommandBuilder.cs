using System.Globalization;
using EzForensics.Core.Catalog;

namespace EzForensics.Core.Commands;

/// <summary>Turns a ToolDefinition plus form values (keyed by parameter id) into an argv list.</summary>
public static class CommandBuilder
{
    public static BuiltCommand Build(ToolDefinition def, IReadOnlyDictionary<string, string?> values)
    {
        var args = new List<string>();
        var errors = new List<string>();
        var lookup = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

        foreach (var p in def.Parameters)
        {
            if (!IsVisible(def, p, lookup)) continue;
            lookup.TryGetValue(p.Id, out var raw);
            var value = raw?.Trim();

            if (p.Type == ParamType.Bool)
            {
                var b = CommandValue.ParseBool(value);
                if (value is { Length: > 0 } && b is null)
                {
                    errors.Add($"{Describe(p)} must be true or false.");
                    continue;
                }
                bool defTrue = p.DefaultIsTrue;
                if (b == true && !defTrue) args.Add(p.Flag);
                else if (b == false && defTrue) { args.Add(p.Flag); args.Add("false"); }
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                if (p.Required) errors.Add($"{Describe(p)} is required.");
                continue;
            }

            // Passing the tool's own default is redundant; keep the command short.
            if (!p.Required && p.Default is not null && string.Equals(value, p.Default, StringComparison.Ordinal))
                continue;

            if (p.Type == ParamType.Int &&
                !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                errors.Add($"{Describe(p)} must be a whole number.");
                continue;
            }
            if (p.Type == ParamType.Enum && p.Values is { Count: > 0 } allowed &&
                !allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{Describe(p)} must be one of: {string.Join(", ", allowed)}.");
                continue;
            }

            args.Add(p.Flag);
            args.Add(value);
        }

        foreach (var g in def.Groups)
            ValidateGroup(def, g, lookup, errors);

        return new BuiltCommand(args, errors);
    }

    /// <summary>True when the parameter's VisibleWhen dependency (if any) is set.</summary>
    public static bool IsVisible(ToolDefinition def, ParameterDef p, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(p.VisibleWhen)) return true;
        var dep = def.FindParameter(p.VisibleWhen);
        return dep is not null && IsVisible(def, dep, values) && IsSet(dep, values);
    }

    /// <summary>Whether a parameter has an effective value: true for bools (incl. default true), non-empty otherwise.</summary>
    public static bool IsSet(ParameterDef p, IReadOnlyDictionary<string, string?> values)
    {
        values.TryGetValue(p.Id, out var raw);
        if (p.Type == ParamType.Bool)
            return (CommandValue.ParseBool(raw) ?? p.DefaultIsTrue) == true;
        return !string.IsNullOrWhiteSpace(raw);
    }

    private static void ValidateGroup(ToolDefinition def, ParamGroup g, IReadOnlyDictionary<string, string?> values, List<string> errors)
    {
        var members = g.Params.Select(id => def.FindParameter(id)).OfType<ParameterDef>().ToList();
        if (members.Count == 0) return;
        var set = members.Where(p => IsVisible(def, p, values) && IsSet(p, values)).ToList();
        var names = string.Join(", ", members.Select(Describe));

        string? error = g.Mode switch
        {
            GroupMode.ExactlyOne when set.Count == 0 => $"Specify one of: {names}.",
            GroupMode.ExactlyOne when set.Count > 1 =>
                $"Specify only one of: {names} (currently set: {string.Join(", ", set.Select(Describe))}).",
            GroupMode.AtLeastOne when set.Count == 0 => $"Specify at least one of: {names}.",
            GroupMode.AllOrNone when set.Count > 0 && set.Count < members.Count =>
                $"{string.Join(" and ", members.Select(Describe))} must be used together " +
                $"(missing: {string.Join(", ", members.Except(set).Select(Describe))}).",
            _ => null,
        };
        if (error is not null) errors.Add(g.Message ?? error);
    }

    private static string Describe(ParameterDef p) =>
        string.IsNullOrEmpty(p.Label) || p.Label == p.Id ? p.Flag : $"{p.Label} ({p.Flag})";
}
