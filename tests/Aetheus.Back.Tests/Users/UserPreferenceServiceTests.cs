// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Users;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Users;

/// <summary>
/// R1-66: the rules of the per-user preferences. The later write wins and is kept with the device's
/// date, a date in the server's future is brought back to now, a removal leaves a tombstone, the bounds
/// per key and per user hold, and every read and write is confined to the user given.
/// </summary>
public sealed class UserPreferenceServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly AppDbContext _db;
    private readonly UserPreferenceService _sut;
    private readonly int _alice;
    private readonly int _bob;

    public UserPreferenceServiceTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            _time);
        var alice = new User { Username = "alice", PasswordHash = "x" };
        var bob = new User { Username = "bob", PasswordHash = "x" };
        _db.Users.AddRange(alice, bob);
        _db.SaveChanges();
        _alice = alice.Id;
        _bob = bob.Id;
        _sut = new UserPreferenceService(new UserPreferenceRepository(_db), _time);
    }

    public void Dispose() => _db.Dispose();

    private static SaveUserPreferenceRequest Write(string? value, DateTimeOffset at) => new() { Value = value, UpdatedAt = at };

    [Fact]
    public async Task Save_KeepsTheDeviceDate_AndIgnoresAnOlderWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        await _sut.SaveAsync(_alice, "aetheus.language", Write("\"fr-FR\"", Now.AddMinutes(-1)), ct);

        var stale = await _sut.SaveAsync(_alice, "aetheus.language", Write("\"en\"", Now.AddMinutes(-2)), ct);

        Assert.Equal("\"fr-FR\"", stale.Value);
        Assert.Equal(Now.AddMinutes(-1), stale.UpdatedAt);
        Assert.Equal("\"fr-FR\"", (await _sut.GetAsync(_alice, "aetheus.language", ct))!.Value);
    }

    [Fact]
    public async Task Save_BringsAFutureDateBackToNow()
    {
        var saved = await _sut.SaveAsync(_alice, "aetheus.language", Write("\"en\"", Now.AddHours(3)), TestContext.Current.CancellationToken);

        Assert.Equal(Now, saved.UpdatedAt);
    }

    [Fact]
    public async Task Remove_LeavesATombstone_ListedSinceItsDate()
    {
        var ct = TestContext.Current.CancellationToken;
        await _sut.SaveAsync(_alice, "aetheus.language", Write("\"en\"", Now.AddMinutes(-5)), ct);

        await _sut.RemoveAsync(_alice, "aetheus.language", ct);

        var tombstone = await _sut.GetAsync(_alice, "aetheus.language", ct);
        Assert.NotNull(tombstone);
        Assert.Null(tombstone.Value);
        Assert.Equal(Now, tombstone.UpdatedAt);
        Assert.Single(await _sut.GetAllAsync(_alice, Now.AddSeconds(-1), ct));
        Assert.Empty(await _sut.GetAllAsync(_alice, Now.AddSeconds(1), ct));
    }

    [Fact]
    public async Task EveryCall_IsConfinedToTheUserGiven()
    {
        var ct = TestContext.Current.CancellationToken;
        await _sut.SaveAsync(_alice, "aetheus.appearance", Write("{\"theme\":\"light\"}", Now), ct);

        Assert.Null(await _sut.GetAsync(_bob, "aetheus.appearance", ct));
        Assert.Empty(await _sut.GetAllAsync(_bob, null, ct));
        await _sut.RemoveAsync(_bob, "aetheus.appearance", ct);

        Assert.Equal("{\"theme\":\"light\"}", (await _sut.GetAsync(_alice, "aetheus.appearance", ct))!.Value);
    }

    [Theory]
    [InlineData("", "1")]
    [InlineData(".hidden", "1")]
    [InlineData("has/slash", "1")]
    [InlineData("aetheus.valid", "{broken")]
    public async Task InvalidKeyOrValue_IsABadRequest(string key, string value)
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SaveAsync(_alice, key, Write(value, Now), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheBoundsPerUser_Hold()
    {
        var ct = TestContext.Current.CancellationToken;
        var big = "\"" + new string('x', UserPreferenceRules.MaxValueLength - 2) + "\"";
        var fit = UserPreferenceRules.MaxTotalLength / UserPreferenceRules.MaxValueLength;
        for (var i = 0; i < fit; i++)
            await _sut.SaveAsync(_alice, $"aetheus.big.{i}", Write(big, Now), ct);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.SaveAsync(_alice, "aetheus.big.more", Write(big, Now), ct));
        // Replacing an existing key does not count it twice, and another user has their own budget.
        await _sut.SaveAsync(_alice, "aetheus.big.0", Write(big, Now), ct);
        await _sut.SaveAsync(_bob, "aetheus.big.0", Write(big, Now), ct);
    }

    [Fact]
    public async Task TheNumberOfKeysPerUser_IsBounded()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < UserPreferenceRules.MaxKeys; i++)
            _db.UserPreferences.Add(new UserPreference { UserId = _alice, Key = $"aetheus.k{i}", Value = "1", ChangedAt = Now.UtcDateTime });
        await _db.SaveChangesAsync(ct);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.SaveAsync(_alice, "aetheus.one-more", Write("1", Now), ct));
        await _sut.SaveAsync(_alice, "aetheus.k0", Write("2", Now), ct);
    }
}
