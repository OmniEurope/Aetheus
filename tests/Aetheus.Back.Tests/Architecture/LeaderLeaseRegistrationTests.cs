// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>The PostgreSQL leader lease (ADR-029) is registered once, in the shared module, and every consumer
/// requires it: a host without it fails instead of running its workers without leader election.</summary>
public sealed class LeaderLeaseRegistrationTests
{
    [Fact]
    public void AWorkerWithoutALeaseRegistration_FailsDependencyInjectionValidation()
    {
        var services = WorkerServices();
        services.AddHostedService<AlertEvaluatorService>();

        var failure = Assert.Throws<AggregateException>(() =>
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }));

        Assert.Contains(failure.InnerExceptions, inner =>
            inner is InvalidOperationException && inner.Message.Contains(nameof(IPostgresLeaderLease), StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameWorkerWithALeaseRegistration_PassesDependencyInjectionValidation()
    {
        var services = WorkerServices();
        services.AddSingleton<IPostgresLeaderLease>(LeaderLeaseDouble.Leader());
        services.AddHostedService<AlertEvaluatorService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.NotNull(provider.GetRequiredService<IPostgresLeaderLease>());
    }

    [Fact]
    public void TheSharedModule_RegistersTheLeaseOnceAsTheRealImplementation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddSharedModule();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPostgresLeaderLease));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<PostgresLeaderLease>(provider.GetRequiredService<IPostgresLeaderLease>());
    }

    [Fact]
    public void EveryConsumerOfTheLeaseRequiresIt()
    {
        var nullability = new NullabilityInfoContext();
        var optional = typeof(PostgresLeaderLease).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type != typeof(PostgresLeaderLease))
            .SelectMany(type => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(ctor => ctor.GetParameters())
                .Where(parameter => parameter.ParameterType == typeof(IPostgresLeaderLease))
                .Where(parameter => parameter.HasDefaultValue
                    || nullability.Create(parameter).ReadState != NullabilityState.NotNull)
                .Select(parameter => $"{type.FullName}.{parameter.Name}"))
            .ToList();

        Assert.True(optional.Count == 0,
            "These constructors take the leader lease as an optional dependency, so a host that forgets to register it "
            + "runs them without leader election: " + string.Join(", ", optional));
    }

    private static ServiceCollection WorkerServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Substitute.For<IHubContext<AlertHub>>());
        return services;
    }
}
