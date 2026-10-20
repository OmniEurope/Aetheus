// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Aetheus.Front.Tests;

/// <summary>
/// xUnit runs the tests on thread-pool threads, and every synchronous bUnit <c>WaitFor*</c> blocks its thread
/// until the state arrives through continuations that need a thread of the same pool (the mocked HTTP answer,
/// the renderer's queued work, a timer). When more tests are blocked at once than the pool has threads, none
/// of those continuations can run, and the pool adds a thread only every half second or so: dumps of loaded
/// runs showed every worker running, none idle, 26 of them inside a <c>WaitFor</c>, and tests stalled
/// together for 22 to 53 seconds, so a correct page failed its wait with a check count of 0 (B7bis).
/// <para>
/// How many tests can be blocked at once is set by how many run at once: one per CPU thread by default,
/// but one per test class (each is its own collection) under <c>-maxThreads unlimited</c>. The pool therefore
/// starts with a thread for every test class, plus room for their continuations on every CPU thread, so a
/// blocked wait never holds back the continuation it waits for. A blocked thread costs no CPU; it only needs
/// to exist.
/// </para>
/// </summary>
internal static class TestHostThreadPool
{
    [ModuleInitializer]
    internal static void Widen()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        var wanted = TestClassCount() + Environment.ProcessorCount * 4;
        if (workers < wanted)
            ThreadPool.SetMinThreads(wanted, completionPorts);
    }

    private static int TestClassCount() =>
        typeof(TestHostThreadPool).Assembly.GetTypes().Count(type => type is { IsClass: true, IsAbstract: false }
            && type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                .Any(method => method.IsDefined(typeof(FactAttribute), inherit: true)));
}
