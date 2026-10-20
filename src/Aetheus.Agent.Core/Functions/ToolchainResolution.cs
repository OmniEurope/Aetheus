// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>What resolving a pinned toolchain gave: the executable path, or why there is none.</summary>
internal sealed record ToolchainResolution(string? Executable, string? Error)
{
    public static ToolchainResolution Found(string executable) => new(executable, null);

    public static ToolchainResolution Failed(string error) => new(null, error);
}
