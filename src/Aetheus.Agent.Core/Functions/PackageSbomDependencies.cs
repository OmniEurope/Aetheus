// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>A dependency as <c>generate-package-sbom.mjs</c> holds it: a name and a version, each the
/// JavaScript value the input gave (the NuGet version may be any truthy JSON value).</summary>
internal sealed record PackageSbomDependency(object? Name, object? Version);

/// <summary>
/// <c>readNuGetPackages</c> and <c>readNpmPackages</c> of the former <c>generate-package-sbom.mjs</c> script
/// over the input parsed by <see cref="JsJson"/>, with JavaScript's iteration, spread and conversion
/// semantics: a string iterates as its code points (none of which carries a package), a value that is
/// not iterable throws V8's <see cref="JsTypeError"/>, a later NuGet duplicate (same lowercased id and
/// version) replaces the value but keeps the place of the first (<c>Map</c>), and the npm maps merge
/// like an object spread.
/// </summary>
internal static class PackageSbomDependencies
{
    private const string NotIterableSuffix = " is not iterable (cannot read property Symbol(Symbol.iterator))";

    /// <summary><c>readNuGetPackages(value)</c>.</summary>
    public static List<PackageSbomDependency> ReadNuGet(object? value)
    {
        var keys = new List<string>();
        var packages = new Dictionary<string, PackageSbomDependency>(StringComparer.Ordinal);
        foreach (var project in ForOf(JsValue.Coalesce(JsValue.Get(value, "projects"), new List<object?>())))
        {
            foreach (var framework in ForOf(JsValue.Coalesce(JsValue.Get(project, "frameworks"), new List<object?>())))
            {
                var items = Spread(JsValue.Coalesce(JsValue.Get(framework, "topLevelPackages"), new List<object?>()), "framework.topLevelPackages")
                    .Concat(Spread(JsValue.Coalesce(JsValue.Get(framework, "transitivePackages"), new List<object?>()), "framework.transitivePackages"))
                    .ToList();
                foreach (var item in items)
                    AddNuGet(item, keys, packages);
            }
        }
        return keys.Select(key => packages[key]).ToList();
    }

    private static void AddNuGet(object? item, List<string> keys, Dictionary<string, PackageSbomDependency> packages)
    {
        var version = JsValue.Coalesce(JsValue.Get(item, "resolvedVersion"), JsValue.Get(item, "requestedVersion"));
        var id = JsValue.Get(item, "id");
        if (!JsValue.Truthy(id) || !JsValue.Truthy(version))
            return;
        if (id is not string text)
            throw new JsTypeError("item.id.toLowerCase is not a function");
        var key = $"{LowerCase(text)}@{JsValue.ToJsString(version)}";
        if (!packages.ContainsKey(key))
            keys.Add(key);
        packages[key] = new PackageSbomDependency(id, version);
    }

    /// <summary><c>String.prototype.toLowerCase</c> for ASCII letters and lone surrogates (which it keeps).
    /// Beyond, JavaScript applies the full Unicode mappings (U+0130 to two characters, a final sigma by
    /// context) that .NET's simple mapping does not reproduce, so such an id is refused rather than
    /// deduplicated differently.</summary>
    private static string LowerCase(string text) => HasOnlyAsciiOrLoneSurrogates(text)
        ? JsValue.AsciiLower(text)
        : throw new NotSupportedException(
            $"The package id '{text}' is not ASCII; its JavaScript lowercase, which deduplicates NuGet packages, is not reproduced.");

    private static bool HasOnlyAsciiOrLoneSurrogates(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var pair = char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
            if (pair || !(char.IsAscii(character) || char.IsSurrogate(character)))
                return false;
        }
        return true;
    }

    /// <summary><c>readNpmPackages(value)</c>: <c>{ ...dependencies, ...optionalDependencies,
    /// ...peerDependencies }</c>, each version converted with <c>String()</c> and stripped of one leading
    /// <c>^</c> or <c>~</c>.</summary>
    public static List<PackageSbomDependency> ReadNpm(object? value)
    {
        var merged = new JsObject();
        foreach (var name in (string[])["dependencies", "optionalDependencies", "peerDependencies"])
        {
            var source = JsValue.Get(value, name);
            if (JsValue.IsNullish(source))
                continue;
            foreach (var (key, item) in JsValue.Entries(source))
                merged.Set(key, item);
        }
        return merged.Properties
            .Select(entry => new PackageSbomDependency(entry.Key, StripRange(JsValue.ToJsString(entry.Value))))
            .ToList();
    }

    private static string StripRange(string version) => version.Length > 0 && version[0] is '^' or '~' ? version[1..] : version;

    /// <summary><c>for (const x of value)</c>: the elements of an array, the code points of a string.</summary>
    private static IEnumerable<object?> ForOf(object? value) => Iterate(value) ?? throw new JsTypeError(Describe(value) + NotIterableSuffix);

    /// <summary><c>...(expression)</c> in an array literal.</summary>
    private static IEnumerable<object?> Spread(object? value, string expression) =>
        Iterate(value) ?? throw new JsTypeError($"({expression} ?? []) is not iterable");

    private static List<object?>? Iterate(object? value) => value switch
    {
        List<object?> array => array,
        string text => text.EnumerateRunes().Select(rune => (object?)rune.ToString()).ToList(),
        _ => null
    };

    /// <summary>How V8 names a non-iterable value in a <c>for...of</c>.</summary>
    private static string Describe(object? value) => value switch
    {
        double number => "number " + JsValue.FormatNumber(number),
        bool flag => flag ? "boolean true" : "boolean false",
        _ => "object"
    };
}
