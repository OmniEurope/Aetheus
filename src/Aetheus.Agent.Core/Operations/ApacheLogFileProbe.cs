// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>What opening a log file for reading tells the agent (recette R-19).</summary>
internal enum LogFileAccess
{
    Readable,
    Missing,
    Denied,
    Unreadable
}

/// <summary>Recette R-19: <see cref="File.Exists"/> answers false both for a missing file and for a file behind a
/// directory the agent account may not traverse (Debian's <c>/var/log/apache2</c> is <c>root:adm 0750</c>), so the
/// log tab said "no log" on a host whose log was there. The file is opened instead, and the error says which.</summary>
internal static class ApacheLogFileProbe
{
    internal static LogFileAccess Probe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return LogFileAccess.Readable;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LogFileAccess.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return LogFileAccess.Denied;
        }
        catch (IOException)
        {
            return LogFileAccess.Unreadable;
        }
    }

    /// <summary>The first readable candidate, in order; otherwise why none can be shown: the first candidate that
    /// exists but cannot be read, or "not found" with every candidate when none exists.</summary>
    internal static ApacheLogResolution Resolve(IReadOnlyList<string> candidates, Func<string, LogFileAccess> probe)
    {
        var probes = candidates.Select(path => (Path: path, Access: probe(path))).ToList();
        var readable = probes.FirstOrDefault(p => p.Access == LogFileAccess.Readable);
        if (readable.Path is not null) return new ApacheLogResolution(readable.Path, null, [readable.Path]);

        var blocked = probes.FirstOrDefault(p => p.Access != LogFileAccess.Missing);
        if (blocked.Path is null) return new ApacheLogResolution(null, ApacheLogReadOutcome.NotFound, candidates);

        var outcome = blocked.Access == LogFileAccess.Denied ? ApacheLogReadOutcome.AccessDenied : ApacheLogReadOutcome.ReadError;
        return new ApacheLogResolution(null, outcome, [blocked.Path]);
    }
}

/// <summary>The log to tail, or the outcome and paths of the <see cref="ApacheLogDiagnostic"/> line to write instead.</summary>
internal readonly record struct ApacheLogResolution(string? ReadablePath, ApacheLogReadOutcome? Failure, IReadOnlyList<string> Paths);
