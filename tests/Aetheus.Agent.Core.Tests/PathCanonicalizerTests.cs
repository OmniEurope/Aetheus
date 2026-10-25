// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// <c>readlink -m</c>, which decides whether a web root is a dedicated child of <c>/var/www</c> once its
/// links are resolved: <c>publish-vitrine</c> refuses a web root that escapes through a link.
/// </summary>
public sealed class PathCanonicalizerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-canonical-").FullName.Replace('\\', '/');

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void APathThatDoesNotExist_IsReturnedWithForwardSlashes()
    {
        Assert.Equal(_root + "/a/b/c", PathCanonicalizer.ReadlinkM(_root + "/a/b/c"));
    }

    [Fact]
    public void DotSegmentsAreResolved()
    {
        Assert.Equal(_root + "/b", PathCanonicalizer.ReadlinkM(_root + "/a/./../b"));
    }

    [Fact]
    public void ADirectoryLink_IsFollowed_EvenWhenTheRestDoesNotExist()
    {
        var real = Directory.CreateDirectory(_root + "/real").FullName.Replace('\\', '/');
        DirectoryLinks.Create(_root + "/hop", real);

        try
        {
            Assert.Equal(real, PathCanonicalizer.ReadlinkM(_root + "/hop"));
            Assert.Equal(real + "/missing/leaf", PathCanonicalizer.ReadlinkM(_root + "/hop/missing/leaf"));
        }
        finally
        {
            DirectoryLinks.Remove(_root + "/hop");
        }
    }

    [Fact]
    public void ALinkToALink_IsFollowedToTheEnd()
    {
        var real = Directory.CreateDirectory(_root + "/real").FullName.Replace('\\', '/');
        DirectoryLinks.Create(_root + "/first", real);
        DirectoryLinks.Create(_root + "/second", _root + "/first");

        try
        {
            Assert.Equal(real, PathCanonicalizer.ReadlinkM(_root + "/second"));
        }
        finally
        {
            DirectoryLinks.Remove(_root + "/second");
            DirectoryLinks.Remove(_root + "/first");
        }
    }
}
