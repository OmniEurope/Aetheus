// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Hardening a release tree reaches the release directory, every nested directory and every file,
/// whatever the platform: the mode accessors are injected, so the walk is asserted on a real temporary tree
/// without a Unix file system.</summary>
public sealed class DeployReleaseFileSystemHardenTreeTests : IDisposable
{
    private const UnixFileMode Wide =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const UnixFileMode PlainWide = Wide & ~(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

    private readonly DirectoryInfo _release = Directory.CreateTempSubdirectory("aetheus-harden-");
    private readonly Dictionary<string, UnixFileMode> _modes = [];

    public void Dispose() => _release.Delete(recursive: true);

    private string Touch(string relativePath, UnixFileMode mode)
    {
        var path = Path.Combine(_release.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        _modes[path] = mode;
        return path;
    }

    private void Harden(bool isLinux) =>
        DeployReleaseFileSystem.HardenTreeWith(
            _release.FullName, isLinux, path => _modes[path], (path, mode) => _modes[path] = mode);

    [Fact]
    public void OnLinux_TheReleaseDirectoryAndEveryNestedDirectoryGetTheDirectoryMode()
    {
        Touch(Path.Combine("bin", "tools", "a.txt"), PlainWide);
        var nested = Path.Combine(_release.FullName, "bin", "tools");

        Harden(isLinux: true);

        Assert.Equal(DeployReleaseFileSystem.ReleaseDirectoryMode, _modes[_release.FullName]);
        Assert.Equal(DeployReleaseFileSystem.ReleaseDirectoryMode, _modes[Path.Combine(_release.FullName, "bin")]);
        Assert.Equal(DeployReleaseFileSystem.ReleaseDirectoryMode, _modes[nested]);
    }

    [Fact]
    public void OnLinux_EveryFileKeepsOnlyWhatItNeeds_AndAnExecutableStaysExecutableForUserAndGroup()
    {
        var data = Touch("data.json", PlainWide);
        var tool = Touch(Path.Combine("bin", "run"), Wide);
        var onlyOtherExecutable = Touch("odd", UnixFileMode.UserRead | UnixFileMode.OtherExecute);

        Harden(isLinux: true);

        const UnixFileMode plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        Assert.Equal(plain, _modes[data]);
        Assert.Equal(plain | UnixFileMode.UserExecute | UnixFileMode.GroupExecute, _modes[tool]);
        Assert.Equal(plain | UnixFileMode.UserExecute | UnixFileMode.GroupExecute, _modes[onlyOtherExecutable]);
    }

    [Fact]
    public void OutsideLinux_NothingIsTouched()
    {
        var data = Touch("data.json", PlainWide);

        Harden(isLinux: false);

        Assert.Equal(PlainWide, _modes[data]);
        Assert.Single(_modes);
    }

    [Fact]
    public void AnEmptyReleaseDirectory_IsHardenedAlone()
    {
        Harden(isLinux: true);

        var only = Assert.Single(_modes);
        Assert.Equal(_release.FullName, only.Key);
        Assert.Equal(DeployReleaseFileSystem.ReleaseDirectoryMode, only.Value);
    }
}
