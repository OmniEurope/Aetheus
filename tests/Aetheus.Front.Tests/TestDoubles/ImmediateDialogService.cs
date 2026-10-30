// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.TestDoubles;

internal sealed class ImmediateDialogService(AppDialogs dialogs, OmniOverlayService overlay) : OmniDialogService(dialogs, overlay)
{
    public int OpenCount { get; private set; }
    public Type? LastComponent { get; private set; }
    public string? LastTitle { get; private set; }
    public Dictionary<string, object?>? LastParameters { get; private set; }
    public object? OpenResult { get; set; }

    /// <summary>Every dialog component opened, in order, for a flow that opens several in a row.</summary>
    public List<Type> OpenedComponents { get; } = [];

    /// <summary>The parameters each dialog in <see cref="OpenedComponents"/> was opened with.</summary>
    public List<Dictionary<string, object?>?> OpenedParameterSets { get; } = [];

    /// <summary>When set, the answer of each opened dialog by its component, instead of <see cref="OpenResult"/>.</summary>
    public Func<Type, object?>? OpenResultFor { get; set; }
    public bool? ConfirmResult { get; set; } = false;
    public string? LastConfirmMessage { get; private set; }
    public OmniConfirmOptions? LastConfirmOptions { get; private set; }
    public int CloseCount { get; private set; }
    public object? LastCloseResult { get; private set; }

    public override void Close(object? result = null)
    {
        CloseCount++;
        LastCloseResult = result;
    }

    public override Task<object?> OpenAsync<T>(string title, Dictionary<string, object?>? parameters = null, OmniDialogOptions? options = null)
    {
        OpenCount++;
        LastComponent = typeof(T);
        LastTitle = title;
        LastParameters = parameters;
        OpenedComponents.Add(typeof(T));
        OpenedParameterSets.Add(parameters);
        return Task.FromResult(OpenResultFor is null ? OpenResult : OpenResultFor(typeof(T)));
    }

    public override Task<bool?> Confirm(string? message, string? title, OmniConfirmOptions? options = null)
    {
        OpenCount++;
        LastTitle = title;
        LastConfirmMessage = message;
        LastConfirmOptions = options;
        return Task.FromResult(ConfirmResult);
    }
}
