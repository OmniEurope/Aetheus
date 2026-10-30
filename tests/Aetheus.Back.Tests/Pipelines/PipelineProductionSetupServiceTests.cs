// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Exceptions;
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.PortRegistry;
using Aetheus.Shared.Components.VariableLibraries;
using Aetheus.Shared.Components.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>ADR-057: reading the project's Compose file, then creating what it asks for in the production library and
/// vault, the port registry and the configuration repository, on the model of a real project's file.</summary>
public sealed class PipelineProductionSetupServiceTests
{
    private const int ProjectId = 7;
    private const int LibraryId = 101;
    private const int VaultId = 201;
    internal const string ComposePath = "deploy/compose/remote.compose.yml";

    // The variables of a production Compose file with an API behind its front: a published port each, the database,
    // a signing key, a mail server and an administrator the operator names.
    private const string ReplaceCompose = """
        services:
          front:
            container_name: ${APPNAME:?}-${ENV:?}-front
            ports:
              - "127.0.0.1:${PORT_FRONT:?}:80"
            environment:
              - API_BASE_URL=${API_BASE_URL:?}
          back:
            ports:
              - "127.0.0.1:${PORT_BACK:?}:8080"
            environment:
              - Password=${DB_PASSWORD:?}
              - Jwt__SigningKey=${JWT_KEY:?}
              - Seed__OwnerEmail=${OWNER_EMAIL:?}
              - Mail__Relay=${MAIL_RELAY:?}
              - Smtp__Port=${SMTP_PORT:-587}
        """;

    // The production Compose file of W6 as the 2026-10-25 trial found it (recette R-96): it requires the application name, the
    // environment, the SDK, the database, the signing key, the administrator and the mail server, none of which the
    // wizard may ask the operator for, and it reads the published ports of a replace deployment.
    internal const string W6Compose = """
        services:
          front:
            container_name: ${APPNAME:?}-${ENV:?}-front
            build:
              args:
                DOTNET_SDK_VERSION: ${DOTNET_SDK_VERSION:?DOTNET_SDK_VERSION comes from global.json}
            ports:
              - "127.0.0.1:${PORT_FRONT:?}:80"
            environment:
              - API_BASE_URL=${API_BASE_URL:?}
          back:
            ports:
              - "127.0.0.1:${PORT_BACK:?}:8080"
            environment:
              - ConnectionStrings__DefaultConnection=Host=db;Database=${APPNAME};Username=${DB_USER:?};Password=${DB_PASSWORD:?DB_PASSWORD is required}
              - Jwt__SigningKey=${JWT_KEY:?JWT_KEY is required}
              - App__Version=${APP_VERSION:?}
              - App__FrontUrl=${FRONT_URL:?}
              - Seed__AdminEmail=${ADMIN_EMAIL:?ADMIN_EMAIL is required}
              - Seed__AdminPassword=${ADMIN_PASSWORD:?ADMIN_PASSWORD is required}
              - Smtp__Host=${SMTP_HOST:?SMTP_HOST is required}
              - Smtp__Port=${SMTP_PORT:-587}
              - Smtp__User=${SMTP_USER:-}
              - Smtp__Password=${SMTP_PASSWORD:-}
              - Smtp__From=${SMTP_FROM:?SMTP_FROM is required}
        """;

