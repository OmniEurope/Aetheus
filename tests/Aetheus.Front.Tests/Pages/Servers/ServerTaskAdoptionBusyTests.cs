// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-12: a task adopted on arrival (the user left the page and came back while it runs) shows the name the
/// page gives the task it launches itself, never the raw backend name, and the page's actions are disabled exactly
/// as when the page launched it, so a second launch is impossible while it runs.
/// </summary>
public sealed class ServerTaskAdoptionBusyTests : BunitContext
{
    private const int ServerId = 4;

    public ServerTaskAdoptionBusyTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private static ServerDetailDto Server() => new()
    {
        Id = ServerId,
        Name = "cert-srv",
        Hostname = "10.0.0.4",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Certbot = new CertbotDataDto { IsInstalled = true, Version = "2.11.0", Certificates = [] }
    };

    private IRenderedComponent<ContainerFragment> RenderCertbotWithHeader() =>
        Render(builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), PageHeaderActions.SectionName);
            builder.CloseComponent();
            builder.OpenComponent<ServerCertbotSection>(2);
            builder.AddAttribute(3, nameof(ServerCertbotSection.Server), Server());
            builder.AddAttribute(4, nameof(ServerCertbotSection.ServerId), ServerId);
            builder.CloseComponent();
        });

    private static ServerTaskDto Running(int id, string name) =>
        new() { Id = id, ServerId = ServerId, Name = name, Status = TaskExecutionStatus.Running };

    [Fact]
    public void R12_AnAdoptedCertbotTask_ShowsTheLocalizedNameNotTheRawTaskName()
    {
        Tracker.Upsert(Running(12879, "Certbot RenewalCheck"));

        var cut = RenderCertbotWithHeader();

        var banner = cut.Find(".server-task-follower").TextContent;
        Assert.Contains("CertbotRenewalCheck", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("Certbot RenewalCheck", banner, StringComparison.Ordinal);
    }

    [Fact]
    public void R12_AnAdoptedCertbotTaskOfACertificate_NamesTheCertificateToo()
    {
        Tracker.Upsert(Running(12880, "Certbot Renew - example.org"));

        var cut = RenderCertbotWithHeader();

        var banner = cut.Find(".server-task-follower").TextContent;
        Assert.Contains("Renew example.org", banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R12_WhileAnAdoptedTaskRuns_TheActionsAreDisabled_AndEnabledAgainWhenItEnds()
    {
        Tracker.Upsert(Running(12879, "Certbot RenewalCheck"));
        var cut = RenderCertbotWithHeader();

        var create = cut.FindAll("button").First(button => button.GetAttribute("title") == "NewCertificate");
        Assert.True(create.HasAttribute("disabled"));

        await cut.InvokeAsync(() => Tracker.HandleTerminal(new TaskCompletedNotification
        {
            TaskId = 12879,
            ServerId = ServerId,
            TaskName = "Certbot RenewalCheck",
            Status = TaskExecutionStatus.Success
        }));

        cut.WaitForAssertion(() =>
        {
            var again = cut.FindAll("button").First(button => button.GetAttribute("title") == "NewCertificate");
            Assert.False(again.HasAttribute("disabled"));
        });
    }

    [Fact]
    public void R12_WithoutATask_TheActionsAreEnabled()
    {
        var cut = RenderCertbotWithHeader();

        var create = cut.FindAll("button").First(button => button.GetAttribute("title") == "NewCertificate");
        Assert.False(create.HasAttribute("disabled"));
    }
}
