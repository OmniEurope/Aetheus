// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.VariableLibraries;

/// <summary>Restoring a version of a library entry: only after a confirmation, through the ordinary update of
/// the entry with the version's key and value; a success closes the history, a key taken by another entry or
/// any other refusal is reported and leaves it open. The current version and a deletion offer no restore.</summary>
public sealed class VersionHistoryRestoreTests : BunitContext
{
    private const string EntryUrl = "api/variable-libraries/1/entries/2";

    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public VersionHistoryRestoreTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse($"{EntryUrl}/versions", new PaginatedResult<VariableEntryVersionDto>
        {
            Items =
            [
                new() { Version = 3, Key = "ENV", Value = "prod", ChangeType = ChangeType.Updated, ChangedAt = new DateTime(2026, 3, 1) },
                new() { Version = 2, Key = "GONE", Value = "", ChangeType = ChangeType.Deleted, ChangedAt = new DateTime(2026, 2, 1) },
                new() { Version = 1, Key = "ENV", Value = "dev", ChangeType = ChangeType.Created, ChangedAt = new DateTime(2026, 1, 1) },
            ],
            TotalCount = 3
        });
    }

    private async Task<IRenderedComponent<VersionHistoryDialog>> RestoreFirstAsync()
    {
        var cut = Render<VersionHistoryDialog>(parameters => parameters
            .Add(dialog => dialog.LibraryId, 1)
            .Add(dialog => dialog.EntryId, 2)
            .Add(dialog => dialog.CurrentVersion, 3));
        cut.WaitForState(() => cut.FindAll("button[title='RestoreVersion']").Count > 0);
        Assert.Single(cut.FindAll("button[title='RestoreVersion']"));
        await cut.Find("button[title='RestoreVersion']").ClickAsync(new());
        return cut;
    }

    private List<(string Method, string Url, string? Body)> Puts() =>
        [.. _handler.RequestDetails.Where(request => request.Method == "PUT" && request.Url.EndsWith(EntryUrl, StringComparison.Ordinal))];

    [Fact]
    public async Task ADeclinedConfirmation_RestoresNothing()
    {
        _dialogs.ConfirmResult = false;

        await RestoreFirstAsync();

        Assert.Equal("RestoreVersionConfirm", _dialogs.LastConfirmMessage);
        Assert.Empty(Puts());
        Assert.Equal(0, _dialogs.CloseCount);
    }

    [Fact]
    public async Task AConfirmedRestore_WritesTheVersionBack_AndClosesTheHistory()
    {
        _dialogs.ConfirmResult = true;
        _handler.SetJsonResponse(HttpMethod.Put, EntryUrl, new VariableEntryDto { Id = 2, Key = "ENV", Value = "dev" });

        await RestoreFirstAsync();

        var put = Assert.Single(Puts());
        Assert.Contains("\"value\":\"dev\"", put.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"key\":\"ENV\"", put.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success && toast.Detail == "VersionRestored");
        Assert.Equal(1, _dialogs.CloseCount);
        Assert.Equal(true, _dialogs.LastCloseResult);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "LibraryEntryKeyExists")]
    [InlineData(HttpStatusCode.Conflict, "OperationFailed")]
    public async Task ARefusedRestore_IsReported_AndTheHistoryStaysOpen(HttpStatusCode status, string expectedDetail)
    {
        _dialogs.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Put, EntryUrl, status);

        await RestoreFirstAsync();

        Assert.Single(Puts());
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == expectedDetail);
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
        Assert.Equal(0, _dialogs.CloseCount);
    }
}
