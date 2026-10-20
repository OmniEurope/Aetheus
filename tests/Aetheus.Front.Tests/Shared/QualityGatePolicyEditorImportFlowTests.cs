// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Drives a file through the editor's importer: a refused file never reaches the API, an
/// accepted one is applied as one batch on the editor's own scope and closes the importer.</summary>
public sealed class QualityGatePolicyEditorImportFlowTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public QualityGatePolicyEditorImportFlowTests() => _handler = BunitTestHelper.RegisterServices(this);

    private sealed class MemoryBrowserFile(byte[] content, long? size = null) : IBrowserFile
    {
        public string Name => "gates.json";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => size ?? content.Length;
        public string ContentType => "application/json";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new MemoryStream(content);
    }

    // Written the way ExportAsync writes it, so the import reads the file the editor itself produces.
    private static readonly string ValidGate = JsonSerializer.Serialize(
        new QualityGatePolicyEditor.QualityGateExport
        {
            SchemaVersion = 1,
            Rules =
            [
                new UpsertAnalysisPolicyRequest
                {
                    PolicyKey = "quality.coverage",
                    Name = "Line coverage",
                    MetricKey = "coverage.line.percent",
                    Operator = AnalysisPolicyOperator.LessThan,
                    Threshold = 75
                }
            ]
        },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private IRenderedComponent<QualityGatePolicyEditor> RenderWithImporterOpen()
    {
        _handler.SetJsonResponse("api/analysis/projects/4/policies", new List<AnalysisPolicyDto>());
        var cut = Render<QualityGatePolicyEditor>(parameters => parameters
            .Add(component => component.Scope, AnalysisPolicyScope.Project)
            .Add(component => component.ProjectId, 4)
            .Add(component => component.CanEdit, true)
            .Add(component => component.InitialPolicies, new List<AnalysisPolicyDto>()));
        BunitTestHelper.Interactive(this);
        cut.FindMenuItem("Import")!.Click();
        cut.WaitForState(() => cut.FindComponents<OmniUpload>().Count == 1);
        return cut;
    }

    private static Task SelectAsync(IRenderedComponent<QualityGatePolicyEditor> cut, IBrowserFile file) =>
        cut.InvokeAsync(() => cut.FindComponent<OmniUpload>().Instance.FilesSelected.InvokeAsync([file]));

    public static TheoryData<string, long?> RefusedFiles => new()
    {
        { "", 0 },
        { "{}", 1_048_577 },
        { "{ not json", null },
        { """{"schemaVersion":2,"rules":[]}""", null }
    };

    [Theory]
    [MemberData(nameof(RefusedFiles))]
    public async Task ARefusedFile_IsReportedAndNeverApplied(string content, long? size)
    {
        var cut = RenderWithImporterOpen();

        await SelectAsync(cut, new MemoryBrowserFile(Encoding.UTF8.GetBytes(content), size));

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "QualityGateImportInvalid");
        Assert.DoesNotContain(_handler.Requests, request => request.Url.EndsWith("/policies/batch", StringComparison.Ordinal));
        Assert.Single(cut.FindComponents<OmniUpload>());
    }

    [Fact]
    public async Task AValidFile_IsAppliedAsOneBatchOnTheProject_AndClosesTheImporter()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/analysis/projects/4/policies/batch", new List<AnalysisPolicyDto>());
        var cut = RenderWithImporterOpen();

        await SelectAsync(cut, new MemoryBrowserFile(Encoding.UTF8.GetBytes(ValidGate)));

        var batch = Assert.Single(_handler.RequestDetails, request =>
            request.Method == "POST" && request.Url.EndsWith("api/analysis/projects/4/policies/batch", StringComparison.Ordinal));
        Assert.Contains("quality.coverage", batch.Body, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindComponents<OmniUpload>()));
    }

    [Fact]
    public async Task ABatchTheServerRefuses_KeepsTheImporterOpen()
    {
        _handler.SetResponse(HttpMethod.Post, "api/analysis/projects/4/policies/batch", System.Net.HttpStatusCode.BadRequest);
        var cut = RenderWithImporterOpen();

        await SelectAsync(cut, new MemoryBrowserFile(Encoding.UTF8.GetBytes(ValidGate)));

        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.EndsWith("/policies/batch", StringComparison.Ordinal));
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
        Assert.Single(cut.FindComponents<OmniUpload>());
    }
}
