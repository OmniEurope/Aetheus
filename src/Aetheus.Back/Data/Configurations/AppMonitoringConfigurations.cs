// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Data.Configurations;

internal static class AppMonitoringConfigurationExtensions
{
    public static void HasMonitoredAppCascade<T>(
        this EntityTypeBuilder<T> builder,
        Expression<Func<T, MonitoredApp?>> navigation,
        Expression<Func<T, object?>> foreignKey)
        where T : class
    {
        builder.HasOne(navigation)
            .WithMany()
            .HasForeignKey(foreignKey)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public static void HasTimestampIndexes<T>(
        this EntityTypeBuilder<T> builder,
        Expression<Func<T, object?>> scopedTimestamp,
        Expression<Func<T, object?>> timestamp)
        where T : class
    {
        builder.HasIndex(scopedTimestamp);
        builder.HasIndex(timestamp);
    }

    public static void HasTimestampIndexesAndCascade<T>(
        this EntityTypeBuilder<T> builder,
        Expression<Func<T, object?>> scopedTimestamp,
        Expression<Func<T, object?>> timestamp,
        Expression<Func<T, MonitoredApp?>> navigation,
        Expression<Func<T, object?>> foreignKey)
        where T : class
    {
        builder.HasTimestampIndexes(scopedTimestamp, timestamp);
        builder.HasMonitoredAppCascade(navigation, foreignKey);
    }
}

internal sealed class MonitoredAppConfiguration : IEntityTypeConfiguration<MonitoredApp>
{
    public void Configure(EntityTypeBuilder<MonitoredApp> builder)
    {
        builder.Property(e => e.Name).HasMaxLength(150).IsRequired();
        builder.Property(e => e.ProbeUrl).HasMaxLength(2048);
        builder.Property(e => e.IngestKeyHash).HasMaxLength(128);
        builder.Property(e => e.IngestKeyExpiresAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP + INTERVAL '90 days'");
        builder.Property(e => e.PreviousIngestKeyHash).HasMaxLength(128);
        builder.Property(e => e.SecondPreviousIngestKeyHash).HasMaxLength(128);
        builder.Property(e => e.AnalyticsSiteId).HasMaxLength(64);
        builder.Property(e => e.AnalyticsAllowedOriginsJson).HasMaxLength(2048);
        builder.Property(e => e.AnalyticsVaultName).HasMaxLength(100);
        builder.Property(e => e.AnalyticsStorageBudgetBytes)
            .HasDefaultValue(Aetheus.Shared.Components.AppMonitoring.AppMonitoringDefaults.DefaultAnalyticsStorageBudgetBytes);
        builder.HasIndex(e => e.IngestKeyHash).IsUnique().HasFilter("\"IngestKeyHash\" IS NOT NULL");
        builder.HasIndex(e => e.PreviousIngestKeyHash)
            .IsUnique()
            .HasFilter("\"PreviousIngestKeyHash\" IS NOT NULL");
        builder.HasIndex(e => e.SecondPreviousIngestKeyHash)
            .IsUnique()
            .HasFilter("\"SecondPreviousIngestKeyHash\" IS NOT NULL");
        builder.HasIndex(e => e.AnalyticsSiteId).IsUnique().HasFilter("\"AnalyticsSiteId\" IS NOT NULL");

        builder.HasIndex(e => e.ProjectId);
        builder.HasIndex(e => e.ServerId);
        builder.HasIndex(e => e.CurrentStatus);

        builder.HasOne(e => e.Project)
               .WithMany()
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.Restrict);

        // Optional references: the app survives (and falls back to backend-probed / informative) when
        // the environment or hosting server is removed.
        builder.HasOne(e => e.Environment)
               .WithMany()
               .HasForeignKey(e => e.EnvironmentId)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.Server)
               .WithMany()
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AppHealthSampleConfiguration : IEntityTypeConfiguration<AppHealthSample>
{
    public void Configure(EntityTypeBuilder<AppHealthSample> builder)
    {
        builder.Property(e => e.Error).HasMaxLength(500);

        builder.HasTimestampIndexesAndCascade(
            e => new { e.MonitoredAppId, e.Timestamp },
            e => e.Timestamp,
            e => e.MonitoredApp,
            e => e.MonitoredAppId);
    }
}

internal sealed class AppHealthHourlyConfiguration : IEntityTypeConfiguration<AppHealthHourly>
{
    public void Configure(EntityTypeBuilder<AppHealthHourly> builder)
    {
        builder.HasIndex(e => new { e.MonitoredAppId, e.HourUtc }).IsUnique();

        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppMetricSampleConfiguration : IEntityTypeConfiguration<AppMetricSample>
{
    public void Configure(EntityTypeBuilder<AppMetricSample> builder)
    {
        builder.Property(e => e.MetricName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Unit).HasMaxLength(32);
        builder.Property(e => e.AttributesJson).HasMaxLength(1024);

        builder.HasIndex(e => new { e.MonitoredAppId, e.MetricName, e.Timestamp });
        builder.HasIndex(e => e.Timestamp);

        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppMetricHourlyConfiguration : IEntityTypeConfiguration<AppMetricHourly>
{
    public void Configure(EntityTypeBuilder<AppMetricHourly> builder)
    {
        builder.Property(e => e.MetricName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.AttributesJson).HasMaxLength(1024);
        builder.Property(e => e.Unit).HasMaxLength(32);
        builder.HasIndex(e => new { e.MonitoredAppId, e.MetricName, e.AttributesJson, e.HourUtc }).IsUnique();
        // Index the hour used by bounded rollup sweeps to avoid scanning application history for
        // each hour.
        builder.HasIndex(e => e.HourUtc);

        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppMetricNameConfiguration : IEntityTypeConfiguration<AppMetricName>
{
    public void Configure(EntityTypeBuilder<AppMetricName> builder)
    {
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.HasIndex(e => new { e.MonitoredAppId, e.Name }).IsUnique();

        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppMetricThresholdConfiguration : IEntityTypeConfiguration<AppMetricThreshold>
{
    public void Configure(EntityTypeBuilder<AppMetricThreshold> builder)
    {
        builder.Property(e => e.MetricName).HasMaxLength(200).IsRequired();
        builder.HasIndex(e => new { e.MonitoredAppId, e.MetricName });

        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppLogEntryConfiguration : IEntityTypeConfiguration<AppLogEntry>
{
    public void Configure(EntityTypeBuilder<AppLogEntry> builder)
    {
        builder.Property(e => e.SeverityText).HasMaxLength(32);
        builder.Property(e => e.Body).HasMaxLength(4000).IsRequired();
        builder.Property(e => e.AttributesJson).HasMaxLength(1024);

        builder.HasTimestampIndexesAndCascade(
            e => new { e.MonitoredAppId, e.Timestamp },
            e => e.Timestamp,
            e => e.MonitoredApp,
            e => e.MonitoredAppId);
    }
}

internal sealed class AppErrorEventConfiguration : IEntityTypeConfiguration<AppErrorEvent>
{
    public void Configure(EntityTypeBuilder<AppErrorEvent> builder)
    {
        builder.Property(e => e.Fingerprint).HasMaxLength(128).IsRequired();
        builder.Property(e => e.ExceptionType).HasMaxLength(256).IsRequired();
        builder.Property(e => e.Message).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.TopFrame).HasMaxLength(512);

        builder.HasIndex(e => new { e.MonitoredAppId, e.Fingerprint }).IsUnique();
        builder.HasIndex(e => new { e.MonitoredAppId, e.LastSeenAt });

        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppAnalyticsEventConfiguration : IEntityTypeConfiguration<AppAnalyticsEvent>
{
    public void Configure(EntityTypeBuilder<AppAnalyticsEvent> builder)
    {
        builder.Property(e => e.Kind).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Route).HasMaxLength(256).IsRequired();
        builder.Property(e => e.ErrorType).HasMaxLength(32);
        builder.Property(e => e.SessionPseudonym).HasMaxLength(64).IsRequired();
        builder.HasIndex(e => new { e.MonitoredAppId, e.EventId }).IsUnique();
        builder.HasIndex(e => new { e.MonitoredAppId, e.OccurredAtUtc });
        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppAnalyticsSessionConfiguration : IEntityTypeConfiguration<AppAnalyticsSession>
{
    public void Configure(EntityTypeBuilder<AppAnalyticsSession> builder)
    {
        builder.Property(e => e.SessionPseudonym).HasMaxLength(64).IsRequired();
        builder.HasIndex(e => new { e.MonitoredAppId, e.SessionPseudonym, e.LastSeenAtUtc });
        builder.HasIndex(e => e.LastSeenAtUtc);
        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppAnalyticsPeriodIdentityConfiguration : IEntityTypeConfiguration<AppAnalyticsPeriodIdentity>
{
    public void Configure(EntityTypeBuilder<AppAnalyticsPeriodIdentity> builder)
    {
        builder.Property(e => e.Pseudonym).HasMaxLength(64).IsRequired();
        builder.HasIndex(e => new { e.MonitoredAppId, e.PeriodKind, e.PeriodStartUtc, e.Pseudonym }).IsUnique();
        builder.HasIndex(e => e.LastSeenAtUtc);
        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppAnalyticsAggregateConfiguration : IEntityTypeConfiguration<AppAnalyticsAggregate>
{
    public void Configure(EntityTypeBuilder<AppAnalyticsAggregate> builder)
    {
        builder.HasIndex(e => new { e.MonitoredAppId, e.PeriodKind, e.PeriodStartUtc }).IsUnique();
        builder.HasIndex(e => e.PeriodStartUtc);
        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppAnalyticsPageAggregateConfiguration : IEntityTypeConfiguration<AppAnalyticsPageAggregate>
{
    public void Configure(EntityTypeBuilder<AppAnalyticsPageAggregate> builder)
    {
        builder.Property(e => e.Route).HasMaxLength(256).IsRequired();
        builder.HasIndex(e => new { e.MonitoredAppId, e.DayUtc, e.Route }).IsUnique();
        builder.HasIndex(e => e.DayUtc);
        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppAnalyticsRejectionConfiguration : IEntityTypeConfiguration<AppAnalyticsRejection>
{
    public void Configure(EntityTypeBuilder<AppAnalyticsRejection> builder)
    {
        builder.Property(e => e.ReasonCode).HasMaxLength(64).IsRequired();
        builder.HasIndex(e => e.OccurredAtUtc);
        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppAnalyticsIngestVolumeConfiguration : IEntityTypeConfiguration<AppAnalyticsIngestVolume>
{
    public void Configure(EntityTypeBuilder<AppAnalyticsIngestVolume> builder)
    {
        builder.HasIndex(e => new { e.MonitoredAppId, e.ReceivedAtUtc });
        builder.HasIndex(e => e.ReceivedAtUtc);
        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}

internal sealed class AppVisitorIdentityConfiguration : IEntityTypeConfiguration<AppVisitorIdentity>
{
    public void Configure(EntityTypeBuilder<AppVisitorIdentity> builder)
    {
        builder.Property(e => e.FingerprintHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(e => new { e.MonitoredAppId, e.DayUtc, e.FingerprintHash }).IsUnique();
        builder.HasIndex(e => e.DayUtc);

        builder.HasMonitoredAppCascade(e => e.MonitoredApp, e => e.MonitoredAppId);
    }
}
