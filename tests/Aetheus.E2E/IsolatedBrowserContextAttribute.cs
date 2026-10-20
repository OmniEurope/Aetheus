// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

/// <summary>Gives one test of a context-sharing fixture its own throwaway browser context, because the
/// test changes state the next tests would inherit (for example it clears the stored session).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class IsolatedBrowserContextAttribute : PropertyAttribute
{
    internal const string PropertyName = "E2EIsolatedBrowserContext";

    public IsolatedBrowserContextAttribute() : base(PropertyName, "true") { }

    internal static bool AppliesToCurrentTest() =>
        TestContext.CurrentContext.Test.Properties.ContainsKey(PropertyName);
}
