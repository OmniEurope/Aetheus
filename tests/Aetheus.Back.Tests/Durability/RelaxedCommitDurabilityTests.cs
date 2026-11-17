// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Aetheus.Back.Tests.Durability;

public sealed class RelaxedCommitDurabilityTests
{
    [Fact]
    public async Task TheAttribute_RunsTheActionInsideTheScope_AndEndsItAfterwards()
    {
        var context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            controller: new object());
        var seenInside = false;

        await new RelaxedCommitDurabilityAttribute().OnActionExecutionAsync(context, () =>
        {
            seenInside = RelaxedCommitDurability.IsActive;
            return Task.FromResult(new ActionExecutedContext(context, [], context.Controller));
        });

        Assert.True(seenInside);
        Assert.False(RelaxedCommitDurability.IsActive);
    }

    [Fact]
    public void ANestedScope_RestoresTheOuterOne()
    {
        using (RelaxedCommitDurability.Begin())
        {
            using (RelaxedCommitDurability.Begin()) { }
            Assert.True(RelaxedCommitDurability.IsActive);
        }

        Assert.False(RelaxedCommitDurability.IsActive);
    }

    /// <summary>The slow requests of the system logs (R-1) are these three telemetry writes; the claim, the lease and the
    /// completion of a task stay durable, so none of them may carry the attribute.</summary>
    [Fact]
    public void TheTelemetryWrites_AreRelaxed_TheTaskClaimIsNot()
    {
        Assert.NotNull(typeof(IngestController).GetCustomAttribute<RelaxedCommitDurabilityAttribute>());
        Assert.NotNull(typeof(TaskLogsController).GetMethod(nameof(TaskLogsController.AppendLogs))!
            .GetCustomAttribute<RelaxedCommitDurabilityAttribute>());
        Assert.NotNull(typeof(AppMonitoringController).GetMethod(nameof(AppMonitoringController.ReportProbeResults))!
            .GetCustomAttribute<RelaxedCommitDurabilityAttribute>());

        Assert.Null(typeof(TasksController).GetMethod(nameof(TasksController.ClaimPendingTasks))!
            .GetCustomAttribute<RelaxedCommitDurabilityAttribute>());
        Assert.Null(typeof(TasksController).GetCustomAttribute<RelaxedCommitDurabilityAttribute>());
    }
}
