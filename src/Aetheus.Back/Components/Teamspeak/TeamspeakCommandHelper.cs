// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Teamspeak;

public static partial class TeamspeakCommandHelper
{
    private const string DefaultInstallPath = "/opt/teamspeak3-server_linux_amd64";
    private const string CredentialPath = "/opt/aetheus-agent/teamspeak/query-credentials";

    public static bool IsValidInstallPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && InstallPathRegex().IsMatch(path);

    public static bool IsValidChannelName(string name) =>
        !string.IsNullOrWhiteSpace(name) && ChannelNameRegex().IsMatch(name);

    // BuildServiceCommand was removed (recette R-119): its `systemctl ... teamspeak3` strings ran without root
    // and carried no operation kind. Start/stop/restart dispatch typed OperationKind.TeamspeakStart/Stop/Restart.

    // TeamSpeak install is now a typed OperationKind.TeamspeakSetup dispatched to the root-owned
    // teamspeak-setup helper (controlled-sudo recipe) - the old free-form shell pipeline lived here but
    // could never run on the secure non-root agent. See TeamspeakService.SetupAsync + the agent's
    // TeamspeakSetupOperationExecutor + install-agent-linux.sh write_teamspeak_setup().

    // BuildGetLogsCommand was removed: its `tail ... | sort | tail` pipe was rejected by the agent's
    // CommandValidator (the `|`), so log reads died silently. GetLogs now dispatches the typed
    // OperationKind.TeamspeakGetLogs (the agent tails {installPath}/logs/ directly). See
    // TeamspeakGracefulRestartOperationExecutor.

    public static string BuildKickCommand(int clientId, string reason, int queryPort)
    {
        var escapedReason = EscapeServerQuery(reason);
        return WrapServerQuery($"clientkick clid={clientId} reasonid=5 reasonmsg={escapedReason}", queryPort);
    }

    public static string BuildBanCommand(string clientUniqueId, int durationSeconds, string reason, int queryPort)
    {
        var escapedReason = EscapeServerQuery(reason);
        var escapedUid = EscapeServerQuery(clientUniqueId);
        return WrapServerQuery($"banadd uid={escapedUid} time={durationSeconds} banreason={escapedReason}", queryPort);
    }

    /// <summary>Item #9 tier-1 - TS3 ServerQuery <c>clientmove clid=X cid=Y [cpw=password]</c>.</summary>
    public static string BuildMoveClientCommand(int clientId, int targetChannelId, string? channelPassword, int queryPort)
    {
        var sq = $"clientmove clid={clientId} cid={targetChannelId}";
        if (!string.IsNullOrEmpty(channelPassword))
        {
            var escaped = EscapeServerQuery(channelPassword);
            sq += $" cpw={escaped}";
        }
        return WrapServerQuery(sq, queryPort);
    }

    /// <summary>Item #9 tier-1 - TS3 ServerQuery <c>clientpoke clid=X msg=...</c>. The popup
    /// message is bounded to 200 chars by the DTO validator (TS3 server-side limit).</summary>
    public static string BuildPokeClientCommand(int clientId, string message, int queryPort)
    {
        var escapedMsg = EscapeServerQuery(message);
        return WrapServerQuery($"clientpoke clid={clientId} msg={escapedMsg}", queryPort);
    }

    // ===== Tier 2/3 =====

    /// <summary>Tier-2 - detailed client info (IP, version, idle, hardware id, ...).</summary>
    public static string BuildClientInfoCommand(int clientId, int queryPort) =>
        WrapServerQuery($"clientinfo clid={clientId}", queryPort);

    // BuildGracefulRestartCommand was removed: its `printf|nc && sleep && ... && systemctl restart`
    // chain carried `|`/`$()`/`2>` metacharacters the agent's CommandValidator rejected (and even named
    // the wrong unit, `teamspeak3` vs `ts3server`), so the action died silently. GracefulRestart now
    // dispatches the typed OperationKind.TeamspeakGracefulRestart, a composite handled agent-side by
    // TeamspeakGracefulRestartOperationExecutor (gm/kick over the native ServerQuery TCP client + a
    // service-control `systemctl restart ts3server`).