    private const string BlueGreenCompose = """
        services:
          front:
            ports:
              - "127.0.0.1:${PORT_FRONT_BLUE:?}:80"
              - "127.0.0.1:${PORT_FRONT_GREEN:?}:80"
          back:
            ports:
              - "127.0.0.1:${PORT_BACK_BLUE:?}:8080"
              - "127.0.0.1:${PORT_BACK_GREEN:?}:8080"
            environment:
              - Password=${DB_PASSWORD:?}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();
    private readonly IPortAllocationService _ports = Substitute.For<IPortAllocationService>();
    private readonly IProjectConfigRepositoryService _configuration = Substitute.For<IProjectConfigRepositoryService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IPipelineSetupServerRepository _setupServers = Substitute.For<IPipelineSetupServerRepository>();
    private readonly PipelineProductionSetupService _sut;

    private readonly List<CreateVariableEntryRequest> _libraryAdds = [];
    private readonly List<CreateVaultSecretRequest> _vaultAdds = [];
    private IReadOnlyDictionary<string, string> _configurationFiles = new Dictionary<string, string>();

    public PipelineProductionSetupServiceTests()
    {
        _sut = new PipelineProductionSetupService(
            _pipelines, _git, _libraries, _vaults, _ports, _configuration, _audit, _setupServers,
            NullLogger<PipelineProductionSetupService>.Instance);
        _pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Wordmorrow");
        _libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items = [new VariableLibraryDto { Id = LibraryId, Name = "wordmorrow.prod", ProjectId = ProjectId }]
            });
        _vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = VaultId, Name = "wordmorrow.prod", ProjectId = ProjectId }]
            });
        _libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>()).Returns([]);
        _vaults.ExportSecretKeysAsync(VaultId, Arg.Any<CancellationToken>()).Returns([]);
        _ports.GetTargetsAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(new PortAllocationTargetsDto
        {
            Servers = [new PortAllocationTargetDto { ServerId = 4, ServerName = "vps" }],
            ProjectId = ProjectId
        });
        _libraries.ImportEntriesAsync(LibraryId, Arg.Do<List<CreateVariableEntryRequest>>(_libraryAdds.AddRange), Arg.Any<CancellationToken>()).Returns(0);
        _vaults.ImportSecretsAsync(VaultId, Arg.Do<List<CreateVaultSecretRequest>>(_vaultAdds.AddRange), Arg.Any<CancellationToken>()).Returns(0);
        _configuration.EnsureAsync(ProjectId, Arg.Do<IReadOnlyDictionary<string, string>>(files => _configurationFiles = files), Arg.Any<CancellationToken>())
            .Returns(new ProjectConfigRepositoryDto { Id = 9, Name = "Wordmorrow configuration", Slug = "wordmorrow-config", Created = true });
        Repository(ReplaceCompose);
    }

    /// <summary>One source repository whose only Compose file is <see cref="ComposePath"/>.</summary>
    private void Repository(string compose, params string[] otherComposeFiles)
    {
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = 5, ProjectId = ProjectId, DefaultBranch = "main" }]);
        _git.GetTreeAsync(5, "main", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call => Listing((string?)call.ArgAt<string?>(2), otherComposeFiles));
        _git.GetBlobAsync(5, "main", ComposePath, Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = ComposePath, Content = compose });
    }

    private static List<GitLightTreeEntryDto> Listing(string? directory, string[] otherComposeFiles)
    {
        var files = new[] { ComposePath }.Concat(otherComposeFiles).ToList();
        var prefix = string.IsNullOrEmpty(directory) ? string.Empty : directory + "/";
        var children = files.Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..]).ToList();
        var entries = new List<GitLightTreeEntryDto>();
        foreach (var child in children)
        {
            var slash = child.IndexOf('/', StringComparison.Ordinal);
            var name = slash < 0 ? child : child[..slash];
            if (entries.Exists(entry => entry.Name == name)) continue;
            entries.Add(new GitLightTreeEntryDto
            {
                Name = name,
                Path = prefix + name,
                Type = slash < 0 ? GitTreeEntryType.Blob : GitTreeEntryType.Tree
            });
        }
        return entries;
    }

    private static PipelineProductionApplyRequest Request(
        string? server = "vps", Dictionary<string, string>? values = null, string? strategy = null) => new()
        {
            ProjectId = ProjectId,
            ComposeFile = ComposePath,
            Server = server ?? string.Empty,
            Values = values ?? [],
            Strategy = strategy
        };

    [Fact]
    public async Task TheAnalysis_ReadsTheComposeFileOfTheRepository_AndSaysWhereEachValueComesFrom()
    {
        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.True(analysis.Readable);
        Assert.Equal(ComposePath, analysis.ComposeFile);
        Assert.Equal("replace", analysis.Strategy);
        Assert.True(analysis.DeclaresApi);
        var kinds = analysis.Variables.ToDictionary(variable => variable.Name, variable => variable.Kind);
        Assert.Equal(ProductionVariableKind.Port, kinds["PORT_FRONT"]);
        Assert.Equal(ProductionVariableKind.GeneratedSecret, kinds["DB_PASSWORD"]);
        Assert.Equal(ProductionVariableKind.DefaultValue, kinds["APPNAME"]);
        Assert.Equal(ProductionVariableKind.NeedsValue, kinds["MAIL_RELAY"]);
        // What the deployment provides itself is never shown.
        Assert.DoesNotContain("API_BASE_URL", kinds.Keys);
        Assert.Equal(["vps"], analysis.Servers.Select(server => server.Name));
        Assert.True(analysis.Variables.Single(variable => variable.Name == "DB_PASSWORD").Secret);
        Assert.Null(analysis.Variables.Single(variable => variable.Name == "DB_PASSWORD").DefaultValue);
    }

    [Fact]
    public async Task WhenTheRepositoryHasSeveralComposeFiles_TheOneThatLooksLikeProductionIsProposed()
    {
        Repository(ReplaceCompose, "docker-compose.dev.yml", "compose.yaml");

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Equal(ComposePath, analysis.ComposeFile);
        Assert.Equal(3, analysis.ComposeFiles.Count);
    }

    [Fact]
    public async Task AProjectWithNoCommit_IsNotReadable_AndNothingIsClaimed()
    {
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = 5, ProjectId = ProjectId, IsEmpty = true }]);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.False(analysis.Readable);
        Assert.Empty(analysis.Variables);
        Assert.Null(analysis.ComposeFile);
    }

    [Fact]
    public async Task Applying_CreatesEveryVariable_WhereItBelongs_AndReservesThePortsOfTheServer()
    {
        var result = await _sut.ApplyAsync(Request(values: new() { ["MAIL_RELAY"] = "mail.example.test", ["OWNER_EMAIL"] = "owner@example.test", ["DOMAIN"] = "example.test" }), Ct);

        // Secrets the application owns are generated into the vault, never read back.
        Assert.Equivalent(new[] { "DB_PASSWORD", "JWT_KEY" }, _vaultAdds.Select(secret => secret.Key));
        Assert.All(_vaultAdds, secret => Assert.False(string.IsNullOrWhiteSpace(secret.Value)));
        // Conventions and the operator's values go into the library, ports through the registry.
        var library = _libraryAdds.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal("wordmorrow", library["APPNAME"]);
        // ENV is a name the agent refuses to hand a step: the entry is scoped, the sample maps it back (R-23).
        Assert.Equal("prod", library["DEPLOY_ENV"]);
        Assert.DoesNotContain("ENV", library.Keys);
        Assert.Equal("mail.example.test", library["MAIL_RELAY"]);
        Assert.Equal("owner@example.test", library["OWNER_EMAIL"]);
        Assert.Equal("example.test", library["DOMAIN"]);
        Assert.DoesNotContain("SMTP_PORT", library.Keys);
        await _ports.Received(1).AllocateIntoLibraryAsync(
            LibraryId, 4, Arg.Is<IReadOnlyList<string>>(keys => keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "PORT_BACK", "PORT_FRONT" })), Ct);
        Assert.Equal("vps", result.Server);
        Assert.Equal("replace", result.Strategy);
        Assert.True(result.DeclaresApi);
        Assert.False(result.NeedsValues);
        Assert.Equal(ProductionEntryOutcome.Allocated, result.Entries.Single(entry => entry.Name == "PORT_FRONT").Outcome);
        Assert.Equal(ProductionEntryOutcome.Generated, result.Entries.Single(entry => entry.Name == "DB_PASSWORD").Outcome);
    }

    [Fact]
    public async Task AValueTheOperatorDidNotGive_IsCreatedEmpty_AndReportedAsMissing()
    {
        var result = await _sut.ApplyAsync(Request(), Ct);

        Assert.Equal(string.Empty, _libraryAdds.Single(entry => entry.Key == "MAIL_RELAY").Value);
        Assert.Equal(ProductionEntryOutcome.NeedsValue, result.Entries.Single(entry => entry.Name == "MAIL_RELAY").Outcome);
        Assert.True(result.NeedsValues);
    }

    [Fact]
    public async Task WithNoServer_ThePortsAreCreatedEmpty_NotInventedAndNotHidden()
    {
        var result = await _sut.ApplyAsync(Request(server: null), Ct);

        await _ports.DidNotReceive().AllocateIntoLibraryAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        Assert.Equal(string.Empty, _libraryAdds.Single(entry => entry.Key == "PORT_FRONT").Value);
        Assert.Equal(ProductionEntryOutcome.PortUnavailable, result.Entries.Single(entry => entry.Name == "PORT_BACK").Outcome);
        Assert.True(result.NeedsValues);
    }

    [Fact]
    public async Task AFullPortRegistry_DoesNotAbortTheSetup()
    {
        _ports.AllocateIntoLibraryAsync(LibraryId, 4, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<List<VariableEntryDto>>>(_ => throw new ConflictException("no free port"));

        var result = await _sut.ApplyAsync(Request(), Ct);

        Assert.Equal(ProductionEntryOutcome.PortUnavailable, result.Entries.Single(entry => entry.Name == "PORT_FRONT").Outcome);
        Assert.Equal(string.Empty, _libraryAdds.Single(entry => entry.Key == "PORT_FRONT").Value);
        Assert.Contains(_vaultAdds, secret => secret.Key == "DB_PASSWORD");
    }

    [Fact]
    public async Task AnEntryTheProjectAlreadyHolds_IsKept_NeverOverwritten()
    {
        _libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(
        [
            new VariableEntryDto { Id = 1, Key = "APPNAME", Value = "chosen" },
            new VariableEntryDto { Id = 2, Key = "PORT_FRONT", Value = "10100" },
            new VariableEntryDto { Id = 3, Key = "PORT_BACK", Value = "10101" }
        ]);
        _vaults.ExportSecretKeysAsync(VaultId, Arg.Any<CancellationToken>()).Returns(["DB_PASSWORD"]);

        var result = await _sut.ApplyAsync(Request(), Ct);

        Assert.DoesNotContain(_libraryAdds, entry => entry.Key == "APPNAME");
        Assert.DoesNotContain(_vaultAdds, secret => secret.Key == "DB_PASSWORD");
        Assert.Equal(ProductionEntryOutcome.Kept, result.Entries.Single(entry => entry.Name == "APPNAME").Outcome);
        Assert.Equal(ProductionEntryOutcome.Kept, result.Entries.Single(entry => entry.Name == "PORT_FRONT").Outcome);
        await _ports.DidNotReceive().AllocateIntoLibraryAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _libraries.DidNotReceive().UpdateEntryAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<UpdateVariableEntryRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Recette R-96 line 5 (R-85): a first run without a server left PORT_FRONT and PORT_BACK empty in the library. The port
    /// allocation now fills an empty key, so the setup run again with a server allocates them instead of reporting them missing
    /// for the library page to fill.</summary>
    [Fact]
    public async Task APortEntryTheLibraryHoldsEmpty_IsAllocatedAtTheNextRun_NotLeftForTheLibraryPage()
    {
        _libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(
        [
            new VariableEntryDto { Id = 2, Key = "PORT_FRONT", Value = string.Empty },
            new VariableEntryDto { Id = 3, Key = "PORT_BACK", Value = string.Empty }
        ]);

        var result = await _sut.ApplyAsync(Request(), Ct);

        await _ports.Received(1).AllocateIntoLibraryAsync(
            LibraryId, 4, Arg.Is<IReadOnlyList<string>>(keys => keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "PORT_BACK", "PORT_FRONT" })), Ct);
        Assert.DoesNotContain(_libraryAdds, entry => entry.Key is "PORT_FRONT" or "PORT_BACK");
        Assert.Equal(ProductionEntryOutcome.Allocated, result.Entries.Single(entry => entry.Name == "PORT_FRONT").Outcome);
        Assert.Equal(ProductionEntryOutcome.Allocated, result.Entries.Single(entry => entry.Name == "PORT_BACK").Outcome);
        Assert.DoesNotContain(result.Entries, entry => entry.Outcome == ProductionEntryOutcome.PortUnavailable);
    }

    /// <summary>Without a server the empty entries stay as they are (reported, not duplicated).</summary>
    [Fact]
    public async Task APortEntryTheLibraryHoldsEmpty_WithNoServer_IsReportedAndNeverCreatedTwice()
    {
        _libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(
            [new VariableEntryDto { Id = 2, Key = "PORT_FRONT", Value = string.Empty }]);

        var result = await _sut.ApplyAsync(Request(server: null), Ct);

        Assert.DoesNotContain(_libraryAdds, entry => entry.Key == "PORT_FRONT");
        Assert.Equal(string.Empty, _libraryAdds.Single(entry => entry.Key == "PORT_BACK").Value);
        Assert.Equal(ProductionEntryOutcome.PortUnavailable, result.Entries.Single(entry => entry.Name == "PORT_FRONT").Outcome);
        Assert.True(result.NeedsValues);
    }

    [Fact]
    public async Task AnEmptyEntry_ReceivesTheValueTheOperatorGivesNow()
    {
        _libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>())
            .Returns([new VariableEntryDto { Id = 6, Key = "MAIL_RELAY", Value = string.Empty }]);

        await _sut.ApplyAsync(Request(values: new() { ["MAIL_RELAY"] = "mail.example.test" }), Ct);

        await _libraries.Received(1).UpdateEntryAsync(
            LibraryId, 6, Arg.Is<UpdateVariableEntryRequest>(update => update.Key == "MAIL_RELAY" && update.Value == "mail.example.test"), Ct);
        Assert.DoesNotContain(_libraryAdds, entry => entry.Key == "MAIL_RELAY");
    }

    [Fact]
    public async Task TheConfigurationRepository_ReceivesTheEnvSampleWithNoValue_AndTheVirtualHostsOfTheFrontAndTheApi()
    {
        var result = await _sut.ApplyAsync(Request(), Ct);

        Assert.Equal("wordmorrow-config", result.ConfigRepository.Slug);
        Assert.Equal(
            [
                ".pipeline/configs/apache/api-http.conf", ".pipeline/configs/apache/api-https.conf",
                ".pipeline/configs/apache/app-http.conf", ".pipeline/configs/apache/app-https.conf",
                ".pipeline/configs/env/production.env"
            ],
            _configurationFiles.Keys.Order(StringComparer.Ordinal));
        var sample = _configurationFiles[".pipeline/configs/env/production.env"];
        Assert.Contains("DB_PASSWORD=#{DB_PASSWORD}#", sample, StringComparison.Ordinal);
        Assert.Contains("MAIL_RELAY=#{MAIL_RELAY}#", sample, StringComparison.Ordinal);
        Assert.Contains("ENV=#{DEPLOY_ENV}#", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("#{ENV}#", sample, StringComparison.Ordinal);
        // No value, no secret: only names; the ports and the defaults the file names are not in it.
        Assert.DoesNotContain("PORT_FRONT", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("SMTP_PORT", sample, StringComparison.Ordinal);
        // Replacing the running services: the virtual hosts proxy to the one published port.
        Assert.Contains("#{PORT_FRONT}#", _configurationFiles[".pipeline/configs/apache/app-https.conf"], StringComparison.Ordinal);
        Assert.Contains("#{PORT_BACK}#", _configurationFiles[".pipeline/configs/apache/api-https.conf"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlueGreenComposeFile_GetsItsUpstreamEntries_AndVirtualHostsThatFollowTheLiveColour()
    {
        Repository(BlueGreenCompose);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);
        var result = await _sut.ApplyAsync(Request(), Ct);

        Assert.Equal("bluegreen", analysis.Strategy);
        Assert.Equal("bluegreen", result.Strategy);
        var library = _libraryAdds.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal("WORDMORROW", library["UPSTREAM_DEFINE"]);
        Assert.EndsWith("/000-wordmorrow-upstream.conf", library["UPSTREAM_CONF"], StringComparison.Ordinal);
        Assert.Contains("/sites-available/", library["UPSTREAM_CONF"], StringComparison.Ordinal);
        Assert.Contains("/sites-enabled/", library["UPSTREAM_LINK"], StringComparison.Ordinal);
        Assert.Contains("APACHE_RELOAD_HELPER", library.Keys);
        await _ports.Received(1).AllocateIntoLibraryAsync(
            LibraryId, 4,
            Arg.Is<IReadOnlyList<string>>(keys => keys.Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "PORT_BACK_BLUE", "PORT_BACK_GREEN", "PORT_FRONT_BLUE", "PORT_FRONT_GREEN" })),
            Ct);
        Assert.Contains("${#{UPSTREAM_DEFINE}#_FRONT_PORT}", _configurationFiles[".pipeline/configs/apache/app-https.conf"], StringComparison.Ordinal);
    }

    /// <summary>Recette R-89: the strategy is the user's choice, not only a deduction from the names the Compose file reads. W6's file
    /// reads PORT_FRONT and PORT_BACK, yet the user wants a blue-green deployment: the setup then reserves the four colour ports, keeps
    /// the Apache upstream entries and writes the virtual hosts that follow the live colour.</summary>
    [Fact]
    public async Task AReplaceComposeFile_ChosenAsBlueGreen_GetsTheColourPortsTheUpstreamAndTheBlueGreenVirtualHosts()
    {
        var result = await _sut.ApplyAsync(Request(strategy: "bluegreen"), Ct);

        Assert.Equal("bluegreen", result.Strategy);
        await _ports.Received(1).AllocateIntoLibraryAsync(
            LibraryId, 4,
            Arg.Is<IReadOnlyList<string>>(keys => keys.Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "PORT_BACK_BLUE", "PORT_BACK_GREEN", "PORT_FRONT_BLUE", "PORT_FRONT_GREEN" })),
            Ct);
        var library = _libraryAdds.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal("WORDMORROW", library["UPSTREAM_DEFINE"]);
        Assert.Contains("${#{UPSTREAM_DEFINE}#_FRONT_PORT}", _configurationFiles[".pipeline/configs/apache/app-https.conf"], StringComparison.Ordinal);
        Assert.DoesNotContain(result.Entries, entry => entry.Name is "PORT_FRONT" or "PORT_BACK");
    }

    [Fact]
    public async Task ABlueGreenComposeFile_ChosenAsReplace_GetsTheTwoPortsAndNoUpstream()
    {
        Repository(BlueGreenCompose);

        var result = await _sut.ApplyAsync(Request(strategy: "replace"), Ct);

        Assert.Equal("replace", result.Strategy);
        await _ports.Received(1).AllocateIntoLibraryAsync(
            LibraryId, 4,
            Arg.Is<IReadOnlyList<string>>(keys => keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "PORT_BACK", "PORT_FRONT" })),
            Ct);
        Assert.DoesNotContain(_libraryAdds, entry => entry.Key == "UPSTREAM_DEFINE");
        Assert.Contains("#{PORT_FRONT}#", _configurationFiles[".pipeline/configs/apache/app-https.conf"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStrategyThatIsNeitherBlueGreenNorReplace_IsRefused_BeforeAnythingIsWritten()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyAsync(Request(strategy: "canary"), Ct));

        await _configuration.DidNotReceive().EnsureAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
        await _libraries.DidNotReceive().ImportEntriesAsync(
            Arg.Any<int>(), Arg.Any<List<CreateVariableEntryRequest>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AComposeFileThatIsNotInTheRepository_IsRefused()
    {
        var request = Request() with { ComposeFile = "elsewhere/compose.yaml" };

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyAsync(request, Ct));
        await _configuration.DidNotReceive().EnsureAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("../outside/compose.yaml")]
    [InlineData("/etc/compose.yaml")]
    [InlineData("deploy\\compose.yaml")]
    public async Task APathOutsideTheRepository_IsRefusedBeforeAnythingIsRead(string path)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyAsync(Request() with { ComposeFile = path }, Ct));
        await _git.DidNotReceive().GetBlobAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AServerThatIsNotTheProjects_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyAsync(Request(server: "elsewhere"), Ct));
        await _vaults.DidNotReceive().ImportSecretsAsync(Arg.Any<int>(), Arg.Any<List<CreateVaultSecretRequest>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutTheProductionLibrary_NothingIsCreated_AndTheRefusalSaysWhy()
    {
        _libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>());

        var failure = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyAsync(Request(), Ct));

        Assert.Contains("wordmorrow.prod", failure.Message, StringComparison.Ordinal);
        await _configuration.DidNotReceive().EnsureAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The project's production environment, holding the given servers.</summary>
    private void ProductionEnvironment(params (int Id, string Name)[] servers) =>
        _pipelines.FindEnvironmentByNameAsync("prod", ProjectId, Arg.Any<CancellationToken>()).Returns(new Aetheus.Back.Data.Entities.Environment
        {
            Name = "prod",
            ProjectId = ProjectId,
            Servers = [.. servers.Select(server => new Aetheus.Back.Data.Entities.EnvironmentServer
            {
                ServerId = server.Id,
                Server = new Aetheus.Back.Data.Entities.Server { Id = server.Id, Name = server.Name }
            })]
        });

    private void ProjectServers(params (int Id, string Name)[] servers) =>
        _ports.GetTargetsAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(new PortAllocationTargetsDto
        {
            Servers = [.. servers.Select(server => new PortAllocationTargetDto { ServerId = server.Id, ServerName = server.Name })],
            ProjectId = ProjectId
        });

    // Recette R-21: the server proposed by default is the production environment's.
    [Fact]
    public async Task TheDefaultServer_IsTheServerOfTheProductionEnvironment_AndItIsAmongTheChoices()
    {
        ProjectServers((4, "vps"), (5, "backup"));
        ProductionEnvironment((6, "prod-host"));

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Equal("prod-host", analysis.DefaultServer);
        Assert.Equal(["backup", "prod-host", "vps"], analysis.Servers.Select(server => server.Name));
    }

    [Fact]
    public async Task WithNoProductionEnvironment_TheOnlyServerOfTheProject_IsTheDefault()
    {
        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Equal("vps", analysis.DefaultServer);
    }

    [Fact]
    public async Task WithSeveralServersAndNoProductionEnvironment_NoDefaultIsGuessed()
    {
        ProjectServers((4, "vps"), (5, "backup"));

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Null(analysis.DefaultServer);
    }

    [Fact]
    public async Task AnEnvironmentOfAnotherProject_IsNeverTakenForTheProductionOne()
    {
        ProjectServers((4, "vps"), (5, "backup"));
        _pipelines.FindEnvironmentByNameAsync("prod", ProjectId, Arg.Any<CancellationToken>()).Returns(new Aetheus.Back.Data.Entities.Environment
        {
            Name = "prod",
            ProjectId = ProjectId + 1,
            Servers = [new Aetheus.Back.Data.Entities.EnvironmentServer { ServerId = 9, Server = new Aetheus.Back.Data.Entities.Server { Id = 9, Name = "foreign" } }]
        });

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Null(analysis.DefaultServer);
        Assert.DoesNotContain(analysis.Servers, server => server.Name == "foreign");
    }

    // Recette R-5: the deployment template builds its hosts from DOMAIN, so the setup asks for it like any value
    // only the operator knows, and an empty entry still counts as missing.
    [Fact]
    public async Task TheDomainTheDeploymentReads_IsAskedFor_AndAnEmptyEntryIsReportedMissing()
    {
        _libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>())
            .Returns([new VariableEntryDto { Id = 3, Key = "DOMAIN", Value = string.Empty }]);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        var domain = analysis.Variables.Single(variable => variable.Name == "DOMAIN");
        Assert.Equal(ProductionVariableKind.NeedsValue, domain.Kind);
        Assert.False(domain.Exists);
    }

    /// <summary>Recette R-96, one case per line: a project with nothing prepared by hand, whose Compose file is W6's, comes out
    /// of the wizard with what the trial did by hand, and is asked for its domain only. The numbers are the lines of R-96.</summary>
    [Fact]
    public async Task ABlankProjectWithTheW6ComposeFile_IsAskedForItsDomainOnly_AndGetsWhatTheTrialDidByHand()
    {
        Repository(W6Compose);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);
        var result = await _sut.ApplyAsync(Request(values: new() { ["DOMAIN"] = "w6.test" }), Ct);

        // R-90, R-91, R-84: the operator is asked for the domain and nothing else; no host is asked, none is stored.
        Assert.Equal(["DOMAIN"], analysis.Variables.Where(variable => variable.Kind == ProductionVariableKind.NeedsValue).Select(variable => variable.Name));
        Assert.False(result.NeedsValues);
        var library = _libraryAdds.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.DoesNotContain("APP_HOST", library.Keys);
        Assert.DoesNotContain("API_HOST", library.Keys);
        // (5) APPNAME, DEPLOY_ENV (ENV under its scoped name), DB_USER by convention, the domain as given; (8) nothing for CERTBOT_EMAIL.
        Assert.Equal("wordmorrow", library["APPNAME"]);
        Assert.Equal("prod", library["DEPLOY_ENV"]);
        Assert.Equal("wordmorrow", library["DB_USER"]);
        Assert.Equal("w6.test", library["DOMAIN"]);
        // Provided by the deployment, never asked: the SDK, the URLs, the release.
        Assert.All(new[] { "DOTNET_SDK_VERSION", "FRONT_URL", "API_BASE_URL", "APP_VERSION" },
            name => Assert.DoesNotContain(name, library.Keys));
        // (5) PORT_FRONT and PORT_BACK are reserved in the registry of the server, not typed.
        await _ports.Received(1).AllocateIntoLibraryAsync(
            LibraryId, 4, Arg.Is<IReadOnlyList<string>>(keys => keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "PORT_BACK", "PORT_FRONT" })), Ct);
        // (4) the administrator and the mail server may stay empty: not asked, no entry that would block the launch, no password invented.
        Assert.Equivalent(new[] { "DB_PASSWORD", "JWT_KEY" }, _vaultAdds.Select(secret => secret.Key));
        Assert.All(new[] { "ADMIN_EMAIL", "ADMIN_PASSWORD", "SMTP_HOST", "SMTP_FROM", "SMTP_USER", "SMTP_PASSWORD", "CERTBOT_EMAIL" },
            name => Assert.DoesNotContain(name, library.Keys));
        // (3) the Compose file and the API the pipeline will carry.
        Assert.Equal(ComposePath, result.ComposeFile);
        Assert.True(result.DeclaresApi);
        // (1) the four generic virtual hosts, (2) the environment sample without the optional names.
        Assert.Equal(
            [
                ".pipeline/configs/apache/api-http.conf", ".pipeline/configs/apache/api-https.conf",
                ".pipeline/configs/apache/app-http.conf", ".pipeline/configs/apache/app-https.conf",
                ".pipeline/configs/env/production.env"
            ],
            _configurationFiles.Keys.Order(StringComparer.Ordinal));
        var sample = _configurationFiles[".pipeline/configs/env/production.env"]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#'));
        Assert.Equal(
            ["APPNAME=#{APPNAME}#", "DB_PASSWORD=#{DB_PASSWORD}#", "DB_USER=#{DB_USER}#", "ENV=#{DEPLOY_ENV}#", "JWT_KEY=#{JWT_KEY}#"],
            sample);
    }

    [Fact]
    public async Task TheDomainGivenByTheOperator_IsWrittenToTheProductionLibrary_ButNotToTheEnvSample()
    {
        await _sut.ApplyAsync(Request(values: new() { ["DOMAIN"] = "wordmorrow.test" }), Ct);

        Assert.Contains(_libraryAdds, entry => entry.Key == "DOMAIN" && entry.Value == "wordmorrow.test");
        var sample = _configurationFiles.Single(file => file.Key.EndsWith(".env", StringComparison.Ordinal)).Value;
        Assert.DoesNotContain("DOMAIN", sample, StringComparison.Ordinal);
    }

    /// <summary>A new project: no port target and no production environment server yet.</summary>
    private void NoProjectServer() =>
        _ports.GetTargetsAsync(LibraryId, Arg.Any<CancellationToken>())
            .Returns(new PortAllocationTargetsDto { Servers = [], ProjectId = ProjectId });

    /// <summary>Recette R-77: a project with no server is offered the servers the user administers, and only those.</summary>
    [Fact]
    public async Task AProjectWithNoServer_IsOfferedTheServersTheUserAdministers_AsCandidates()
    {
        NoProjectServer();
        IReadOnlyCollection<int> administered = [4, 9];
        _setupServers.ListServersAsync(administered, Arg.Any<CancellationToken>()).Returns([(4, "vpssim"), (9, "other")]);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, new ProductionServerScope(administered), Ct);

        Assert.Equal(["vpssim", "other"], analysis.Servers.Select(server => server.Name));
        Assert.All(analysis.Servers, server => Assert.True(server.Candidate));
        // With two candidates none is guessed.
        Assert.Null(analysis.DefaultServer);
    }

    [Fact]
    public async Task TheOnlyServerTheUserAdministers_IsPreselected()
    {
        NoProjectServer();
        _setupServers.ListServersAsync(Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>()).Returns([(4, "vpssim")]);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, new ProductionServerScope(null), Ct);

        Assert.Equal("vpssim", analysis.DefaultServer);
    }

    /// <summary>The overload without a scope offers no candidate: nothing the caller was not checked for is listed.</summary>
    [Fact]
    public async Task WithoutAScope_NoCandidateIsOffered()
    {
        NoProjectServer();

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Empty(analysis.Servers);
        await _setupServers.DidNotReceiveWithAnyArgs().ListServersAsync(default, Ct);
    }

    [Fact]
    public async Task ApplyingWithACandidate_ReservesItsPorts_ThenAttachesItToTheProject()
    {
        NoProjectServer();
        _setupServers.ListServersAsync(Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>()).Returns([(4, "vpssim")]);
        _setupServers.AttachAgentServerAsync(ProjectId, 4, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.ApplyAsync(Request(server: "vpssim"), new ProductionServerScope([4]), Ct);

        Assert.Equal("vpssim", result.Server);
        Received.InOrder(() =>
        {
            _ports.AllocateIntoLibraryAsync(LibraryId, 4, Arg.Any<IReadOnlyList<string>>(), Ct);
            _configuration.EnsureAsync(ProjectId, Arg.Any<IReadOnlyDictionary<string, string>>(), Ct);
            _setupServers.AttachAgentServerAsync(ProjectId, 4, Ct);
        });
        await _audit.Received(1).LogAsync("ProductionServerAttached", "Project", ProjectId, "vpssim", Ct);
    }

    /// <summary>A setup that fails while writing the variables and secrets leaves the candidate unattached: the
    /// project never gains a server without its configuration.</summary>
    [Fact]
    public async Task ApplyingWithACandidate_WhenTheWritesFail_AttachesNothing()
    {
        NoProjectServer();
        _setupServers.ListServersAsync(Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>()).Returns([(4, "vpssim")]);
        _setupServers.AttachAgentServerAsync(ProjectId, 4, Arg.Any<CancellationToken>()).Returns(true);
        _vaults.ImportSecretsAsync(VaultId, Arg.Any<List<CreateVaultSecretRequest>>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("vault write failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ApplyAsync(Request(server: "vpssim"), new ProductionServerScope([4]), Ct));

        await _setupServers.DidNotReceiveWithAnyArgs().AttachAgentServerAsync(default, default, Ct);
        await _audit.DidNotReceive().LogAsync("ProductionServerAttached", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyingWithACandidate_WhenTheConfigurationRepositoryFails_AttachesNothing()
    {
        NoProjectServer();
        _setupServers.ListServersAsync(Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>()).Returns([(4, "vpssim")]);
        _setupServers.AttachAgentServerAsync(ProjectId, 4, Arg.Any<CancellationToken>()).Returns(true);
        _configuration.EnsureAsync(ProjectId, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns<ProjectConfigRepositoryDto>(_ => throw new InvalidOperationException("repository creation failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ApplyAsync(Request(server: "vpssim"), new ProductionServerScope([4]), Ct));

        await _setupServers.DidNotReceiveWithAnyArgs().AttachAgentServerAsync(default, default, Ct);
        await _audit.DidNotReceive().LogAsync("ProductionServerAttached", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyingWithAServerOutsideTheScope_IsRefused_AndNothingIsAttached()
    {
        NoProjectServer();
        _setupServers.ListServersAsync(Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>()).Returns([(4, "vpssim")]);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyAsync(Request(server: "other"), new ProductionServerScope([4]), Ct));

        await _setupServers.DidNotReceiveWithAnyArgs().AttachAgentServerAsync(default, default, Ct);
    }

    [Fact]
    public async Task ApplyingWithAServerOfTheProject_AttachesNothing()
    {
        await _sut.ApplyAsync(Request(), new ProductionServerScope(null), Ct);

        await _setupServers.DidNotReceiveWithAnyArgs().ListServersAsync(default, Ct);
        await _setupServers.DidNotReceiveWithAnyArgs().AttachAgentServerAsync(default, default, Ct);
    }
}
