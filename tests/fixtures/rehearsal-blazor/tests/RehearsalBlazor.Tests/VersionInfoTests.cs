// SPDX-License-Identifier: EUPL-1.2
using RehearsalBlazor;

namespace RehearsalBlazor.Tests;

public sealed class VersionInfoTests
{
    [Test]
    public void Current_IsNeverEmpty()
    {
        Assert.That(VersionInfo.Current, Is.Not.Empty);
    }
}
