// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The blue-green steps mutate a live environment, so the contract worth pinning is what they refuse
/// to do: act on a scope they cannot validate, overwrite an unfinished transaction, or commit a
/// cutover that never happened.
/// </summary>
public sealed class BlueGreenOperationExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bg-tests-{Guid.NewGuid():N}");

    public BlueGreenOperationExecutorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("aetheus-prod")]
    [InlineData("aetheus-demo")]
    [InlineData("app_1")]
    public void ComposeProject_IsAcceptedAsTarget(string project)
    {
        Assert.True(OperationTargetValidator.IsValid(OperationKind.BlueGreenMigrate, project));
        Assert.True(OperationTargetValidator.IsValid(OperationKind.BlueGreenCommit, project));
    }

    [Theory]
    [InlineData("Aetheus-Prod")]
    [InlineData("-leading")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("")]
    public void ComposeProject_RejectsAnythingComposeWouldNot(string project) =>
        Assert.False(OperationTargetValidator.IsValid(OperationKind.BlueGreenUp, project));

    [Fact]
    public void Executor_HandlesOnlyTheFiveBlueGreenOperations()
    {
        var executor = CreateExecutor(Substitute.For<IShellRunner>());

        Assert.True(executor.CanHandle(OperationKind.BlueGreenMigrate));
        Assert.True(executor.CanHandle(OperationKind.BlueGreenUp));
        Assert.True(executor.CanHandle(OperationKind.BlueGreenSwitch));
        Assert.True(executor.CanHandle(OperationKind.BlueGreenCommit));
        Assert.True(executor.CanHandle(OperationKind.BlueGreenRollback));
        Assert.False(executor.CanHandle(OperationKind.PipelineSmoke));
        Assert.False(executor.CanHandle(OperationKind.PipelineDeploy));
    }


    [Fact]
    public async Task UnusableScope_IsRefusedBeforeDockerIsEverContacted()
    {
        var shell = Substitute.For<IShellRunner>();
        var executor = CreateExecutor(shell);

        var result = await executor.ExecuteAsync(
            OperationKind.BlueGreenUp, "aetheus-demo",
            new Dictionary<string, string> { ["AETHEUS_BG_STATE_DIR"] = "relative/path" },
            60, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        await shell.DidNotReceiveWithAnyArgs().RunExecAsync(default!, default!, CancellationToken.None);
    }

    [Fact]
    public void Context_RequiresFourDistinctPorts()
    {
        var env = BaseEnvironment();
        env["AETHEUS_BG_PORT_BACK_GREEN"] = env["AETHEUS_BG_PORT_FRONT_BLUE"];

        Assert.False(BlueGreenContext.TryCreate("aetheus-demo", env, out _, out var error));
        Assert.Contains("distinct", error, StringComparison.OrdinalIgnoreCase);
    }

    // A first deployment has no state directory yet, so it is created rather than refused.
    [Fact]
    public void Context_CreatesTheStateDirectoryOnAFirstDeployment()
    {
        var env = BaseEnvironment();
        var fresh = Path.Combine(_root, "fresh-state");
        env["AETHEUS_BG_STATE_DIR"] = fresh;

        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out var error));
        Assert.True(Directory.Exists(fresh), error);
        Assert.Equal(fresh, context!.StateDir);
    }

    /// <summary>
    /// Compose interpolates the image tags, the version and the source revision from its process
    /// environment. The agent service environment has none of them, so without this forwarding
    /// Compose would substitute its own defaults and start the placeholder image while the step still
    /// reported a successful cutover. Only the prefixed variables cross: the task environment also
    /// carries vault secrets that have no business reaching the containers.
    /// </summary>
    [Fact]
    public void Context_ForwardsOnlyThePrefixedComposeInputs()
    {
        var env = BaseEnvironment();
        env["AETHEUS_BG_COMPOSE_ENV_AETHEUS_BACK_IMAGE"] = "aetheus-back:deadbeef";
        env["AETHEUS_BG_COMPOSE_ENV_APP_VERSION"] = "1.1.7";
        env["DEMO_VAULT_SECRET"] = "not-for-compose";

        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out var error), error);

        Assert.Equal("aetheus-back:deadbeef", context!.ComposeEnvironment["AETHEUS_BACK_IMAGE"]);
        Assert.Equal("1.1.7", context.ComposeEnvironment["APP_VERSION"]);
        Assert.DoesNotContain("DEMO_VAULT_SECRET", context.ComposeEnvironment.Keys);
        Assert.DoesNotContain("AETHEUS_BG_STATE_DIR", context.ComposeEnvironment.Keys);
    }

    /// <summary>Render the template ACTIVE_COLOR placeholder and reject unresolved placeholders
    /// before writing the upstream file, since that write controls traffic.</summary>
    [Theory]
    [InlineData("port #{FRONT_PORT}# / #{BACK_PORT}# colour #{COLOR}#", "port 10031 / 10032 colour green")]
    [InlineData("port #{FRONT_PORT}# / #{BACK_PORT}# colour #{ACTIVE_COLOR}#", "port 10031 / 10032 colour green")]
    public void Upstream_RendersBothSpellingsOfTheColourPlaceholder(string template, string expected) =>
        Assert.Equal(expected, BlueGreenUpstream.Render(template, "green", 10031, 10032));

    [Fact]
    public void Upstream_RefusesToRenderWhenAPlaceholderSurvives() =>
        Assert.Null(BlueGreenUpstream.Render("host #{DEMO_HOST}# port #{FRONT_PORT}#", "green", 10031, 10032));

    [Fact]
    public void Context_HasNoComposeInputsWhenTheStepDeclaresNone()
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", BaseEnvironment(), out var context, out _));

        Assert.Empty(context!.ComposeEnvironment);
    }

    [Fact]
    public void Context_DerivesTheDatabaseIdentityFromTheEnvironmentFile()
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", BaseEnvironment(), out var context, out _));

        Assert.Equal("aetheus-demo-database", context!.DatabaseContainer);
        Assert.Equal("aetheus", context.DatabaseUser);
        Assert.Equal("aetheus_demo", context.DatabaseName);
    }

    // Without these the executor could not name the container it would run psql against.
    [Fact]
    public void Context_RejectsAnEnvironmentFileMissingTheDatabaseIdentity()
    {
        var env = BaseEnvironment();
        var incomplete = Path.Combine(_root, ".env-incomplete");
        File.WriteAllText(incomplete, "APPNAME=aetheus\n");
        env["AETHEUS_BG_ENV_FILE"] = incomplete;

        Assert.False(BlueGreenContext.TryCreate("aetheus-demo", env, out _, out var error));
        Assert.Contains("DB_USER", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_MapsEachColourToItsOwnPorts()
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", BaseEnvironment(), out var context, out _));

        Assert.Equal((10029, 10030), context!.PortsFor("blue"));
        Assert.Equal((10031, 10032), context.PortsFor("green"));
        Assert.Equal("green", BlueGreenContext.Opposite("blue"));
        Assert.Equal("blue", BlueGreenContext.Opposite("green"));
    }

    // A second deployment that silently replaced a live journal would destroy the only record of how
    // to get back to the previous colour.
    [Fact]
    public void Journal_RefusesToOverwriteAnUnfinishedTransaction()
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", BaseEnvironment(), out var context, out _));
        var journal = new BlueGreenJournal(context!);

        Assert.True(journal.TryOpen("blue", "green", "rev-1", out _));
        journal.MarkSwitched();

        Assert.False(journal.TryOpen("green", "blue", "rev-2", out var error));
        Assert.Contains("unfinished", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("blue", journal.PreviousLive);
        Assert.Equal("rev-1", journal.Revision);
    }

    /// <summary>
    /// A journal directory with no state file is not a transaction, and must not block the next
    /// deployment. TryOpen writes `state` last, so anything that stopped before it left the upstream
    /// configuration and both colours untouched. Refusing it froze the environment permanently:
    /// nothing in the product removes a journal it will not read, so every later deployment failed
    /// with "state=unknown" until the directory was deleted by hand (mirror nightly run 1228).
    /// </summary>
    [Fact]
    public void Journal_WithNoStateFile_IsNotATransactionAndDoesNotBlockTheNextDeployment()
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", BaseEnvironment(), out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Directory.CreateDirectory(context!.JournalDir);

        Assert.True(journal.Exists);
        Assert.Null(journal.State);

        Assert.True(journal.TryOpen("blue", "green", "rev-1", out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(BlueGreenJournal.Prepared, journal.State);
        Assert.Equal("blue", journal.PreviousLive);
    }

    /// <summary>
    /// The complement of the case above, and the reason it is scoped to an ABSENT file: a journal
    /// that carries a state is a real transaction and is still refused, whatever that state says.
    /// </summary>
    [Fact]
    public void Journal_WithAnUnrecognisedState_IsStillRefused()
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", BaseEnvironment(), out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Directory.CreateDirectory(context!.JournalDir);
        File.WriteAllText(Path.Combine(context.JournalDir, "state"), "HALF-WAY");

        Assert.False(journal.TryOpen("blue", "green", "rev-1", out var error));
        Assert.Contains("HALF-WAY", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Journal_CommitRecordsTheRevisionAndClearsItself()
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", BaseEnvironment(), out var context, out _));
        var journal = new BlueGreenJournal(context!);

        Assert.True(journal.TryOpen("blue", "green", "deadbeef", out _));
        journal.WriteLiveColour("green");
        journal.MarkSwitched();
        Assert.Equal(BlueGreenJournal.Switched, journal.State);

        journal.WriteDeployedRevision("deadbeef");
        journal.Commit();

        Assert.False(journal.Exists);
        Assert.Equal("green", journal.ReadLiveColour());
        Assert.Equal("deadbeef", File.ReadAllText(context!.SourceCommitFile).Trim());
    }

    [Fact]
    public void Journal_IgnoresAnUnrecognisedLiveColour()
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", BaseEnvironment(), out var context, out _));
        var journal = new BlueGreenJournal(context!);

        File.WriteAllText(context!.ColorFile, "purple\n");

        Assert.Null(journal.ReadLiveColour());
    }

    // ── Execution paths ─────────────────────────────────────────────────────────────────────────
    // Everything above pins what the steps refuse. These pin what they actually do, because the
    // counters of green input-validation tests proved nothing about the cutover itself.

    /// <summary>A PREPARED journal can still accompany switched traffic if interruption occurred
    /// between reload and recording SWITCHED. Rollback must restore traffic and close the journal
    /// rather than report nothing to undo.</summary>
    [Fact]
    public async Task Rollback_OnAnInterruptedSwitch_RestoresAndClearsInsteadOfClaimingNothingToUndo()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Assert.True(journal.TryOpen("blue", "green", "rev-1", out _));
        // Exactly the interrupted state: opened, never marked switched.
        Assert.Equal(BlueGreenJournal.Prepared, journal.State);

        var confPath = Path.Combine(_root, "upstream.conf");
        await File.WriteAllTextAsync(confPath, "server 127.0.0.1:10031;\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(context!.JournalDir, "upstream.before"), "server 127.0.0.1:10029;\n",
            TestContext.Current.CancellationToken);
        env["AETHEUS_BG_UPSTREAM_CONF"] = confPath;
        env["AETHEUS_BG_RELOAD_HELPER"] = "/usr/local/bin/reload";

        var log = new List<string>();

        // The previous colour answers, so traffic may be moved back to it. Restoring traffic to a
        // colour that cannot serve would turn a failed deployment into an outage, which is why this is
        // gated rather than assumed.
        var result = await CreateExecutor(new FakeShell(), HttpStatusCode.OK).ExecuteAsync(
            OperationKind.BlueGreenRollback, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(log, line => line.Contains("interrupted before the switch was recorded", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("nothing to undo", StringComparison.Ordinal));
        Assert.Equal("server 127.0.0.1:10029;\n", await File.ReadAllTextAsync(confPath, TestContext.Current.CancellationToken));
        Assert.Equal("blue", journal.ReadLiveColour());
        // The environment is usable again: a next deployment can open its own transaction.
        Assert.False(journal.Exists);
        Assert.True(journal.TryOpen("blue", "green", "rev-2", out _));
    }

    /// <summary>
    /// Declaring the Compose inputs is not enough; they have to reach the process that runs Compose.
    /// This asserts the actual invocation, because a context that merely holds the values while the
    /// executor calls the environment-less overload would leave Compose on its own defaults - a
    /// placeholder image started under the banner of a successful cutover.
    /// </summary>
    [Fact]
    public async Task Rollback_HandsTheDeclaredComposeInputsToEveryDockerInvocation()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Assert.True(journal.TryOpen("blue", "green", "rev-1", out _));
        var confPath = Path.Combine(_root, "upstream.conf");
        await File.WriteAllTextAsync(confPath, "server 127.0.0.1:10031;\n", TestContext.Current.CancellationToken);
        env["AETHEUS_BG_UPSTREAM_CONF"] = confPath;
        env["AETHEUS_BG_RELOAD_HELPER"] = "/usr/local/bin/reload";
        env["AETHEUS_BG_COMPOSE_ENV_AETHEUS_BACK_IMAGE"] = "aetheus-back:deadbeef";

        var shell = new FakeShell();
        var result = await CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.BlueGreenRollback, "aetheus-demo", env, 60,
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(shell.DockerEnvironments);
        Assert.All(
            shell.DockerEnvironments,
            environment => Assert.Equal("aetheus-back:deadbeef", environment["AETHEUS_BACK_IMAGE"]));
    }

    // Decision of 2026-10-02: a run undoes its own deployment, never another run's.
    [Fact]
    public async Task Rollback_OfAnotherRunsTransaction_IsRefusedAndLeavesItIntact()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Assert.True(journal.TryOpen("blue", "green", "rev-1", "2490", out _));
        env["AETHEUS_BG_OWNER_RUN"] = "2491";
        var log = new List<string>();

        var result = await CreateExecutor(new FakeShell()).ExecuteAsync(
            OperationKind.BlueGreenRollback, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(log, line => line.Contains("belongs to run 2490", StringComparison.Ordinal));
        Assert.Equal(BlueGreenJournal.Prepared, journal.State);
    }

    // Without the environment lock, another run may still be mid-deployment: rebuilding the idle colour
    // would destroy what its open transaction falls back to.
    [Fact]
    public async Task Up_WithAnotherRunsOpenTransaction_AndNoEnvironmentLock_RefusesBeforeTouchingAColour()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Assert.True(journal.TryOpen("blue", "green", "rev-1", "2490", out _));
        journal.MarkSwitched();
        env["AETHEUS_BG_OWNER_RUN"] = "2491";
        var shell = new FakeShell();
        var log = new List<string>();

        var result = await CreateExecutor(shell).ExecuteAsync(
            OperationKind.BlueGreenUp, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(log, line => line.Contains("Run 2490 left a deployment transaction open", StringComparison.Ordinal));
        Assert.Empty(shell.DockerEnvironments);
        Assert.Equal(BlueGreenJournal.Switched, journal.State);
    }

    // With the lock, no other run can be deploying here: the open transaction is an orphan and is undone first.
    [Fact]
    public async Task Up_WithAnOrphanTransaction_AndTheEnvironmentLock_RollsTheOrphanBackFirst()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Assert.True(journal.TryOpen("blue", "green", "rev-1", "2490", out _));
        var confPath = Path.Combine(_root, "upstream.conf");
        await File.WriteAllTextAsync(confPath, "server 127.0.0.1:10031;\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(context!.JournalDir, "upstream.before"), "server 127.0.0.1:10029;\n",
            TestContext.Current.CancellationToken);
        env["AETHEUS_BG_UPSTREAM_CONF"] = confPath;
        env["AETHEUS_BG_RELOAD_HELPER"] = "/usr/local/bin/reload";
        env["AETHEUS_BG_OWNER_RUN"] = "2491";
        env["AETHEUS_BG_EXCLUSIVE"] = "true";
        var log = new List<string>();

        await CreateExecutor(new FakeShell(), HttpStatusCode.OK).ExecuteAsync(
            OperationKind.BlueGreenUp, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Contains(log, line => line.Contains("is an orphan and is undone first", StringComparison.Ordinal));
        Assert.Equal("server 127.0.0.1:10029;\n", await File.ReadAllTextAsync(confPath, TestContext.Current.CancellationToken));
        Assert.False(journal.Exists && journal.OwnerRun == "2490", "The orphan transaction is still open.");
    }

    // Runs 1422/1423 on the simulator: a first deployment switched, its run failed without retiring it, and
    // every later deployment was refused forever. No environment lock (the generic template declares no
    // environment), but run 1422 is earlier and no longer in flight: its transaction is an orphan, and a first
    // deployment has no colour to roll back to, so it is retired before this run rebuilds blue.
    [Fact]
    public async Task Up_WithAnEndedEarlierRunsInitialSwitchedTransaction_RetiresItAndDeploys()
    {
        var (env, journal, confPath) = await OpenInitialSwitchedTransactionAsync(ownerRun: "1422");
        env["AETHEUS_BG_OWNER_RUN"] = "1423";
        env["AETHEUS_BG_ACTIVE_RUNS"] = "1423,1424";
        var log = new List<string>();

        var result = await CreateExecutor(new FakeShell(), HttpStatusCode.OK).ExecuteAsync(
            OperationKind.BlueGreenUp, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(log, line => line.Contains("run 1422 is no longer in flight", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("Initial deployment retired", StringComparison.Ordinal));
        Assert.Equal("# none\n", await File.ReadAllTextAsync(confPath, TestContext.Current.CancellationToken));
        Assert.False(journal.Exists, "The orphan transaction is still open.");
        Assert.Contains(log, line => line.EndsWith("BLUEGREEN_LIVE_COLOR]none", StringComparison.Ordinal));
    }

    // The owner is still in flight: it may be about to roll back or commit, so nothing of it is touched.
    [Fact]
    public async Task Up_WithAnEarlierRunStillInFlight_RefusesAndLeavesItsTransaction()
    {
        var (env, journal, _) = await OpenInitialSwitchedTransactionAsync(ownerRun: "1422");
        env["AETHEUS_BG_OWNER_RUN"] = "1423";
        env["AETHEUS_BG_ACTIVE_RUNS"] = "1422,1423";
        var shell = new FakeShell();
        var log = new List<string>();

        var result = await CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.BlueGreenUp, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(log, line => line.Contains("Run 1422 left a deployment transaction open", StringComparison.Ordinal));
        Assert.Empty(shell.DockerEnvironments);
        Assert.Equal(BlueGreenJournal.Switched, journal.State);
    }

    // A later run missing from the list may simply have started after it was taken; an absent list proves
    // nothing either (an older control plane).
    [Theory]
    [InlineData("1424", "1423")]
    [InlineData("1422", null)]
    public async Task Up_WhenTheOwnerCannotBeShownToHaveEnded_Refuses(string ownerRun, string? activeRuns)
    {
        var (env, journal, _) = await OpenInitialSwitchedTransactionAsync(ownerRun);
        env["AETHEUS_BG_OWNER_RUN"] = "1423";
        if (activeRuns is not null) env["AETHEUS_BG_ACTIVE_RUNS"] = activeRuns;

        var result = await CreateExecutor(new FakeShell(), HttpStatusCode.OK).ExecuteAsync(
            OperationKind.BlueGreenUp, "aetheus-demo", env, 60,
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(BlueGreenJournal.Switched, journal.State);
    }

    // The template's Rollback stage retires a failed first deployment with a step of the same run.
    [Fact]
    public async Task Retire_OfThisRunsInitialSwitchedTransaction_ClosesIt()
    {
        var (env, journal, _) = await OpenInitialSwitchedTransactionAsync(ownerRun: "1422");
        env["AETHEUS_BG_OWNER_RUN"] = "1422";

        var result = await CreateExecutor(new FakeShell()).ExecuteAsync(
            OperationKind.BlueGreenRetire, "aetheus-demo", env, 60,
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(journal.Exists);
    }

    // Retiring stops a colour: never the first deployment another run may still be validating.
    [Fact]
    public async Task Retire_OfAnotherRunStillInFlight_IsRefusedAndLeavesItIntact()
    {
        var (env, journal, confPath) = await OpenInitialSwitchedTransactionAsync(ownerRun: "1422");
        env["AETHEUS_BG_OWNER_RUN"] = "1423";
        env["AETHEUS_BG_ACTIVE_RUNS"] = "1422,1423";
        var log = new List<string>();

        var result = await CreateExecutor(new FakeShell()).ExecuteAsync(
            OperationKind.BlueGreenRetire, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(log, line => line.Contains("belongs to run 1422, which may still be running", StringComparison.Ordinal));
        Assert.Equal(BlueGreenJournal.Switched, journal.State);
        Assert.Equal("# blue\n", await File.ReadAllTextAsync(confPath, TestContext.Current.CancellationToken));
    }

    /// <summary>A first deployment (previous colour none) switched to blue by <paramref name="ownerRun"/>.</summary>
    private async Task<(Dictionary<string, string> Env, BlueGreenJournal Journal, string ConfPath)> OpenInitialSwitchedTransactionAsync(
        string ownerRun)
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Assert.True(journal.TryOpen("none", "blue", "rev-1", ownerRun, out _));
        journal.MarkSwitched();
        journal.WriteLiveColour("blue");
        var confPath = Path.Combine(_root, "upstream.conf");
        await File.WriteAllTextAsync(confPath, "# blue\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(context!.JournalDir, "upstream.before"), "# none\n", TestContext.Current.CancellationToken);
        env["AETHEUS_BG_UPSTREAM_CONF"] = confPath;
        env["AETHEUS_BG_RELOAD_HELPER"] = "/usr/local/bin/reload";
        return (env, journal, confPath);
    }

    // Only the complete absence of a transaction is "nothing to undo".
    [Fact]
    public async Task Rollback_WithNoOpenTransaction_SucceedsWithoutTouchingAnything()
    {
        var env = BaseEnvironment();
        var log = new List<string>();

        var result = await CreateExecutor(new FakeShell()).ExecuteAsync(
            OperationKind.BlueGreenRollback, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(log, line => line.Contains("No open deployment transaction", StringComparison.Ordinal));
    }

    /// <summary>Without deployment context, consult the journal: no open transaction completes
    /// without contacting Docker.</summary>
    [Fact]
    public async Task R2092_RollbackOfARunThatNeverPreparedTheHost_SucceedsAndSaysNothingWasSwitched()
    {
        var shell = Substitute.For<IShellRunner>();
        var env = MissingEnvFileEnvironment();
        var log = new List<string>();

        var result = await CreateExecutor(shell).ExecuteAsync(
            OperationKind.BlueGreenRollback, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["No deployment transaction is open: this run never switched colour, nothing to roll back."], log);
        await shell.DidNotReceiveWithAnyArgs().RunExecAsync(default!, default!, CancellationToken.None);
    }

    /// <summary>Fail when a switched transaction cannot be reached for rollback.</summary>
    [Fact]
    public async Task R2092_RollbackWithAnOpenTransaction_AndNoEnvFile_StillFails()
    {
        var env = MissingEnvFileEnvironment();
        var journalDir = Path.Combine(_root, "deployment-transaction");
        Directory.CreateDirectory(journalDir);
        await File.WriteAllTextAsync(Path.Combine(journalDir, "state"), "SWITCHED\n", TestContext.Current.CancellationToken);
        var log = new List<string>();

        var result = await CreateExecutor(Substitute.For<IShellRunner>()).ExecuteAsync(
            OperationKind.BlueGreenRollback, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(log, line => line.StartsWith("Environment file is missing", StringComparison.Ordinal));
    }

    /// <summary>Only the rollback may conclude anything from a missing env file; the other steps refuse.</summary>
    [Fact]
    public async Task R2092_ASwitchWithoutItsEnvFile_IsStillRefused()
    {
        var result = await CreateExecutor(Substitute.For<IShellRunner>()).ExecuteAsync(
            OperationKind.BlueGreenSwitch, "aetheus-demo", MissingEnvFileEnvironment(), 60,
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
    }

    private Dictionary<string, string> MissingEnvFileEnvironment()
    {
        var env = BaseEnvironment();
        env["AETHEUS_BG_ENV_FILE"] = Path.Combine(_root, ".demo-state", ".env");
        return env;
    }

    [Fact]
    public async Task Rollback_WithoutTheUpstreamTargets_RefusesToRestoreTraffic()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Assert.True(journal.TryOpen("blue", "green", "rev-1", out _));
        var shell = new FakeShell();
        var log = new List<string>();

        var result = await CreateExecutor(shell, HttpStatusCode.OK).ExecuteAsync(
            OperationKind.BlueGreenRollback, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("AETHEUS_BG_UPSTREAM_CONF and AETHEUS_BG_RELOAD_HELPER are required to restore traffic.", log);
        Assert.Equal(BlueGreenJournal.Prepared, journal.State);
    }

    [Fact]
    public async Task Rollback_ToAPreviousColourThatIsNotReady_LeavesTrafficAndTheJournal()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        var journal = new BlueGreenJournal(context!);
        Assert.True(journal.TryOpen("blue", "green", "rev-1", out _));
        journal.MarkSwitched();
        var confPath = Path.Combine(_root, "upstream.conf");
        await File.WriteAllTextAsync(confPath, "server 127.0.0.1:10031;\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(context!.JournalDir, "upstream.before"), "server 127.0.0.1:10029;\n",
            TestContext.Current.CancellationToken);
        env["AETHEUS_BG_UPSTREAM_CONF"] = confPath;
        env["AETHEUS_BG_RELOAD_HELPER"] = "/usr/local/bin/reload";
        var log = new List<string>();

        var result = await CreateExecutor(new FakeShell(), HttpStatusCode.ServiceUnavailable).ExecuteAsync(
            OperationKind.BlueGreenRollback, "aetheus-demo", env, 1,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(log, line => line.Contains("The previous blue colour did not become ready; traffic was NOT moved back.", StringComparison.Ordinal));
        Assert.Equal("server 127.0.0.1:10031;\n", await File.ReadAllTextAsync(confPath, TestContext.Current.CancellationToken));
        Assert.Equal(BlueGreenJournal.Switched, journal.State);
    }

    // A state nobody wrote means the journal is corrupt; guessing what to restore from it would be
    // worse than stopping.
    [Fact]
    public async Task Rollback_WithAnUnrecognisedState_FailsInsteadOfGuessing()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        Directory.CreateDirectory(context!.JournalDir);
        await File.WriteAllTextAsync(
            Path.Combine(context.JournalDir, "state"), "HALF-WAY\n", TestContext.Current.CancellationToken);
        env["AETHEUS_BG_UPSTREAM_CONF"] = Path.Combine(_root, "upstream.conf");
        env["AETHEUS_BG_RELOAD_HELPER"] = "/usr/local/bin/reload";

        var log = new List<string>();
        var result = await CreateExecutor(new FakeShell()).ExecuteAsync(
            OperationKind.BlueGreenRollback, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(log, line => line.Contains("unrecognised state", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shell cutover held a flock across the whole deployment; the split into steps lost it, and
    /// TryOpen alone cannot make up for it because reading the state and writing it is a check-then-act.
    /// Two runs reading the same idle colour is how a transaction gets corrupted.
    /// </summary>
    [Fact]
    public async Task ConcurrentOperation_OnTheSameEnvironment_IsRefusedRatherThanRunInParallel()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        Assert.True(BlueGreenEnvironmentLease.TryAcquire(context!, out var first, out _));
        using var held = first!;

        Assert.False(BlueGreenEnvironmentLease.TryAcquire(context!, out _, out var refusal));
        Assert.Contains("already running", refusal, StringComparison.Ordinal);

        var log = new List<string>();
        var result = await CreateExecutor(new FakeShell()).ExecuteAsync(
            OperationKind.BlueGreenUp, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(log, line => line.Contains("already running", StringComparison.Ordinal));
    }

    /// <summary>
    /// A rejected reload restores the previous configuration. When that restore ALSO fails the site
    /// may be down, so the journal is the only record left to reconcile from and must survive.
    /// </summary>
    [Fact]
    public async Task Switch_WhenTheReloadAndTheRestoreBothFail_KeepsTheJournalAndFails()
    {
        var env = BaseEnvironment();
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", env, out var context, out _));
        var confPath = Path.Combine(_root, "upstream.conf");
        var templatePath = Path.Combine(_root, "upstream.template");
        await File.WriteAllTextAsync(confPath, "server 127.0.0.1:10029;\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            templatePath, "server 127.0.0.1:#{BACK_PORT}#; # #{COLOR}# #{FRONT_PORT}#\n",
            TestContext.Current.CancellationToken);
        env["AETHEUS_BG_UPSTREAM_CONF"] = confPath;
        env["AETHEUS_BG_UPSTREAM_TEMPLATE"] = templatePath;
        env["AETHEUS_BG_RELOAD_HELPER"] = "/usr/local/bin/reload";
        env["AETHEUS_BG_REVISION"] = "deadbeef";

        var log = new List<string>();
        // Every reload is rejected, so both the switch and its compensating restore fail.
        var result = await CreateExecutor(new FakeShell(reloadExitCode: 1)).ExecuteAsync(
            OperationKind.BlueGreenSwitch, "aetheus-demo", env, 60,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(log, line => line.Contains("ALSO failed", StringComparison.Ordinal));
        Assert.True(new BlueGreenJournal(context!).Exists, "The journal is the only reconciliation record left.");
    }

    /// <summary>
    /// The readiness budget is the step's own timeout. A fixed two minutes failed applications whose
    /// cold start legitimately took longer even when the operator had configured an hour.
    /// </summary>
    [Fact]
    public async Task Readiness_HonoursTheConfiguredBudgetInsteadOfAFixedCount()
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-demo", BaseEnvironment(), out var context, out _));
        using var client = new HttpClient(new FixedStatusHandler(HttpStatusCode.ServiceUnavailable));
        var started = System.Diagnostics.Stopwatch.StartNew();

        // The colour never becomes ready, so this can only end by exhausting the budget.
        var ready = await BlueGreenReadiness.WaitAsync(
            client, context!, "green", TimeSpan.FromSeconds(1), NullLogger.Instance,
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.False(ready);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"Gave up after {started.Elapsed}, not the 1s budget.");
    }

    private Dictionary<string, string> BaseEnvironment()
    {
        var envFile = Path.Combine(_root, ".env");
        var composeFile = Path.Combine(_root, "compose.yml");
        // The database identity comes from the same env file Compose interpolates, so the context
        // refuses an environment that cannot name the container it would talk to.
        if (!File.Exists(envFile))
            File.WriteAllText(envFile, "APPNAME=aetheus\nENV=demo\nDB_USER=aetheus\nDB_NAME=aetheus_demo\n");
        if (!File.Exists(composeFile)) File.WriteAllText(composeFile, "services: {}\n");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_BG_STATE_DIR"] = _root,
            ["AETHEUS_BG_ENV_FILE"] = envFile,
            ["AETHEUS_BG_COMPOSE_FILES"] = composeFile,
            ["AETHEUS_BG_PORT_FRONT_BLUE"] = "10029",
            ["AETHEUS_BG_PORT_BACK_BLUE"] = "10030",
            ["AETHEUS_BG_PORT_FRONT_GREEN"] = "10031",
            ["AETHEUS_BG_PORT_BACK_GREEN"] = "10032"
        };
    }

    private static BlueGreenOperationExecutor CreateExecutor(
        IShellRunner shell, HttpStatusCode readiness = HttpStatusCode.ServiceUnavailable) =>
        new(shell, Options.Create(new AetheusAgentOptions()), HttpFactory(readiness),
            NullLogger<BlueGreenOperationExecutor>.Instance);

    /// <summary>
    /// A factory whose client answers every probe with one status. Agent tests may not open a
    /// listening socket - a Windows firewall prompt would stall the run - so the readiness gate is
    /// exercised through the handler the executor is given rather than through a real colour.
    /// </summary>
    private static IHttpClientFactory HttpFactory(HttpStatusCode status)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new FixedStatusHandler(status)));
        return factory;
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    /// <summary>
    /// A shell that answers the Docker capability probe and every Compose call, so the tests exercise
    /// the executor's own decisions rather than the absence of Docker. Only the reload verdict is
    /// configurable, because that is the branch these tests are about.
    /// </summary>
    private sealed class FakeShell(int reloadExitCode = 0) : IShellRunner
    {
        [Obsolete("Matches the interface; the executor never uses the shell form.")]
        public Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<string> RunWithStdinAsync(
            string fileName, IReadOnlyList<string> args, string stdin, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (args.Count > 0 && args[0] == "info") return Task.FromResult(new ShellExecResult(0, "27.0.0", string.Empty));
            if (fileName == "sudo") return Task.FromResult(new ShellExecResult(reloadExitCode, string.Empty, "rejected"));
            return Task.FromResult(new ShellExecResult(0, string.Empty, string.Empty));
        }

        /// <summary>Environment handed to each <c>docker</c> invocation, in call order.</summary>
        internal List<IReadOnlyDictionary<string, string>> DockerEnvironments { get; } = [];

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            CancellationToken ct, TimeSpan? timeout = null)
        {
            if (fileName == "docker") DockerEnvironments.Add(environmentVariables);
            return RunExecAsync(fileName, args, ct, timeout);
        }
    }

}
