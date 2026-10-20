// SPDX-License-Identifier: EUPL-1.2
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization.NamingConventions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary> Names every key of a pipeline YAML that the running backend does not bind. The
/// deserializer skips such keys so that a definition written for a newer backend still runs; this
/// walk is what keeps the skip visible, in the editor's validation result and in the run's
/// warnings. </summary>
internal static class PipelineYamlDiagnostics
{
    // Derived from the definitions the deserializer binds, with its own naming convention:
    // four hand-kept lists drifted from them, and the editor announced that honoured fields such as
    // confirm_minutes, reload_helper or deployed "will be ignored". The walk follows the property types,
    // so a nested block (isolation, strategy, a step's outputs...) is checked the same way.
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Type>> KnownKeys = new();

    /// <summary>Returns the unknown-key warnings of <paramref name="yaml"/> (empty when it does not parse).</summary>
    public static List<string> UnknownPropertyWarnings(string yaml, ILogger logger)
    {
        var warnings = new List<string>();
        AppendUnknownPropertyWarnings(yaml, warnings, logger);
        return warnings;
    }

    /// <summary>
    /// Unknown keys one or two letters away from a key the same block knows (`approval_timeout_minuts`,
    /// `requries`). Those are typos, not keys of a newer backend, and skipping them would silently drop
    /// the guard they spell, so they refuse the definition (session challenge 2026-10-01).
    /// </summary>
    public static List<string> NearMissKeyErrors(string yaml, ILogger logger)
    {
        var unknown = new List<(string Key, string Context, IReadOnlyDictionary<string, Type> Known)>();
        Walk(yaml, logger, (key, context, known) => unknown.Add((key, context, known)));
        var errors = new List<string>();
        foreach (var (key, context, known) in unknown)
        {
            var closest = known.Keys.FirstOrDefault(candidate => IsNearMiss(key, candidate));
            if (closest is not null)
                errors.Add($"Unknown {context} property '{key}': did you mean '{closest}'? A misspelled key would be ignored.");
        }
        return errors;
    }

    public static void AppendUnknownPropertyWarnings(string yaml, List<string> warnings, ILogger logger) =>
        Walk(yaml, logger, (key, context, _) => warnings.Add($"Unknown {context} property '{key}' will be ignored."));

    private static void Walk(string yaml, ILogger logger, Action<string, string, IReadOnlyDictionary<string, Type>> onUnknown)
    {
        try
        {
            using var reader = new StringReader(yaml);
            var yamlStream = new YamlStream();
            yamlStream.Load(reader);
            if (yamlStream.Documents.Count == 0 || yamlStream.Documents[0].RootNode is not YamlMappingNode root)
                return;

            VisitMapping(root, typeof(PipelineYamlDefinition), "top-level", onUnknown);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidCastException or InvalidOperationException)
        {
            logger.LogDebug(ex, "YAML DOM walk for unknown-key warnings failed");
        }
    }

    private static void VisitMapping(
        YamlMappingNode node, Type type, string context, Action<string, string, IReadOnlyDictionary<string, Type>> onUnknown)
    {
        var known = KnownKeys.GetOrAdd(type, KeysOf);
        foreach (var item in node.Children)
        {
            var key = Scalar(item.Key) ?? string.Empty;
            if (!known.TryGetValue(key, out var propertyType))
            {
                onUnknown(key, context, known);
                continue;
            }
            VisitValue(item.Value, propertyType, $"{context} > {key}", onUnknown);
        }
    }

    private static void VisitValue(
        YamlNode value, Type type, string context, Action<string, string, IReadOnlyDictionary<string, Type>> onUnknown)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (value is YamlMappingNode mapping && IsDefinition(target))
        {
            VisitMapping(mapping, target, context, onUnknown);
            return;
        }
        if (value is not YamlSequenceNode sequence || ElementType(target) is not { } element)
            return;
        foreach (var child in sequence.Children.OfType<YamlMappingNode>())
            VisitMapping(child, element, ItemContext(element, child, context), onUnknown);
    }

    // The three levels people write most keep the short labels the editor has always shown.
    private static string ItemContext(Type element, YamlMappingNode item, string parentContext)
    {
        if (element == typeof(PipelineStageDefinition))
            return $"stage '{Scalar(item.Children.FirstOrDefault(k => Scalar(k.Key) == "name").Value) ?? "?"}'";
        if (element == typeof(PipelineJobDefinition)) return "job";
        if (element == typeof(PipelineStepDefinition)) return "step";
        return parentContext;
    }

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string) || typeof(IDictionary).IsAssignableFrom(type)) return null;
        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(candidate =>
                candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        var element = enumerable?.GetGenericArguments()[0];
        return element is not null && IsDefinition(element) ? element : null;
    }

    // Only the pipeline contract types are walked: a dictionary (variables, config_files) has free keys,
    // and a scalar has none.
    private static bool IsDefinition(Type type) =>
        type.IsClass && type != typeof(string)
        && !typeof(IEnumerable).IsAssignableFrom(type)
        && type.Namespace?.StartsWith("Aetheus.", StringComparison.Ordinal) == true;

    private static IReadOnlyDictionary<string, Type> KeysOf(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true })
            .ToDictionary(
                property => UnderscoredNamingConvention.Instance.Apply(property.Name),
                property => property.PropertyType,
                StringComparer.Ordinal);

    private static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value;

    // Two edits away for a key of six letters or more, one for a shorter one: close enough to be a typo
    // of that key, far enough that a genuinely new key of a newer backend is not mistaken for one.
    private static bool IsNearMiss(string key, string known)
    {
        if (key.Length < 4 || string.Equals(key, known, StringComparison.Ordinal)) return false;
        var allowed = Math.Min(key.Length, known.Length) >= 6 ? 2 : 1;
        return Math.Abs(key.Length - known.Length) <= allowed && Distance(key, known) <= allowed;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
