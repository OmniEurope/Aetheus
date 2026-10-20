// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Resources;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>PLAN-010 lot 1: the SSH card says what is known, and says "unknown" with its reason
/// for what the agent could not read, never a reassuring value.</summary>
public sealed class ServerSshConfigCardTests : BunitContext
{
    public ServerSshConfigCardTests() => BunitTestHelper.RegisterServices(this);

    private static SshConfigStatusDto Status(SshConfigDataDto snapshot, params SshConfigChangeDto[] changes) => new()
    {
        Snapshot = snapshot,
        BaselineAt = new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Local),
        ObservedAt = new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Local),
        RecentChanges = [.. changes]
    };

    [Fact]
    public void NoSnapshot_SaysNotCollected()
    {
        var cut = Render<ServerSshConfigCard>(p => p.Add(x => x.Status, null));

        Assert.Contains("SshNotCollected", cut.Markup, StringComparison.Ordinal);
        Assert.NotNull(cut.Find(".ssh-not-collected"));
        Assert.Empty(cut.FindAll(".ssh-setting-permitrootlogin"));
    }

    [Fact]
    public void KnownValues_ShowValueAndTheLineThatSetIt()
    {
        var cut = Render<ServerSshConfigCard>(p => p.Add(x => x.Status, Status(new SshConfigDataDto
        {
            Collected = true,
            PermitRootLogin = new SshSettingDto { Value = "yes", Source = "/etc/ssh/sshd_config.d/10-open.conf:1" },
            PasswordAuthentication = new SshSettingDto { Value = "no", Source = "default", OverriddenInMatch = true },
            AuthorizedKeysFile = new SshSettingDto { Value = ".ssh/authorized_keys", Source = "default" }
        })));

        var root = cut.Find(".ssh-setting-permitrootlogin");
        Assert.Contains("yes", root.TextContent, StringComparison.Ordinal);
        Assert.Contains("/etc/ssh/sshd_config.d/10-open.conf:1", root.TextContent, StringComparison.Ordinal);
        var password = cut.Find(".ssh-setting-passwordauthentication").TextContent;
        Assert.Contains("SshDefaultSource", password, StringComparison.Ordinal);
        Assert.Contains("SshAlsoInMatch", password, StringComparison.Ordinal);
        Assert.Equal(OmniTone.Danger, ServerSshConfigCard.RootLoginTone("yes"));
    }

    [Fact]
    public void UnreadableRootKeysAndInclude_AreShownUnknown_WithTheReason()
    {
        Services.GetRequiredService<IStringLocalizer<AppStrings>>()["SshUnreadableConfigFiles"]
            .Returns(new LocalizedString("SshUnreadableConfigFiles", "Unreadable: {0}"));
        var cut = Render<ServerSshConfigCard>(p => p.Add(x => x.Status, Status(new SshConfigDataDto
        {
            Collected = true,
            PermitRootLogin = new SshSettingDto(),
            UnreadableFiles = ["/etc/ssh/sshd_config.d/50-cloud-init.conf"],
            AuthorizedKeys =
            [
                new AuthorizedKeysFileDto { User = "root", Path = "/root/.ssh/authorized_keys", State = AuthorizedKeysFileState.Unreadable },
                new AuthorizedKeysFileDto { User = "root", Path = "/root/.ssh/authorized_keys2", State = AuthorizedKeysFileState.Absent }
            ]
        })));

        Assert.Contains("SshUnknown", cut.Find(".ssh-setting-permitrootlogin").TextContent, StringComparison.Ordinal);
        Assert.Contains("SshUnknownSource", cut.Find(".ssh-setting-permitrootlogin").TextContent, StringComparison.Ordinal);
        Assert.Contains("50-cloud-init.conf", cut.Find(".ssh-unreadable-files").TextContent, StringComparison.Ordinal);
        var file = Assert.Single(cut.FindAll(".ssh-keys-file"));
        Assert.Contains("/root/.ssh/authorized_keys", file.TextContent, StringComparison.Ordinal);
        Assert.Contains("SshKeysUnreadableHint", file.TextContent, StringComparison.Ordinal);
        Assert.Equal(OmniTone.Warning, ServerSshConfigCard.RootLoginTone(null));
    }

    [Fact]
    public void ReadKeys_ShowFingerprints_AndChangesShowTheirMessage()
    {
        var cut = Render<ServerSshConfigCard>(p => p.Add(x => x.Status, Status(
            new SshConfigDataDto
            {
                Collected = true,
                PermitRootLogin = new SshSettingDto { Value = "no", Source = "/etc/ssh/sshd_config:3" },
                AuthorizedKeys =
                [
                    new AuthorizedKeysFileDto
                    {
                        User = "alice",
                        Path = "/home/alice/.ssh/authorized_keys",
                        State = AuthorizedKeysFileState.Read,
                        Keys = [new AuthorizedKeyDto { KeyType = "ssh-rsa", Fingerprint = "SHA256:uk8r4OWgoXqqkyaJlnxSuoizSkl4Ol+CyT/U5k6quaU" }]
                    }
                ]
            },
            new SshConfigChangeDto
            {
                DetectedAt = DateTime.Now,
                Severity = "Critical",
                Message = "new key added to /home/alice/.ssh/authorized_keys (ssh-rsa SHA256:uk8r4OWgoXqqkyaJlnxSuoizSkl4Ol+CyT/U5k6quaU)"
            })));

        Assert.Equal("ssh-rsa SHA256:uk8r4OWgoXqqkyaJlnxSuoizSkl4Ol+CyT/U5k6quaU", cut.Find(".ssh-key-fingerprint").TextContent.Trim());
        var change = cut.Find(".ssh-change").TextContent;
        Assert.Contains("SshChangeCritical", change, StringComparison.Ordinal);
        Assert.Contains("new key added to /home/alice/.ssh/authorized_keys", change, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".ssh-no-change"));
    }

    [Fact]
    public void Overview_HostsTheSshCard()
    {
        var cut = Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, new ServerDetailDto { Id = 1, Name = "web-1", Status = ServerStatus.Online })
            .Add(x => x.MetricsLoadCompleted, true));

        Assert.NotNull(cut.Find(".ssh-config-card"));
    }
}
