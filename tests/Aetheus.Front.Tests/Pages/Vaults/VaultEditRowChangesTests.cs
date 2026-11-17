// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Vaults;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Vaults;

/// <summary>
/// R-10 on the secrets of a vault: a secret added, saved, rotated or deleted shows at once from the action's answer, before the
/// vault is read again; the read runs in the background and replaces the rows, and a failed or stale read never takes the change
/// away. A refused action (a 409 included) leaves the rows and shows an error toast. The values sent are placeholders, and no
/// assertion prints a value.
/// </summary>
public sealed class VaultEditRowChangesTests : BunitContext
{
    private const string VaultUrl = "api/vaults/1";
    private const string Placeholder = "placeholder-not-a-secret";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public VaultEditRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "aetheus" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse(HttpMethod.Get, VaultUrl, Detail(Secret(10, "DB_PASS"), Secret(11, "API_TOKEN")));
    }

    private static VaultSecretDto Secret(int id, string key) => new() { Id = id, Key = key, VersionCount = 1 };

    private static readonly Guid RowVersion = Guid.NewGuid();

    private static VaultDetailDto Detail(params VaultSecretDto[] secrets) => new()
    {
        Id = 1,
        Name = "prod-vault",
        ProjectId = 1,
        RowVersion = RowVersion,
        SecretCount = secrets.Length,
        Secrets = [.. secrets]
    };

    private TaskCompletionSource<VaultDetailDto> HoldTheNextDetailRead()
    {
        var gate = new TaskCompletionSource<VaultDetailDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, VaultUrl, _ => gate.Task);
        return gate;
    }

    private int DetailReads() => _handler.RequestsSnapshot()
        .Count(request => request.Method == "GET" && request.Url.EndsWith(VaultUrl, StringComparison.Ordinal));

    private IRenderedComponent<VaultEdit> RenderVault()
    {
        var cut = Render<VaultEdit>(parameters => parameters.Add(page => page.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("API_TOKEN", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    // The first row's Edit button, then the secret rows' menus: the last trigger is the last row's (API_TOKEN, id 11).
    private static IElement RowMenuItem(IRenderedComponent<VaultEdit> cut, string title)
    {
        cut.FindAll(".omni-overflow-menu__trigger").Last().Click();
        return cut.FindAll(".omni-menu__item").First(entry => entry.TextContent.Contains(title, StringComparison.Ordinal));
    }

    // The real add form: the Key and Value inputs are filled and the form is submitted, as a user would.
    private static async Task AddAsync(IRenderedComponent<VaultEdit> cut, string key)
    {
        cut.Find("input[aria-label='Key']").Input(key);
        cut.Find("input[aria-label='Value']").Input(Placeholder);
        await cut.Find("form").SubmitAsync();
    }

    private void AssertDanger(string detail) =>
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == detail);

    // ---------- add ----------

    [Fact]
    public async Task AnAddedSecret_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderVault();
        _handler.SetJsonResponse(HttpMethod.Post, VaultUrl + "/secrets", Secret(12, "NEW_KEY"));
        var gate = HoldTheNextDetailRead();
        var readsBefore = DetailReads();

        var action = AddAsync(cut, "NEW_KEY");

        cut.WaitForAssertion(() => Assert.Contains("NEW_KEY", cut.Markup, StringComparison.Ordinal));
        // The read behind the row is held: the row on screen comes from the answer of the add alone.
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, DetailReads()));
        Assert.Contains("DB_PASS", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(Detail(Secret(10, "DB_PASS"), Secret(11, "API_TOKEN"), Secret(12, "NEW_KEY"), Secret(13, "OTHER_KEY")));
        cut.WaitForAssertion(() => Assert.Contains("OTHER_KEY", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("NEW_KEY", cut.Markup, StringComparison.Ordinal);
        await action;
    }

    [Fact]
    public async Task ARefusedAdd_LeavesTheRows_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderVault();
        _handler.SetResponse(HttpMethod.Post, VaultUrl + "/secrets", HttpStatusCode.Conflict);
        var readsBefore = DetailReads();

        await AddAsync(cut, "NEW_KEY");

        // The typed Key field keeps what was typed after a refusal: only the grid's rows tell what the table holds.
        Assert.DoesNotContain(cut.FindAll("tbody tr"), row => row.TextContent.Contains("NEW_KEY", StringComparison.Ordinal));
        Assert.Contains("API_TOKEN", cut.Markup, StringComparison.Ordinal);
        AssertDanger("SaveFailed");
        Assert.Equal(readsBefore, DetailReads());
    }

    // ---------- save ----------

    [Fact]
    public async Task ASavedSecret_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderVault();
        _dialog.OpenResult = Placeholder;
        _handler.SetJsonResponse(HttpMethod.Put, VaultUrl + "/secrets/10", Secret(10, "DB_PASS_SAVED"));
        var gate = HoldTheNextDetailRead();
        var readsBefore = DetailReads();

        var action = cut.FindAll("button[title='Edit']")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("DB_PASS_SAVED", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, DetailReads()));
        Assert.Contains("API_TOKEN", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(Detail(Secret(10, "DB_PASS_SERVER"), Secret(11, "API_TOKEN")));
        cut.WaitForAssertion(() => Assert.Contains("DB_PASS_SERVER", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("DB_PASS_SAVED", cut.Markup, StringComparison.Ordinal);
        await action;
    }

    // The row takes its version count from the answer while the read behind it is held.
    [Fact]
    public async Task ASavedSecret_ShowsOneMoreVersion_WhileTheReadBehindItIsHeld()
    {
        _handler.SetJsonResponse(HttpMethod.Get, VaultUrl, Detail(Secret(10, "DB_PASS") with { VersionCount = 6 }, Secret(11, "API_TOKEN")));
        var cut = RenderVault();
        _dialog.OpenResult = Placeholder;
        _handler.SetJsonResponse(HttpMethod.Put, VaultUrl + "/secrets/10", new VaultSecretDto { Id = 10, Key = "DB_PASS_SAVED", VersionCount = 7 });
        var gate = HoldTheNextDetailRead();

        var action = cut.FindAll("button[title='Edit']")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("tbody tr"), row =>
            row.TextContent.Contains("DB_PASS_SAVED", StringComparison.Ordinal) && row.TextContent.Contains('7')));

        gate.SetResult(Detail(Secret(10, "DB_PASS_SAVED") with { VersionCount = 8 }, Secret(11, "API_TOKEN")));
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("tbody tr"), row =>
            row.TextContent.Contains("DB_PASS_SAVED", StringComparison.Ordinal) && row.TextContent.Contains('8')));
        await action;
    }

    [Fact]
    public async Task ARefusedSave_KeepsTheRow_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderVault();
        _dialog.OpenResult = Placeholder;
        _handler.SetResponse(HttpMethod.Put, VaultUrl + "/secrets/10", HttpStatusCode.Conflict);
        var readsBefore = DetailReads();

        await cut.FindAll("button[title='Edit']")[0].ClickAsync(new());

        cut.WaitForAssertion(() => AssertDanger("SaveFailed"));
        Assert.Contains("DB_PASS", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(readsBefore, DetailReads());
    }

    // ---------- rotate ----------

    [Fact]
    public async Task ARotatedSecret_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderVault();
        _dialog.ConfirmResult = true;
        _dialog.OpenResult = Placeholder;
        _handler.SetJsonResponse(HttpMethod.Post, VaultUrl + "/secrets/11/rotate", Secret(11, "API_TOKEN_ROTATED"));
        var gate = HoldTheNextDetailRead();
        var readsBefore = DetailReads();

        var action = RowMenuItem(cut, "Rotate").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("API_TOKEN_ROTATED", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, DetailReads()));
        Assert.Contains("DB_PASS", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(Detail(Secret(10, "DB_PASS"), Secret(11, "API_TOKEN_SERVER")));
        cut.WaitForAssertion(() => Assert.Contains("API_TOKEN_SERVER", cut.Markup, StringComparison.Ordinal));
        await action;
    }

    [Fact]
    public async Task ARefusedRotation_KeepsTheRow_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderVault();
        _dialog.ConfirmResult = true;
        _dialog.OpenResult = Placeholder;
        _handler.SetResponse(HttpMethod.Post, VaultUrl + "/secrets/11/rotate", HttpStatusCode.Conflict);
        var readsBefore = DetailReads();

        await RowMenuItem(cut, "Rotate").ClickAsync(new());

        cut.WaitForAssertion(() => AssertDanger("SaveFailed"));
        Assert.Contains("API_TOKEN", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(readsBefore, DetailReads());
    }

    // ---------- delete ----------

    [Fact]
    public async Task ADeletedSecret_LeavesTheGridAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderVault();
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, VaultUrl + "/secrets/11", HttpStatusCode.NoContent);
        var gate = HoldTheNextDetailRead();
        var readsBefore = DetailReads();

        var action = RowMenuItem(cut, "Delete").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("API_TOKEN", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, DetailReads()));
        Assert.Contains("DB_PASS", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(Detail(Secret(10, "DB_PASS"), Secret(14, "LATE_KEY")));
        cut.WaitForAssertion(() => Assert.Contains("LATE_KEY", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("API_TOKEN", cut.Markup, StringComparison.Ordinal);
        await action;
    }

    [Fact]
    public async Task ARefusedDelete_KeepsTheRow_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderVault();
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, VaultUrl + "/secrets/11", HttpStatusCode.Conflict);
        var readsBefore = DetailReads();

        await RowMenuItem(cut, "Delete").ClickAsync(new());

        cut.WaitForAssertion(() => AssertDanger("DeleteFailed"));
        Assert.Contains("API_TOKEN", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(readsBefore, DetailReads());
    }

    // ---------- import: the answer carries a count, not the rows ----------

    [Fact]
    public async Task AnImport_ReadsTheDetailInTheBackground_WithoutBlockingTheAction()
    {
        var cut = RenderVault();
        _dialog.OpenResult = "[{\"key\":\"IMPORTED_KEY\",\"value\":\"" + Placeholder + "\"}]";
        _handler.SetJsonResponse(HttpMethod.Post, VaultUrl + "/import", new ImportResultDto { ImportedCount = 1 });
        var gate = HoldTheNextDetailRead();
        var readsBefore = DetailReads();
        cut.Find(".omni-overflow-menu__trigger").Click();
        var import = cut.FindAll(".omni-menu__item").First(item => item.TextContent.Contains("Import", StringComparison.Ordinal));

        // The click returns while the read is still held: the action is done once the import answered.
        var action = import.ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, DetailReads()));
        cut.WaitForAssertion(() => Assert.True(action.IsCompleted));
        Assert.DoesNotContain("IMPORTED_KEY", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);

        gate.SetResult(Detail(Secret(10, "DB_PASS"), Secret(11, "API_TOKEN"), Secret(15, "IMPORTED_KEY")));
        cut.WaitForAssertion(() => Assert.Contains("IMPORTED_KEY", cut.Markup, StringComparison.Ordinal));
        await action;
    }

    [Fact]
    public async Task ARefusedImport_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderVault();
        _dialog.OpenResult = "[{\"key\":\"IMPORTED_KEY\",\"value\":\"" + Placeholder + "\"}]";
        _handler.SetResponse(HttpMethod.Post, VaultUrl + "/import", HttpStatusCode.Conflict);
        var readsBefore = DetailReads();
        cut.Find(".omni-overflow-menu__trigger").Click();
        var import = cut.FindAll(".omni-menu__item").First(item => item.TextContent.Contains("Import", StringComparison.Ordinal));

        await import.ClickAsync(new());

        cut.WaitForAssertion(() => AssertDanger("SaveFailed"));
        Assert.DoesNotContain("IMPORTED_KEY", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(readsBefore, DetailReads());
    }

    // ---------- the read behind the rows ----------

    [Fact]
    public async Task AFailedReadBehindTheRows_KeepsTheChangeDrawn()
    {
        var cut = RenderVault();
        _handler.SetJsonResponse(HttpMethod.Post, VaultUrl + "/secrets", Secret(12, "NEW_KEY"));
        var gate = HoldTheNextDetailRead();

        var action = AddAsync(cut, "NEW_KEY");
        cut.WaitForAssertion(() => Assert.Contains("NEW_KEY", cut.Markup, StringComparison.Ordinal));
        var read = cut.Instance.BackgroundDetailRead;
        Assert.False(read.IsCompleted);

        gate.SetException(new HttpRequestException("read failed"));
        await cut.InvokeAsync(() => read);

        Assert.Contains("NEW_KEY", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("API_TOKEN", cut.Markup, StringComparison.Ordinal);
        await action;
    }

    [Fact]
    public async Task AReadStartedBeforeALaterChange_IsDropped_SoItNeverTakesTheChangeAway()
    {
        var cut = RenderVault();
        _handler.SetJsonResponse(HttpMethod.Post, VaultUrl + "/secrets", Secret(12, "FIRST_KEY"));
        var firstRead = HoldTheNextDetailRead();
        var firstAdd = AddAsync(cut, "FIRST_KEY");
        cut.WaitForAssertion(() => Assert.Contains("FIRST_KEY", cut.Markup, StringComparison.Ordinal));
        var first = cut.Instance.BackgroundDetailRead;

        _handler.SetJsonResponse(HttpMethod.Post, VaultUrl + "/secrets", Secret(13, "SECOND_KEY"));
        var secondRead = HoldTheNextDetailRead();
        var secondAdd = AddAsync(cut, "SECOND_KEY");
        cut.WaitForAssertion(() => Assert.Contains("SECOND_KEY", cut.Markup, StringComparison.Ordinal));

        // The first read, older than the second change, answers without either key: it must not replace the rows.
        firstRead.SetResult(Detail(Secret(10, "DB_PASS"), Secret(11, "API_TOKEN")));
        await cut.InvokeAsync(() => first);
        Assert.Contains("FIRST_KEY", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("SECOND_KEY", cut.Markup, StringComparison.Ordinal);

        secondRead.SetResult(Detail(Secret(10, "DB_PASS"), Secret(11, "API_TOKEN"), Secret(12, "FIRST_KEY"), Secret(13, "SECOND_KEY"), Secret(16, "SERVER_KEY")));
        cut.WaitForAssertion(() => Assert.Contains("SERVER_KEY", cut.Markup, StringComparison.Ordinal));
        await firstAdd;
        await secondAdd;
    }
}
