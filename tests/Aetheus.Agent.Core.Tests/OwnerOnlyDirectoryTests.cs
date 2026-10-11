// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

public sealed class OwnerOnlyDirectoryTests
{
    [PlatformFact("linux")]
    public void TrySet_ReportsExpectedFilesystemFailure()
    {
        Exception? reported = null;
        string? reportedDirectory = null;

        OwnerOnlyDirectory.TrySet(
            "/restricted/workspace",
            (exception, directory) =>
            {
                reported = exception;
                reportedDirectory = directory;
            },
            static (_, _) => throw new UnauthorizedAccessException("permission denied"));

        Assert.IsType<UnauthorizedAccessException>(reported);
        Assert.Equal("/restricted/workspace", reportedDirectory);
    }

    [PlatformFact("linux")]
    public void TrySet_DoesNotHideUnexpectedFailure()
    {
        Assert.Throws<InvalidOperationException>(() => OwnerOnlyDirectory.TrySet(
            "/restricted/workspace",
            static (_, _) => { },
            static (_, _) => throw new InvalidOperationException("unexpected")));
    }
}
