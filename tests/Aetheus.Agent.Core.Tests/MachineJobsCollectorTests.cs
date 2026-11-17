// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel;
using Aetheus.Agent.Core.Collectors.MachineJobs;
using Aetheus.Agent.Core.Collectors.MachineReboot;
using Aetheus.Agent.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R-27: what the agent reads of the jobs the host schedules by itself (crontabs, run-parts directories,
/// whitelisted systemd timers). Fake files and a substituted shell stand for the host: the parsing, the whitelist and the
/// "skip and count rather than guess" rules are what is exercised, not the real /etc of a machine.</summary>
public sealed class MachineJobsCollectorTests
{
    private const string Paris = "Europe/Paris";

    private sealed class FakeFiles : IMachineRebootHostFiles
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public string? ReadText(string path) => Files.GetValueOrDefault(path);

        public IReadOnlyList<string> ListFiles(string directory) =>
            [.. Files.Keys.Where(k => Path.GetDirectoryName(k)?.Replace('\\', '/') == directory).Order(StringComparer.Ordinal)];

        public IReadOnlyList<string> ListDirectories(string directory) => [];

        public bool FileExists(string path) => Files.ContainsKey(path);
    }

    /// <summary>A shell that answers the systemctl calls and remembers them.</summary>
    private sealed class FakeSystemctl
    {
        public string? ListJson { get; init; }
        public string? ListTable { get; init; }
        public Dictionary<string, string> Show { get; init; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public IShellRunner Runner { get; }

        public FakeSystemctl()
        {
            Runner = Substitute.For<IShellRunner>();
            Runner.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
                .Returns(call =>
                {
                    var file = call.ArgAt<string>(0);
                    var args = call.ArgAt<IReadOnlyList<string>>(1);
                    Calls.Add($"{file} {string.Join(' ', args)}");
                    return Task.FromResult(Answer(args));
                });
        }

        private ShellExecResult Answer(IReadOnlyList<string> args)
        {
            if (args[0] == "list-timers" && args.Contains("--output=json"))
                return ListJson is null ? new ShellExecResult(1, string.Empty, "unknown option") : new ShellExecResult(0, ListJson, string.Empty);
            if (args[0] == "list-timers")
                return ListTable is null ? new ShellExecResult(1, string.Empty, "failed") : new ShellExecResult(0, ListTable, string.Empty);
            if (args[0] == "show")
                return Show.TryGetValue(args[^1], out var text) ? new ShellExecResult(0, text, string.Empty) : new ShellExecResult(1, string.Empty, "not found");
            throw new InvalidOperationException($"Unexpected systemctl call: {string.Join(' ', args)}");
        }
    }

    private static IShellRunner NoSystemctl()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Throws(new Win32Exception("No such file or directory"));
        return shell;
    }

    private static async Task<MachineJobsDataDto> CollectAsync(FakeFiles files, IShellRunner? shell = null, string? zone = Paris, bool windows = false) =>
        await new MachineJobsCollector(files, shell ?? NoSystemctl(), windows, zone).CollectAsync(TestContext.Current.CancellationToken);

    // ---------------------------------------------------------------- crontabs

    [Fact]
    public async Task SystemCrontab_DebianDefault_ListsTheRunPartsDirectoriesThatHoldScriptsAndSkipsTheOthers()
    {
        var files = new FakeFiles();
        files.Files["/etc/crontab"] = """
            # /etc/crontab: system-wide crontab
            SHELL=/bin/sh
            PATH=/usr/local/sbin:/usr/local/bin:/sbin:/bin:/usr/sbin:/usr/bin

            # m h dom mon dow user  command
            17 *	* * *	root    cd / && run-parts --report /etc/cron.hourly
            25 6	* * *	root	test -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.daily; }
            47 6	* * 7	root	test -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.weekly; }
            52 6	1 * *	root	test -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.monthly; }
            """;
        files.Files["/etc/cron.daily/logrotate"] = "#!/bin/sh";
        files.Files["/etc/cron.daily/.placeholder"] = string.Empty;
        files.Files["/etc/cron.weekly/man-db"] = "#!/bin/sh";
        files.Files["/etc/cron.monthly/.placeholder"] = string.Empty;

        var data = await CollectAsync(files);

        Assert.True(data.Collected);
        Assert.Equal(Paris, data.TimeZoneId);
        Assert.Collection(data.Jobs,
            daily =>
            {
                Assert.Equal(MachineJobSource.PeriodicDirectory, daily.Source);
                Assert.Equal("/etc/cron.daily", daily.Name);
                Assert.Equal("25 6 * * *", daily.CronExpression);
                Assert.Equal(Paris, daily.TimeZoneId);
                Assert.Equal("root", daily.User);
                Assert.True(daily.Conditional);
                Assert.Equal("/etc/crontab", daily.Detail);
            },
            weekly =>
            {
                Assert.Equal("/etc/cron.weekly", weekly.Name);
                Assert.Equal("47 6 * * 7", weekly.CronExpression);
                Assert.True(weekly.Conditional);
            });
        // cron.hourly and cron.monthly hold no script: nothing to show, and nothing to count either.
        Assert.Equal(0, data.SkippedLines);
    }

    [Fact]
    public async Task SystemCrontab_AHourlyRunPartsLineWithoutAnacron_IsNotConditional()
    {
        var files = new FakeFiles();
        files.Files["/etc/crontab"] = "17 *\t* * *\troot    cd / && run-parts --report /etc/cron.hourly";
        files.Files["/etc/cron.hourly/sysstat"] = "#!/bin/sh";

        var job = Assert.Single((await CollectAsync(files)).Jobs);

        Assert.Equal("17 * * * *", job.CronExpression);
        Assert.False(job.Conditional);
    }

    [Fact]
    public async Task PeriodicDirectory_WithScriptsAndNoCrontabLineNamingIt_HasNoTimeAndIsCounted()
    {
        var files = new FakeFiles();
        files.Files["/etc/crontab"] = "SHELL=/bin/sh";
        files.Files["/etc/cron.daily/backup"] = "#!/bin/sh";

        var data = await CollectAsync(files);

        Assert.Empty(data.Jobs);
        Assert.Equal(1, data.SkippedLines);
    }

    [Fact]
    public async Task CronD_SystemFormat_ReadsTheUserFieldAndKeepsOnlyTheProgramNotItsArguments()
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/webapp"] = """
            MAILTO=ops@example.com
            */5 * * * * www-data /usr/bin/php /var/www/cron.php --token=hunter2
            """;
        files.Files["/etc/cron.d/webapp.dpkg-old"] = "0 1 * * * root /bin/ignored";

        var data = await CollectAsync(files);

        var job = Assert.Single(data.Jobs);
        Assert.Equal(MachineJobSource.CronDirectory, job.Source);
        Assert.Equal("php", job.Name);
        Assert.Equal("www-data", job.User);
        Assert.Equal("*/5 * * * *", job.CronExpression);
        Assert.Equal("/etc/cron.d/webapp", job.Detail);
        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Macros_AreExpanded_AndRebootHasNoTimeSoItIsCounted()
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/macros"] = """
            @daily root /usr/local/bin/cleanup
            @hourly root /usr/local/bin/ping-home
            @reboot root /usr/local/bin/on-boot
            """;

        var data = await CollectAsync(files);

        Assert.Collection(data.Jobs,
            daily => Assert.Equal("0 0 * * *", daily.CronExpression),
            hourly => Assert.Equal("0 * * * *", hourly.CronExpression));
        Assert.Equal(1, data.SkippedLines);
    }

    [Fact]
    public async Task UserCrontab_HasNoUserColumn_AndTheFileNameIsTheUser()
    {
        var files = new FakeFiles();
        files.Files["/var/spool/cron/crontabs/alice"] = """
            # edit with crontab -e
            30 4 * * 1 /home/alice/bin/backup.sh --full
            """;

        var job = Assert.Single((await CollectAsync(files)).Jobs);

        Assert.Equal(MachineJobSource.UserCrontab, job.Source);
        Assert.Equal("alice", job.User);
        Assert.Equal("backup.sh", job.Name);
        Assert.Equal("30 4 * * 1", job.CronExpression);
        Assert.Equal("/var/spool/cron/crontabs/alice", job.Detail);
    }

    [Fact]
    public async Task UserCrontab_RedHatSpoolDirectory_IsReadToo()
    {
        var files = new FakeFiles();
        files.Files["/var/spool/cron/bob"] = "0 3 * * * /usr/bin/rsync -a /data /backup";

        var job = Assert.Single((await CollectAsync(files)).Jobs);

        Assert.Equal("bob", job.User);
        Assert.Equal("rsync", job.Name);
    }

    [Fact]
    public async Task CronTz_ChangesTheZoneOfTheFollowingLinesOfThatFileOnly()
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/a"] = "CRON_TZ=America/New_York\n0 9 * * * root /bin/tokyo-sync";
        files.Files["/etc/cron.d/b"] = "0 9 * * * root /bin/local-sync";

        var data = await CollectAsync(files);

        Assert.Collection(data.Jobs,
            a => Assert.Equal("America/New_York", a.TimeZoneId),
            b => Assert.Equal(Paris, b.TimeZoneId));
    }

    [Theory]
    [InlineData("61 * * * * root /bin/bad")]
    [InlineData("* * 32 * * root /bin/bad")]
    [InlineData("not a cron line at all here root /bin/bad")]
    public async Task ALineTheCronEngineRejects_IsSkippedAndCounted_NeverFatal(string line)
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/mixed"] = $"{line}\n0 2 * * * root /bin/good";

        var data = await CollectAsync(files);

        Assert.Equal("good", Assert.Single(data.Jobs).Name);
        Assert.Equal(1, data.SkippedLines);
    }

    [Fact]
    public async Task ACronLineThatRebootsTheHost_IsLeftToTheRebootReader()
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/reboot"] = "0 5 * * 0 root /sbin/reboot\n0 6 * * * root /usr/local/bin/report";

        var job = Assert.Single((await CollectAsync(files)).Jobs);

        Assert.Equal("report", job.Name);
    }

    [Fact]
    public async Task ProgramName_SkipsPlumbingAndWrappers()
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/plumbing"] = "0 1 * * * root cd /srv/app && nice -n10 /usr/bin/node worker.js --key=abc";

        Assert.Equal("node", Assert.Single((await CollectAsync(files)).Jobs).Name);
    }

    [Fact]
    public async Task MissingOrUnreadableFiles_ReadAsNothing_ButTheHostWasRead()
    {
        var data = await CollectAsync(new FakeFiles());

        Assert.True(data.Collected);
        Assert.Empty(data.Jobs);
        Assert.Equal(0, data.SkippedLines);
    }

    [Fact]
    public async Task WithoutAMachineZone_ACronJobCannotBePlaced_SoItIsCountedNotGuessed()
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/a"] = "0 2 * * * root /bin/a\nCRON_TZ=UTC\n0 3 * * * root /bin/b";

        var data = await CollectAsync(files, zone: null);

        Assert.Null(data.TimeZoneId);
        Assert.Equal("b", Assert.Single(data.Jobs).Name);
        Assert.Equal(1, data.SkippedLines);
    }

    [Fact]
    public async Task Windows_HasNoCron_AndReportsNotCollected_WithoutRunningAnything()
    {
        var shell = Substitute.For<IShellRunner>();

        var data = await CollectAsync(new FakeFiles(), shell, windows: true);

        Assert.False(data.Collected);
        Assert.Empty(data.Jobs);
        await shell.DidNotReceiveWithAnyArgs().RunExecAsync(default!, default!, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task MoreJobsThanAReportCarries_AreCutAndCounted()
    {
        var files = new FakeFiles();
        for (var i = 0; i < MachineJobLimits.MaxEntries + 5; i++)
            files.Files[$"/etc/cron.d/job{i:000}"] = $"0 {i % 24} * * * root /bin/job{i}";

        var data = await CollectAsync(files);

        Assert.Equal(MachineJobLimits.MaxEntries, data.Jobs.Count);
        Assert.Equal(5, data.SkippedLines);
    }

    // ---------------------------------------------------------------- systemd timers

    private const long NextMicroseconds = 1_800_000_000_000_000;
    private static readonly DateTime NextUtc = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).UtcDateTime;

    private static string TimersJson(params string[] units) =>
        "[" + string.Join(',', units.Select(u =>
            $$"""{"next":{{NextMicroseconds}},"left":1,"last":2,"passed":3,"unit":"{{u}}","activates":"{{u[..^6]}}.service"}""")) + "]";

    [Fact]
    public async Task Timers_OnlyTheWhitelistedOnesAreKept_AndTheOthersAreNeverQueried()
    {
        var systemctl = new FakeSystemctl
        {
            ListJson = TimersJson("logrotate.timer", "backup-customer.timer", "fstrim.timer", "logrotate.timer.evil.timer"),
            Show =
            {
                ["logrotate.timer"] = "TimersCalendar={ OnCalendar=*-*-* 00:00:00 ; next_elapse=Fri 2026-11-13 00:00:00 UTC }\nRandomizedDelayUSec=0\n",
                ["fstrim.timer"] = "TimersCalendar={ OnCalendar=Mon *-*-* 00:00:00 ; next_elapse=Mon 2026-11-16 00:00:00 UTC }\nRandomizedDelayUSec=0\n"
            }
        };

        var data = await CollectAsync(new FakeFiles(), systemctl.Runner);

        Assert.Collection(data.Jobs,
            fstrim =>
            {
                Assert.Equal(MachineJobSource.SystemdTimer, fstrim.Source);
                Assert.Equal("fstrim.timer", fstrim.Name);
                Assert.Equal("0 0 * * 1", fstrim.CronExpression);
                Assert.Equal(Paris, fstrim.TimeZoneId);
                Assert.Null(fstrim.User);
                Assert.Null(fstrim.AtUtc);
            },
            logrotate =>
            {
                Assert.Equal("logrotate.timer", logrotate.Name);
                Assert.Equal("0 0 * * *", logrotate.CronExpression);
            });
        Assert.DoesNotContain(systemctl.Calls, call => call.Contains("backup-customer", StringComparison.Ordinal) || call.Contains("evil", StringComparison.Ordinal));
        Assert.All(systemctl.Calls, call => Assert.StartsWith("systemctl ", call, StringComparison.Ordinal));
        Assert.Equal(0, data.SkippedLines);
    }

    [Fact]
    public async Task Timers_AUnitWithARandomDelay_IsReportedAsItsNextInstantOnly()
    {
        var systemctl = new FakeSystemctl
        {
            ListJson = TimersJson("apt-daily.timer"),
            Show = { ["apt-daily.timer"] = "TimersCalendar={ OnCalendar=*-*-* 6,18:00 ; next_elapse=Fri 2026-11-13 09:14:02 UTC }\nRandomizedDelayUSec=12h\n" }
        };

        var job = Assert.Single((await CollectAsync(new FakeFiles(), systemctl.Runner)).Jobs);

        Assert.Equal("apt-daily.timer", job.Name);
        Assert.Null(job.CronExpression);
        Assert.Equal(NextUtc, job.AtUtc);
    }

    [Fact]
    public async Task Timers_AMonotonicTimerWithoutCalendar_IsReportedAsItsNextInstant()
    {
        var systemctl = new FakeSystemctl
        {
            ListJson = TimersJson("man-db.timer"),
            Show = { ["man-db.timer"] = "TimersCalendar=\nRandomizedDelayUSec=0\n" }
        };

        var job = Assert.Single((await CollectAsync(new FakeFiles(), systemctl.Runner)).Jobs);

        Assert.Equal(NextUtc, job.AtUtc);
    }

    [Fact]
    public async Task Timers_AWhitelistedTimerWithNeitherCalendarNorNextInstant_IsCounted()
    {
        var systemctl = new FakeSystemctl
        {
            ListJson = """[{"next":null,"left":null,"last":null,"passed":null,"unit":"certbot.timer","activates":"certbot.service"}]""",
            Show = { ["certbot.timer"] = "TimersCalendar=\nRandomizedDelayUSec=0\n" }
        };

        var data = await CollectAsync(new FakeFiles(), systemctl.Runner);

        Assert.Empty(data.Jobs);
        Assert.Equal(1, data.SkippedLines);
    }

    [Fact]
    public async Task Timers_WhenJsonOutputIsNotSupported_FallsBackToTheUnitNamesOfThePlainTable()
    {
        var systemctl = new FakeSystemctl
        {
            ListTable = """
                Fri 2026-11-13 00:00:00 CET  5h left Thu 2026-11-12 00:00:03 CET 18h ago logrotate.timer  logrotate.service
                Fri 2026-11-13 06:21:11 CET  11h left n/a                          n/a    some-custom.timer  some-custom.service
                """,
            Show = { ["logrotate.timer"] = "TimersCalendar={ OnCalendar=*-*-* 00:00:00 ; next_elapse=Fri 2026-11-13 00:00:00 CET }\nRandomizedDelayUSec=0\n" }
        };

        var job = Assert.Single((await CollectAsync(new FakeFiles(), systemctl.Runner)).Jobs);

        Assert.Equal("logrotate.timer", job.Name);
        Assert.Equal("0 0 * * *", job.CronExpression);
        Assert.Contains(systemctl.Calls, call => call.Contains("--output=json", StringComparison.Ordinal));
        Assert.Contains(systemctl.Calls, call => call.Contains("--no-legend", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Timers_SystemctlAbsent_ReadsAsNoTimers_AndTheCronJobsAreStillRead()
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/a"] = "0 2 * * * root /bin/a";

        var data = await CollectAsync(files, NoSystemctl());

        Assert.True(data.Collected);
        Assert.Equal("a", Assert.Single(data.Jobs).Name);
    }

    [Fact]
    public async Task Timers_ACalendarWithItsOwnZone_KeepsThatZone()
    {
        var systemctl = new FakeSystemctl
        {
            ListJson = TimersJson("logrotate.timer"),
            Show = { ["logrotate.timer"] = "TimersCalendar={ OnCalendar=*-*-* 03:00:00 UTC ; next_elapse=Fri 2026-11-13 03:00:00 UTC }\nRandomizedDelayUSec=0\n" }
        };

        var job = Assert.Single((await CollectAsync(new FakeFiles(), systemctl.Runner)).Jobs);

        Assert.Equal("0 3 * * *", job.CronExpression);
        Assert.Equal("UTC", job.TimeZoneId);
    }

    [Fact]
    public async Task Timers_AreSentFirstWhenTheReportIsCut_BecauseTheyAreTheFewest()
    {
        var files = new FakeFiles();
        for (var i = 0; i < MachineJobLimits.MaxEntries; i++)
            files.Files[$"/etc/cron.d/job{i:000}"] = $"0 {i % 24} * * * root /bin/job{i}";
        var systemctl = new FakeSystemctl
        {
            ListJson = TimersJson("logrotate.timer"),
            Show = { ["logrotate.timer"] = "TimersCalendar={ OnCalendar=*-*-* 00:00:00 ; next_elapse=x }\nRandomizedDelayUSec=0\n" }
        };

        var data = await CollectAsync(files, systemctl.Runner);

        Assert.Equal(MachineJobLimits.MaxEntries, data.Jobs.Count);
        Assert.Equal("logrotate.timer", data.Jobs[0].Name);
        Assert.Equal(1, data.SkippedLines);
    }

    // ---------------------------------------------------------------- whitelist and zone

    [Fact]
    public void Whitelist_IsExact_AndEveryUnitHasItsReason()
    {
        Assert.True(MachineTimerWhitelist.Contains("logrotate.timer"));
        Assert.False(MachineTimerWhitelist.Contains("logrotate"));
        Assert.False(MachineTimerWhitelist.Contains("Logrotate.timer"));
        Assert.False(MachineTimerWhitelist.Contains("sysstat-collect.timer"));
        Assert.All(MachineTimerWhitelist.Units, unit =>
        {
            Assert.EndsWith(".timer", unit, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(MachineTimerWhitelist.ReasonFor(unit)));
        });
        Assert.Null(MachineTimerWhitelist.ReasonFor("backup-customer.timer"));
    }

    [Theory]
    [InlineData("Europe/Paris", null, "Europe/Paris")]
    [InlineData("Local", "Europe/Berlin\n", "Europe/Berlin")]
    [InlineData("Local", null, null)]
    [InlineData("Local", "not a zone at all", null)]
    public void MachineZone_IsTheRuntimeOne_ElseTheDebianTimezoneFile_ElseUnknown(string runtime, string? timezoneFile, string? expected)
    {
        var files = new FakeFiles();
        if (timezoneFile is not null) files.Files["/etc/timezone"] = timezoneFile;

        Assert.Equal(expected, MachineJobsCollector.ResolveZone(files, runtime));
    }
}
