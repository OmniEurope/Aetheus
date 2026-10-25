// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

public class VisitorIngestServiceTests
{
    private readonly IAppVisitorRepository _visitors = Substitute.For<IAppVisitorRepository>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Record_RehashesAndStoresOnlyDailyOpaqueIdentity()
    {
        var submitted = new string('a', 64);
        var service = new VisitorIngestService(_visitors, _time);

        await service.RecordAsync(7, submitted, TestContext.Current.CancellationToken);

        await _visitors.Received(1).RecordAsync(7, new DateOnly(2026, 1, 10),
            Arg.Is<string>(hash => hash.Length == 64 && hash != submitted),
            _time.GetUtcNow().UtcDateTime, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Record_ScopesStoredIdentityByApplicationAndUtcDay()
    {
        var stored = new List<string>();
        _visitors.RecordAsync(
                Arg.Any<int>(), Arg.Any<DateOnly>(), Arg.Do<string>(stored.Add),
                Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var submitted = new string('b', 64);
        var service = new VisitorIngestService(_visitors, _time);

        await service.RecordAsync(7, submitted, TestContext.Current.CancellationToken);
        await service.RecordAsync(8, submitted, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromDays(1));
        await service.RecordAsync(7, submitted, TestContext.Current.CancellationToken);

        Assert.Equal(3, stored.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void VisitorAction_DeclaresSmallBodyCap()
    {
        var method = typeof(IngestController).GetMethod(nameof(IngestController.Visitor));
        Assert.NotNull(method);
        var attribute = Assert.Single(method.GetCustomAttributes(typeof(RequestSizeLimitAttribute), true)
            .Cast<RequestSizeLimitAttribute>());
        Assert.Equal(2048, ((IRequestSizeLimitMetadata)attribute).MaxRequestBodySize);
    }
}
