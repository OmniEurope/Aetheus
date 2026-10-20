// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Data;
using Microsoft.AspNetCore.Mvc;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Formalizes architecture conventions as executable tests:
/// - Services must inject repositories, not <see cref="AppDbContext"/> directly.
/// - Controllers must inject services, not repositories or <see cref="AppDbContext"/>.
/// </summary>
public class NetArchTests
{
    private static readonly Assembly BackAssembly = typeof(Program).Assembly;

    /// <summary>
    /// Services that legitimately need DbContext in their constructor (e.g. dev helpers,
    /// services that ARE repositories by exception). Each must be justified.
    /// </summary>
    private static readonly HashSet<string> ServiceDbContextWhitelist = [];

    /// <summary>
    /// Non-<c>*Service</c>/<c>*Repository</c> collaborators in Components that legitimately
    /// touch <see cref="AppDbContext"/>. Each must be justified. The name-suffix guard above
    /// only catches <c>*Service</c>, so a class named <c>*Resolver</c>/<c>*Helper</c> could
    /// otherwise reach into the DbContext undetected (the PipelineServerResolver case).
    /// </summary>
    private static readonly HashSet<string> ComponentsDbContextCollaboratorWhitelist =
    [
        // This read-only PipelineRepository collaborator belongs to the repository layer despite
        // lacking the Repository suffix.
        "PipelineServerResolver",
        // Dev-only controller for test data management (also whitelisted in ControllerRepoWhitelist).
        "DevController",
    ];

    /// <summary>
    /// Controllers that legitimately inject repositories or DbContext.
    /// </summary>
    private static readonly HashSet<string> ControllerRepoWhitelist =
    [
        // Dev helper - injects repository directly for test data management
        "DevController",
    ];

    [Fact]
    public void Services_ShouldNot_Inject_DbContext_Directly()
    {
        var services = BackAssembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface
                        && t.Name.EndsWith("Service")
                        && t.Namespace?.Contains("Components") == true
                        && !ServiceDbContextWhitelist.Contains(t.Name))
            .ToList();

        Assert.True(services.Count > 10, $"Expected many service types, found {services.Count}");

        var violations = new List<string>();
        foreach (var svc in services)
        {
            var ctors = svc.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            foreach (var ctor in ctors)
            {
                foreach (var param in ctor.GetParameters())
                {
                    if (param.ParameterType == typeof(AppDbContext) ||
                        param.ParameterType.IsAssignableTo(typeof(Microsoft.EntityFrameworkCore.DbContext)))
                    {
                        violations.Add($"{svc.Name} injects {param.ParameterType.Name} via constructor");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Services must inject repositories, not DbContext directly. "
            + "Offenders (add to whitelist with justification if intentional):\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void ComponentsCollaborators_ShouldNot_Inject_DbContext_Unless_Repository()
    {
        // Broader net than the *Service-suffix rule above: ANY concrete class under Components that
        // is not a *Repository must not inject AppDbContext, so a *Resolver/*Helper/*Manager cannot
        // reach into the DbContext undetected (the gap that let PipelineServerResolver through).
        var candidates = BackAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && t.Namespace?.Contains("Components") == true
                        && !t.Name.EndsWith("Repository")
                        && !t.Name.EndsWith("Service") // covered by the dedicated service test
                        && !ComponentsDbContextCollaboratorWhitelist.Contains(t.Name)
                        && !t.Name.Contains("<")) // skip compiler-generated closure/display classes
            .ToList();

        var violations = new List<string>();
        foreach (var type in candidates)
        {
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var param in ctor.GetParameters())
                {
                    if (param.ParameterType == typeof(AppDbContext) ||
                        param.ParameterType.IsAssignableTo(typeof(Microsoft.EntityFrameworkCore.DbContext)))
                    {
                        violations.Add($"{type.Name} injects {param.ParameterType.Name} via constructor");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Only *Repository classes may inject DbContext in Components. "
            + "Offenders (rename to *Repository, route through one, or whitelist with justification):\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Controllers_ShouldNot_Inject_Repositories_Or_DbContext()
    {
        var controllers = BackAssembly.GetTypes()
            .Where(t => !t.IsAbstract
                        && typeof(ControllerBase).IsAssignableFrom(t)
                        && !ControllerRepoWhitelist.Contains(t.Name))
            .ToList();

        Assert.True(controllers.Count > 10, $"Expected many controller types, found {controllers.Count}");

        var violations = new List<string>();
        foreach (var ctrl in controllers)
        {
            var ctors = ctrl.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            foreach (var ctor in ctors)
            {
                foreach (var param in ctor.GetParameters())
                {
                    var typeName = param.ParameterType.Name;
                    if (param.ParameterType == typeof(AppDbContext) ||
                        param.ParameterType.IsAssignableTo(typeof(Microsoft.EntityFrameworkCore.DbContext)))
                    {
                        violations.Add($"{ctrl.Name} injects {typeName}");
                    }
                    else if (typeName.EndsWith("Repository") &&
                             param.ParameterType.Namespace?.Contains("Components") == true)
                    {
                        violations.Add($"{ctrl.Name} injects {typeName} - use service instead");
                    }
                }
            }

            // F-029: [FromServices] action parameters are the same reach-in as a
            // constructor injection - scan them with identical rules.
            var methods = ctrl.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                foreach (var param in method.GetParameters())
                {
                    if (param.GetCustomAttribute<FromServicesAttribute>() is null) continue;

                    var typeName = param.ParameterType.Name;
                    if (param.ParameterType == typeof(AppDbContext) ||
                        param.ParameterType.IsAssignableTo(typeof(Microsoft.EntityFrameworkCore.DbContext)))
                    {
                        violations.Add($"{ctrl.Name}.{method.Name} injects {typeName} via [FromServices]");
                    }
                    else if (typeName.EndsWith("Repository") &&
                             param.ParameterType.Namespace?.Contains("Components") == true)
                    {
                        violations.Add($"{ctrl.Name}.{method.Name} injects {typeName} via [FromServices] - use service instead");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Controllers must inject services, not repositories or DbContext. "
            + "Offenders (add to whitelist with justification if intentional):\n  "
            + string.Join("\n  ", violations));
    }
}
