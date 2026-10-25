// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Services;

internal static class StorageSizeFormatting
{
    public static long GiB(int value) => checked((long)value * 1024 * 1024 * 1024);

    public static string FormatBytes(long bytes) =>
        bytes >= GiB(1)
            ? $"{bytes / (double)GiB(1):0.##} Go"
            : $"{bytes / (1024d * 1024d):0.##} Mo";
}
