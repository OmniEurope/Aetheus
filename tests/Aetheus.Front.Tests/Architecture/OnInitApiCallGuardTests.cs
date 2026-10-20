// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Guards the convention that every direct API call inside <c>OnInitializedAsync</c>
/// in page code-behind files is enclosed by a <c>try</c> whose associated catch chain
/// explicitly handles <see cref="HttpRequestException"/>.
/// </summary>
public class OnInitApiCallGuardTests
{
    private static readonly Regex OnInitRegex = new(
        @"\b(?:protected|private|public|internal)\s+(?:override\s+)?(?:async\s+)?Task\s+OnInitializedAsync\s*\(",
        RegexOptions.Compiled);
    /// <summary>
    /// Matches both call shapes the typed client can take: the flat <c>Api.XAsync(</c> and the
    /// per-domain <c>Api.Domain.XAsync(</c>. The optional middle segment is what keeps this guard
    /// honest once the client is split into sub-clients - without it the scanner would silently stop
    /// seeing the calls it exists to protect, and report a green it did not earn.
    /// </summary>
    private static readonly Regex ApiCallRegex = new(
        @"\b(?:Api|_api|Http|_http|ApiClient)\s*\.\s*(?:\w+\s*\.\s*)?\w+Async\s*(?:<|\()",
        RegexOptions.Compiled);
    private static readonly Regex TryRegex = new(@"\btry\b", RegexOptions.Compiled);
    private static readonly Regex HttpRequestCatchRegex = new(
        @"\bHttpRequestException\b",
        RegexOptions.Compiled);

    [Fact]
    public void OnInitializedAsync_ApiCalls_Are_GuardedByHttpRequestCatch()
    {
        var violations = new List<string>();
        var callsScanned = 0;

        foreach (var (pagesDir, file) in RepositoryScan.EnumerateUnion(RepositoryScan.PageRoots, "*.razor.cs"))
        {
            // The guard targets module page folders and excludes cross-module shared components.
            if (IsSharedComponent(file))
                continue;
            var source = File.ReadAllText(file);
            var unguardedCalls = FindUnguardedApiCalls(source, out var scannedInFile);
            callsScanned += scannedInFile;
            foreach (var call in unguardedCalls)
            {
                violations.Add(
                    $"{Path.GetRelativePath(pagesDir, file)}:{call.Line} - {call.Expression} is not enclosed "
                    + "by try/catch (HttpRequestException)");
            }
        }

        Assert.True(callsScanned >= 5,
            $"Scanner found only {callsScanned} OnInitializedAsync API calls - likely a scanner regression.");
        Assert.Empty(violations);
    }

    [Fact]
    public void Scanner_Requires_The_Api_Call_To_Be_Inside_The_Protected_Try()
    {
        const string source = """
            protected override async Task OnInitializedAsync()
            {
                try { await Api.GetFirstAsync(); }
                catch (HttpRequestException) { }
                await Api.GetSecondAsync();
            }
            """;

        var violations = FindUnguardedApiCalls(source, out var callsScanned);

        Assert.Equal(2, callsScanned);
        Assert.Equal("Api.GetSecondAsync(", Assert.Single(violations).Expression);
    }

    [Fact]
    public void Scanner_Rejects_A_General_Catch_And_Ignores_Textual_Decoys()
    {
        const string source = """
            protected override async Task OnInitializedAsync()
            {
                var decoy = "try catch HttpRequestException Api.GetFakeAsync(";
                // try { await Api.GetCommentAsync(); } catch (HttpRequestException) { }
                try { await Api.GetRealAsync(); }
                catch (Exception) { }
            }
            """;

        var violations = FindUnguardedApiCalls(source, out var callsScanned);

        Assert.Equal(1, callsScanned);
        Assert.Equal("Api.GetRealAsync(", Assert.Single(violations).Expression);
    }

    /// <summary>
    /// The scanner must see a per-domain call exactly as it sees a flat one. This is the self-test that
    /// makes the sub-client split safe: if the regex ever loses the middle segment, this fails instead
    /// of the suite quietly scanning nothing.
    /// </summary>
    [Fact]
    public void Scanner_Sees_SubClient_Calls_As_Well_As_Flat_Ones()
    {
        const string source = """
            protected override async Task OnInitializedAsync()
            {
                try { await Api.Pipelines.GetGuardedAsync(); }
                catch (HttpRequestException) { }
                await Api.Servers.GetUnguardedAsync();
                await Api.GetFlatUnguardedAsync();
            }
            """;

        var violations = FindUnguardedApiCalls(source, out var callsScanned);

        Assert.Equal(3, callsScanned);
        Assert.Equal(
            ["Api.Servers.GetUnguardedAsync(", "Api.GetFlatUnguardedAsync("],
            violations.Select(violation => violation.Expression));
    }

