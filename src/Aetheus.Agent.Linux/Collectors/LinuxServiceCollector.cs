// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;

namespace Aetheus.Agent.Linux.Collectors;

public sealed class LinuxServiceCollector(ILogger<LinuxServiceCollector> logger, IShellRunner shellRunner) : IServiceCollector
{
    private static readonly HashSet<string> SystemCriticalServices =
        ["sshd", "cron", "rsyslog", "systemd-journald", "systemd-logind", "systemd-udevd", "dbus", "auditd"];

    public async Task<List<ServiceInfoDto>> CollectAsync(CancellationToken ct = default)
    {
        var services = new List<ServiceInfoDto>();
        services.AddRange(await CollectSystemdServicesAsync(ct).ConfigureAwait(false));
        services.AddRange(await CollectDockerContainersAsync(ct).ConfigureAwait(false));
        await AddBinaryDetectedServicesAsync(services, ct).ConfigureAwait(false);
        await RemoveServicesOfUninstalledPackagesAsync(services, ct).ConfigureAwait(false);
        return services;
    }

    // For services supplied exclusively by their catalogue Debian package, dpkg installation status
    // determines availability. Services with alternative providers use unit and binary evidence
    // instead.
    private static readonly string[] PackageDecidedServices = ["apache2", "postfix", "dovecot", "portsentry", "rkhunter"];

    private async Task RemoveServicesOfUninstalledPackagesAsync(List<ServiceInfoDto> services, CancellationToken ct)
    {
        if (!services.Any(s => s.Type == ServiceType.Systemd && PackageDecidedServices.Contains(s.Name, StringComparer.OrdinalIgnoreCase)))
            return;

        var installed = await DpkgInstalledPackages.ReadAsync(shellRunner, logger, ct).ConfigureAwait(false);
        if (installed is null) return;

        foreach (var service in PackageDecidedServices)
        {
            var package = ManageablePackages.AptPackageFor(service);
            if (package is null || installed.Contains(package)) continue;
            services.RemoveAll(s => s.Type == ServiceType.Systemd && s.Name.Equals(service, StringComparison.OrdinalIgnoreCase));
        }
    }

    // Some "modules" don't ship a systemd unit on every distro:
    //   - certbot may be installed via snap, pip, or pure binary (timer-only - no .service unit)
    //   - teamspeak3 server is most often deployed as a tarball under /opt without a unit file
    //   - apache/postfix/dovecot/etc. may be installed but masked or deployed as binaries on minimal images
    // Detect each manageable module via well-known binaries / install paths so they still surface as "Installed".
    private async Task AddBinaryDetectedServicesAsync(List<ServiceInfoDto> services, CancellationToken ct)
    {
        // audit E-1: docker presence is an argv-pure probe - invoke the binary directly
        // (no shell, no `command -v`/redirection) and treat a clean launch as "installed".
        await TryAddDockerDetectedAsync(services, ct).ConfigureAwait(false);

        foreach (var probe in BinaryProbes)
        {
            await TryAddDetectedAsync(services, probe.Name, probe.Aliases, probe.Command, ct).ConfigureAwait(false);
        }
    }

    // Shell-only probes: each Command is a compile-time-constant string with ZERO external
    // interpolation, but genuinely needs a shell because it relies on `command -v`/`test`/`||`/pipes
    // that have no argv equivalent. They are routed through the injected IShellRunner (still
    // `/bin/bash -c <constant>`) - documented architectural exception to the "argv-only" rule.
    private static readonly (string Name, string[] Aliases, string Command)[] BinaryProbes =
    [
        ("certbot",    new[] { "certbot" },
            "command -v certbot >/dev/null 2>&1 || command -v /snap/bin/certbot >/dev/null 2>&1 || test -x /usr/bin/certbot"),
        ("teamspeak",  new[] { "teamspeak", "ts3server", "teamspeak3-server" },
            "ls -d /opt/teamspeak* /opt/ts3server* /usr/local/teamspeak* 2>/dev/null | head -n1 | grep -q ."),
        ("apache2",    new[] { "apache2", "httpd" },
            "command -v apache2 >/dev/null 2>&1 || command -v httpd >/dev/null 2>&1"),
        // Checks the executable because package removal may leave configuration directories behind.
        ("postfix",    new[] { "postfix" },
            "command -v postfix >/dev/null 2>&1 || test -x /usr/sbin/postfix"),
        ("dovecot",    new[] { "dovecot" },
            "command -v dovecot >/dev/null 2>&1 || test -x /usr/sbin/dovecot"),
        ("portsentry", new[] { "portsentry" },
            "command -v portsentry >/dev/null 2>&1 || test -x /usr/sbin/portsentry"),
        ("rkhunter",   new[] { "rkhunter" },
            "command -v rkhunter >/dev/null 2>&1 || test -x /usr/bin/rkhunter")
    ];

