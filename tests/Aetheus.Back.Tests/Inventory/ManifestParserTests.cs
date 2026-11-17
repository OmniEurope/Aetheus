// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Inventory;

namespace Aetheus.Back.Tests.Inventory;

/// <summary>Each parser against the shape its own tool writes.</summary>
public sealed class ManifestParserTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Key(InventoryEntry entry) => $"{entry.Name}@{entry.Version}";

    [Fact]
    public void PackagesLock_ListsDirectAndTransitive_ButNotProjectReferences()
    {
        var entries = DotnetManifestParser.ParseLock(Bytes("""
            {
              "version": 2,
              "dependencies": {
                "net10.0": {
                  "Serilog": { "type": "Direct", "requested": "[4.0.0, )", "resolved": "4.0.0", "contentHash": "x" },
                  "Microsoft.Extensions.Primitives": { "type": "Transitive", "resolved": "10.0.1", "contentHash": "y" },
                  "Polly": { "type": "CentralTransitive", "requested": "[8.0.0, )", "resolved": "8.0.0", "contentHash": "z" },
                  "aetheus.shared": { "type": "Project", "dependencies": { "Serilog": "[4.0.0, )" } }
                }
              }
            }
            """));

        Assert.Equal(["Serilog@4.0.0", "Microsoft.Extensions.Primitives@10.0.1", "Polly@8.0.0"], entries.Select(Key));
        Assert.Equal([true, false, false], entries.Select(entry => entry.IsDirect));
    }

    [Fact]
    public void ProjectFile_TakesItsVersionFromCentralPackages_AndCountsWhatItCannotResolve()
    {
        var central = DotnetManifestParser.ParseCentralVersions("""
            <Project><ItemGroup><PackageVersion Include="Serilog" Version="4.0.0" /></ItemGroup></Project>
            """);

        var (entries, unresolved) = DotnetManifestParser.ParseProject("""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <PackageReference Include="serilog" />
              <PackageReference Include="Polly" Version="8.1.0" />
              <PackageReference Include="Floating" Version="1.*" />
              <PackageReference Include="ByProperty" Version="$(Lib)" />
              <PackageReference Include="Unknown" />
            </ItemGroup></Project>
            """, central);

        Assert.Equal(["serilog@4.0.0", "Polly@8.1.0"], entries.Select(Key));
        Assert.All(entries, entry => Assert.True(entry.IsDirect));
        Assert.Equal(3, unresolved);
    }

    [Fact]
    public void ProjectFile_RefusesADocumentTypeDefinition() =>
        Assert.ThrowsAny<System.Xml.XmlException>(() => DotnetManifestParser.ParseProject(
            "<!DOCTYPE p [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><Project>&x;</Project>", new Dictionary<string, string>()));

    [Fact]
    public void GlobalJson_NamesTheRuntime_AndToleratesComments()
    {
        var entry = DotnetManifestParser.ParseGlobalJson(Bytes("{ // pinned\n \"sdk\": { \"version\": \"10.0.100\", \"rollForward\": \"latestFeature\" }, }"));

        Assert.Equal(new InventoryEntry("runtime", "dotnet", "10.0.100", true, ProjectInventoryItemKind.Runtime), entry);
        Assert.Null(DotnetManifestParser.ParseGlobalJson(Bytes("{}")));
    }

    [Fact]
    public void NpmLock_Version3_NamesNestedPackages_AndSkipsTheRootAndTheWorkspaces()
    {
        var entries = NodeManifestParser.ParseNpmLock(Bytes("""
            {
              "lockfileVersion": 3,
              "packages": {
                "": { "name": "app", "version": "1.0.0", "dependencies": { "a": "^1.0.0" } },
                "packages/web": { "name": "web", "version": "2.0.0" },
                "node_modules/a": { "version": "1.2.3" },
                "node_modules/@scope/b": { "version": "2.0.0", "dev": true },
                "node_modules/a/node_modules/c": { "version": "3.0.0" },
                "node_modules/alias": { "name": "real", "version": "4.0.0" },
                "node_modules/web": { "resolved": "packages/web", "link": true }
              }
            }
            """));

        Assert.Equal(["a@1.2.3", "@scope/b@2.0.0", "c@3.0.0", "real@4.0.0"], entries.Select(Key));
    }

    [Fact]
    public void NpmLock_Version1_WalksNestedDependencies()
    {
        var entries = NodeManifestParser.ParseNpmLock(Bytes("""
            { "lockfileVersion": 1, "dependencies": { "a": { "version": "1.0.0", "dependencies": { "b": { "version": "2.0.0" } } } } }
            """));

        Assert.Equal(["a@1.0.0", "b@2.0.0"], entries.Select(Key));
    }

    [Fact]
    public void PnpmLock_ReadsEveryKeyFormat_OnlyInThePackagesSection()
    {
        var entries = NodeManifestParser.ParsePnpmLock("""
            lockfileVersion: '9.0'

            importers:

              .:
                dependencies:
                  ignored:
                    specifier: ^1.0.0
                    version: 1.0.0

            packages:

              '@babel/core@7.24.0':
                resolution: {integrity: sha512-x}
                engines: {node: '>=6.9.0'}

              lodash@4.17.21:
                resolution: {integrity: sha512-y}

              /old-format/1.2.3:
                resolution: {integrity: sha512-z}

              /six-format@2.0.0(peer@1.0.0):
                resolution: {integrity: sha512-w}

              github.com/acme/tool/abcdef:
                resolution: {tarball: https://codeload.github.com/acme/tool/tar.gz/abcdef}

            snapshots:

              lodash@4.17.21: {}
            """);

        Assert.Equal(["@babel/core@7.24.0", "lodash@4.17.21", "old-format@1.2.3", "six-format@2.0.0"], entries.Select(Key));
    }

    [Fact]
    public void YarnLock_ReadsBothGenerations_AndSkipsTheWorkspaces()
    {
        var classic = NodeManifestParser.ParseYarnLock("""
            # yarn lockfile v1

            "@babel/code-frame@^7.0.0", "@babel/code-frame@^7.12.13":
              version "7.24.2"
              resolved "https://registry.yarnpkg.com/x"
              dependencies:
                version-like "^1.0.0"

            lodash@^4.17.21:
              version "4.17.21"
            """);
        var berry = NodeManifestParser.ParseYarnLock("""
            __metadata:
              version: 8

            "app@workspace:.":
              version: 0.0.0-use.local
              linkType: soft

            "lodash@npm:^4.17.21":
              version: 4.17.21
              resolution: "lodash@npm:4.17.21"
            """);

        Assert.Equal(["@babel/code-frame@7.24.2", "lodash@4.17.21"], classic.Select(Key));
        Assert.Equal(["lodash@4.17.21"], berry.Select(Key));
    }

    [Fact]
    public void PackageJson_GivesTheDirectNames_AndTheRuntime()
    {
        var (direct, runtime) = NodeManifestParser.ParsePackageJson(Bytes("""
            { "dependencies": { "a": "1" }, "devDependencies": { "b": "2" }, "engines": { "node": ">=20" } }
            """));

        Assert.Equal(["a", "b"], direct.Order());
        Assert.Equal(">=20", runtime!.Version);
        Assert.Equal("20.11.0", NodeManifestParser.ParseNvmrc("v20.11.0\n")!.Version);
    }

    [Fact]
    public void ComposerLock_ListsRuntimeAndDevelopmentPackages()
    {
        var entries = PhpManifestParser.ParseLock(Bytes("""
            { "packages": [ { "name": "symfony/console", "version": "v7.1.0" } ], "packages-dev": [ { "name": "phpunit/phpunit", "version": "11.0.0" } ] }
            """));

        Assert.Equal(["symfony/console@v7.1.0", "phpunit/phpunit@11.0.0"], entries.Select(Key));
    }

    [Fact]
    public void ComposerJson_KeepsThePhpConstraintAsRuntime_AndOnlyExactPinsAsPackages()
    {
        var (entries, direct, unpinned) = PhpManifestParser.ParseProject(Bytes("""
            { "require": { "php": "^8.2", "ext-json": "*", "laravel/framework": "^11.0", "acme/pinned": "1.2.3" } }
            """));

        Assert.Equal(["php@^8.2", "acme/pinned@1.2.3"], entries.Select(Key));
        Assert.Equal(ProjectInventoryItemKind.Runtime, entries[0].Kind);
        Assert.Equal(["acme/pinned", "laravel/framework"], direct.Order());
        Assert.Equal(1, unpinned);
    }

    [Fact]
    public void Requirements_KeepPins_CountRanges_AndIgnoreOptions()
    {
        var (entries, unpinned) = TextManifestParser.ParseRequirements("""
            # production
            -r base.txt
            --hash=sha256:abc
            Django==5.0.1  # framework
            requests[socks]==2.31.0 ; python_version > "3.8"
            flask>=3.0
            numpy
            """);

        Assert.Equal(["Django@5.0.1", "requests@2.31.0"], entries.Select(Key));
        Assert.Equal(2, unpinned);
    }

    [Fact]
    public void TomlLocks_ReadPackageTablesOnly()
    {
        var entries = TextManifestParser.ParseTomlLock("""
            version = 3

            [[package]]
            name = "serde"
            version = "1.0.197"
            source = "registry"
            dependencies = [
             "serde_derive",
            ]

            [[package]]
            name = "serde_derive"
            version = "1.0.197"

            [metadata]
            name = "not-a-package"
            version = "9.9.9"
            """, InventoryEcosystems.Crates);

        Assert.Equal(["serde@1.0.197", "serde_derive@1.0.197"], entries.Select(Key));
        Assert.All(entries, entry => Assert.Equal("crates.io", entry.Ecosystem));
    }

    [Fact]
    public void Pom_ResolvesItsOwnProperties_AndCountsAnInheritedVersion()
    {
        var (entries, unresolved) = TextManifestParser.ParsePom("""
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <properties><spring.version>6.1.4</spring.version></properties>
              <dependencyManagement><dependencies><dependency><groupId>x</groupId><artifactId>managed</artifactId><version>1</version></dependency></dependencies></dependencyManagement>
              <dependencies>
                <dependency><groupId>org.springframework</groupId><artifactId>spring-core</artifactId><version>${spring.version}</version></dependency>
                <dependency><groupId>junit</groupId><artifactId>junit</artifactId><version>4.13.2</version></dependency>
                <dependency><groupId>org.inherited</groupId><artifactId>lib</artifactId></dependency>
                <dependency><groupId>org.parent</groupId><artifactId>lib</artifactId><version>${from.parent}</version></dependency>
              </dependencies>
            </project>
            """);

        Assert.Equal(["org.springframework:spring-core@6.1.4", "junit:junit@4.13.2"], entries.Select(Key));
        Assert.Equal(2, unresolved);
    }

    [Fact]
    public void GoMod_SplitsDirectFromIndirect_AndKnowsWhenItIsComplete()
    {
        var (entries, complete) = TextManifestParser.ParseGoMod("""
            module example.com/app

            go 1.22.1

            require github.com/single/line v1.0.0

            require (
                github.com/gin-gonic/gin v1.9.1
                golang.org/x/net v0.20.0 // indirect
            )

            replace example.com/old => example.com/new v1.0.0
            """);
        var (_, old) = TextManifestParser.ParseGoMod("go 1.16\n");

        Assert.True(complete);
        Assert.False(old);
        Assert.Equal(["go@1.22.1", "github.com/single/line@v1.0.0", "github.com/gin-gonic/gin@v1.9.1", "golang.org/x/net@v0.20.0"], entries.Select(Key));
        Assert.True(entries[2].IsDirect);
        Assert.False(entries[3].IsDirect);
    }

    [Fact]
    public void Dockerfile_ResolvesArguments_SkipsStages_AndCountsTheUnknown()
    {
        var (entries, unresolved) = DockerfileParser.Parse("""
            ARG DOTNET_VERSION=10.0.1
            ARG NO_DEFAULT
            FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
            FROM --platform=linux/amd64 node:22-alpine AS web
            FROM build AS publish
            FROM ${NO_DEFAULT}
            FROM ${WITH_DEFAULT:-debian:12}
            FROM scratch
            FROM ubuntu
            FROM registry.local:5000/team/app@sha256:abc
            FROM nginx:1.27@sha256:def
            """);

        Assert.Equal(
            [
                "mcr.microsoft.com/dotnet/sdk@10.0.1", "node@22-alpine", "debian@12", "ubuntu@latest",
                "registry.local:5000/team/app@sha256:abc", "nginx@1.27@sha256:def"
            ],
            entries.Select(Key));
        Assert.Equal(1, unresolved);
    }

    [Theory]
    [InlineData("src/App/packages.lock.json", nameof(ManifestKind.PackagesLock))]
    [InlineData("src/App/App.csproj", nameof(ManifestKind.DotnetProject))]
    [InlineData("deploy/docker/Dockerfile.back", nameof(ManifestKind.Dockerfile))]
    [InlineData("images/web.dockerfile", nameof(ManifestKind.Dockerfile))]
    [InlineData("requirements-dev.txt", nameof(ManifestKind.PythonRequirements))]
    [InlineData("composer.lock", nameof(ManifestKind.ComposerLock))]
    public void Catalog_RecognizesManifests(string path, string kind) =>
        Assert.Equal(kind, ManifestCatalog.Classify(path)?.ToString());

    [Theory]
    [InlineData("vendor/acme/lib/composer.lock")]
    [InlineData("web/node_modules/a/package.json")]
    [InlineData("src/App/bin/Debug/packages.lock.json")]
    [InlineData("docs/readme.md")]
    public void Catalog_IgnoresInstalledCode_AndOrdinaryFiles(string path) =>
        Assert.Null(ManifestCatalog.Classify(path));
}