    /// <summary>Tier-2 - snapshot capture. The whole blob comes back in the task stdout.</summary>
    public static string BuildSnapshotCreateCommand(int queryPort) =>
        WrapServerQuery("serversnapshotcreate", queryPort);

    /// <summary>Tier-2 - snapshot deploy. Caller's confirmation has already been checked
    /// upstream; the blob is passed verbatim. WARNING: this rewrites channels + groups +
    /// permissions, hence the typed-confirmation guard on the back endpoint.</summary>
    public static string BuildSnapshotDeployCommand(string snapshotBlob, int queryPort)
    {
        var escaped = EscapeServerQuery(snapshotBlob);
        return WrapServerQuery($"serversnapshotdeploy {escaped}", queryPort);
    }

    /// <summary>Tier-2 - list server groups.</summary>
    public static string BuildServerGroupListCommand(int queryPort) =>
        WrapServerQuery("servergrouplist", queryPort);

    /// <summary>Tier-2 - promote a client into a server group.</summary>
    public static string BuildServerGroupAddClientCommand(int serverGroupId, int clientDbid, int queryPort) =>
        WrapServerQuery($"servergroupaddclient sgid={serverGroupId} cldbid={clientDbid}", queryPort);

    /// <summary>Tier-2 - demote a client from a server group.</summary>
    public static string BuildServerGroupDelClientCommand(int serverGroupId, int clientDbid, int queryPort) =>
        WrapServerQuery($"servergroupdelclient sgid={serverGroupId} cldbid={clientDbid}", queryPort);

    /// <summary>Tier-2 - list existing tokens.</summary>
    public static string BuildTokenListCommand(int queryPort) =>
        WrapServerQuery("tokenlist", queryPort);

    /// <summary>Tier-2 - create a new token.</summary>
    public static string BuildTokenAddCommand(int tokenType, int groupId, int channelId, string? description, int queryPort)
    {
        var sq = $"tokenadd tokentype={tokenType} tokenid1={groupId} tokenid2={channelId}";
        if (!string.IsNullOrEmpty(description))
            sq += $" tokendescription={EscapeServerQuery(description)}";
        return WrapServerQuery(sq, queryPort);
    }

    /// <summary>Tier-2 - delete a token by value.</summary>
    public static string BuildTokenDeleteCommand(string token, int queryPort) =>
        WrapServerQuery($"tokendelete token={EscapeServerQuery(token)}", queryPort);

    /// <summary>Tier-3 - virtualserver stats (slots used/peak, transfer, uptime, license).</summary>
    public static string BuildServerInfoCommand(int queryPort) =>
        WrapServerQuery("serverinfo", queryPort);

    /// <summary>Tier-3 - list all complaints.</summary>
    public static string BuildComplaintListCommand(int queryPort) =>
        WrapServerQuery("complainlist", queryPort);

    /// <summary>Tier-3 - dismiss a complaint. If <paramref name="sourceClientDbid"/> is null,
    /// deletes ALL complaints for the target via <c>complaindelall</c>.</summary>
    public static string BuildComplaintDeleteCommand(int targetClientDbid, int? sourceClientDbid, int queryPort)
    {
        var sq = sourceClientDbid is null
            ? $"complaindelall tcldbid={targetClientDbid}"
            : $"complaindel tcldbid={targetClientDbid} fcldbid={sourceClientDbid.Value}";
        return WrapServerQuery(sq, queryPort);
    }

    public static string BuildUnbanCommand(int banId, int queryPort)
    {
        return WrapServerQuery($"bandel banid={banId}", queryPort);
    }

    public static string BuildGetBansCommand(int queryPort)
    {
        return WrapServerQuery("banlist", queryPort);
    }

