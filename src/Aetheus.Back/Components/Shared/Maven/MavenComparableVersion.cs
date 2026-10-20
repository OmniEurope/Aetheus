// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Numerics;

namespace Aetheus.Back.Components.Shared.Maven;

/// <summary>
/// Maven's version ordering (<c>org.apache.maven.artifact.versioning.ComparableVersion</c>): the
/// version is split on <c>.</c>, <c>-</c> and digit/letter transitions; a <c>-</c> opens a nested list;
/// numbers compare numerically; known qualifiers order as
/// <c>alpha &lt; beta &lt; milestone &lt; rc = cr &lt; snapshot &lt; "" = ga = final = release &lt; sp</c>;
/// unknown qualifiers come after them, case-insensitively, in lexical order; trailing zero and empty
/// release items are dropped, so <c>1 = 1.0 = 1.0.0 = 1-0</c>.
/// </summary>
public sealed class MavenComparableVersion : IComparable<MavenComparableVersion>, IEquatable<MavenComparableVersion>
{
    private readonly ListItem _items;

    public MavenComparableVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        Value = version;
        _items = Parse(version.ToLowerInvariant());
    }

    public string Value { get; }

    /// <summary>Canonical form, items separated as Maven prints them; equal versions share it.</summary>
    public string Canonical => _items.ToString();

    public static int Compare(string left, string right)
        => new MavenComparableVersion(left).CompareTo(new MavenComparableVersion(right));

    /// <summary>Orders version strings the Maven way. Equal versions keep their input order.</summary>
    public static IComparer<string> Ordering { get; } =
        Comparer<string>.Create(static (left, right) => Compare(left, right));

    public int CompareTo(MavenComparableVersion? other) => other is null ? 1 : _items.CompareTo(other._items);

    public bool Equals(MavenComparableVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is MavenComparableVersion other && Equals(other);

    public override int GetHashCode() => Canonical.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;

    private static ListItem Parse(string version)
    {
        var root = new ListItem();
        var list = root;
        var stack = new Stack<ListItem>();
        stack.Push(list);
        var isDigit = false;
        var start = 0;

        for (var i = 0; i < version.Length; i++)
        {
            var c = version[i];
            if (c == '.')
            {
                list.Add(i == start ? IntItem.Zero : ParseItem(isDigit, version[start..i]));
                start = i + 1;
            }
            else if (c == '-')
            {
                list.Add(i == start ? IntItem.Zero : ParseItem(isDigit, version[start..i]));
                start = i + 1;
                var child = new ListItem();
                list.Add(child);
                list = child;
                stack.Push(list);
            }
            else if (char.IsAsciiDigit(c))
            {
                if (!isDigit && i > start)
                {
                    list.Add(new StringItem(version[start..i], followedByDigit: true));
                    start = i;
                    var child = new ListItem();
                    list.Add(child);
                    list = child;
                    stack.Push(list);
                }
                isDigit = true;
            }
            else
            {
                if (isDigit && i > start)
                {
                    list.Add(ParseItem(true, version[start..i]));
                    start = i;
                    var child = new ListItem();
                    list.Add(child);
                    list = child;
                    stack.Push(list);
                }
                isDigit = false;
            }
        }

        if (version.Length > start)
            list.Add(ParseItem(isDigit, version[start..]));

        while (stack.Count > 0)
            stack.Pop().Normalize();
        return root;
    }

    private static Item ParseItem(bool isDigit, string text)
        => isDigit
            ? new IntItem(BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture))
            : new StringItem(text, followedByDigit: false);

    private abstract class Item
    {
        public abstract bool IsNull { get; }

        /// <summary>Compares with another item, or with "nothing" when <paramref name="other"/> is null.</summary>
        public abstract int CompareTo(Item? other);
    }

    private sealed class IntItem(BigInteger value) : Item
    {
        public static readonly IntItem Zero = new(BigInteger.Zero);

        public override bool IsNull => value.IsZero;

        public override int CompareTo(Item? other) => other switch
        {
            null => value.IsZero ? 0 : 1,
            IntItem number => value.CompareTo(number.Value),
            _ => 1
        };

        public BigInteger Value => value;

        public override string ToString() => value.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class StringItem : Item
    {
        private static readonly string[] Qualifiers = ["alpha", "beta", "milestone", "rc", "snapshot", "", "sp"];
        private static readonly string ReleaseIndex = Array.IndexOf(Qualifiers, "").ToString(CultureInfo.InvariantCulture);
        private readonly string _value;

        public StringItem(string value, bool followedByDigit)
        {
            if (followedByDigit && value.Length == 1)
            {
                value = value[0] switch
                {
                    'a' => "alpha",
                    'b' => "beta",
                    'm' => "milestone",
                    _ => value
                };
            }

            _value = value switch
            {
                "ga" or "final" or "release" => "",
                "cr" => "rc",
                _ => value
            };
        }

        public override bool IsNull => string.CompareOrdinal(Comparable(_value), ReleaseIndex) == 0;

        public override int CompareTo(Item? other) => other switch
        {
            null => Math.Sign(string.CompareOrdinal(Comparable(_value), ReleaseIndex)),
            IntItem => -1,
            StringItem text => Math.Sign(string.CompareOrdinal(Comparable(_value), Comparable(text._value))),
            _ => -1
        };

        private static string Comparable(string qualifier)
        {
            var index = Array.IndexOf(Qualifiers, qualifier);
            return index == -1
                ? Qualifiers.Length.ToString(CultureInfo.InvariantCulture) + "-" + qualifier
                : index.ToString(CultureInfo.InvariantCulture);
        }

        public override string ToString() => _value;
    }

    private sealed class ListItem : Item
    {
        private readonly List<Item> _items = [];

        public void Add(Item item) => _items.Add(item);

        public override bool IsNull => _items.Count == 0;

        public void Normalize()
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                var last = _items[i];
                if (last.IsNull)
                    _items.RemoveAt(i);
                else if (last is not ListItem)
                    break;
            }
        }

        public override int CompareTo(Item? other)
        {
            switch (other)
            {
                case null:
                    return _items.Count == 0 ? 0 : _items[0].CompareTo(null);
                case IntItem:
                    return -1;
                case StringItem:
                    return 1;
            }

            var right = ((ListItem)other)._items;
            var count = Math.Max(_items.Count, right.Count);
            for (var i = 0; i < count; i++)
            {
                var l = i < _items.Count ? _items[i] : null;
                var r = i < right.Count ? right[i] : null;
                var result = l is null
                    ? r is null ? 0 : -r.CompareTo(null)
                    : l.CompareTo(r);
                if (result != 0)
                    return result;
            }
            return 0;
        }

        public override string ToString()
        {
            var builder = new System.Text.StringBuilder();
            foreach (var item in _items)
            {
                if (builder.Length > 0)
                    builder.Append(item is ListItem ? '-' : '.');
                builder.Append(item);
            }
            return builder.ToString();
        }
    }
}
