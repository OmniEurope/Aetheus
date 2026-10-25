// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>R-41: a log line keeps its ANSI escape sequences in storage and in exports; only the display
/// reads them (<c>OmniAnsiText</c>). A view that decorates a plain line itself (variable accent, masked
/// value) uses this test to hand a line with sequences to <c>OmniAnsiText</c> instead, so no raw
/// <c>[90m</c> reaches the screen.</summary>
public static class AnsiLine
{
    /// <summary>True when the line holds at least one control sequence that <c>OmniAnsi</c> reads.</summary>
    public static bool HasSequences(string? line) =>
        !string.IsNullOrEmpty(line) && !ReferenceEquals(OmniAnsi.Strip(line), line);
}
