// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R-119: an action of a server page that queues an agent task keeps the task follower on screen and veils
/// the section (OE's <c>omni-busy</c>, content kept mounted) until the task ends, success or failure. The veil is
/// held by <c>ServerTaskFollower</c> itself (it asks the server layout to veil the section body while its task
/// runs), so a section is covered when its source places the follower: <c>&lt;ServerTaskFollower</c> in the markup, or a
/// <c>ServerTaskFollower</c> handed to it by its page (the Certbot dialog), or a zone of its own that carries the
/// veil (<c>omni-busy</c>, the mail page's task banner).
/// The guard reads every section file of the folder: one that calls an API method which queues an agent task
/// (<see cref="QueuingCall"/>) must be covered, or the call is listed in <see cref="NotAQueuedTask"/> with its reason.
/// It cannot prove that a given handler reaches the follower: the behaviour tests of the follower and of the
/// layout prove the veil itself.
/// </summary>
public sealed partial class ServerTaskVeilGuardTests
{
    private const string SectionsDirectory = "src/Aetheus.Front/Components/Servers/ServerDetailSections";

    /// <summary>A call to one of the API clients whose methods queue agent tasks.</summary>
    [GeneratedRegex(@"\bApi\.(?:ServerTools|Security|Teamspeak|Mail)\.(?<method>\w+Async)\b")]
    private static partial Regex QueuingCall();

    /// <summary>The section's source carries the veil: the follower, or a zone of its own that is veiled.</summary>
    [GeneratedRegex(@"<ServerTaskFollower\b|\bServerTaskFollower\?|\bomni-busy\b|<MailTaskBanner\b")]
    private static partial Regex Veil();

    /// <summary>API methods that do not queue an agent task that changes the host (a read, or a change kept in
    /// Aetheus's own database), with the reason. A method not listed and not read-only by its name is a task.</summary>
    private static readonly Dictionary<string, string> NotAQueuedTask = new(StringComparer.Ordinal)
    {
        ["AllocatePortsAsync"] = "the port registry is Aetheus's own database",
        ["SetPortRangeAsync"] = "the port registry is Aetheus's own database",
        ["CreatePortReservationAsync"] = "the port registry is Aetheus's own database",
        ["ReleasePortReservationAsync"] = "the port registry is Aetheus's own database",
        ["AddPortsentryWhitelistIpAsync"] = "the whitelist is Aetheus's own database",
        ["RemovePortsentryWhitelistIpAsync"] = "the whitelist is Aetheus's own database",
        ["SetRkhunterScheduleAsync"] = "the schedule is Aetheus's own database",
        ["RequestServiceLogsAsync"] = "a read of the service logs, shown in the page's own log zone (omni-busy)",
        ["InspectContainerAsync"] = "a read, shown in a dialog of its own",
        ["ListContainerFilesAsync"] = "a read, shown in a dialog of its own",
        ["ExecuteShellCommandAsync"] = "a command typed in a dialog, whose output is shown there",
    };

