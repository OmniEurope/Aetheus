// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The order of <c>left.localeCompare(right)</c> in the ported Node.js scripts, which sorted the keys
/// of every object they sealed. It is part of the contract, not a detail: the delivery contract's
/// candidate id is a digest over the JSON written in that order, and the verifier
/// (<c>verify-delivery-promotion.mjs</c>) recomputes it by sorting with <c>localeCompare</c> again, so
/// any other order would make a valid contract unverifiable.
///
/// <c>localeCompare</c> is the ICU root collation. On the printable ASCII characters it is reproduced
/// exactly: a first pass compares the characters case-insensitively in ICU's order (punctuation, then
/// symbols, then <c>$</c>, then digits, then letters), a second pass breaks ties on case, lower case
/// first, and identical strings come last. The order is built in, not read from the host, because the
/// container build of the functions runs with invariant globalization, where the culture comparers are
/// plain ordinal. Known difference: a character outside printable ASCII sorts after all of them by code
/// point, where ICU would interleave it; no key these functions seal holds one.
/// </summary>
internal sealed class ScriptCollation : IComparer<string>
{
    /// <summary>Printable ASCII in ICU root order, as Node.js 24 sorts it with <c>localeCompare</c>
    /// (upper-case letters share the weight of their lower-case letter).</summary>
    private const string AsciiOrder = " _-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$0123456789abcdefghijklmnopqrstuvwxyz";

    public static ScriptCollation Instance { get; } = new();

    private ScriptCollation()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;
        var primary = CompareSequences(x, y, Primary);
        if (primary != 0)
            return primary;
        var tertiary = CompareSequences(x, y, character => char.IsAsciiLetterUpper(character) ? 1 : 0);
        return tertiary != 0 ? tertiary : string.CompareOrdinal(x, y);
    }

    private static int Primary(char character)
    {
        var index = AsciiOrder.IndexOf(char.ToLowerInvariant(character), StringComparison.Ordinal);
        return index >= 0 && character <= '~' ? index : AsciiOrder.Length + character;
    }

    private static int CompareSequences(string x, string y, Func<char, int> weight)
    {
        var length = Math.Min(x.Length, y.Length);
        for (var index = 0; index < length; index++)
        {
            var difference = weight(x[index]).CompareTo(weight(y[index]));
            if (difference != 0)
                return difference;
        }
        return x.Length.CompareTo(y.Length);
    }
}
