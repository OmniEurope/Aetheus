// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The exit codes of <c>aetheus-fn</c>, one per failure code of a <c>type: aetheus</c> step, so the
/// agent reports the same failure whether the function ran on the host or inside the step container.
/// They are the BSD <c>sysexits</c> values, clear of the shell's own codes (126, 127), of docker's
/// (125) and of the toolchain preflight's infrastructure mismatch (86).
/// </summary>
public static class AetheusFnExitCodes
{
    public const int Success = 0;

    /// <summary><c>invalid-request</c> (EX_USAGE).</summary>
    public const int InvalidRequest = 64;

    /// <summary><c>function-not-implemented</c> (EX_UNAVAILABLE).</summary>
    public const int FunctionNotImplemented = 69;

    /// <summary><c>function-error</c> (EX_SOFTWARE).</summary>
    public const int FunctionError = 70;

    /// <summary>The exit code of a step result, from its failure code.</summary>
    public static int FromResult(ExecutorResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode == 0 && !result.TimedOut)
            return Success;
        return result.FailureCode switch
        {
            PipelineFunctionOperationExecutor.InvalidRequest => InvalidRequest,
            PipelineFunctionOperationExecutor.FunctionNotImplemented => FunctionNotImplemented,
            _ => FunctionError
        };
    }

    /// <summary>The failure code an <c>aetheus-fn</c> exit code stands for, or null for another exit
    /// code (one the shell, docker or the function's process itself produced).</summary>
    public static string? ToFailureCode(int exitCode) => exitCode switch
    {
        InvalidRequest => PipelineFunctionOperationExecutor.InvalidRequest,
        FunctionNotImplemented => PipelineFunctionOperationExecutor.FunctionNotImplemented,
        FunctionError => PipelineFunctionOperationExecutor.FunctionError,
        _ => null
    };
}