    [Fact]
    public void ASectionThatQueuesAnAgentTask_ShowsTheFollowerAndVeilsItsZone()
    {
        var directory = Path.Combine(RepositoryScan.Root, SectionsDirectory);
        var files = RepositoryScan.Enumerate(directory, "*.razor.cs");
        Assert.True(files.Count >= 30, $"Only {files.Count} section code-behind files scanned: the scan is broken.");

        var used = new HashSet<string>(StringComparer.Ordinal);
        var queuingFiles = 0;
        var offenders = new List<string>();
        foreach (var codeBehind in files)
        {
            var source = SectionSource(codeBehind);
            var calls = QueuedCalls(File.ReadAllText(codeBehind), used);
            if (calls.Count == 0) continue;
            queuingFiles++;
            if (!Veil().IsMatch(source))
                offenders.Add($"{Path.GetFileName(codeBehind)} ({string.Join(", ", calls)})");
        }

        // The scan must really see the queuing sections, or it protects nothing.
        Assert.True(queuingFiles >= 15, $"Only {queuingFiles} sections queuing a task were seen: the scan is broken.");
        var stale = NotAQueuedTask.Keys.Where(key => !used.Contains(key)).ToList();
        Assert.True(stale.Count == 0, "These methods are no longer called by a section, remove them from the list:\n  " + string.Join("\n  ", stale));
        Assert.True(offenders.Count == 0,
            "R-119: a section that queues an agent task shows <ServerTaskFollower> (it veils the section until the task ends), "
            + "or veils a zone of its own with omni-busy:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Every follower tag of the markup (a section may place one per tab).</summary>
    [GeneratedRegex(@"<ServerTaskFollower\b[^>]*>")]
    private static partial Regex FollowerTag();

    /// <summary>The sections that show an inventory the agent collects and sends back with its heartbeat (the
    /// agent keeps most of them for up to three minutes, and collects them again after an operation that changed
    /// them). Their follower keeps the veil until that state has arrived (<c>SettleOnHeartbeat</c>), or the
    /// veil lifts on the state from before the task. Source file, with what it shows.</summary>
    private static readonly Dictionary<string, string> InventorySections = new(StringComparer.Ordinal)
    {
        ["ServerApacheSection.razor"] = "Apache sites and modules",
        ["ServerCertbotSection.razor"] = "Certbot certificates",
        ["ServerFirewallSection.razor"] = "firewall rules",
        ["ServerPortsentrySection.razor"] = "Portsentry state and blocked addresses",
        ["ServerRkhunterSection.razor"] = "Rkhunter state and results",
        ["ServerTeamspeakSection.razor"] = "TeamSpeak state, channels and clients",
        ["ServerMailSection.razor"] = "mail stack",
        ["ServerPatchSection.razor"] = "pending security updates",
        ["ServerServicesSection.razor"] = "services",
        ["ServerDockerSection.razor"] = "Docker containers, images and networks",
        ["DockerContainersTab.razor"] = "Docker containers",
        ["DockerComposeTab.razor"] = "Docker compose stacks",
        ["DockerImagesTab.razor"] = "Docker images",
    };

    /// <summary>Among them, the sections whose data is read from the backend's own copy of the inventory, not from
    /// the server state the heartbeat pushes: they read it again once the wait is over (<c>OnSettled</c>).</summary>
    private static readonly string[] ReadsItsOwnData =
    [
        "ServerFirewallSection.razor", "ServerPatchSection.razor", "ServerPortsentrySection.razor",
        "ServerRkhunterSection.razor", "ServerTeamspeakSection.razor", "ServerMailSection.razor"
    ];

    [Fact]
    public void ASectionThatShowsAnAgentInventory_KeepsTheVeilUntilTheNewStateHasArrived()
    {
        var directory = Path.Combine(RepositoryScan.Root, SectionsDirectory);
        var offenders = new List<string>();
        foreach (var (file, shows) in InventorySections)
        {
            var path = Path.Combine(directory, file);
            Assert.True(File.Exists(path), $"{file} no longer exists: update the list ({shows}).");
            var tags = FollowerTag().Matches(File.ReadAllText(path)).Select(match => match.Value).ToList();
            if (tags.Count == 0 || tags.Any(tag => !tag.Contains("SettleOnHeartbeat=\"true\"", StringComparison.Ordinal)))
                offenders.Add($"{file} ({shows})");
        }

        Assert.True(offenders.Count == 0,
            "R-119: the follower of a section that shows an agent inventory settles on the heartbeat "
            + "(SettleOnHeartbeat=\"true\"), or the veil lifts on the state from before the task:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ASectionThatReadsItsOwnData_ReadsItAgainWhenTheNewStateHasArrived()
    {
        var directory = Path.Combine(RepositoryScan.Root, SectionsDirectory);
        var offenders = ReadsItsOwnData
            .Where(file => FollowerTag().Matches(File.ReadAllText(Path.Combine(directory, file)))
                .Any(match => !match.Value.Contains("OnSettled=", StringComparison.Ordinal)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "R-119: these sections read their data from the backend, which has it only after the heartbeat; "
            + "their follower reads it again (OnSettled) before the veil lifts:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ASectionThatSettlesOnTheHeartbeat_DoesNotWaitForAStateAfterAReadOnlyTask()
    {
        var directory = Path.Combine(RepositoryScan.Root, SectionsDirectory);
        var offenders = new List<string>();
        foreach (var file in InventorySections.Keys)
        {
            var codeBehind = Path.Combine(directory, file + ".cs");
            if (!File.Exists(codeBehind)) continue;
            foreach (var line in File.ReadAllLines(codeBehind))
            {
                // Reading a log changes nothing the agent reports: no new state to wait for.
                if (line.Contains("Expect(L[\"ViewLogs\"]", StringComparison.Ordinal) && !line.Contains("settle: false", StringComparison.Ordinal))
                    offenders.Add($"{file}.cs: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "R-119: a task that only reads is followed with settle: false, or the veil waits up to 15 s for a state "
            + "that did not change:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheTeamspeakLogRead_DoesNotWaitForAState()
    {
        // The TeamSpeak section runs every task through TeamspeakOperationRunner, which settles by default:
        // the log read changes nothing, so it must opt out or the veil waits up to 15 s after it (R-119).
        var code = File.ReadAllText(Path.Combine(RepositoryScan.Root, SectionsDirectory, "ServerTeamspeakSection.razor.cs"));
        var reads = System.Text.RegularExpressions.Regex.Matches(
            code, @"RunOperationAsync\(\s*\(\)\s*=>\s*Api\.Teamspeak\.GetTeamspeakLogsAsync\([^;]*;");

        Assert.NotEmpty(reads);
        Assert.All(reads, read => Assert.Contains("settle: false", read.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void TheScan_SeesASectionThatQueuesATaskWithoutAFollower()
    {
        const string codeBehind = "private async Task GoAsync() => await Api.ServerTools.ExecuteApacheActionAsync(ServerId, request);";
        Assert.Equal(["ExecuteApacheActionAsync"], QueuedCalls(codeBehind, []));
        Assert.DoesNotMatch(Veil(), "<OmniStack><OmniButton OnClick=\"@GoAsync\" /></OmniStack>");
        Assert.Matches(Veil(), "<ServerTaskFollower @ref=\"_follower\" ServerId=\"@ServerId\" />");
        Assert.Matches(Veil(), "<OmniCard Class=\"@(Busy ? \"omni-busy\" : null)\">");
    }

    [Fact]
    public void TheScan_IgnoresAReadAndAChangeKeptInTheDatabase()
    {
        const string codeBehind = """
            var state = await Api.ServerTools.GetApacheStateAsync(ServerId);
            var preview = await Api.Mail.PreviewMailMxAsync(ServerId, domain);
            var reserved = await Api.ServerTools.CreatePortReservationAsync(ServerId, request);
            """;
        Assert.Empty(QueuedCalls(codeBehind, []));
    }

    /// <summary>The queued tasks of <paramref name="source"/>: calls that are neither a read by name nor listed.</summary>
    private static List<string> QueuedCalls(string source, HashSet<string> used)
    {
        var calls = new List<string>();
        foreach (Match match in QueuingCall().Matches(source))
        {
            var method = match.Groups["method"].Value;
            if (NotAQueuedTask.ContainsKey(method))
            {
                used.Add(method);
                continue;
            }
            if (IsRead(method)) continue;
            if (!calls.Contains(method)) calls.Add(method);
        }
        return calls;
    }

    /// <summary>A read by its name: it queues no change of the host, or shows its answer where it is asked.</summary>
    private static bool IsRead(string method) =>
        method.StartsWith("Get", StringComparison.Ordinal)
        || method.StartsWith("Preview", StringComparison.Ordinal)
        || method.StartsWith("Verify", StringComparison.Ordinal)
        || method.StartsWith("Check", StringComparison.Ordinal)
        || method.StartsWith("Ingest", StringComparison.Ordinal);

    /// <summary>The code-behind and its markup: the follower is placed in the markup.</summary>
    private static string SectionSource(string codeBehind)
    {
        var markup = codeBehind[..^".cs".Length];
        return File.ReadAllText(codeBehind) + (File.Exists(markup) ? File.ReadAllText(markup) : string.Empty);
    }
}