    private async Task TryAddDockerDetectedAsync(List<ServiceInfoDto> services, CancellationToken ct)
    {
        if (services.Any(s => s.Name.Equals("docker", StringComparison.OrdinalIgnoreCase)))
            return;

        try
        {
            // argv-pure: `docker --version` exits 0 when the binary is on PATH and runnable.
            var res = await shellRunner.RunExecAsync("docker", ["--version"], ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (res.ExitCode != 0) return;

            services.Add(BuildDetectedService("docker"));
        }
        catch (Exception ex)
        {
            // Docker not installed: the launch itself throws (file not found) - degrade silently.
            logger.LogDebug(ex, "Binary-detection probe failed for {Service}", "docker");
        }
    }

    private async Task TryAddDetectedAsync(List<ServiceInfoDto> services, string name, string[] aliases, string probe, CancellationToken ct)
    {
        if (services.Any(s => aliases.Any(a => s.Name.Equals(a, StringComparison.OrdinalIgnoreCase))))
            return;

        try
        {
            var exitCode = await RunShellExitCodeAsync(probe, ct).ConfigureAwait(false);
            if (exitCode != 0) return;

            services.Add(BuildDetectedService(name));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Binary-detection probe failed for {Service}", name);
        }
    }

    private static ServiceInfoDto BuildDetectedService(string name) => new()
    {
        Name = name,
        Type = ServiceType.Systemd,
        Status = "installed",
        IsRunning = false,
        IsInstalled = true,
        IsManageable = true
    };

    // audit E-1: shell-only probe - constant string, zero external interpolation; argv conversion
    // not possible (needs command -v/test/pipe). Routed through the injected IShellRunner.
    private async Task<int> RunShellExitCodeAsync(string command, CancellationToken ct)
    {
        var res = await shellRunner.RunExecAsync("/bin/bash", ["-c", command], ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return res.ExitCode;
    }

    // Known oneshot services that do not ship a systemd .timer (cron-driven or invoked on demand).
    // Their .service SubState is `dead` at rest by design - surface that as `idle` so the UI
    // does not falsely alarm. Members are matched case-insensitively against the bare unit name.
    private static readonly HashSet<string> KnownOneshotWithoutTimer =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "logrotate",
            "man-db",
            "mlocate",
            "rkhunter",
            "unattended-upgrades"
        };

    private async Task<List<ServiceInfoDto>> CollectSystemdServicesAsync(CancellationToken ct)
    {
        var byName = new Dictionary<string, ServiceInfoDto>(StringComparer.OrdinalIgnoreCase);
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await CollectInstalledSystemdServicesAsync(byName, generated, ct).ConfigureAwait(false);
        await OverlaySystemdRuntimeStatusAsync(byName, ct).ConfigureAwait(false);
        await RemoveGeneratedUnitsWithoutProgramAsync(byName, generated, ct).ConfigureAwait(false);
        await ReclassifyIdleSystemdServicesAsync(byName, ct).ConfigureAwait(false);
        return byName.Values.ToList();
    }

    // Known generated SysV units require their service executable because package removal may
    // retain init scripts. Units with unknown executable mappings retain their normal detection.
    private static readonly Dictionary<string, string[]> GeneratedUnitPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nginx"] = ["/usr/sbin/nginx"],
        ["apache2"] = ["/usr/sbin/apache2"],
        ["postfix"] = ["/usr/sbin/postfix"],
        ["dovecot"] = ["/usr/sbin/dovecot"],
        ["portsentry"] = ["/usr/sbin/portsentry"],
        ["fail2ban"] = ["/usr/bin/fail2ban-server"],
        ["redis-server"] = ["/usr/bin/redis-server"],
        ["mysql"] = ["/usr/sbin/mysqld"],
        ["mariadb"] = ["/usr/sbin/mariadbd", "/usr/sbin/mysqld"],
        ["mongod"] = ["/usr/bin/mongod"],
    };

    private async Task RemoveGeneratedUnitsWithoutProgramAsync(
        IDictionary<string, ServiceInfoDto> byName, HashSet<string> generated, CancellationToken ct)
    {
        foreach (var name in generated)
        {
            if (!GeneratedUnitPrograms.TryGetValue(name, out var programs)) continue;
            if (await AnyProgramPresentAsync(name, programs, ct).ConfigureAwait(false)) continue;
            byName.Remove(name);
        }
    }

    private async Task<bool> AnyProgramPresentAsync(string name, string[] programs, CancellationToken ct)
    {
        foreach (var program in programs)
        {
            try
            {
                // argv-pure: `test -x <constant path>`, no shell.
                var res = await shellRunner.RunExecAsync("/usr/bin/test", ["-x", program], ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (res.ExitCode == 0) return true;
            }
            catch (Exception ex)
            {
                // The probe itself failed: nothing is known, so the unit is kept as reported.
                logger.LogDebug(ex, "Program probe failed for generated unit {Service}", name);
                return true;
            }
        }
        return false;
    }

    private async Task CollectInstalledSystemdServicesAsync(
        IDictionary<string, ServiceInfoDto> byName, HashSet<string> generated, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("systemctl list-unit-files --type=service --no-legend --no-pager --plain", ct).ConfigureAwait(false);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) continue;

                var name = parts[0].Replace(".service", string.Empty);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (parts.Length > 1 && parts[1].Equals("generated", StringComparison.OrdinalIgnoreCase))
                    generated.Add(name);

                byName[name] = new ServiceInfoDto
                {
                    Name = name,
                    Type = ServiceType.Systemd,
                    Status = parts.Length > 1 ? parts[1] : "installed",
                    IsRunning = false,
                    IsInstalled = true,
                    IsManageable = !SystemCriticalServices.Contains(name)
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to list systemd unit files");
        }
    }

