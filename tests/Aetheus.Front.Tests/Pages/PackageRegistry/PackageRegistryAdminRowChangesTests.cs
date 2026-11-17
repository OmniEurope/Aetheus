// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.PackageRegistry;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.PackageRegistry;

/// <summary>R-10 on the package registry grid: a package dialog that closes answers no row (a version may have been listed,
/// deprecated, the access changed), so the list is read again behind the rows, quietly: the grid shows no bar while it waits.</summary>
public sealed class PackageRegistryAdminRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string ListUrl = "api/package-registry?";
    private readonly BunitTestHelper.TestHandler _handler;

    public PackageRegistryAdminRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse(HttpMethod.Get, ListUrl, Page(Package(1, "Aetheus.Telemetry")));
    }

    private static PackageRegistryPackageDto Package(int id, string name, string version = "1.2.3") =>
        new() { Id = id, Kind = PackageRegistryKind.NuGet, Name = name, LatestVersion = version, VersionCount = 2 };

    private static PaginatedResult<PackageRegistryPackageDto> Page(params PackageRegistryPackageDto[] items) =>
        new() { Items = [.. items], TotalCount = items.Length };

    private TaskCompletionSource<PaginatedResult<PackageRegistryPackageDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<PackageRegistryPackageDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, ListUrl, _ => gate.Task);
        return gate;
    }

    private int ListReads() => _handler.RequestsSnapshot()
        .Count(request => request.Method == "GET" && request.Url.Contains(ListUrl, StringComparison.Ordinal));

    [Fact]
    public async Task AClosedPackageDialog_ReadsTheListAgainBehindTheRows_WithoutTheBar()
    {
        var cut = Render<PackageRegistryAdmin>();
        cut.WaitForAssertion(() => Assert.Contains("Aetheus.Telemetry", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(1, ListReads());
        var gate = HoldTheNextListRead();

        var open = cut.FindAll("button[title='ViewDetails']")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(2, ListReads()));
        Assert.Contains("Aetheus.Telemetry", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Package(1, "Aetheus.Telemetry", "2.0.0")));
        await open;
        cut.WaitForAssertion(() => Assert.Contains("2.0.0", cut.Markup, StringComparison.Ordinal));
    }
}
