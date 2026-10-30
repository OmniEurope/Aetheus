// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Tests.Architecture;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// A deployment to an EMPTY environment is the one case the self-deploy path had never taken, and it
/// could not have succeeded. Reproduced end to end against a local agent: every stage passed and only
/// the blocking Evidence gate failed, on two compounding causes.
///
///   The smoke demanded business data. A freshly deployed control plane runs with
///   ASPNETCORE_ENVIRONMENT=Production, so neither Seed__Demo nor Seed__ConformanceToto applies and it
///   holds no projects. Asserting projectCount > 0 made "the environment is new" indistinguishable
///   from "the deployment is broken".
///
///   The instance was then unreachable. The run-scoped identity travelled as ADMIN_USER/ADMIN_PASSWORD,
///   and DbInitializer hashes Auth:AdminPassword into the persisted `admin` on a first run - so the
///   environment got a permanent administrator whose password was that run's throwaway secret, which
///   the run masks out of its own log. Observed on the deployed container:
///   Auth__AdminUser=deploy-smoke-7d1c1f5bd9ebf49a, expiring ten minutes after the cutover.
///
/// These read the shipped files, because both causes live in wiring rather than in code paths a unit
/// test reaches, and nothing else exercises the first-deployment path.
/// </summary>
public sealed class FirstDeploymentContractTests
{
    private static string ComposeFile =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "deploy", "compose", "remote-bluegreen.compose.yml"));
    [Fact]
    public void PersistentAdministrator_ComesFromTheEnvironmentFileAndNotFromTheRun()
    {
        var compose = ComposeFile;

        // DbInitializer seeds the persisted `admin` from this value on a first run, so it has to be
        // the durable secret the environment file holds.
        Assert.Contains("Auth__AdminPassword=${ADMIN_PASSWORD}", compose, StringComparison.Ordinal);
        Assert.Contains("Auth__AdminEmail=${ADMIN_EMAIL:-}", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentIdentity_TravelsUnderItsOwnNames()
    {
        var compose = ComposeFile;

        Assert.Contains("Auth__DeploymentBootstrapUser=${DEPLOYMENT_BOOTSTRAP_USER", compose, StringComparison.Ordinal);
        Assert.Contains("Auth__DeploymentBootstrapPassword=${DEPLOYMENT_BOOTSTRAP_PASSWORD", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentIdentity_IsNeverInterpolatedIntoTheAdministratorPair()
    {
        var compose = ComposeFile;

        // The exact shape of the regression: the run-scoped names feeding the admin settings.
        Assert.DoesNotContain("Auth__AdminPassword=${DEPLOYMENT_BOOTSTRAP", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Auth__AdminEmail=${DEPLOYMENT_BOOTSTRAP", compose, StringComparison.Ordinal);
    }
}
