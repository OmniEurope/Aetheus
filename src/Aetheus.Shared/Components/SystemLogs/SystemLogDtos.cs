// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.SystemLogs;

public sealed record ClientErrorLogRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(64)]
    public string CorrelationId { get; init; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(200)]
    public string Summary { get; init; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(2000)]
    public string Message { get; init; } = string.Empty;

    public const int MaxPathLength = 500;
    public const int MaxStackTraceLength = 4000;

    /// <summary>The page the error happened on, path only (no query string, no fragment).</summary>
    [System.ComponentModel.DataAnnotations.StringLength(MaxPathLength)]
    public string? Path { get; init; }

    /// <summary>The exception's call stack (frames only, no values), bounded to
    /// <see cref="MaxStackTraceLength"/> characters; null when the error was not an exception.</summary>
    [System.ComponentModel.DataAnnotations.StringLength(MaxStackTraceLength)]
    public string? StackTrace { get; init; }

    /// <summary>The system log level the report is written at: <see cref="ClientErrorLogLevels.Error"/>,
    /// <see cref="ClientErrorLogLevels.Warning"/> or <see cref="ClientErrorLogLevels.Information"/>, matched
    /// exactly. Null (a client that predates the field) is an error, as every report was before (recette R1-89).</summary>
    [System.ComponentModel.DataAnnotations.StringLength(16)]
    [System.ComponentModel.DataAnnotations.AllowedValues(null, ClientErrorLogLevels.Error, ClientErrorLogLevels.Warning, ClientErrorLogLevels.Information)]
    public string? Level { get; init; }

    /// <summary>Drops the query string and the fragment: either can carry a token or text the user
    /// typed, and the route alone names the screen.</summary>
    public static string PathWithoutQuery(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var cut = path.IndexOfAny(['?', '#']);
        return cut < 0 ? path : path[..cut];
    }
}

public record SystemLogEntryDto(
    int Id,
    DateTime Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception,
    string? CorrelationId);

public record SystemLogFileDto(
    string FileName,
    long SizeBytes,
    DateTime LastModified);

/// <summary>
/// An organization's exclusion rule of the system logs (R-28): an entry whose message contains
/// <see cref="Pattern"/> (and whose category contains <see cref="Category"/> when it is not empty) is shown
/// on the logs page's second tab instead of the first. Nothing is removed from the log.
/// </summary>
public record SystemLogExclusionRuleDto(
    int Id,
    int OrganizationId,
    string Pattern,
    string Category,
    bool Enabled,
    string? CreatedBy,
    DateTime CreatedAt);

public sealed record SaveSystemLogExclusionRuleRequest
{
    public const int MaxPatternLength = 300;
    public const int MaxCategoryLength = 200;

    /// <summary>Text the entry's message contains, case-insensitive.</summary>
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(MaxPatternLength, MinimumLength = 1)]
    public string Pattern { get; init; } = string.Empty;

    /// <summary>Text the entry's category contains, case-insensitive; empty for any category.</summary>
    [System.ComponentModel.DataAnnotations.StringLength(MaxCategoryLength)]
    public string Category { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;
}

public sealed record SetSystemLogExclusionRuleEnabledRequest(bool Enabled);
