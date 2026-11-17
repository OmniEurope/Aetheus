// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Recette R-12: what the TeamSpeak page calls a task it adopted on arrival, with the resources it uses as the
/// label of the operation it launches itself. The backend names are "TeamSpeak - {operation}". A name the page has no
/// operation for (a server group change, a token) is left to the follower.</summary>
internal static class TeamspeakAdoptedTitles
{
    private const string Prefix = "TeamSpeak - ";

    public static string? For(IStringLocalizer<AppStrings> l, string taskName) => ServerAdoptedTaskTitles.Resolve(taskName,
    [
        ServerAdoptedTaskTitles.Key(l, Prefix + "install", "TeamspeakSetup"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "logs", "Logs"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "kick client ", "KickClient"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "ban ", "BanClient"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "unban ", "Unban"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "move client ", "MoveClient"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "poke client ", "PokeClient"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "clientinfo ", "ClientInfo"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "graceful restart", "GracefulRestart"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "snapshot DEPLOY", "SnapshotDeploy"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "snapshot", "SnapshotCreate"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "servergrouplist", "TeamspeakServerGroups"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "tokenlist", "TeamspeakTokens"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "serverinfo", "TeamspeakStats"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "complainlist", "TeamspeakComplaints"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "create channel ", "CreateChannel"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "edit channel ", "EditChannel"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "delete channel ", "DeleteChannel"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "edit server", "EditServer"),
        ServerAdoptedTaskTitles.Key(l, Prefix + "global message", "SendMessage"),
        new(Prefix, rest => ServerAdoptedTaskTitles.Action<TeamspeakAction>(rest, (action, _) => l[action.ToString()].Value))
    ]);
}
