// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>A refused no-reply password is re-provisioned once per cool-down, never in a loop.</summary>
public sealed class MailNoReplyCredentialRepairTests
{
    private readonly IMailNoReplySenderService _prepare = Substitute.For<IMailNoReplySenderService>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 18, 37, 0, TimeSpan.Zero));
    private readonly MailNoReplyCredentialRepair _sut;

    public MailNoReplyCredentialRepairTests()
    {
        var services = new ServiceCollection().AddSingleton(_prepare).BuildServiceProvider();
        _sut = new MailNoReplyCredentialRepair(
            services, new NoReplyRepairThrottle(_time), NullLogger<MailNoReplyCredentialRepair>.Instance);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstRefusal_GeneratesAndQueuesANewPassword()
    {
        var repaired = await _sut.TryRepairAsync(Ct);

        Assert.True(repaired);
        await _prepare.Received(1).PrepareAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefusalsInsideTheCooldown_DoNotRotateThePasswordAgain()
    {
        await _sut.TryRepairAsync(Ct);
        _time.Advance(MailNoReplyCredentialRepair.Cooldown - TimeSpan.FromSeconds(1));

        var repaired = await _sut.TryRepairAsync(Ct);

        Assert.False(repaired);
        await _prepare.Received(1).PrepareAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AfterTheCooldown_ItRepairsAgain()
    {
        await _sut.TryRepairAsync(Ct);
        _time.Advance(MailNoReplyCredentialRepair.Cooldown);

        var repaired = await _sut.TryRepairAsync(Ct);

        Assert.True(repaired);
        await _prepare.Received(2).PrepareAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefusedRequest_IsReportedAsNotRepaired_WithoutThrowing()
    {
        _prepare.PrepareAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new BadRequestException("No mail domain is managed by Aetheus."));

        var repaired = await _sut.TryRepairAsync(Ct);

        Assert.False(repaired);
    }
}
