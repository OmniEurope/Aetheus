// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Cli;

internal sealed record CliInvocation(Uri Server, string? Token, int Page, int PageSize)
{
    internal const int MaximumPage = 1_000_000;

    internal static async Task<CliInvocation> CreateAsync(
        string server,
        bool readTokenFromStandardInput,
        int requestedPage,
        int requestedPageSize,
        CancellationToken ct)
    {
        var token = await CliRuntime.ResolveTokenAsync(readTokenFromStandardInput, ct);
        return new CliInvocation(
            CliRuntime.ParseServerUri(server),
            token,
            ClampPage(requestedPage),
            ClampPageSize(requestedPageSize));
    }

    private static int ClampPage(int requested)
    {
        var clamped = Math.Clamp(requested, 1, MaximumPage);
        if (clamped != requested)
        {
            Console.Error.WriteLine(
                $"Warning: --page must be between 1 and {MaximumPage} "
                + $"(got {requested}); using {clamped}.");
        }
        return clamped;
    }

    private static int ClampPageSize(int requested)
    {
        var clamped = Math.Clamp(requested, 1, PaginationRequest.MaxPageSize);
        if (clamped != requested)
        {
            Console.Error.WriteLine(
                $"Warning: --page-size must be between 1 and {PaginationRequest.MaxPageSize} "
                + $"(got {requested}); using {clamped}.");
        }
        return clamped;
    }

    internal AetheusApiClient CreateClient() => new(Server, Token);
}
