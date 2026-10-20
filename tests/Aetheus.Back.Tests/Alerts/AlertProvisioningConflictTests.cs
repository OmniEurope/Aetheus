// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aetheus.Back.Tests;

/// <summary>Seeding an alert rule that a concurrent request already created is not an error; only a unique
/// violation on the provisioning key or the name qualifies. Any other failure must stay a failure.</summary>
public class AlertProvisioningConflictTests
{
    private static DbUpdateException Failure(string sqlState, string? constraint) =>
        new("save failed", new PostgresException(
            messageText: "duplicate", severity: "ERROR", invariantSeverity: "ERROR",
            sqlState: sqlState, constraintName: constraint));

    [Theory]
    [InlineData("IX_AlertRules_ProvisioningKey")]
    [InlineData("IX_AlertRules_Name")]
    public void IsPotentialProvisioningConflict_AUniqueViolationOnAKnownIndex_IsAConflict(string constraint) =>
        Assert.True(AlertRepository.IsPotentialProvisioningConflict(Failure(PostgresErrorCodes.UniqueViolation, constraint)));

    [Fact]
    public void IsPotentialProvisioningConflict_AUniqueViolationOnAnotherIndex_IsNotAConflict() =>
        Assert.False(AlertRepository.IsPotentialProvisioningConflict(Failure(PostgresErrorCodes.UniqueViolation, "IX_Other")));

    [Fact]
    public void IsPotentialProvisioningConflict_AnotherSqlStateOnTheKnownIndex_IsNotAConflict() =>
        Assert.False(AlertRepository.IsPotentialProvisioningConflict(
            Failure(PostgresErrorCodes.ForeignKeyViolation, "IX_AlertRules_Name")));

    [Fact]
    public void IsPotentialProvisioningConflict_ANonPostgresFailure_IsNotAConflict() =>
        Assert.False(AlertRepository.IsPotentialProvisioningConflict(new DbUpdateException("save failed", new InvalidOperationException())));

    [Fact]
    public void IsPotentialProvisioningConflict_NoInnerException_IsNotAConflict() =>
        Assert.False(AlertRepository.IsPotentialProvisioningConflict(new DbUpdateException("save failed")));
}
