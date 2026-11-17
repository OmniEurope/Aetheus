// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-8: the card and the block info of a server's scheduled action in the planner. They name the
/// action and its server, link the server's page, and say in words whether the reader may move it (the Administrator
/// right on the server) or not.</summary>
public sealed class ServerActionScheduleCardTests : BunitContext
{
    private static readonly DateTime Now = new(2027, 1, 3, 12, 0, 0, DateTimeKind.Utc);

    public ServerActionScheduleCardTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
    }

    private static ScheduledServerActionDto Action(bool movable, ScheduleFrequency frequency = ScheduleFrequency.EveryEvening) => new()
    {
        Id = "rkhunter:7",
        Kind = ServerActionKind.RkhunterScan,
        ServerId = 7,
        ServerName = "srv-prod-01",
        CronExpression = "30 3 * * *",
        Frequency = frequency,
        Hour = frequency == ScheduleFrequency.Custom ? null : 3,
        Minute = frequency == ScheduleFrequency.Custom ? null : 30,
        DurationMinutes = 6,
        IsPlanned = false,
        NextRunUtc = new DateTime(2027, 1, 4, 3, 30, 0, DateTimeKind.Utc),
        Movable = movable
    };

    private IRenderedComponent<ServerActionScheduleCard> RenderCard(ScheduledServerActionDto action)
    {
        var plan = new SchedulePlanDto { ServerActions = [action] };
        var summary = SchedulePlanSummary.Build(plan, TimeZoneInfo.Utc, Now);
        return Render<ServerActionScheduleCard>(parameters => parameters
            .Add(card => card.Item, action)
            .Add(card => card.Summary, summary)
            .Add(card => card.Zone, TimeZoneInfo.Utc)
            .Add(card => card.NowUtc, Now));
    }

    private IRenderedComponent<ServerActionBlockInfo> RenderInfo(ScheduledServerActionDto action) =>
        Render<ServerActionBlockInfo>(parameters => parameters.Add(info => info.Action, action));

    [Fact]
    public void TheCard_NamesTheScanAndItsServer_AndOpensTheServersPage()
    {
        var cut = RenderCard(Action(movable: true));

        var card = cut.Find("[data-testid='schedule-server-card-rkhunter:7']");
        Assert.Contains("Rkhunter scan on srv-prod-01", card.TextContent, StringComparison.Ordinal);
        Assert.Equal("Server action", cut.Find("[data-testid='schedule-server-kind']").TextContent.Trim());
        // The title links the server's page (the menu, closed here, holds the same link).
        Assert.Equal("/servers/7/rkhunter", card.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("Average duration: 6 min", cut.Find("[data-testid='schedule-duration']").TextContent.Trim());
    }

    [Fact]
    public void AMovableAction_HasNoReadOnlyBadge()
    {
        var cut = RenderCard(Action(movable: true));

        Assert.Empty(cut.FindAll("[data-testid='schedule-read-only']"));
    }

    [Fact]
    public void AnActionTheReaderDoesNotAdminister_IsSaidReadOnly_WithTheReason()
    {
        var cut = RenderCard(Action(movable: false));

        var badge = cut.Find("[data-testid='schedule-read-only']");
        Assert.Equal("Read only", badge.TextContent.Trim());
        Assert.Equal("Moving this action needs the Administrator right on the server.", badge.GetAttribute("title"));
    }

    [Fact]
    public void TheInfoOfAMovableAction_SaysItCanBeMoved()
    {
        var cut = RenderInfo(Action(movable: true));

        Assert.Equal("Can be moved", cut.Find("[data-testid='schedule-info-movable']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='schedule-info-read-only']"));
        Assert.Equal("/servers/7/rkhunter", cut.Find("[data-testid='schedule-info-server']").GetAttribute("href"));
    }

    [Fact]
    public void TheInfoOfAReadOnlyAction_SaysTheAdministratorRightIsNeeded()
    {
        var cut = RenderInfo(Action(movable: false));

        Assert.Empty(cut.FindAll("[data-testid='schedule-info-movable']"));
        Assert.NotNull(cut.Find("[data-testid='schedule-info-read-only']"));
        Assert.Contains("Moving this action needs the Administrator right on the server.", cut.Markup, StringComparison.Ordinal);
    }

    private static ScheduledServerActionDto AiTask(bool movable, bool ofProject = true) => new()
    {
        Id = "ai:3",
        Kind = ServerActionKind.AiTask,
        SourceId = 3,
        SourceName = "nightly review",
        ServerId = ofProject ? 0 : 7,
        ServerName = ofProject ? string.Empty : "srv-prod-01",
        ProjectId = ofProject ? 12 : null,
        ProjectName = ofProject ? "Aetheus" : null,
        CronExpression = "0 30 3 * * *",
        Frequency = ScheduleFrequency.EveryEvening,
        Hour = 3,
        Minute = 30,
        DurationMinutes = 60,
        IsPlanned = true,
        NextRunUtc = new DateTime(2027, 1, 4, 3, 30, 0, DateTimeKind.Utc),
        Movable = movable
    };

    private static ScheduledServerActionDto Backup(bool movable) => new()
    {
        Id = "backup:5",
        Kind = ServerActionKind.AppBackup,
        SourceId = 5,
        SourceName = "orders-db",
        ServerId = 7,
        ServerName = "srv-db",
        ProjectId = 12,
        ProjectName = "Aetheus",
        CronExpression = "15 1 * * *",
        Frequency = ScheduleFrequency.EveryEvening,
        Hour = 1,
        Minute = 15,
        DurationMinutes = 9,
        IsPlanned = false,
        NextRunUtc = new DateTime(2027, 1, 4, 1, 15, 0, DateTimeKind.Utc),
        Movable = movable
    };

    [Fact]
    public void TheCardOfAnAiTask_NamesTheTaskAndItsProject_AndOpensTheAiTasksOfThatProject()
    {
        var cut = RenderCard(AiTask(movable: true));

        var card = cut.Find("[data-testid='schedule-server-card-ai:3']");
        Assert.Contains("AI task nightly review", card.TextContent, StringComparison.Ordinal);
        Assert.Equal("AI task", cut.Find("[data-testid='schedule-server-kind']").TextContent.Trim());
        Assert.Equal("Aetheus", cut.Find("[data-testid='schedule-server-name']").TextContent.Trim());
        Assert.Equal("/projects/12/ai-tasks", card.QuerySelector("a")!.GetAttribute("href"));
        // Never ran: the planned hour, said as such.
        Assert.Equal("Planned duration: 1 h", cut.Find("[data-testid='schedule-duration']").TextContent.Trim());
    }

    [Fact]
    public void TheCardOfAnAiTaskOfAServer_NamesItsServer()
    {
        var cut = RenderCard(AiTask(movable: true, ofProject: false));

        Assert.Equal("srv-prod-01", cut.Find("[data-testid='schedule-server-name']").TextContent.Trim());
        Assert.Equal("/ai-tasks", cut.Find("[data-testid='schedule-server-card-ai:3']").QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void TheCardOfABackup_NamesThePolicy_AndOpensTheBackups()
    {
        var cut = RenderCard(Backup(movable: true));

        var card = cut.Find("[data-testid='schedule-server-card-backup:5']");
        Assert.Contains("Backup orders-db", card.TextContent, StringComparison.Ordinal);
        Assert.Equal("Backup", cut.Find("[data-testid='schedule-server-kind']").TextContent.Trim());
        Assert.Equal("srv-db", cut.Find("[data-testid='schedule-server-name']").TextContent.Trim());
        Assert.Equal("/backups", card.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("Average duration: 9 min", cut.Find("[data-testid='schedule-duration']").TextContent.Trim());
    }

    [Fact]
    public void AnAiTaskAndABackupTheReaderCannotWrite_AreSaidReadOnly_WithTheirOwnReason()
    {
        var ai = RenderCard(AiTask(movable: false)).Find("[data-testid='schedule-read-only']");
        var backup = RenderCard(Backup(movable: false)).Find("[data-testid='schedule-read-only']");

        Assert.Equal("Read only", ai.TextContent.Trim());
        Assert.Equal("Moving this AI task needs the Write right on its project or its server.", ai.GetAttribute("title"));
        Assert.Equal("Moving this backup needs the Write right on its project.", backup.GetAttribute("title"));
    }

    [Fact]
    public void TheInfoOfAnAiTask_LinksItsProjectAndTheAiTasks_AndSaysWhatAMoveWrites()
    {
        var cut = RenderInfo(AiTask(movable: true));

        Assert.Equal("/projects/12", cut.Find("[data-testid='schedule-info-project']").GetAttribute("href"));
        Assert.Equal("/projects/12/ai-tasks", cut.Find("[data-testid='schedule-info-open']").GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-info-server']"));
        Assert.Contains("definition of the AI task", cut.Find("[data-testid='schedule-info-source']").TextContent, StringComparison.Ordinal);
        Assert.Equal("Can be moved", cut.Find("[data-testid='schedule-info-movable']").TextContent.Trim());
        Assert.Contains("Nothing else about the task changes.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInfoOfABackup_LinksItsServerAndItsProject_AndSaysTheWriteRightIsNeeded()
    {
        var movable = RenderInfo(Backup(movable: true));
        var locked = RenderInfo(Backup(movable: false));

        Assert.Equal("/servers/7", movable.Find("[data-testid='schedule-info-server']").GetAttribute("href"));
        Assert.Equal("/projects/12", movable.Find("[data-testid='schedule-info-project']").GetAttribute("href"));
        Assert.Equal("/backups", movable.Find("[data-testid='schedule-info-open']").GetAttribute("href"));
        Assert.Contains("restore check and the retention do not change", movable.Markup, StringComparison.Ordinal);
        Assert.NotNull(locked.Find("[data-testid='schedule-info-read-only']"));
        Assert.Contains("Moving this backup needs the Write right on its project.", locked.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowsOfAScanAnAiTaskAndABackupWithTheSameSourceId_NeverMeet()
    {
        var scan = Action(movable: true) with { SourceId = 7 };
        var ai = AiTask(movable: true) with { SourceId = 7 };
        var backup = Backup(movable: true) with { SourceId = 7 };

        var rows = new[] { scan, ai, backup }.Select(ServerActionPlannerTask.RowId).ToList();

        Assert.Equal(3, rows.Distinct().Count());
        Assert.All(rows, row => Assert.True(row < 0));
    }

    [Fact]
    public void TheInfoOfACustomSchedule_IsReadOnlyEvenForAnAdministrator_AndSaysWhy()
    {
        var cut = RenderInfo(Action(movable: true, ScheduleFrequency.Custom));

        Assert.NotNull(cut.Find("[data-testid='schedule-info-read-only']"));
        Assert.Contains("This action has a custom schedule", cut.Markup, StringComparison.Ordinal);
    }
}
