// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors.MachineReboot;

/// <summary>The settings of a systemd unit file by section and key, in order. An assignment with an empty value resets the
/// key, as systemd does for list settings (<c>ExecStart=</c>). Comments start with <c>#</c> or <c>;</c>. Drop-ins are not
/// read: a setting they add is unknown to the reader, which then shows nothing rather than a guess.</summary>
internal sealed class UnitFile
{
    private readonly Dictionary<(string Section, string Key), List<string>> _values = [];

    public static UnitFile Parse(string text)
    {
        var unit = new UnitFile();
        var section = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;
            var key = (section, line[..separator].Trim());
            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0)
            {
                unit._values.Remove(key);
                continue;
            }

            if (!unit._values.TryGetValue(key, out var list)) unit._values[key] = list = [];
            list.Add(value);
        }

        return unit;
    }

    public IReadOnlyList<string> Values(string section, string key) =>
        _values.TryGetValue((section, key), out var list) ? list : [];
}
