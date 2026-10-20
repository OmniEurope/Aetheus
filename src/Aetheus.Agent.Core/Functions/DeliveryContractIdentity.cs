// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The sealed identity of a delivery contract, as <c>deploy/scripts/generate-delivery-contract.mjs</c>
/// seals it and <c>verify-delivery-promotion.mjs</c> recomputes it: <c>candidateId</c> is the SHA-256 of
/// <c>JSON.stringify(canonicalize(identity))</c>, the identity being the contract without
/// <c>candidateId</c> and <c>candidateVersion</c>. <c>canonicalize</c> orders object keys with
/// <see cref="JsLocaleCompare"/> (not ordinally: <c>_</c> sorts before <c>-</c>, <c>a</c> before
/// <c>B</c>) and rebuilds each object with <c>Object.fromEntries</c>, so array-index keys still come
/// first, ascending, as <see cref="JsObject"/> keeps them.
/// </summary>
internal static class DeliveryContractIdentity
{
    /// <summary><c>canonicalize(value)</c>.</summary>
    public static object? Canonicalize(object? value)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (value)
        {
            case List<object?> array:
                return array.Select(Canonicalize).ToList();
            case JsObject obj:
                var canonical = new JsObject();
                foreach (var (key, item) in JsLocaleCompare.Sort(obj.Properties, property => property.Key))
                    canonical.Set(key, Canonicalize(item));
                return canonical;
            default:
                return value;
        }
    }

    /// <summary><c>hasValidIdentity(contract)</c> for a contract object.</summary>
    public static bool HasValidIdentity(JsObject contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var identity = contract.Clone();
        identity.Remove("candidateId");
        identity.Remove("candidateVersion");
        return JsValue.StrictEquals(contract["candidateId"], Sha256(JsJson.Stringify(Canonicalize(identity))!));
    }

    /// <summary><c>sha256(value)</c> of a string: the lowercase hex digest of its UTF-8 bytes.</summary>
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
