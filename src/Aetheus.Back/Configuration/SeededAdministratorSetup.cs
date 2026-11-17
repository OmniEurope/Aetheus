// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Configuration;

/// <summary>R-1 (owner decision 2026-10-29): whether the administrator a fresh install seeds must first be turned
/// into its holder's own account (<see cref="Entities.User.MustSetUpAccount"/>). Required by default. Two seeds
/// opt out: <c>Auth:AdministratorSetupRequired=false</c>, set by the local development configuration whose
/// seeded account every test and QA stack signs in with, and the public demo, whose administrator credential is
/// published on purpose: arming it there would hand the demo to its first visitor.</summary>
internal static class SeededAdministratorSetup
{
    internal const string ConfigurationKey = "Auth:AdministratorSetupRequired";

    internal static bool IsRequired(IConfiguration? config) =>
        !PublicDemoConfiguration.IsEnabled(config)
        && (config is null || config.GetValue(ConfigurationKey, true));
}
