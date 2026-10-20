// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary> A refresh asked by a push rather than by the reader : the grid reloads in place, and
/// the load it triggers reads <see cref="Running"/> to know it must show no loader. </summary>
internal sealed class PushRefresh
{
    /// <summary>True while a pushed refresh is reloading the grid.</summary>
    public bool Running { get; private set; }

    public async Task RunAsync(Func<Task> refresh)
    {
        Running = true;
        try
        {
            await refresh();
        }
        finally
        {
            Running = false;
        }
    }
}
