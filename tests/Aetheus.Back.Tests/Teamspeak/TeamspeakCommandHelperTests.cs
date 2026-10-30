// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Teamspeak;

namespace Aetheus.Back.Tests;

public class TeamspeakCommandHelperTests
{
    // --- IsValidInstallPath ---

    [Theory]
    [InlineData("/opt/teamspeak", true)]
    [InlineData("/opt/teamspeak3-server_linux_amd64", true)]
    [InlineData("/usr/local/ts3server", true)]
    [InlineData("/home/user/ts3", true)]
    [InlineData("/a", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("relative/path", false)]
    [InlineData("/path with spaces", false)]
    [InlineData("/path;rm -rf", false)]
    public void IsValidInstallPath_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, TeamspeakCommandHelper.IsValidInstallPath(path));
    }

    // --- IsValidChannelName ---

    [Theory]
    [InlineData("Default Channel", true)]
    [InlineData("AFK Room", true)]
    [InlineData("Gaming", true)]
    [InlineData("Channel 1", true)]
    [InlineData("Café & Musique", true)]
    [InlineData("", false)]
    [InlineData("bad|name", false)]
    public void IsValidChannelName_ReturnsExpected(string name, bool expected)
    {
        Assert.Equal(expected, TeamspeakCommandHelper.IsValidChannelName(name));
    }

    [Fact]
    public void IsValidChannelName_TooLong_ReturnsFalse()
    {
        var longName = new string('A', 41);
        Assert.False(TeamspeakCommandHelper.IsValidChannelName(longName));
    }

    // BuildServiceCommand was removed (R-119: typed OperationKind.TeamspeakStart/Stop/Restart). See
    // TeamspeakServiceTests (dispatch) + TeamspeakServiceOperationExecutorTests (agent argv).

    // BuildGetLogsCommand was removed (typed OperationKind.TeamspeakGetLogs). See
    // TeamspeakServiceTests (dispatch) + TeamspeakGracefulRestartOperationExecutorTests (agent read).

    // --- BuildKickCommand ---

    [Fact]
    public void BuildKickCommand_ContainsClientIdAndReason()
    {
        var cmd = TeamspeakCommandHelper.BuildKickCommand(42, "Being rude", 10011);

        Assert.Contains("clientkick", cmd);
        Assert.Contains("clid=42", cmd);
        Assert.Contains("reasonid=5", cmd);
        Assert.Contains("Being\\srude", cmd);
    }

    // --- BuildBanCommand ---

    [Fact]
    public void BuildBanCommand_ContainsUidAndDuration()
    {
        var cmd = TeamspeakCommandHelper.BuildBanCommand("uid123", 3600, "spam", 10011);

        Assert.Contains("banadd", cmd);
        Assert.Contains("uid=uid123", cmd);
        Assert.Contains("time=3600", cmd);
        Assert.Contains("banreason=spam", cmd);
    }

    // --- BuildUnbanCommand ---

    [Fact]
    public void BuildUnbanCommand_ContainsBanId()
    {
        var cmd = TeamspeakCommandHelper.BuildUnbanCommand(77, 10011);

        Assert.Contains("bandel", cmd);
        Assert.Contains("banid=77", cmd);
    }

    // --- BuildGetBansCommand ---

    [Fact]
    public void BuildGetBansCommand_ContainsBanlist()
    {
        var cmd = TeamspeakCommandHelper.BuildGetBansCommand(10011);
        Assert.Contains("banlist", cmd);
    }

    // --- BuildCreateChannelCommand ---

    [Fact]
    public void BuildCreateChannelCommand_AllOptions_ContainsAll()
    {
        var cmd = TeamspeakCommandHelper.BuildCreateChannelCommand("Lobby", 2, "pass123", 10, true, 10011);

        Assert.Contains("channelcreate", cmd);
        Assert.Contains("channel_name=Lobby", cmd);
        Assert.Contains("cpid=2", cmd);
        Assert.Contains("channel_password=pass123", cmd);
        Assert.Contains("channel_maxclients=10", cmd);
        Assert.Contains("channel_flag_maxclients_unlimited=0", cmd);
        Assert.Contains("channel_flag_permanent=1", cmd);
    }

    [Fact]
    public void BuildCreateChannelCommand_NoOptions_Unlimited()
    {
        var cmd = TeamspeakCommandHelper.BuildCreateChannelCommand("Test", null, null, null, false, 10011);

        Assert.Contains("channel_name=Test", cmd);
        Assert.DoesNotContain("cpid=", cmd);
        Assert.DoesNotContain("channel_password=", cmd);
        Assert.Contains("channel_flag_maxclients_unlimited=1", cmd);
        Assert.DoesNotContain("channel_flag_permanent=1", cmd);
    }

    // --- BuildEditChannelCommand ---

    [Fact]
    public void BuildEditChannelCommand_WithName_ContainsCid()
    {
        var cmd = TeamspeakCommandHelper.BuildEditChannelCommand(5, "Renamed", null, null, 10011);

        Assert.Contains("channeledit", cmd);
        Assert.Contains("cid=5", cmd);
        Assert.Contains("channel_name=Renamed", cmd);
    }

    [Fact]
    public void BuildEditChannelCommand_WithPasswordAndMaxClients()
    {
        var cmd = TeamspeakCommandHelper.BuildEditChannelCommand(3, null, "pwd", 8, 10011);

        Assert.Contains("channel_password=pwd", cmd);
        Assert.Contains("channel_maxclients=8", cmd);
    }

    // --- BuildDeleteChannelCommand ---

    [Fact]
    public void BuildDeleteChannelCommand_ContainsCidAndForce()
    {
        var cmd = TeamspeakCommandHelper.BuildDeleteChannelCommand(10, 10011);

        Assert.Contains("channeldelete", cmd);
        Assert.Contains("cid=10", cmd);
        Assert.Contains("force=1", cmd);
    }

    // --- BuildServerEditCommand ---

    [Fact]
    public void BuildServerEditCommand_AllOptions_ContainsAll()
    {
        var cmd = TeamspeakCommandHelper.BuildServerEditCommand("My Server", "pass", 64, "Welcome!", 10011);

        Assert.Contains("serveredit", cmd);
        Assert.Contains("virtualserver_name=My\\sServer", cmd);
        Assert.Contains("virtualserver_password=pass", cmd);
        Assert.Contains("virtualserver_maxclients=64", cmd);
        Assert.Contains("virtualserver_welcomemessage=Welcome!", cmd);
    }

    [Fact]
    public void BuildServerEditCommand_NoOptions_OnlyServeredit()
    {
        var cmd = TeamspeakCommandHelper.BuildServerEditCommand(null, null, null, null, 10011);

        Assert.Contains("serveredit", cmd);
        Assert.DoesNotContain("virtualserver_name", cmd);
        Assert.DoesNotContain("virtualserver_password", cmd);
    }

    // --- BuildGlobalMessageCommand ---

    [Fact]
    public void BuildGlobalMessageCommand_EscapesMessage()
    {
        var cmd = TeamspeakCommandHelper.BuildGlobalMessageCommand("Hello World", 10011);

        Assert.Contains("sendtextmessage", cmd);
        Assert.Contains("targetmode=3", cmd);
        Assert.Contains("msg=Hello\\sWorld", cmd);
    }

    // ServerQuery helpers produce bare command lines; the agent executor owns framing, credentials
    // and TCP transport. GracefulRestart uses its dedicated shell-chain path.

    [Fact]
    public void ServerQueryCommands_AreBareLines_WithoutShellFraming()
    {
        var commands = new[]
        {
            TeamspeakCommandHelper.BuildKickCommand(1, "test", 10011),
            TeamspeakCommandHelper.BuildBanCommand("uid", 0, "test", 10011),
            TeamspeakCommandHelper.BuildGetBansCommand(10011),
            TeamspeakCommandHelper.BuildCreateChannelCommand("ch", null, null, null, false, 10011),
            TeamspeakCommandHelper.BuildDeleteChannelCommand(1, 10011),
            TeamspeakCommandHelper.BuildGlobalMessageCommand("hi", 10011),
        };

        foreach (var cmd in commands)
        {
            Assert.DoesNotContain("login serveradmin", cmd);
            Assert.DoesNotContain("use sid=1", cmd);
            Assert.DoesNotContain("printf", cmd);
            Assert.DoesNotContain("| nc", cmd);
            Assert.DoesNotContain('\n', cmd); // no control chars: the agent appends the protocol framing
        }
    }

    // BuildGracefulRestartCommand was removed (typed OperationKind.TeamspeakGracefulRestart composite).
    // The gm/kick/restart sequence is covered by TeamspeakGracefulRestartOperationExecutorTests and the
    // dispatch by TeamspeakServiceTests.

    // --- EscapeServerQuery edge cases (tested via command builders) ---

    [Fact]
    public void KickCommand_SpecialCharsInReason_AreEscaped()
    {
        var cmd = TeamspeakCommandHelper.BuildKickCommand(1, "go away\nnow", 10011);
        Assert.Contains("go\\saway\\nnow", cmd);
    }

    [Fact]
    public void BanCommand_PipeInReason_IsEscaped()
    {
        var cmd = TeamspeakCommandHelper.BuildBanCommand("uid", 0, "test|reason", 10011);
        Assert.Contains("test\\preason", cmd);
    }

    [Fact]
    public void CreateChannelCommand_SpacesInName_AreEscaped()
    {
        var cmd = TeamspeakCommandHelper.BuildCreateChannelCommand("My Channel", null, null, null, false, 10011);
        Assert.Contains("channel_name=My\\sChannel", cmd);
    }
}
