// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.CompilerServices;
using Aetheus.Front.Tests.TestDoubles;

namespace Aetheus.Front.Tests;

/// <summary>
/// A bUnit <c>WaitFor*</c> checks its condition once, then again after every render of the component and at
/// no other time: it waits for an event, the render the test caused. Its timeout decided nothing about the
/// page; it measured how fast the machine rendered. With every test class running at once
/// (<c>-maxThreads unlimited</c>) a large page took seconds of wall time to render, and a correct page
/// failed its one-, two- or three-second wait with a check count of 0 or 1. The waits therefore have the
/// hang guard as their only bound, like every other event wait of this suite (<see cref="HangGuard"/>): a
/// page that never reaches the state still fails, with the same assertion, instead of passing or failing
/// with the load. A wait on a state that no render follows (a request count, a hub invocation, a timer) is
/// not this kind of wait and awaits its own signal instead (<see cref="StateSignal"/>).
/// </summary>
internal static class TestHostRenderWaits
{
    [ModuleInitializer]
    internal static void BoundByTheHangGuardOnly() => Bunit.BunitContext.DefaultWaitTimeout = HangGuard.Limit;
}
