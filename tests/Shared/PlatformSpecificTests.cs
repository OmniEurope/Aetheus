// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.CompilerServices;
using Xunit.Sdk;
using Xunit.v3;

namespace Aetheus.Tests.Shared;

/// <summary>Restricts platform-specific tests at discovery so incompatible tests are neither run
/// nor counted as skipped. Discovery avoids conflicting Microsoft.Testing.Platform trait and caller
/// filters.</summary>
public interface IPlatformSpecificTest
{
    /// <summary>"windows" or "linux".</summary>
    string Platform { get; }
}

[XunitTestCaseDiscoverer(typeof(PlatformFactDiscoverer))]
public sealed class PlatformFactAttribute(
    string platform,
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : FactAttribute(sourceFilePath, sourceLineNumber), IPlatformSpecificTest
{
    public string Platform { get; } = platform;
}

[XunitTestCaseDiscoverer(typeof(PlatformTheoryDiscoverer))]
public sealed class PlatformTheoryAttribute(
    string platform,
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : TheoryAttribute(sourceFilePath, sourceLineNumber), IPlatformSpecificTest
{
    public string Platform { get; } = platform;
}

public sealed class PlatformFactDiscoverer : FactDiscoverer
{
    public override ValueTask<IReadOnlyCollection<IXunitTestCase>> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions, IXunitTestMethod testMethod, IFactAttribute factAttribute) =>
        TestPlatform.IsCurrent(factAttribute)
            ? base.Discover(discoveryOptions, testMethod, factAttribute)
            : new([]);
}

public sealed class PlatformTheoryDiscoverer : TheoryDiscoverer
{
    public override ValueTask<IReadOnlyCollection<IXunitTestCase>> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions, IXunitTestMethod testMethod, IFactAttribute factAttribute) =>
        TestPlatform.IsCurrent(factAttribute)
            ? base.Discover(discoveryOptions, testMethod, factAttribute)
            : new([]);
}

internal static class TestPlatform
{
    public static bool IsCurrent(IFactAttribute attribute) =>
        ((IPlatformSpecificTest)attribute).Platform switch
        {
            "windows" => OperatingSystem.IsWindows(),
            "linux" => OperatingSystem.IsLinux(),
            var other => throw new ArgumentException($"Unknown test platform '{other}'.", nameof(attribute))
        };
}
