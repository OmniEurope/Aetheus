// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R-12: an action in progress is shown again when the user leaves a server page and comes back. The
/// follower adopts the tasks already in flight on arrival, but only for the task names it is told
/// (<c>NamePrefix</c> or <c>AdoptPrefixes</c>): one that names none, or that opts out with
/// <c>AdoptRunning="false"</c>, hides an action in progress after the user comes back. The guard reads every
/// follower tag of the sections. An opt-out is listed here with its reason.
/// </summary>
public sealed partial class ServerTaskAdoptionGuardTests
{
    private const string SectionsDirectory = "src/Aetheus.Front/Components/Servers/ServerDetailSections";

    [GeneratedRegex(@"<ServerTaskFollower\b[^>]*>")]
    private static partial Regex FollowerTag();

    /// <summary>The sections whose follower does not adopt, with the reason. A section is added here only with a
    /// reason that says where the task is shown instead.</summary>
    private static readonly Dictionary<string, string> NotAdopting = new(StringComparer.Ordinal)
    {
        ["DockerContainersTab.razor"] = "the Docker page's own follower (ServerDockerSection) adopts the Docker tasks and veils the page once",
        ["DockerComposeTab.razor"] = "the Docker page's own follower (ServerDockerSection) adopts the Docker tasks and veils the page once",
        ["DockerImagesTab.razor"] = "the Docker page's own follower (ServerDockerSection) adopts the Docker tasks and veils the page once",
    };

    [Fact]
    public void EveryServerTaskFollower_AdoptsTheTasksAlreadyRunningOnArrival_OrSaysWhyNot()
    {
        var directory = Path.Combine(RepositoryScan.Root, SectionsDirectory);
        var files = RepositoryScan.Enumerate(directory, "*.razor");
        var offenders = new List<string>();
        var seen = 0;
        var seenNotAdopting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var file = Path.GetFileName(path);
            foreach (Match match in FollowerTag().Matches(File.ReadAllText(path)))
            {
                seen++;
                var tag = match.Value;
                var names = tag.Contains("NamePrefix=", StringComparison.Ordinal) || tag.Contains("AdoptPrefixes=", StringComparison.Ordinal);
                var optedOut = tag.Contains("AdoptRunning=\"false\"", StringComparison.Ordinal);
                if (names && !optedOut) continue;
                if (NotAdopting.ContainsKey(file)) seenNotAdopting.Add(file);
                else offenders.Add(file);
            }
        }

        Assert.True(seen >= 10, $"Only {seen} followers were seen: the scan is broken.");
        var stale = NotAdopting.Keys.Where(key => !seenNotAdopting.Contains(key)).ToList();
        Assert.True(stale.Count == 0, "These sections adopt now (or no longer have a follower), remove them from the list:\n  " + string.Join("\n  ", stale));
        Assert.True(offenders.Count == 0,
            "R-12: the follower of a section names the tasks it adopts on arrival (NamePrefix, or AdoptPrefixes), "
            + "or the action in progress disappears when the user leaves the page and comes back:\n  " + string.Join("\n  ", offenders.Distinct()));
    }
}
