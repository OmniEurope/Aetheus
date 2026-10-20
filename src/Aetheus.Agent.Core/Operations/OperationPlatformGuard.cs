// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

internal static class OperationPlatformGuard
{
    public static async Task<ExecutorResult?> RequireLinuxAsync(
        Func<string, TaskLogLevel, Task> onOutput,
        string unsupportedMessage,
        bool? isLinux = null)
    {
        if (isLinux ?? RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;
        await onOutput(unsupportedMessage, TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(-1, false);
    }
}
