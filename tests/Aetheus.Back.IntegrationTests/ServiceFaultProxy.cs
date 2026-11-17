// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// A real service with one method that calls an action first, to fail or to cancel at an exact point of a real path. Everything else,
/// that method included when the action returns, reaches the real service.
/// </summary>
public class ServiceFaultProxy : DispatchProxy
{
    private object _inner = null!;
    private string _method = string.Empty;
    private Action _before = () => { };

    /// <summary>Replaces the registration of <typeparamref name="T"/> by the same service behind the proxy, with the same lifetime.</summary>
    public static void Intercept<T>(IServiceCollection services, string method, Action before) where T : class
    {
        var original = services.Last(descriptor => descriptor.ServiceType == typeof(T));
        services.Remove(original);
        services.Add(ServiceDescriptor.Describe(typeof(T), provider => Wrap((T)Build(provider, original), method, before), original.Lifetime));
    }

    private static object Build(IServiceProvider provider, ServiceDescriptor original) =>
        original.ImplementationInstance
        ?? original.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);

    private static T Wrap<T>(T inner, string method, Action before) where T : class
    {
        var proxy = Create<T, ServiceFaultProxy>();
        var fault = (ServiceFaultProxy)(object)proxy;
        fault._inner = inner;
        fault._method = method;
        fault._before = before;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == _method) _before();
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
