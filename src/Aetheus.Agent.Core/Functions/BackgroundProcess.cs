// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Functions;

/// <summary>A process a function leaves running while it works (the script's <c>cmd &gt; log 2&gt;&amp;1
/// &amp;</c>), stopped and awaited when disposed (the script's <c>kill; wait</c>).</summary>
internal interface IBackgroundProcess : IAsyncDisposable;

/// <summary>Starts a background process whose standard output and error are appended to a log
/// file.</summary>
internal delegate IBackgroundProcess BackgroundProcessLauncher(ProcessStartInfo startInfo, string logFile);

/// <inheritdoc cref="IBackgroundProcess"/>
internal sealed class BackgroundProcess : IBackgroundProcess
{
    private readonly Process _process;
    private readonly StreamWriter _log;

    private BackgroundProcess(Process process, StreamWriter log)
    {
        _process = process;
        _log = log;
    }

    /// <summary>The launcher outside tests.</summary>
    public static IBackgroundProcess Start(ProcessStartInfo startInfo, string logFile)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        var log = new StreamWriter(logFile, append: true) { AutoFlush = true };
        var process = new Process { StartInfo = startInfo };
        DataReceivedEventHandler append = (_, line) =>
        {
            if (line.Data is null)
                return;
            lock (log)
                log.WriteLine(line.Data);
        };
        process.OutputDataReceived += append;
        process.ErrorDataReceived += append;
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            log.Dispose();
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new BackgroundProcess(process, log);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Already gone: nothing to stop.
        }
        finally
        {
            _process.Dispose();
            lock (_log)
                _log.Dispose();
        }
    }
}
