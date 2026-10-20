// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors.Ssh;

/// <summary>The settings read from an sshd configuration, and the files that could not be read.</summary>
internal sealed record SshdConfigResult(
    SshSettingDto PermitRootLogin,
    SshSettingDto PasswordAuthentication,
    SshSettingDto AuthorizedKeysFile,
    List<string> UnreadableFiles);

/// <summary>
/// Reads the global value of the watched sshd keywords the way OpenSSH does, without running
/// <c>sshd -T</c> (which needs root: it loads the host keys and exits with "no hostkeys available"
/// for any other user).
/// <list type="bullet">
///   <item>The first value of a keyword wins; later ones are ignored.</item>
///   <item><c>Include</c> is expanded in place, a relative path taken from <c>/etc/ssh</c>, glob
///   matches in lexical order; a glob that matches nothing is not an error.</item>
///   <item>A <c>Match</c> block lasts until the next <c>Match</c> or the end of the file that holds
///   it; what it sets is not the global value, only flagged as <see
///   cref="SshSettingDto.OverriddenInMatch"/>. <c>Match all</c> returns to the global section.</item>
///   <item>A file read before a keyword's first value that could not be read makes the value unknown
///   (null): it might have set it. A missing value is the OpenSSH default only when every file was read.</item>
/// </list>
/// </summary>
internal sealed class SshdConfigParser(ISshHostFiles files)
{
    internal const string MainConfigPath = "/etc/ssh/sshd_config";
    private const string ConfigDirectory = "/etc/ssh";
    private const int MaxIncludeDepth = 16;

    private static readonly (string Keyword, string Default)[] Watched =
    [
        ("permitrootlogin", "prohibit-password"),
        ("passwordauthentication", "yes"),
        ("authorizedkeysfile", ".ssh/authorized_keys .ssh/authorized_keys2"),
    ];

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly List<string> _unreadable = [];
    private bool _opaqueSoFar;

    public SshdConfigResult Parse(string mainPath = MainConfigPath)
    {
        _slots.Clear();
        _unreadable.Clear();
        _opaqueSoFar = false;
        foreach (var (keyword, _) in Watched) _slots[keyword] = new Slot();

        ParseFile(mainPath, inMatch: false, depth: 0);

        return new SshdConfigResult(
            Resolve("permitrootlogin"),
            Resolve("passwordauthentication"),
            Resolve("authorizedkeysfile"),
            [.. _unreadable]);
    }

    private SshSettingDto Resolve(string keyword)
    {
        var slot = _slots[keyword];
        if (slot.Resolved)
            return new SshSettingDto { Value = slot.Value, Source = slot.Source, OverriddenInMatch = slot.InMatch };
        // Never set: the default holds only if no unread file could have set it.
        return _opaqueSoFar
            ? new SshSettingDto { OverriddenInMatch = slot.InMatch }
            : new SshSettingDto
            {
                Value = Watched.First(w => w.Keyword == keyword).Default,
                Source = "default",
                OverriddenInMatch = slot.InMatch
            };
    }

