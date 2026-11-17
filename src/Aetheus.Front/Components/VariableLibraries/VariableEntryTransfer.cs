// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
namespace Aetheus.Front.Components.VariableLibraries;

/// <summary>The JSON export and import of a library's entries: the file the user downloads, and the file
/// pasted back, read and sent to the server.</summary>
internal sealed class VariableEntryTransfer(ApiClient api, IJSRuntime js, NotifyHelper toast)
{
    public async Task ExportAsync(int libraryId, string? libraryName)
    {
        var entries = await api.Variables.ExportVariableEntriesAsync(libraryId);
        var json = JsonSerializer.Serialize(entries.Select(e => new { e.Key, e.Value }), new JsonSerializerOptions { WriteIndented = true });
        await js.InvokeVoidAsync("downloadFile", $"{libraryName ?? "entries"}.json", json, "application/json");
        toast.Success("Exported", "EntriesExported", entries.Count);
    }

    /// <summary>Sends the entries of <paramref name="content"/> to the library. True when the server imported
    /// them, so the caller reloads; an unreadable file says so and imports nothing.</summary>
    public async Task<bool> ImportAsync(int libraryId, string content)
    {
        List<CreateVariableEntryRequest>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<CreateVariableEntryRequest>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            toast.Error("Error", "InvalidJsonFormat");
            return false;
        }

        if (entries is null or { Count: 0 }) return false;

        var result = await api.Variables.ImportVariableEntriesAsync(libraryId, entries);
        if (result is null) return false;
        toast.Success("Imported", "EntriesImported", result.ImportedCount);
        return true;
    }
}