    private async Task OverlaySystemdRuntimeStatusAsync(
        IDictionary<string, ServiceInfoDto> byName, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("systemctl list-units --type=service --all --no-pager --no-legend --plain", ct).ConfigureAwait(false);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Some systemd versions flag a failed or missing unit with a leading bullet column.
                if (parts.Length > 0 && parts[0] == "●") parts = parts[1..];
                if (parts.Length < 4) continue;

                var name = parts[0].Replace(".service", string.Empty);
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Columns are UNIT LOAD ACTIVE SUB; a not-found unit has no unit file and does not
                // prove that a service is installed.
                if (parts[1].Equals("not-found", StringComparison.OrdinalIgnoreCase)) continue;

                var status = parts[3];
                var running = status.Equals("running", StringComparison.OrdinalIgnoreCase);

                if (byName.TryGetValue(name, out var existing))
                {
                    byName[name] = existing with { Status = status, IsRunning = running };
                }
                else
                {
                    byName[name] = new ServiceInfoDto
                    {
                        Name = name,
                        Type = ServiceType.Systemd,
                        Status = status,
                        IsRunning = running,
                        IsInstalled = true,
                        IsManageable = !SystemCriticalServices.Contains(name)
                    };
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to collect running systemd services");
        }
    }

    private async Task ReclassifyIdleSystemdServicesAsync(
        IDictionary<string, ServiceInfoDto> byName, CancellationToken ct)
    {
        try
        {
            var servicesWithActiveTimer = await CollectActiveTimerServiceNamesAsync(ct).ConfigureAwait(false);
            foreach (var key in byName.Keys.ToList())
            {
                var svc = byName[key];
                if (svc.IsRunning) continue;
                if (!IsAtRestSub(svc.Status)) continue;

                if (servicesWithActiveTimer.Contains(svc.Name))
                {
                    byName[key] = svc with { Status = "scheduled" };
                }
                else if (KnownOneshotWithoutTimer.Contains(svc.Name))
                {
                    byName[key] = svc with { Status = "idle" };
                }
            }
        }
        catch (Exception ex)
        {
            // Reclassification is a UX-only improvement; a failure here means we keep the raw
            // SubState and the user still sees the service, just with the original `dead` label.
            logger.LogDebug(ex, "Failed to reclassify oneshot/timer-driven services");
        }
    }

    private static bool IsAtRestSub(string status) =>
        status.Equals("dead", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("inactive", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("exited", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the set of bare service names (no `.service` suffix) whose paired `.timer` is
    /// currently active - i.e. the unit is alive, just waiting for its next trigger.
    /// Empty set on any collection failure (degraded, not fatal).
    /// </summary>
    private async Task<HashSet<string>> CollectActiveTimerServiceNamesAsync(CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var output = await RunCommandAsync("systemctl list-units --type=timer --state=active --no-pager --no-legend --plain", ct).ConfigureAwait(false);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) continue;

                var unit = parts[0];
                if (!unit.EndsWith(".timer", StringComparison.OrdinalIgnoreCase)) continue;

                var bare = unit[..^".timer".Length];
                if (!string.IsNullOrWhiteSpace(bare))
                    result.Add(bare);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to list active systemd timers");
        }
        return result;
    }

    private async Task<List<ServiceInfoDto>> CollectDockerContainersAsync(CancellationToken ct)
    {
        var result = new List<ServiceInfoDto>();
        try
        {
            // audit E-1: argv-pure - `docker ps` invoked directly (no shell, no `2>/dev/null`).
            // Non-zero exit (e.g. docker absent / daemon down) yields empty stdout and is ignored.
            var res = await shellRunner.RunExecAsync(
                "docker", ["ps", "--format", "{{.Names}}\t{{.Status}}\t{{.State}}"], ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (res.ExitCode != 0) return result;

            foreach (var line in res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;

                result.Add(new ServiceInfoDto
                {
                    Name = parts[0],
                    Type = ServiceType.Docker,
                    Status = parts[1],
                    IsRunning = parts[2].Equals("running", StringComparison.OrdinalIgnoreCase),
                    IsManageable = true
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to collect Docker containers (Docker may not be installed)");
        }
        return result;
    }

    // Executes constant systemctl commands through IShellRunner without external interpolation.
    private async Task<string> RunCommandAsync(string command, CancellationToken ct)
    {
        var res = await shellRunner.RunExecAsync("/bin/bash", ["-c", command], ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return res.StdOut;
    }
}
