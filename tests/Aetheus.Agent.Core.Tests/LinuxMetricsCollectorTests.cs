// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Linux.Collectors;

namespace Aetheus.Agent.Core.Tests;

public sealed class LinuxMetricsCollectorTests
{
    [Fact]
    public void ParseCpuTimes_DoesNotCountGuestTimeTwice()
    {
        // user=50 (of which guest=50), idle=50, nice=20 (of which guest_nice=20): 50% busy, not 66.7%.
        var (idle, total) = LinuxMetricsCollector.ParseCpuTimes("cpu  50 20 0 50 0 0 0 0 50 20");

        Assert.Equal(50, idle);
        Assert.Equal(120, total);
    }

    [Fact]
    public void ParseCpuTimes_CountsIowaitAsIdleAndStealInTotal()
    {
        var (idle, total) = LinuxMetricsCollector.ParseCpuTimes("cpu  10 1 2 30 4 5 6 7");

        Assert.Equal(34, idle);
        Assert.Equal(65, total);
    }
}
