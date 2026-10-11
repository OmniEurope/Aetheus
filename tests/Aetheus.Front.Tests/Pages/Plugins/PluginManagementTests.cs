// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class PluginManagementTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PluginManagementTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    [Fact]
    public void Renders_Loading_ThenGrid()
    {
        // FRONTTEST-004: while the page is held loading, the grid shows no empty state; once the empty
        // page has arrived, the loading ends and the empty text replaces it.
        var answer = new TaskCompletionSource<PaginatedResult<PluginRegistrationDto>>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/plugins", _ => answer.Task);
        var cut = Render<PluginManagement>();
        Assert.DoesNotContain("NoRecords", cut.Markup);

        answer.SetResult(new PaginatedResult<PluginRegistrationDto> { Items = [], TotalCount = 0 });

        cut.WaitForAssertion(() => Assert.Contains("NoRecords", cut.Markup));
    }

    [Fact]
    public void Renders_PluginRows()
    {
        _handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>
        {
            new() { Id = 1, Name = "DockerScanner", Version = "1.0.0", Type = PluginType.Collector, Author = "Dev", Status = PluginStatus.Enabled, CreatedAt = DateTime.UtcNow },
            new() { Id = 2, Name = "SlackNotify", Version = "2.1.0", Type = PluginType.Notifier, Author = "Ops", Status = PluginStatus.Disabled, CreatedAt = DateTime.UtcNow }
        });

        var cut = Render<PluginManagement>();
        cut.WaitForState(() => cut.Markup.Contains("DockerScanner"));

        Assert.Contains("DockerScanner", cut.Markup);
        Assert.Contains("SlackNotify", cut.Markup);
        Assert.Contains("1.0.0", cut.Markup);
    }

    [Fact]
    public void Renders_StatusBadges()
    {
        _handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>
        {
            new() { Id = 1, Name = "Enabled Plugin", Version = "1.0", Type = PluginType.Executor, Status = PluginStatus.Enabled, CreatedAt = DateTime.UtcNow },
            new() { Id = 2, Name = "Errored Plugin", Version = "0.9", Type = PluginType.Custom, Status = PluginStatus.Error, CreatedAt = DateTime.UtcNow }
        });

        var cut = Render<PluginManagement>();
        cut.WaitForState(() => cut.Markup.Contains("Enabled Plugin"));

        Assert.Contains("Enabled", cut.Markup);
        Assert.Contains("Error", cut.Markup);
    }

    [Fact]
    public void RedirectsToHome_WhenNotAdmin()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false);
        handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>());
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var cut = Render<PluginManagement>();

        Assert.EndsWith("/", nav.Uri);
    }

    [Fact]
    public void RegisterButton_IsRendered()
    {
        _handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>());
        var cut = Render<PluginManagement>();
        cut.WaitForState(() => cut.Markup.Contains("Register"));

        Assert.Contains("Register", cut.Markup);
    }

    [Fact]
    public void Handles_HttpError_Gracefully()
    {
        // FRONTTEST-004: a failed load ends the loading state (the grid falls back to its empty text)
        // and never reports success.
        _handler.SetResponse("api/plugins", System.Net.HttpStatusCode.InternalServerError);
        var cut = Render<PluginManagement>();
        cut.WaitForAssertion(() => Assert.Contains("NoRecords", cut.Markup));
        Assert.DoesNotContain("DockerScanner", cut.Markup);
    }

    [Theory]
    [InlineData(PluginStatus.Enabled, OmniTone.Success)]
    [InlineData(PluginStatus.Disabled, OmniTone.Warning)]
    [InlineData(PluginStatus.Error, OmniTone.Danger)]
    public void GetStatusBadgeStyle_ReturnsExpected(PluginStatus status, OmniTone expected)
    {
        var method = typeof(PluginManagement).GetMethod("GetStatusBadgeStyle",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = (OmniTone)method.Invoke(null, [status])!;
        Assert.Equal(expected, result);
    }

    // X4D8: register flow moved to PluginRegisterDialog (see PluginRegisterDialogTests) - the old
    // inline-form register tests were removed with the panel. RegisterButton_IsRendered still covers
    // that the page exposes the entry point.

    [Fact]
    public void Renders_PluginWithAllTypes()
    {
        _handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>
        {
            new() { Id = 1, Name = "Collector A", Version = "1.0.0", Type = PluginType.Collector, Status = PluginStatus.Enabled, CreatedAt = DateTime.UtcNow },
            new() { Id = 2, Name = "Notifier B", Version = "2.0.0", Type = PluginType.Notifier, Status = PluginStatus.Disabled, CreatedAt = DateTime.UtcNow },
            new() { Id = 3, Name = "Executor C", Version = "3.0.0", Type = PluginType.Executor, Status = PluginStatus.Error, CreatedAt = DateTime.UtcNow },
            new() { Id = 4, Name = "Custom D", Version = "4.0.0", Type = PluginType.Custom, Status = PluginStatus.Enabled, CreatedAt = DateTime.UtcNow }
        });

        var cut = Render<PluginManagement>();
        cut.WaitForState(() => cut.Markup.Contains("Collector A"));

        Assert.Contains("Collector A", cut.Markup);
        Assert.Contains("Notifier B", cut.Markup);
    }

    [Fact]
    public async Task TogglePlugin_ChangesStatus()
    {
        _handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>
        {
            new() { Id = 1, Name = "ToggleMe", Version = "1.0", Type = PluginType.Collector, Status = PluginStatus.Enabled, CreatedAt = DateTime.UtcNow }
        });
        // PUT api/plugins/1 (distinct URL from the GET list) succeeds.
        _handler.SetJsonResponse("api/plugins/1", new PluginRegistrationDto
        {
            Id = 1,
            Name = "ToggleMe",
            Version = "1.0",
            Type = PluginType.Collector,
            Status = PluginStatus.Disabled,
            CreatedAt = DateTime.UtcNow
        });

        var cut = Render<PluginManagement>();
        cut.WaitForState(() => cut.Markup.Contains("ToggleMe"));

        // After the toggle succeeds the page reloads the list; return the now-Disabled plugin so
        // we can prove the success branch (reload) actually executed.
        _handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>
        {
            new() { Id = 1, Name = "ToggleMe", Version = "1.0", Type = PluginType.Collector, Status = PluginStatus.Disabled, CreatedAt = DateTime.UtcNow }
        });

        var method = typeof(PluginManagement).GetMethod("TogglePlugin",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var plugin = new PluginRegistrationDto
        {
            Id = 1,
            Name = "ToggleMe",
            Version = "1.0",
            Type = PluginType.Collector,
            Status = PluginStatus.Enabled,
            CreatedAt = DateTime.UtcNow,
            EntryPoint = "entry.dll"
        };

        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [plugin])!);

        var plugins = (List<PluginRegistrationDto>)typeof(PluginManagement).GetField("_plugins",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Single(plugins);
        Assert.Equal(PluginStatus.Disabled, plugins[0].Status);
    }
}
