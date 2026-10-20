// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// <c>String.prototype.localeCompare</c> without arguments, as V8 computes it through ICU with the
/// default collation (CLDR root, which <c>en</c> and <c>fr</c> keep unchanged for ASCII), for the
/// ported Node programs that sort object keys or identifiers with it before hashing or writing them.
/// It never depends on .NET culture data, which the function host may run without (globalization
/// invariant mode).
///
/// Only ASCII is reproduced. The control characters other than tab, line feed, vertical tab, form feed
/// and carriage return are completely ignorable; every other character carries one primary weight, in
/// this order: <c>\t \n \v \f \r</c>, space, <c>_ - , ; : ! ? . ' " ( ) [ ] { } @ * / \ &amp; # % ` ^ + &lt; = &gt; | ~ $</c>,
/// the digits, then the letters case-insensitively. Two strings are compared on their primary weights
/// first (a proper prefix sorts first), then, when those are equal, on case at the first letter that
/// differs only by case, lowercase first. Strings equal at both levels compare as 0 (for instance
/// <c>"a\u0001"</c> and <c>"a"</c>). The proof is <c>TestData/js-locale-compare.json</c>, written by node.
///
/// A string holding a character above U+007F is refused with <see cref="NotSupportedException"/>:
/// guessing its weight could order keys differently from the script, and a seal recomputed over a
/// different order would accept or refuse the wrong contract.
/// </summary>
internal static class JsLocaleCompare
{
    private const string PrimaryOrder = "\t\n\v\f\r _-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$0123456789abcdefghijklmnopqrstuvwxyz";
    private const int Ignorable = -1;
    private static readonly int[] Primary = BuildPrimaryWeights();

    /// <summary><c>left.localeCompare(right)</c>: -1, 0 or 1.</summary>
    public static int Compare(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        EnsureSupported(left);
        EnsureSupported(right);
        var primary = CompareLevel(left, right, static character => Primary[character]);
        return primary != 0 ? primary : CompareLevel(left, right, static character => char.IsAsciiLetterUpper(character) ? 1 : 0);
    }

    /// <summary><c>array.sort((a, b) =&gt; key(a).localeCompare(key(b)))</c>: a stable sort, as
    /// <c>Array.prototype.sort</c> is, so items whose keys compare equal keep their order. Every key is
    /// checked before sorting, so the refusal does not depend on which pairs the algorithm compares.</summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, string> key)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(key);
        var list = items.ToList();
        if (list.Count < 2)
            return list;
        foreach (var item in list)
            EnsureSupported(key(item));
        return [.. list.OrderBy(key, Comparer<string>.Create(Compare))];
    }

    private static void EnsureSupported(string text)
    {
        foreach (var character in text)
        {
            if (character > '\u007f')
                throw new NotSupportedException(
                    $"localeCompare is only reproduced for ASCII text, and '{text}' holds U+{(int)character:X4}; refusing rather than guessing its collation order.");
        }
    }

    /// <summary>Compares the weights of one level, ignorable characters skipped.</summary>
    private static int CompareLevel(string left, string right, Func<char, int> weight)
    {
        var (i, j) = (0, 0);
        while (true)
        {
            i = NextSignificant(left, i);
            j = NextSignificant(right, j);
            if (i == left.Length || j == right.Length)
                return (i == left.Length ? 0 : 1) - (j == right.Length ? 0 : 1);
            var difference = weight(left[i]).CompareTo(weight(right[j]));
            if (difference != 0)
                return Math.Sign(difference);
            i++;
            j++;
        }
    }

    private static int NextSignificant(string text, int index)
    {
        while (index < text.Length && Primary[text[index]] == Ignorable)
            index++;
        return index;
    }

    private static int[] BuildPrimaryWeights()
    {
        var weights = new int[128];
        Array.Fill(weights, Ignorable);
        for (var rank = 0; rank < PrimaryOrder.Length; rank++)
            weights[PrimaryOrder[rank]] = rank;
        for (var letter = 'A'; letter <= 'Z'; letter++)
            weights[letter] = weights[char.ToLowerInvariant(letter)];
        return weights;
    }
}
