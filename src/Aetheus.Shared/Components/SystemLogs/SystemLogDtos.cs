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
