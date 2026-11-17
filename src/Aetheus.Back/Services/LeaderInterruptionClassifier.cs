// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;
using Npgsql;

namespace Aetheus.Back.Services;

/// <summary>Why a leader worker's session ended (recette R-2, decision of 2026-10-29: a restart is never a fatal).</summary>
internal enum LeaderInterruption
{
    /// <summary>The backend is stopping: the host cancelled the worker and the connections went with it.</summary>
    HostStopping,

    /// <summary>The database server ended the session on administrator command (SQLSTATE 57P01), as a restart
    /// or a maintenance of the database does.</summary>
    DatabaseStoppedSession,

    /// <summary>The database cannot be reached (connection refused or reset, SQLSTATE 57P03): down or starting.</summary>
    DatabaseUnavailable,

    /// <summary>Anything else: a real failure of the worker or of its session.</summary>
    Failure
}

internal static class LeaderInterruptionClassifier
{
    private const string AdminShutdown = "57P01";
    private const string CannotConnectNow = "57P03";

    /// <summary>Reads <paramref name="ex"/> and its inner exceptions. Only a cause that is plainly the backend's or the
    /// database's own stop is named as such; an exception the classifier does not recognise is a
    /// <see cref="LeaderInterruption.Failure"/>, so a real failure is never taken for a restart.</summary>
    internal static LeaderInterruption Classify(Exception ex, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (stoppingToken.IsCancellationRequested)
            return LeaderInterruption.HostStopping;

        var unavailable = false;
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: AdminShutdown })
                return LeaderInterruption.DatabaseStoppedSession;
            // Npgsql reads "end of stream" when the server's end of the connection vanished without a word: a
            // container killed, or a host that went down, as the nightly reboot does (reproduced against a real
            // PostgreSQL killed with docker kill: NpgsqlException > EndOfStreamException, which was a Critical line).
            if (current is PostgresException { SqlState: CannotConnectNow }
                || current is NpgsqlException { InnerException: EndOfStreamException }
                || current is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.HostUnreachable or SocketError.TimedOut })
                unavailable = true;
        }

        return unavailable ? LeaderInterruption.DatabaseUnavailable : LeaderInterruption.Failure;
    }
}