    public static string BuildCreateChannelCommand(string name, int? parentId, string? password, int? maxClients, bool isPermanent, int queryPort)
    {
        var sb = new StringBuilder();
        sb.Append($"channelcreate channel_name={EscapeServerQuery(name)}");
        if (parentId.HasValue)
            sb.Append($" cpid={parentId.Value}");
        if (!string.IsNullOrEmpty(password))
            sb.Append($" channel_password={EscapeServerQuery(password)}");
        if (maxClients.HasValue)
            sb.Append($" channel_maxclients={maxClients.Value} channel_flag_maxclients_unlimited=0");
        else
            sb.Append(" channel_flag_maxclients_unlimited=1");
        if (isPermanent)
            sb.Append(" channel_flag_permanent=1");
        return WrapServerQuery(sb.ToString(), queryPort);
    }

    public static string BuildEditChannelCommand(int channelId, string? name, string? password, int? maxClients, int queryPort)
    {
        var sb = new StringBuilder();
        sb.Append($"channeledit cid={channelId}");
        if (!string.IsNullOrEmpty(name))
            sb.Append($" channel_name={EscapeServerQuery(name)}");
        if (!string.IsNullOrEmpty(password))
            sb.Append($" channel_password={EscapeServerQuery(password)}");
        if (maxClients.HasValue)
            sb.Append($" channel_maxclients={maxClients.Value} channel_flag_maxclients_unlimited=0");
        return WrapServerQuery(sb.ToString(), queryPort);
    }

    public static string BuildDeleteChannelCommand(int channelId, int queryPort)
    {
        return WrapServerQuery($"channeldelete cid={channelId} force=1", queryPort);
    }

    public static string BuildServerEditCommand(string? serverName, string? password, int? maxClients, string? welcomeMessage, int queryPort)
    {
        var sb = new StringBuilder("serveredit");
        if (!string.IsNullOrEmpty(serverName))
            sb.Append($" virtualserver_name={EscapeServerQuery(serverName)}");
        if (!string.IsNullOrEmpty(password))
            sb.Append($" virtualserver_password={EscapeServerQuery(password)}");
        if (maxClients.HasValue)
            sb.Append($" virtualserver_maxclients={maxClients.Value}");
        if (!string.IsNullOrEmpty(welcomeMessage))
            sb.Append($" virtualserver_welcomemessage={EscapeServerQuery(welcomeMessage)}");
        return WrapServerQuery(sb.ToString(), queryPort);
    }

    public static string BuildGlobalMessageCommand(string message, int queryPort)
    {
        return WrapServerQuery($"sendtextmessage targetmode=3 target=1 msg={EscapeServerQuery(message)}", queryPort);
    }

    // S-TECH-87: the ServerQuery write/query path is now a typed agent operation
    // (OperationKind.TeamspeakServerQuery) executed over a native TcpClient - no shell, no `nc`.
    // Build* therefore return the BARE ServerQuery line; TeamspeakService dispatches it as a typed
    // task carrying the query port out-of-band (TEAMSPEAK_QUERY_PORT env var), and the agent prepends
    // `login serveradmin <cred> / use sid=1` (credential read from its local file) and appends `quit`.
    // The queryPort parameter is retained on the Build* contract so callers stay uniform, but it is no
    // longer baked into the command. (WrapServerQueryShell - the legacy `printf … | nc` framing - was
    // removed along with BuildGracefulRestartCommand; the graceful restart is now a typed agent op.)
    private static string WrapServerQuery(string command, int queryPort) => command;

    private static string EscapeServerQuery(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("/", "\\/")
            .Replace(" ", "\\s")
            .Replace("|", "\\p")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    // Install path: only valid filesystem paths
    [GeneratedRegex(@"^/[a-zA-Z0-9._/-]+$")]
    private static partial Regex InstallPathRegex();

    // Channel name: printable chars, no pipe
    [GeneratedRegex(@"^[^\x00-\x1f|]{1,40}$")]
    private static partial Regex ChannelNameRegex();
}
