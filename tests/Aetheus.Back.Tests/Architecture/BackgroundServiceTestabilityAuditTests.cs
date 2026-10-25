// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.Servers;
using Microsoft.Extensions.Hosting;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Architectural guard: every <see cref="BackgroundService"/> must expose an <c>internal</c>
/// instance method carrying its real work, separate from the timer loop in <c>ExecuteAsync</c>.
/// This is the E-3 lesson - without an extracted, directly-callable work method the only thing a
/// test can do is start the host and assert a <c>Task.Delay</c> didn't throw (an assertion-free
/// timer test). Keeping the work method internal lets a <c>FakeTimeProvider</c>-driven unit test
/// exercise the real effect (cutoff, deletion, run trigger…) deterministically.
/// </summary>
public class BackgroundServiceTestabilityAuditTests
{
    [Fact]
    public void EveryBackgroundService_ExposesAnInternalWorkMethod()
    {
        var services = typeof(MetricsCleanupService).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(BackgroundService).IsAssignableFrom(t))
            .ToList();

        Assert.True(services.Count >= 5, $"Only found {services.Count} BackgroundService(s) - likely a discovery bug.");

        var offenders = services
            .Where(svc => !svc.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Any(m => m.IsAssembly                      // internal
                          && !m.IsSpecialName
                          && m.Name != nameof(IDisposable.Dispose)))
            .Select(svc => svc.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Each BackgroundService must extract its work into an `internal` method so it can be unit-tested "
            + "deterministically (E-3 lesson) instead of an assertion-free Task.Delay timer test. Offenders: "
            + string.Join(", ", offenders));
    }
}
