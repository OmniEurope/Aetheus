// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// Waits for a state that a test double changes, by being told of every change instead of reading the
/// clock: the double calls <see cref="Pulse"/> after each change and <see cref="WhenAsync"/> checks its
/// condition again on each one. The wait ends when the state is reached, however loaded the machine is:
/// a polling loop with a three-second deadline failed whenever the continuations it waited for queued
/// behind a few hundred parallel tests, although the product did exactly what was expected.
/// </summary>
internal sealed class StateSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewChange();

    /// <summary>Tells every pending <see cref="WhenAsync"/> that the state may have changed.</summary>
    public void Pulse()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _changed;
            _changed = NewChange();
        }

        previous.TrySetResult();
    }

    /// <summary>Completes once <paramref name="condition"/> holds; <paramref name="what"/> names the state
    /// when the hang guard ends a wait whose state never comes.</summary>
    public async Task WhenAsync(Func<bool> condition, string what)
    {
        using var guard = HangGuard.Start();
        while (true)
        {
            // The change is taken before the condition is read: a change made in between still wakes us.
            Task changed;
            lock (_gate) changed = _changed.Task;
            if (condition()) return;
            await HangGuard.AwaitAsync(changed, guard, what);
        }
    }

    private static TaskCompletionSource NewChange() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Ends a wait for an event that never comes, so a broken product fails its test instead of freezing the
/// whole run. It is not a performance bound: every wait it guards is for an event the code under test
/// signals, never for time to pass, so on a working product it never fires, whatever the machine load.
/// </summary>
internal static class HangGuard
{
    /// <summary>Far above any stall measured on a loaded run (the worst seen held a continuation 100 s).</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    public static CancellationTokenSource Start()
    {
        var guard = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        guard.CancelAfter(Limit);
        return guard;
    }

    /// <summary>Awaits <paramref name="task"/>; a hang becomes a failure naming <paramref name="what"/>.</summary>
    public static async Task AwaitAsync(Task task, CancellationTokenSource guard, string what)
    {
        try
        {
            await task.WaitAsync(guard.Token);
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested
            && !Xunit.TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Never happened: {what}.");
        }
    }

    /// <summary>Awaits <paramref name="task"/> under its own guard.</summary>
    public static async Task AwaitAsync(Task task, string what)
    {
        using var guard = Start();
        await AwaitAsync(task, guard, what);
    }

    /// <summary>
    /// Waits for the rendered output of <paramref name="cut"/> to satisfy <paramref name="assertion"/>. bUnit
    /// checks it once, then after every render of the component and at no other time, so this is a wait
    /// for an event (the render the test caused), and the hang guard is its only bound. The assertion must
    /// read the rendered output only: a state a render does not follow (a request count, a hub
    /// invocation) is awaited on its own signal instead.
    /// </summary>
    public static Task WhenRenderedAsync<TComponent>(this Bunit.IRenderedComponent<TComponent> cut, Action assertion)
        where TComponent : Microsoft.AspNetCore.Components.IComponent =>
        Bunit.RenderedComponentWaitForHelperExtensions.WaitForAssertionAsync(cut, assertion, Limit);

    /// <summary>The same wait as <see cref="WhenRenderedAsync{TComponent}(Bunit.IRenderedComponent{TComponent}, Action)"/>
    /// for a condition on what the component rendered (its markup or a child's parameters).</summary>
    public static Task WhenRenderedAsync<TComponent>(this Bunit.IRenderedComponent<TComponent> cut, Func<bool> rendered)
        where TComponent : Microsoft.AspNetCore.Components.IComponent =>
        Bunit.RenderedComponentWaitForHelperExtensions.WaitForStateAsync(cut, rendered, Limit);

    /// <summary>Awaits <paramref name="task"/> under its own guard and returns its result.</summary>
    public static async Task<T> AwaitAsync<T>(Task<T> task, string what)
    {
        await AwaitAsync((Task)task, what);
        return await task;
    }
}
