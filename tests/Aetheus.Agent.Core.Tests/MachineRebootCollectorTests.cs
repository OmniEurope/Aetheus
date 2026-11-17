// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors.MachineReboot;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R-11: what the agent reads of the reboots the host decides by itself. Fake files stand for the host: the
/// parsing and the "show nothing rather than guess" rules are what is exercised, not the real /etc of a machine.</summary>
public sealed class MachineRebootCollectorTests
{
    private const string Paris = "Europe/Paris";

    private sealed class FakeFiles : IMachineRebootHostFiles
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public string? ReadText(string path) => Files.GetValueOrDefault(path);

        public IReadOnlyList<string> ListFiles(string directory) =>
            [.. Files.Keys.Where(k => Path.GetDirectoryName(k)?.Replace('\\', '/') == directory).Order(StringComparer.Ordinal)];

        public IReadOnlyList<string> ListDirectories(string directory) =>
            [.. Files.Keys
                .Select(k => Path.GetDirectoryName(k)?.Replace('\\', '/') ?? string.Empty)
                .Where(d => Path.GetDirectoryName(d)?.Replace('\\', '/') == directory)
                .Distinct().Order(StringComparer.Ordinal)];

        public bool FileExists(string path) => Files.ContainsKey(path);
    }

    private static async Task<MachineRebootDataDto> CollectAsync(FakeFiles files, string? zone = Paris, bool windows = false) =>
        await new MachineRebootCollector(files, windows, zone).CollectAsync(TestContext.Current.CancellationToken);

    private static FakeFiles Unattended(string rebootConfig, string periodic = "APT::Periodic::Unattended-Upgrade \"1\";")
    {
        var files = new FakeFiles();
        files.Files["/etc/apt/apt.conf.d/20auto-upgrades"] = periodic;
        files.Files["/etc/apt/apt.conf.d/50unattended-upgrades"] = rebootConfig;
        return files;
    }

    // ---------------------------------------------------------------- unattended-upgrades

    [Fact]
    public async Task UnattendedUpgrades_RebootOnWithATime_IsReadAsADailyConditionalReboot()
    {
        var files = Unattended("Unattended-Upgrade::Automatic-Reboot \"true\";\nUnattended-Upgrade::Automatic-Reboot-Time \"02:30\";");

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.Equal(MachineRebootSource.UnattendedUpgrades, entry.Source);
        Assert.Equal("30 2 * * *", entry.CronExpression);
        Assert.Equal(Paris, entry.TimeZoneId);
        Assert.True(entry.Conditional);
        Assert.Equal("/etc/apt/apt.conf.d/50unattended-upgrades", entry.Detail);
    }

    [Fact]
    public async Task UnattendedUpgrades_RebootOnWithoutATime_RebootsNow_SoItHasNoTimeOfDay()
    {
        var files = Unattended("Unattended-Upgrade::Automatic-Reboot \"true\";");

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.Equal(MachineRebootSource.UnattendedUpgrades, entry.Source);
        Assert.Null(entry.CronExpression);
        Assert.Null(entry.AtUtc);
    }

    [Fact]
    public async Task UnattendedUpgrades_ATimeOfNow_IsTheSameAsNoTime()
    {
        var files = Unattended("Unattended-Upgrade::Automatic-Reboot \"true\";\nUnattended-Upgrade::Automatic-Reboot-Time \"now\";");

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.Null(entry.CronExpression);
    }

    [Theory]
    [InlineData("Unattended-Upgrade::Automatic-Reboot \"false\";\nUnattended-Upgrade::Automatic-Reboot-Time \"02:00\";")]
    [InlineData("// Unattended-Upgrade::Automatic-Reboot \"true\";")]
    [InlineData("Unattended-Upgrade::Automatic-Reboot-Time \"02:00\";")]
    [InlineData("Unattended-Upgrade::Automatic-Reboot \"maybe\";")]
    public async Task UnattendedUpgrades_RebootOffOrCommentedOrUnsettled_ListsNothing(string config)
    {
        Assert.Empty((await CollectAsync(Unattended(config))).Entries);
    }

    [Fact]
    public async Task UnattendedUpgrades_WithTheUpgradesThemselvesDisabled_ListsNothing()
    {
        var files = Unattended("Unattended-Upgrade::Automatic-Reboot \"true\";", periodic: "APT::Periodic::Unattended-Upgrade \"0\";");

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("2am")]
    [InlineData("02:75")]
    [InlineData("")]
    public async Task UnattendedUpgrades_ATimeThatIsNotAClock_ListsNothing_NeverAGuessedTime(string time)
    {
        var files = Unattended($"Unattended-Upgrade::Automatic-Reboot \"true\";\nUnattended-Upgrade::Automatic-Reboot-Time \"{time}\";");

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    [Fact]
    public async Task UnattendedUpgrades_ATimeWithoutAKnownMachineZone_ListsNothing()
    {
        var files = Unattended("Unattended-Upgrade::Automatic-Reboot \"true\";\nUnattended-Upgrade::Automatic-Reboot-Time \"02:00\";");

        Assert.Empty((await CollectAsync(files, zone: null)).Entries);
    }

    [Fact]
    public async Task UnattendedUpgrades_TheLastFileInNameOrderWins_AndTheBracedFormIsRead()
    {
        var files = Unattended("Unattended-Upgrade::Automatic-Reboot \"false\";");
        files.Files["/etc/apt/apt.conf.d/52local"] = "Unattended-Upgrade { Automatic-Reboot \"true\"; Automatic-Reboot-Time \"04:15\"; };";

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.Equal("15 4 * * *", entry.CronExpression);
        Assert.Equal("/etc/apt/apt.conf.d/52local", entry.Detail);
    }

    [Fact]
    public async Task UnattendedUpgrades_ABackupOrDisabledFileOfTheDirectory_IsNotRead()
    {
        var files = Unattended("Unattended-Upgrade::Automatic-Reboot \"false\";");
        files.Files["/etc/apt/apt.conf.d/52local.dpkg-old"] = "Unattended-Upgrade::Automatic-Reboot \"true\";";
        files.Files["/etc/apt/apt.conf.d/53local.disabled"] = "Unattended-Upgrade::Automatic-Reboot \"true\";";

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    // ---------------------------------------------------------------- scheduled shutdown

    [Fact]
    public async Task ScheduledShutdown_OfModeReboot_IsASingleInstant()
    {
        var files = new FakeFiles();
        files.Files["/run/systemd/shutdown/scheduled"] = "USEC=1800000000000000\nWARN_WALL=1\nMODE=reboot\n";

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.Equal(MachineRebootSource.ScheduledShutdown, entry.Source);
        Assert.Equal(new DateTime(2027, 1, 15, 8, 0, 0, DateTimeKind.Utc), entry.AtUtc);
        Assert.Null(entry.CronExpression);
    }

    [Theory]
    [InlineData("USEC=1800000000000000\nMODE=poweroff\n")]
    [InlineData("USEC=1800000000000000\nMODE=halt\n")]
    [InlineData("MODE=reboot\n")]
    [InlineData("USEC=soon\nMODE=reboot\n")]
    [InlineData("USEC=0\nMODE=reboot\n")]
    public async Task ScheduledShutdown_ThatIsNotAReadableReboot_ListsNothing(string content)
    {
        var files = new FakeFiles();
        files.Files["/run/systemd/shutdown/scheduled"] = content;

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    // ---------------------------------------------------------------- cron

    [Theory]
    [InlineData("30 4 * * * root /sbin/reboot", "30 4 * * *")]
    [InlineData("0 5 * * 0 root /usr/sbin/shutdown -r now", "0 5 * * 0")]
    [InlineData("15 3 1 * * root systemctl reboot", "15 3 1 * *")]
    [InlineData("0 6 * * * root sudo /bin/systemctl --force reboot", "0 6 * * *")]
    [InlineData("0 2 * * * root /bin/sh -c \"sleep 5 && /sbin/reboot\"", "0 2 * * *")]
    [InlineData("@daily root /sbin/reboot", "0 0 * * *")]
    public async Task Cron_AnEntryThatReboots_IsListedWithItsFieldsInTheMachineZone(string line, string expected)
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/nightly-reboot"] = "SHELL=/bin/sh\nPATH=/usr/bin\n# comment\n" + line + "\n";

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.Equal(MachineRebootSource.Cron, entry.Source);
        Assert.Equal(expected, entry.CronExpression);
        Assert.Equal(Paris, entry.TimeZoneId);
        Assert.Equal("/etc/cron.d/nightly-reboot", entry.Detail);
        Assert.False(entry.Conditional);
    }

    [Theory]
    [InlineData("0 4 * * * root /sbin/shutdown -h now")]
    [InlineData("0 4 * * * root /sbin/poweroff")]
    [InlineData("0 4 * * * root /sbin/reboot -w")]
    [InlineData("0 4 * * * root /usr/bin/backup --reboot-after-notify")]
    [InlineData("0 4 * * * root echo reboot")]
    [InlineData("@reboot root /usr/local/bin/startup")]
    [InlineData("0 4 * * * /sbin/reboot")]
    public async Task Cron_AnEntryThatDoesNotReboot_OrIsNotAWellFormedSystemLine_ListsNothing(string line)
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/misc"] = line + "\n";

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    [Fact]
    public async Task Cron_AGuardedReboot_IsConditional()
    {
        var files = new FakeFiles();
        files.Files["/etc/crontab"] = "0 4 * * * root [ -f /var/run/reboot-required ] && /sbin/reboot\n";

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.True(entry.Conditional);
        Assert.Equal("/etc/crontab", entry.Detail);
    }

    [Fact]
    public async Task Cron_ACronTimeZoneLine_GivesTheZoneOfTheEntriesAfterIt()
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/zoned"] = "0 1 * * * root /sbin/reboot\nCRON_TZ=UTC\n0 2 * * * root /sbin/reboot\n";

        var entries = (await CollectAsync(files)).Entries;

        Assert.Equal([Paris, "UTC"], entries.Select(e => e.TimeZoneId));
    }

    [Fact]
    public async Task Cron_AFileOfCronDWithADotInItsName_IsIgnoredAsCronDoes()
    {
        var files = new FakeFiles();
        files.Files["/etc/cron.d/reboot.dpkg-old"] = "0 4 * * * root /sbin/reboot\n";

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    [Fact]
    public async Task Cron_AUserCrontab_HasNoUserColumn()
    {
        var files = new FakeFiles();
        files.Files["/var/spool/cron/crontabs/root"] = "45 23 * * 6 /sbin/reboot\n";

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.Equal("45 23 * * 6", entry.CronExpression);
    }

    // ---------------------------------------------------------------- systemd timers

    private static FakeFiles Timer(string timer, string service, bool enabled = true, string name = "weekly-reboot")
    {
        var files = new FakeFiles();
        files.Files[$"/etc/systemd/system/{name}.timer"] = timer;
        files.Files[$"/etc/systemd/system/{name}.service"] = service;
        if (enabled) files.Files[$"/etc/systemd/system/timers.target.wants/{name}.timer"] = timer;
        return files;
    }

    private const string RebootService = "[Service]\nType=oneshot\nExecStart=/usr/bin/systemctl reboot\n";

    [Theory]
    [InlineData("Sun *-*-* 03:30:00", "30 3 * * 0")]
    [InlineData("*-*-* 04:00", "0 4 * * *")]
    [InlineData("Mon..Fri 02:15", "15 2 * * 1-5")]
    [InlineData("Mon,Thu *-*-* 05:05:00", "5 5 * * 1,4")]
    [InlineData("daily", "0 0 * * *")]
    [InlineData("weekly", "0 0 * * 1")]
    [InlineData("*-*-15 01:00", "0 1 15 * *")]
    public async Task SystemdTimer_AnEnabledTimerWhoseServiceReboots_IsListedAsACronExpression(string calendar, string expected)
    {
        var files = Timer($"[Timer]\nOnCalendar={calendar}\nPersistent=true\n", RebootService);

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.Equal(MachineRebootSource.SystemdTimer, entry.Source);
        Assert.Equal(expected, entry.CronExpression);
        Assert.Equal(Paris, entry.TimeZoneId);
        Assert.Equal("/etc/systemd/system/weekly-reboot.timer", entry.Detail);
    }

    [Fact]
    public async Task SystemdTimer_ATimeZoneInTheCalendar_IsTheZoneOfTheEntry()
    {
        var files = Timer("[Timer]\nOnCalendar=*-*-* 03:00:00 UTC\n", RebootService);

        var entry = Assert.Single((await CollectAsync(files)).Entries);

        Assert.Equal("UTC", entry.TimeZoneId);
        Assert.Equal("0 3 * * *", entry.CronExpression);
    }

    [Fact]
    public async Task SystemdTimer_ATimerNotEnabled_ListsNothing()
    {
        var files = Timer("[Timer]\nOnCalendar=daily\n", RebootService, enabled: false);

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    [Fact]
    public async Task SystemdTimer_AServiceThatDoesNotReboot_ListsNothing()
    {
        var files = Timer("[Timer]\nOnCalendar=daily\n", "[Service]\nExecStart=/usr/local/bin/cleanup\n");

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    [Fact]
    public async Task SystemdTimer_ARandomizedDelay_ListsNothing_BecauseTheRealTimeIsUnknown()
    {
        var files = Timer("[Timer]\nOnCalendar=daily\nRandomizedDelaySec=6h\n", RebootService);

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    [Theory]
    [InlineData("*-*-* 03:00:30")]
    [InlineData("2027-03-01 03:00")]
    [InlineData("Fri..Mon 03:00")]
    [InlineData("minutely")]
    [InlineData("*-*-* 03:00 Mars/Olympus!")]
    public async Task SystemdTimer_ACalendarThatIsNotASimpleDailyOrWeeklyForm_ListsNothing(string calendar)
    {
        var files = Timer($"[Timer]\nOnCalendar={calendar}\n", RebootService);

        Assert.Empty((await CollectAsync(files)).Entries);
    }

    [Fact]
    public async Task SystemdTimer_TheServiceNamedByUnit_IsTheOneRead()
    {
        var files = Timer("[Timer]\nOnCalendar=daily\nUnit=do-reboot.service\n", "[Service]\nExecStart=/bin/true\n");
        files.Files["/etc/systemd/system/do-reboot.service"] = "[Service]\nExecStart=-/usr/sbin/shutdown -r +1\n";

        Assert.Single((await CollectAsync(files)).Entries);
    }

    // ---------------------------------------------------------------- whole host

    [Fact]
    public async Task ANothingConfiguredHost_ListsNothing_ButIsCollected()
    {
        var data = await CollectAsync(new FakeFiles());

        Assert.True(data.Collected);
        Assert.Empty(data.Entries);
    }

    [Fact]
    public async Task AWindowsHost_IsNotCollected()
    {
        var data = await CollectAsync(new FakeFiles(), windows: true);

        Assert.False(data.Collected);
        Assert.Empty(data.Entries);
    }

    [Fact]
    public async Task SeveralSources_AreAllListed_InAStableOrder()
    {
        var files = Unattended("Unattended-Upgrade::Automatic-Reboot \"true\";\nUnattended-Upgrade::Automatic-Reboot-Time \"03:00\";");
        files.Files["/run/systemd/shutdown/scheduled"] = "USEC=1800000000000000\nMODE=reboot\n";
        files.Files["/etc/cron.d/r"] = "0 5 * * * root /sbin/reboot\n";

        var sources = (await CollectAsync(files)).Entries.Select(e => e.Source);

        Assert.Equal(
            [MachineRebootSource.UnattendedUpgrades, MachineRebootSource.ScheduledShutdown, MachineRebootSource.Cron], sources);
    }
}