    private void ParseFile(string path, bool inMatch, int depth)
    {
        var read = files.Read(path);
        if (read.State != AuthorizedKeysFileState.Read)
        {
            // A missing main file is unreadable for our purpose too: nothing says what sshd uses.
            MarkOpaque(path, inMatch);
            return;
        }

        var includedFromMatch = inMatch;
        var lineNumber = 0;
        foreach (var rawLine in read.Content!.Split('\n'))
        {
            lineNumber++;
            var tokens = Tokenize(rawLine);
            if (tokens.Count == 0) continue;
            var keyword = tokens[0].ToLowerInvariant();
            var args = tokens.Skip(1).ToList();

            switch (keyword)
            {
                case "match":
                    // A file included from inside a Match block stays conditional whatever it says.
                    inMatch = includedFromMatch
                        || !(args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase));
                    break;
                case "include":
                    foreach (var pattern in args) Include(pattern, inMatch, depth);
                    break;
                default:
                    Record(keyword, args, $"{path}:{lineNumber}", inMatch);
                    break;
            }
        }
    }

    private void Include(string pattern, bool inMatch, int depth)
    {
        if (depth >= MaxIncludeDepth)
        {
            MarkOpaque(pattern, inMatch);
            return;
        }
        var resolved = pattern.StartsWith('/') ? pattern : $"{ConfigDirectory}/{pattern}";
        var slash = resolved.LastIndexOf('/');
        var directory = slash <= 0 ? "/" : resolved[..slash];
        var namePattern = resolved[(slash + 1)..];
        if (!HasWildcard(namePattern) && !HasWildcard(directory))
        {
            // A literal include that does not exist is skipped by OpenSSH like an empty glob.
            if (files.Read(resolved).State == AuthorizedKeysFileState.Absent) return;
            ParseFile(resolved, inMatch, depth + 1);
            return;
        }
        if (HasWildcard(directory))
        {
            // Wildcards in a directory component are not expanded here: say so rather than guess.
            MarkOpaque(resolved, inMatch);
            return;
        }

        var listed = files.List(directory);
        if (listed is null)
        {
            MarkOpaque(resolved, inMatch);
            return;
        }
        var regex = GlobToRegex(namePattern);
        foreach (var file in listed.Where(f => regex.IsMatch(Path.GetFileName(f))))
            ParseFile(file, inMatch, depth + 1);
    }

    private void Record(string keyword, List<string> args, string source, bool inMatch)
    {
        if (!_slots.TryGetValue(keyword, out var slot) || args.Count == 0) return;
        if (inMatch)
        {
            slot.InMatch = true;
            return;
        }
        if (slot.Resolved) return; // first value wins
        slot.Resolved = true;
        if (_opaqueSoFar)
            return; // an unread file came first and may hold the real value: unknown
        slot.Value = Normalize(keyword, args);
        slot.Source = source;
    }

    private void MarkOpaque(string path, bool inMatch)
    {
        _unreadable.Add(path);
        // Content hidden inside a Match block cannot change a global value, only a conditional one.
        if (inMatch)
        {
            foreach (var slot in _slots.Values) slot.InMatch = true;
            return;
        }
        _opaqueSoFar = true;
    }

    private static string Normalize(string keyword, List<string> args)
    {
        if (keyword == "authorizedkeysfile") return string.Join(' ', args);
        var value = args[0].ToLowerInvariant();
        // OpenSSH's own synonym: sshd -T prints without-password for prohibit-password.
        return keyword == "permitrootlogin" && value == "without-password" ? "prohibit-password" : value;
    }

    /// <summary>Splits a line as OpenSSH does: whitespace or one '=' between keyword and value,
    /// double quotes group, a token starting with '#' ends the line.</summary>
    internal static List<string> Tokenize(string line)
    {
        var tokens = SplitQuoted(line.TrimEnd('\r').Trim());
        if (tokens.Count == 0) return tokens;
        // One '=' may stand between the keyword and its first value, glued or spaced.
        var equals = tokens[0].IndexOf('=', StringComparison.Ordinal);
        if (equals >= 0)
        {
            var value = tokens[0][(equals + 1)..];
            tokens[0] = tokens[0][..equals];
            if (value.Length > 0) tokens.Insert(1, value);
        }
        else if (tokens.Count > 1 && tokens[1].StartsWith('='))
        {
            tokens[1] = tokens[1][1..];
            if (tokens[1].Length == 0) tokens.RemoveAt(1);
        }
        return tokens;
    }

    /// <summary>Whitespace-separated tokens, double quotes grouping; stops at a token that starts with '#'.</summary>
    private static List<string> SplitQuoted(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var inToken = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                inToken = true;
            }
            else if (quoted || !char.IsWhiteSpace(c))
            {
                if (c == '#' && !inToken) break;
                current.Append(c);
                inToken = true;
            }
            else if (inToken)
            {
                tokens.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
        }
        if (inToken) tokens.Add(current.ToString());
        return tokens;
    }

    private static bool HasWildcard(string value) => value.IndexOfAny(['*', '?', '[']) >= 0;

    private static Regex GlobToRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*': pattern.Append("[^/]*"); break;
                case '?': pattern.Append("[^/]"); break;
                case '[':
                    var close = glob.IndexOf(']', i + 1);
                    if (close < 0) { pattern.Append(@"\["); break; }
                    var set = glob[(i + 1)..close];
                    if (set.StartsWith('!')) set = "^" + set[1..];
                    pattern.Append('[').Append(set.Replace(@"\", @"\\")).Append(']');
                    i = close;
                    break;
                default: pattern.Append(Regex.Escape(c.ToString())); break;
            }
        }
        // Like glob(3), a leading dot is only matched by a pattern that starts with one.
        var regex = pattern.Append('$').ToString();
        if (!glob.StartsWith('.')) regex = "^(?!\\.)" + regex[1..];
        return new Regex(regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private sealed class Slot
    {
        public bool Resolved { get; set; }
        public string? Value { get; set; }
        public string? Source { get; set; }
        public bool InMatch { get; set; }
    }
}
