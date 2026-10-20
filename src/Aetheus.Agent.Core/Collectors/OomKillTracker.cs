// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Builds the OOM report of each heartbeat (PLAN-010 lot 1). The kill counter is read every time; the
/// kernel log, which names the victims, is read once at start and then only when the counter moves,
/// so a quiet host costs two small <c>/proc</c> reads per beat.
/// </summary>
/// <param name="readVmstat">Text of <c>/proc/vmstat</c>, or null when unreadable.</param>
/// <param name="readBootId">Text of <c>/proc/sys/kernel/random/boot_id</c>, or null when unreadable.</param>
/// <param name="readKernelLog">Kernel log records, or null when the agent may not read them.</param>
public sealed class OomKillTracker(
    Func<string?> readVmstat,
    Func<string?> readBootId,
    Func<IReadOnlyList<string>?> readKernelLog)
{
    private long? _countAtLastLogRead;
    private bool _victimsReadable;
    private List<OomKillVictimDto> _victims = [];

    /// <summary>Null when the counter or the boot id cannot be read: unknown, nothing is raised.</summary>
    public OomKillReportDto? Read()
    {
        var count = OomKillParser.ParseVmstatOomKill(readVmstat());
        var bootId = readBootId()?.Trim();
        if (count is null || string.IsNullOrEmpty(bootId) || bootId.Length > 64) return null;

        if (_countAtLastLogRead != count)
            count = RefreshVictims(count.Value);

        return new OomKillReportDto
        {
            BootId = bootId,
            KillCount = count.Value,
            VictimsReadable = _victimsReadable,
            RecentVictims = [.. _victims]
        };
    }

    // A kill landing between the counter read and the log read would pair a count with the wrong last
    // victim, so the counter is read again after the log and the pair retried once when it moved.
    private long RefreshVictims(long count)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var records = readKernelLog();
            _victimsReadable = records is not null;
            _victims = records is null ? [] : OomKillParser.ParseVictims(records);
            var after = OomKillParser.ParseVmstatOomKill(readVmstat());
            if (after is null || after == count) break;
            count = after.Value;
        }
        _countAtLastLogRead = count;
        return count;
    }
}
