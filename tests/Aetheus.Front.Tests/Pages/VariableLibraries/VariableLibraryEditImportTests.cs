// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.VariableLibraries;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.VariableLibraries;

/// <summary>The entries import: the JSON the dialog returns is sent as one import, a malformed one is
/// reported and sent nowhere, and an empty answer is a cancellation.</summary>
public sealed class VariableLibraryEditImportTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditImportTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/variable-libraries/1", new VariableLibraryDetailDto { Id = 1, Name = "lib", ProjectId = 1, Entries = [] });
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private bool ImportSent() => _handler.Requests.Any(r => r.Method == "POST" && r.Url.EndsWith("api/variable-libraries/1/import", StringComparison.Ordinal));

    private async Task ImportAsync(object? dialogAnswer)
    {
        Dialog.OpenResult = dialogAnswer;
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.FindAll(".omni-overflow-menu__trigger").Count > 0);
        // The Tools menu's Import item, as the reader picks it.
        var import = cut.FindMenuItem("Import") ?? throw new InvalidOperationException("The Tools menu has no Import item.");
        await import.ClickAsync(new MouseEventArgs());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("[]")]
    public async Task ACancelledOrEmptyImport_SendsNothing(string? answer)
    {
        await ImportAsync(answer);

        Assert.Equal(typeof(ImportJsonDialog), Dialog.LastComponent);
        Assert.False(ImportSent());
        Assert.Empty(Services.Toasts());
    }

    [Fact]
    public async Task MalformedJson_IsReportedAndNotSent()
    {
        await ImportAsync("[{ not json");

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "InvalidJsonFormat");
        Assert.False(ImportSent());
    }

    [Fact]
    public async Task EntriesAreImportedInOneRequest_AndTheCountIsReported()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/variable-libraries/1/import", new ImportResultDto { ImportedCount = 2 });

        await ImportAsync("""[{"key":"A","value":"1"},{"KEY":"B","Value":"2"}]""");

        var body = _handler.RequestDetails.Single(r => r.Method == "POST" && r.Url.EndsWith("/import", StringComparison.Ordinal)).Body!;
        Assert.Contains("\"A\"", body, StringComparison.Ordinal);
        Assert.Contains("\"B\"", body, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task AnImportTheServerRefuses_ReportsNoSuccess()
    {
        _handler.SetResponse(HttpMethod.Post, "api/variable-libraries/1/import", System.Net.HttpStatusCode.BadRequest);

        await ImportAsync("""[{"key":"A","value":"1"}]""");

        Assert.True(ImportSent());
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }
}
