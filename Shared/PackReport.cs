using System;
using System.Collections.Generic;
using System.Linq;

namespace ProsperoThemes.Shared;

/// <summary>How serious a pack problem is.</summary>
public enum PackSeverity
{
    /// <summary>The pack is still usable; the entry or pack is reported and handled gracefully.</summary>
    Warning,

    /// <summary>The pack must not be applied.</summary>
    Error,
}

/// <summary>One problem found while reading or validating a pack.</summary>
public sealed record PackIssue(PackSeverity Severity, string Code, string Message)
{
    public override string ToString() => $"{Severity}: {Code}: {Message}";
}

/// <summary>
/// The outcome of reading and validating a pack.
/// </summary>
/// <remarks>
/// Errors are fatal for the whole pack, warnings are not. The important distinction is <em>unknown root
/// versus unknown layer</em>: an unknown root could name a path outside the assets we understand, so it is
/// refused; an unknown layer only means this app is older than the pack, so the entry is skipped and the
/// rest still applies. That is what lets the format grow without breaking older readers.
/// </remarks>
public sealed class PackReport
{
    private readonly List<PackIssue> _issues = new();

    public IReadOnlyList<PackIssue> Issues => _issues;

    public bool HasErrors { get; private set; }

    public IEnumerable<PackIssue> Errors => _issues.Where(i => i.Severity == PackSeverity.Error);

    public IEnumerable<PackIssue> Warnings => _issues.Where(i => i.Severity == PackSeverity.Warning);

    public void Warn(string code, string message) => _issues.Add(new PackIssue(PackSeverity.Warning, code, message));

    public void Error(string code, string message)
    {
        _issues.Add(new PackIssue(PackSeverity.Error, code, message));
        HasErrors = true;
    }

    /// <summary>A one-line summary for logs and the UI.</summary>
    public string Summary() =>
        HasErrors
            ? $"invalid ({Errors.Count()} error(s), {Warnings.Count()} warning(s))"
            : _issues.Count == 0
                ? "valid"
                : $"valid with {Warnings.Count()} warning(s)";
}
