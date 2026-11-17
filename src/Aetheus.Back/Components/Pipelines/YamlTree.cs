// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>Structural equality of two documents read by the generic YAML deserializer: maps compare
/// without regard to key order, lists in order, scalars as text.</summary>
internal static class YamlTree
{
    public static bool AreEqual(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (IDictionary<object, object?> a, IDictionary<object, object?> b) => MapsEqual(a, b),
        (IList<object?> a, IList<object?> b) => a.Count == b.Count && a.Zip(b).All(pair => AreEqual(pair.First, pair.Second)),
        (IDictionary<object, object?>, _) or (_, IDictionary<object, object?>) => false,
        (IList<object?>, _) or (_, IList<object?>) => false,
        _ => string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal)
    };

    private static bool MapsEqual(IDictionary<object, object?> a, IDictionary<object, object?> b) =>
        a.Count == b.Count && a.All(entry => b.TryGetValue(entry.Key, out var other) && AreEqual(entry.Value, other));
}
