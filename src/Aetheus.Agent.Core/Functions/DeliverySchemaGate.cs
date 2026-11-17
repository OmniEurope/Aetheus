// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The schema half of <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/verify-delivery-promotion.mjs</c>, in its order and with its
/// messages: the live production schema must be a state the candidate was proven against (before or
/// after its migrations, an empty or identical database for a bootstrap), an intermediate state of its
/// own forward migration (a migrating deploy interrupted after some of the candidate's migrations
/// committed: the live history strictly extends <c>schemaBefore</c> and is strictly extended by
/// <c>schemaAfter</c>, element by element, so the remaining migrations are the ones QA applied next), or
/// the state of the release production runs when that release's own sealed contract proves the
/// candidate is its predecessor (ADR-042). Every read of the candidate's <c>baseline</c> keeps
/// JavaScript's meaning, so a malformed baseline fails where the script threw, with V8's message.
/// </summary>
/// <param name="baseline">The candidate's <c>baseline</c>, any JSON value.</param>
/// <param name="productionSchemaPath">The migration history read from the live database, or <c>-</c>.</param>
/// <param name="liveContractPath">The delivery contract of the release production runs, or <c>-</c>.</param>
/// <param name="log">Writes one line of the script's standard output.</param>
internal sealed class DeliverySchemaGate(
    object? baseline, string productionSchemaPath, string liveContractPath, Func<string, Task> log)
{
    private const string NoDatabase = "-";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        if (JsValue.StrictEquals(JsValue.GetOptional(baseline, "bootstrap"), true))
            await VerifyBootstrapAsync(cancellationToken).ConfigureAwait(false);
        else
            await VerifyMigrationAsync(cancellationToken).ConfigureAwait(false);
    }

    // A first release has no prior state: either the database is empty, or it already carries exactly
    // what this delivery produces (a retried bootstrap).
    private async Task VerifyBootstrapAsync(CancellationToken cancellationToken)
    {
        if (productionSchemaPath == NoDatabase)
        {
            await log("Bootstrap delivery onto a host with no database yet.").ConfigureAwait(false);
            return;
        }
        var live = await ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        if (live.Migrations.Count != 0 && !JsValue.StrictEquals(live.Hash, SchemaAfterHash()))
            throw new JsError(
                "A bootstrap candidate cannot replace an existing schema.\n"
                + $"  live         : {live.Describe()}\n"
                + $"  schemaAfter  : {DeliverySchemaState.Describe(JsValue.Get(baseline, "schemaAfter"))}");
    }

    private async Task VerifyMigrationAsync(CancellationToken cancellationToken)
    {
        if (productionSchemaPath == NoDatabase)
            throw new JsError("The production schema state required to verify this candidate is missing.");
        var live = await ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        if (JsValue.StrictEquals(live.Hash, JsValue.Get(JsValue.Get(baseline, "schemaBefore"), "hash")))
        {
            await log($"Production stands on the proven pre-migration schema {live.Describe()}; migrating deploy.").ConfigureAwait(false);
            return;
        }
        if (JsValue.StrictEquals(live.Hash, SchemaAfterHash()))
        {
            await log(
                $"Production already stands on the proven post-migration schema {live.Describe()}; "
                + "the migration is a no-op and only the binaries move.").ConfigureAwait(false);
            return;
        }
        if (InterruptedForwardMigrationOf(live) is var (applied, total))
        {
            await log(
                "Production stopped partway through this candidate's own forward migration: "
                + $"{applied} of {total} migrations already applied on {live.Describe()}; "
                + $"the migrating deploy applies the remaining {total - applied}.").ConfigureAwait(false);
            return;
        }
        var (found, liveVersion) = await PreviousReleaseOfAsync(live, cancellationToken).ConfigureAwait(false);
        if (!found)
            throw new JsError(
                "The production schema is neither state this candidate was proven against.\n"
                + $"  live         : {live.Describe()}\n"
                + $"  schemaBefore : {DeliverySchemaState.Describe(JsValue.Get(baseline, "schemaBefore"))}\n"
                + $"  schemaAfter  : {DeliverySchemaState.Describe(JsValue.Get(baseline, "schemaAfter"))}\n"
                + "Requalify the candidate against the current production schema.");
        // Template literal order: the version is converted before either state is described.
        var version = JsValue.ToJsString(liveVersion);
        await log(
            $"Previously deployed release: production runs {version} on {live.Describe()}, whose QA "
            + $"exercised the release before it on that schema from {DeliverySchemaState.Describe(JsValue.Get(baseline, "schemaAfter"))}; "
            + "the migration is a no-op and only the binaries move back.").ConfigureAwait(false);
    }

    /// <summary>The interrupted forward migration of this very candidate: a migrating deploy that stopped
    /// partway leaves production on an intermediate state of the path QA walked from <c>schemaBefore</c> to
    /// <c>schemaAfter</c>. The live history must strictly extend <c>schemaBefore.migrations</c> and be
    /// strictly extended by <c>schemaAfter.migrations</c>, element by element; a list that is not an array
    /// (<c>Array.isArray</c>) proves nothing. Returns the candidate's migrations already applied and the
    /// number it carries, else <see langword="null"/>.</summary>
    private (int Applied, int Total)? InterruptedForwardMigrationOf(DeliverySchemaState live)
    {
        // Both states were read for their hash already, so neither is nullish here.
        var before = JsValue.Get(JsValue.Get(baseline, "schemaBefore"), "migrations");
        var after = JsValue.Get(JsValue.Get(baseline, "schemaAfter"), "migrations");
        if (before is not List<object?> beforeMigrations
            || after is not List<object?> afterMigrations
            || beforeMigrations.Count >= live.Migrations.Count
            || live.Migrations.Count >= afterMigrations.Count
            || beforeMigrations.Where((migration, index) => !JsValue.StrictEquals(live.Migrations[index], migration)).Any()
            || live.Migrations.Where((migration, index) => !JsValue.StrictEquals(afterMigrations[index], migration)).Any())
            return null;
        return (live.Migrations.Count - beforeMigrations.Count, afterMigrations.Count - beforeMigrations.Count);
    }

    /// <summary>The previous-release branch: the live release's version when every link holds, else not
    /// found, so that a refusal still reads as the ordinary "neither state" diagnostic.</summary>
    private async Task<(bool Found, object? Version)> PreviousReleaseOfAsync(DeliverySchemaState live, CancellationToken cancellationToken)
    {
        if (liveContractPath == NoDatabase
            || !JsValue.Truthy(JsValue.GetOptional(JsValue.GetOptional(baseline, "schemaAfter"), "hash")))
            return (false, null);
        var liveContract = JsJson.Parse(Utf8.GetString(await File.ReadAllBytesAsync(liveContractPath, cancellationToken).ConfigureAwait(false)));
        // A schema-1 live contract carries no schema states, so it proves no predecessor either way.
        if (!JsValue.StrictEquals(JsValue.Get(liveContract, "schema"), 2d))
            return (false, null);
        if (!JsValue.Truthy(JsValue.Get(liveContract, "candidateId"))
            || !DeliveryContractIdentity.HasValidIdentity((JsObject)liveContract!))
            throw new JsError("The delivery contract of the release production runs is invalid.");
        var liveBaseline = JsValue.Get(liveContract, "baseline");
        if (!JsValue.StrictEquals(JsValue.GetOptional(liveBaseline, "bootstrap"), false))
            return (false, null);
        // N's forward migration must be what separates the candidate's state from the live one...
        if (!JsValue.StrictEquals(JsValue.GetOptional(JsValue.Get(liveBaseline, "schemaBefore"), "hash"), SchemaAfterHash())
            || !JsValue.StrictEquals(JsValue.GetOptional(JsValue.Get(liveBaseline, "schemaAfter"), "hash"), live.Hash))
            return (false, null);
        // ...which also means the candidate's migrations are an exact prefix of the live history.
        if (!IsStrictPrefix(JsValue.Get(JsValue.Get(baseline, "schemaAfter"), "migrations"), live.Migrations))
            return (false, null);
        return (true, JsValue.Coalesce(JsValue.Get(liveContract, "candidateVersion"), JsValue.Get(liveContract, "candidateId")));
    }

    /// <summary><c>!(prefix.length &gt;= live.length || prefix.some((m, i) =&gt; live[i] !== m))</c>:
    /// <c>some</c> exists on arrays only, so any other prefix that is not long enough throws.</summary>
    private static bool IsStrictPrefix(object? prefix, IReadOnlyList<string> live)
    {
        if (JsValue.GreaterOrEqual(JsValue.Get(prefix, "length"), live.Count))
            return false;
        if (prefix is not List<object?> migrations)
            throw new JsTypeError("prefix.some is not a function");
        return !migrations.Where((migration, index) => !JsValue.StrictEquals(live[index], migration)).Any();
    }

    private object? SchemaAfterHash() => JsValue.Get(JsValue.Get(baseline, "schemaAfter"), "hash");

    private async Task<DeliverySchemaState> ReadLiveAsync(CancellationToken cancellationToken)
    {
        var text = Utf8.GetString(await File.ReadAllBytesAsync(productionSchemaPath, cancellationToken).ConfigureAwait(false));
        return DeliverySchemaState.Of(DeliverySchemaState.MigrationsFromHistory(text));
    }
}
