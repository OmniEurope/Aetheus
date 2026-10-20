// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

/// <summary>Marks a test that proves the previous release (V-1) still works on the current release's
/// (V) database schema: a write path that would break on a renamed or constrained column, or a read
/// sweep over the pages that load the most of the schema. A V-1 gate may run only this subset with
/// <c>--filter "Category=RollbackRelevant"</c>.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RollbackRelevantAttribute : CategoryAttribute
{
    public const string CategoryName = "RollbackRelevant";

    public RollbackRelevantAttribute() : base(CategoryName) { }
}
