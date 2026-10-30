// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Audit;
using Aetheus.Front.Components.Logs;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Servers.AgentWizard;
using Aetheus.Front.Components.Settings;
using Aetheus.Front.Components.Shared;
using AngleSharp.Dom;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// R-68 (user decision of 2026-10-25): a copy button shows only its icon, never the word "Copy". Its accessible
/// name and tooltip stay (the keys are echoed by the test localizer).
/// </summary>
public class CopyButtonsIconOnlyTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public CopyButtonsIconOnlyTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    private static void AssertIconOnly(IElement button, string name)
    {
        Assert.Equal(string.Empty, button.TextContent.Trim());
        Assert.Equal(name, button.GetAttribute("aria-label"));
        Assert.Equal(name, button.GetAttribute("title"));
        Assert.NotNull(button.QuerySelector("svg, .omni-icon"));
    }

    [Fact]
    public void AuditDetailDialog_CopyButton_ShowsOnlyTheIcon()
    {
        _handler.SetJsonResponse("api/audit/1/verify", new AuditChainVerificationResult { IsValid = true, TotalEntries = 1 });
        var cut = Render<AuditDetailDialog>(p => p.Add(c => c.Log, new AuditLogDto
        {
            Id = 1,
            Username = "admin",
            Action = "Updated",
            EntityType = "Server",
            EntityId = 42,
            Details = "{\"a\":1}",
            Timestamp = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc)
        }));

        AssertIconOnly(cut.Find(".dialog-footer button[aria-label='Copy']"), "Copy");
    }

    [Fact]
    public void NotificationDetailDialog_CopyButton_ShowsOnlyTheIcon()
    {
        var cut = Render<NotificationDetailDialog>(p => p.Add(c => c.Detail, "something happened"));

        AssertIconOnly(cut.Find("button[aria-label='Copy']"), "Copy");
    }

    [Fact]
    public void PipelineRunVariablesDialog_CopyButton_ShowsOnlyTheIcon()
    {
        var cut = Render<PipelineRunVariablesDialog>(p => p.Add(c => c.Variables, new Dictionary<string, string> { ["A"] = "1" }));

        AssertIconOnly(cut.Find("button[aria-label='Copy']"), "Copy");
    }

    [Fact]
    public void WizardCombinedCopyAll_CopyButton_ShowsOnlyTheIcon()
    {
        var cut = Render<WizardCombinedCopyAll>(p => p.Add(c => c.Combined, "echo one && echo two"));

        AssertIconOnly(cut.Find("button[aria-label='WizardCopyAll']"), "WizardCopyAll");
    }

    [Fact]
    public void LogEntryDialog_CopySplitButton_ShowsOnlyTheIcon_AndKeepsItsName()
    {
        var cut = Render<LogEntryDialog>(p => p.Add(c => c.Message, "boom"));

        var main = cut.Find(".dialog-footer .log-entry-copy .omni-split-button__main");
        Assert.Equal(string.Empty, main.TextContent.Trim());
        Assert.Equal("Copy", main.GetAttribute("aria-label"));
        Assert.Equal("Copy", cut.Find(".dialog-footer .log-entry-copy").GetAttribute("title"));
        Assert.NotNull(main.QuerySelector("svg, .omni-icon"));
    }

    [Fact]
    public void AppErrorDetailDialog_CopyButton_ShowsOnlyTheIcon()
    {
        var cut = Render<AppErrorDetailDialog>(p => p.Add(c => c.Error, new AppErrorEventDto
        {
            Id = 4,
            Fingerprint = "fp",
            ExceptionType = "InvalidOperationException",
            Message = "boom",
            OccurrenceCount = 1,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow
        }));

        AssertIconOnly(cut.Find(".dialog-footer .app-error-detail-copy"), "CopyAsMarkdown");
    }

    [Fact]
    public void PersonalAccessTokens_RevealedTokenCopyButton_ShowsOnlyTheIcon()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "personal-access-tokens", Array.Empty<PersonalAccessTokenDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "personal-access-tokens", new CreatedPersonalAccessTokenDto
        {
            Token = new PersonalAccessTokenDto { Id = 3, Name = "deploy", TokenPrefix = "aeth_pat_ab" },
            PlaintextToken = "aeth_pat_SECRET"
        });
        var cut = Render<PersonalAccessTokens>();
        cut.Find("input#Name").Input("deploy");
        cut.Find("button[type=\"submit\"]").Click();

        AssertIconOnly(cut.Find("button[aria-label='Copy']"), "Copy");
    }
}