    /// <summary>A file of <c>Components/Shared</c>, which is not part of the page area.</summary>
    private static bool IsSharedComponent(string file) =>
        file.Contains(
            $"{Path.DirectorySeparatorChar}Components{Path.DirectorySeparatorChar}Shared{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    private static List<UnguardedCall> FindUnguardedApiCalls(string source, out int callsScanned)
    {
        var masked = SourceMasker.MaskCommentsAndStrings(source);
        var violations = new List<UnguardedCall>();
        callsScanned = 0;

        foreach (var methodMatch in OnInitRegex.Matches(masked).Cast<Match>())
        {
            var parameterOpen = masked.IndexOf('(', methodMatch.Index + methodMatch.Length - 1);
            var parameterClose = FindMatching(masked, parameterOpen, '(', ')');
            if (parameterClose < 0) continue;

            var bodyOpen = NextNonWhitespace(masked, parameterClose + 1);
            if (bodyOpen < 0 || masked[bodyOpen] != '{') continue;
            var bodyClose = FindMatching(masked, bodyOpen, '{', '}');
            if (bodyClose < 0) continue;

            var protectedRanges = FindHttpRequestProtectedTryRanges(masked, bodyOpen, bodyClose);
            for (var call = ApiCallRegex.Match(masked, bodyOpen);
                 call.Success && call.Index < bodyClose;
                 call = call.NextMatch())
            {
                callsScanned++;
                if (protectedRanges.Any(range => call.Index > range.Start && call.Index < range.End))
                    continue;

                violations.Add(new UnguardedCall(
                    LineNumberAt(source, call.Index),
                    source.Substring(call.Index, call.Length).Trim()));
            }
        }

        return violations;
    }

    private static List<SourceRange> FindHttpRequestProtectedTryRanges(
        string source,
        int methodBodyOpen,
        int methodBodyClose)
    {
        var ranges = new List<SourceRange>();
        for (var tryMatch = TryRegex.Match(source, methodBodyOpen);
             tryMatch.Success && tryMatch.Index < methodBodyClose;
             tryMatch = tryMatch.NextMatch())
        {
            var tryBodyOpen = NextNonWhitespace(source, tryMatch.Index + tryMatch.Length);
            if (tryBodyOpen < 0 || source[tryBodyOpen] != '{') continue;
            var tryBodyClose = FindMatching(source, tryBodyOpen, '{', '}');
            if (tryBodyClose < 0 || tryBodyClose > methodBodyClose) continue;

            var cursor = tryBodyClose + 1;
            var handlesHttpRequest = false;
            while (true)
            {
                cursor = NextNonWhitespace(source, cursor);
                if (!StartsWithKeyword(source, cursor, "catch")) break;

                var catchSignatureStart = cursor + "catch".Length;
                var catchBodyOpen = source.IndexOf('{', catchSignatureStart);
                if (catchBodyOpen < 0 || catchBodyOpen > methodBodyClose) break;
                handlesHttpRequest |= HttpRequestCatchRegex.IsMatch(
                    source[catchSignatureStart..catchBodyOpen]);

                var catchBodyClose = FindMatching(source, catchBodyOpen, '{', '}');
                if (catchBodyClose < 0) break;
                cursor = catchBodyClose + 1;
            }

            if (handlesHttpRequest)
                ranges.Add(new SourceRange(tryBodyOpen, tryBodyClose));
        }

        return ranges;
    }

    private static bool StartsWithKeyword(string source, int index, string keyword) =>
        index >= 0
        && index + keyword.Length <= source.Length
        && source.AsSpan(index, keyword.Length).SequenceEqual(keyword)
        && (index + keyword.Length == source.Length
            || !char.IsLetterOrDigit(source[index + keyword.Length]) && source[index + keyword.Length] != '_');

    private static int NextNonWhitespace(string source, int index)
    {
        while (index >= 0 && index < source.Length && char.IsWhiteSpace(source[index])) index++;
        return index < source.Length ? index : -1;
    }

    private static int FindMatching(string source, int openIndex, char open, char close)
    {
        if (openIndex < 0 || openIndex >= source.Length || source[openIndex] != open) return -1;
        var depth = 1;
        for (var i = openIndex + 1; i < source.Length; i++)
        {
            if (source[i] == open) depth++;
            else if (source[i] == close && --depth == 0) return i;
        }
        return -1;
    }

    private static int LineNumberAt(string source, int index) =>
        1 + source.AsSpan(0, index).Count('\n');

    private sealed record SourceRange(int Start, int End);
    private sealed record UnguardedCall(int Line, string Expression);

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
