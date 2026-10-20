// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.AiTasks;

/// <summary>
/// Recette R3-17: a run's verdict read as words and a colour. "None" was shown raw; it means the task
/// asked for no verdict, so it reads "No verdict" in neutral.
/// </summary>
internal static class AiVerdictText
{
    public static string Label(IStringLocalizer localizer, AiVerdict verdict) => verdict switch
    {
        AiVerdict.Pass => localizer["AiVerdictPass"],
        AiVerdict.Fail => localizer["AiVerdictFail"],
        _ => localizer["AiVerdictNone"]
    };

    public static OmniTone Tone(AiVerdict verdict) => verdict switch
    {
        AiVerdict.Pass => OmniTone.Success,
        AiVerdict.Fail => OmniTone.Danger,
        _ => OmniTone.Neutral
    };
}
