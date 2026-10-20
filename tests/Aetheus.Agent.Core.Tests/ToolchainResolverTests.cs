// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Agent.Core.Toolchains;

namespace Aetheus.Agent.Core.Tests;

public sealed class ToolchainResolverTests
{
    private static readonly string ImmutableImage =
        $"registry.example/aetheus/toolchain@sha256:{new string('b', 64)}";

    [Fact]
    public async Task ResolveAsync_DirectImageRequiresDigest()
    {
        using var workspace = new TemporaryWorkspace();
        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Image = "node:24.4.1" },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("immutable", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_DirectDigestAndShellArePreserved()
    {
        using var workspace = new TemporaryWorkspace();
        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Image = ImmutableImage, Shell = "sh" },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImmutableImage, result.Image);
        Assert.Equal("sh", result.Shell);
        Assert.Null(result.Toolchain);
    }

    [Theory]
    [InlineData("dotnet", "10.0.202", "global.json")]
    [InlineData("node", "24.4.1", "package.json")]
    [InlineData("java", "25.0.2", ".java-version")]
    [InlineData("python", "3.14.1", ".python-version")]
    public async Task ResolveAsync_NativeContractMatchesManifest_ReturnsLockedImage(
        string toolchain,
        string version,
        string versionFile)
    {
        using var workspace = new TemporaryWorkspace();
        await WriteNativeContractAsync(workspace.Path, toolchain, version);
        await WriteManifestAsync(workspace.Path, toolchain, version, ImmutableImage);

        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Toolchain = toolchain },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.FailureReason);
        Assert.Equal(toolchain, result.Toolchain);
        Assert.Equal(version, result.Version);
        Assert.Equal(ImmutableImage, result.Image);
        Assert.EndsWith(versionFile, result.NativeVersionFile, StringComparison.Ordinal);
        Assert.Single(result.Caches);
        Assert.Contains(toolchain, result.Caches[0].Key, StringComparison.Ordinal);
        Assert.Contains(version, result.Caches[0].Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_NativeVersionDiffers_ReturnsInfrastructureMismatchWithoutMutation()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteNativeContractAsync(workspace.Path, "dotnet", "10.0.202");
        await WriteManifestAsync(workspace.Path, "dotnet", "10.0.301", ImmutableImage);
        var before = Snapshot(workspace.Path);

        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Toolchain = "dotnet" },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("10.0.301", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("10.0.202", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(workspace.Path));
    }

    [Fact]
    public async Task ResolveAsync_MissingManifest_ReturnsInfrastructureMismatch()
    {
        using var workspace = new TemporaryWorkspace();
        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Toolchain = "dotnet" },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains(ToolchainResolver.ManifestRelativePath, result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_ImageAndToolchainTogether_AreRejected()
    {
        using var workspace = new TemporaryWorkspace();
        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Toolchain = "dotnet", Image = ImmutableImage },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("not both", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_UnsupportedManifestVersion_IsRejected()
    {
        using var workspace = new TemporaryWorkspace();
        var manifest = System.IO.Path.Combine(
            workspace.Path,
            ToolchainResolver.ManifestRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(manifest)!);
        await File.WriteAllTextAsync(
            manifest,
            "version: 2\ntoolchains: {}\n",
            TestContext.Current.CancellationToken);

        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Toolchain = "dotnet" },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("version '2'", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_CacheKeyChangesWithLockFile()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteNativeContractAsync(workspace.Path, "node", "24.4.1");
        await WriteManifestAsync(workspace.Path, "node", "24.4.1", ImmutableImage);
        var resolver = new ToolchainResolver();

        var first = await resolver.ResolveAsync(
            new ContainerSpec { Toolchain = "node" },
            workspace.Path,
            TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            System.IO.Path.Combine(workspace.Path, "toolchains", "node", "package-lock.json"),
            Environment.NewLine,
            TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(
            new ContainerSpec { Toolchain = "node" },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Caches[0].Key, second.Caches[0].Key);
    }

    [Theory]
    [InlineData("version: 1\ntoolchains:\n", "null toolchains")]
    [InlineData("version: 1\ntoolchains:\n  dotnet:\n", "null manifest entry")]
    [InlineData(
        "version: 1\ntoolchains:\n  dotnet:\n    version: \"10.0.202\"\n    image: \"registry.example/aetheus/toolchain@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"\n    path: \"toolchains/dotnet\"\n    caches:\n      -\n",
        "null entry")]
    public async Task ResolveAsync_NullManifestStructures_ReturnInfrastructureMismatch(
        string yaml,
        string expected)
    {
        using var workspace = new TemporaryWorkspace();
        await WriteNativeContractAsync(workspace.Path, "dotnet", "10.0.202");
        await WriteRawManifestAsync(workspace.Path, yaml);

        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Toolchain = "dotnet" },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, ToolchainResolution.InfrastructureMismatchCode);
    }

    [Theory]
    [InlineData("ruby", "3.4.1", "IMMUTABLE", "bash", "has no supported native contract reader")]
    [InlineData("dotnet", "10.*", "IMMUTABLE", "bash", "version '10.*' is not exact")]
    [InlineData("node", "24.4.1", "node:24.4.1", "bash", "image must end in an immutable SHA-256 digest")]
    [InlineData("java", "25.0.2", "IMMUTABLE", "zsh", "shell 'zsh' is unsupported")]
    public async Task ResolveAsync_ManifestEntryOutsideContract_IsRejectedWithItsReason(
        string toolchain,
        string version,
        string image,
        string shell,
        string expected)
    {
        using var workspace = new TemporaryWorkspace();
        var resolvedImage = image == "IMMUTABLE" ? ImmutableImage : image;
        await WriteRawManifestAsync(workspace.Path, $"""
            version: 1
            toolchains:
              {toolchain}:
                version: "{version}"
                image: "{resolvedImage}"
                path: "toolchains/{toolchain}"
                shell: "{shell}"
            """);

        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Toolchain = toolchain },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.FailureReason, StringComparison.Ordinal);
        Assert.Contains($"'{toolchain}'", result.FailureReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sdk\":null}")]
    [InlineData("{\"sdk\":{\"version\":10}}")]
    public async Task ResolveAsync_InvalidNativeJson_ReturnsInfrastructureMismatch(string globalJson)
    {
        using var workspace = new TemporaryWorkspace();
        var nativeRoot = System.IO.Path.Combine(workspace.Path, "toolchains", "dotnet");
        Directory.CreateDirectory(nativeRoot);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(nativeRoot, "global.json"),
            globalJson,
            TestContext.Current.CancellationToken);
        await WriteManifestAsync(workspace.Path, "dotnet", "10.0.202", ImmutableImage);

        var result = await new ToolchainResolver().ResolveAsync(
            new ContainerSpec { Toolchain = "dotnet" },
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("sdk.version", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, ToolchainResolution.InfrastructureMismatchCode);
    }

    [Fact]
    public void BuildPreflightScript_UsesStructuredMismatchExit()
    {
        var resolution = new ToolchainResolution
        {
            IsSuccess = true,
            Toolchain = "dotnet",
            Version = "10.0.202",
            Image = ImmutableImage
        };

        var script = resolution.BuildPreflightScript();

        Assert.Contains("dotnet --version", script, StringComparison.Ordinal);
        Assert.Contains("InfrastructureMismatch", script, StringComparison.Ordinal);
        Assert.Contains(
            $"exit {ToolchainResolution.InfrastructureMismatchExitCode}",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain('\r', script);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, ToolchainResolution.InfrastructureMismatchCode);
    }

    private static async Task WriteManifestAsync(
        string root,
        string toolchain,
        string version,
        string image)
    {
        var manifest = System.IO.Path.Combine(
            root,
            ToolchainResolver.ManifestRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(manifest)!);
        var lockFile = toolchain switch
        {
            "dotnet" => "global.json",
            "node" => "package-lock.json",
            "java" => "pom.xml",
            "python" => "requirements.lock",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain))
        };
        var target = toolchain switch
        {
            "dotnet" => "/home/aetheus/.nuget/packages",
            "node" => "/home/aetheus/.npm",
            "java" => "/home/aetheus/.m2/repository",
            "python" => "/home/aetheus/.cache",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain))
        };
        var yaml = $"""
            version: 1
            toolchains:
              {toolchain}:
                version: "{version}"
                image: "{image}"
                path: "toolchains/{toolchain}"
                shell: "bash"
                caches:
                  - name: "{toolchain}"
                    target: "{target}"
                    lock_file: "{lockFile}"
            """;
        await File.WriteAllTextAsync(manifest, yaml, TestContext.Current.CancellationToken);
    }

    private static async Task WriteRawManifestAsync(string root, string yaml)
    {
        var manifest = System.IO.Path.Combine(
            root,
            ToolchainResolver.ManifestRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(manifest)!);
        await File.WriteAllTextAsync(manifest, yaml, TestContext.Current.CancellationToken);
    }

    private static async Task WriteNativeContractAsync(string root, string toolchain, string version)
    {
        var path = System.IO.Path.Combine(root, "toolchains", toolchain);
        Directory.CreateDirectory(path);
        switch (toolchain)
        {
            case "dotnet":
                await WriteAsync(
                    path,
                    "global.json",
                    "{\"sdk\":{\"version\":\"" + version + "\",\"rollForward\":\"disable\"}}");
                break;
            case "node":
                await WriteAsync(
                    path,
                    "package.json",
                    "{\"engines\":{\"node\":\"" + version + "\"}}");
                await WriteAsync(path, "package-lock.json", """{"lockfileVersion":3}""");
                break;
            case "java":
                await WriteAsync(path, ".java-version", version);
                await WriteAsync(path, "pom.xml", "<project />");
                await WriteAsync(path, "mvnw", "#!/bin/sh");
                break;
            case "python":
                await WriteAsync(path, ".python-version", version);
                await WriteAsync(path, "pyproject.toml", "[project]\nname='toto'");
                await WriteAsync(path, "requirements.lock", string.Empty);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(toolchain));
        }
    }

    private static Task WriteAsync(string root, string relativePath, string content) =>
        File.WriteAllTextAsync(
            System.IO.Path.Combine(root, relativePath),
            content,
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

    private static string Snapshot(string root) =>
        string.Join(
            "|",
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(path =>
                    $"{System.IO.Path.GetRelativePath(root, path)}:{Convert.ToHexString(File.ReadAllBytes(path))}"));

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("aetheus-toolchain-test-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
