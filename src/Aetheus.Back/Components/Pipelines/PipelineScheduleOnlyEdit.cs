// SPDX-License-Identifier: EUPL-1.2
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Tells whether a new pipeline definition differs from the old one by nothing but the value of its
/// top-level <c>schedule:</c> line and its <c>trigger:</c> line. It decides who owns the pipeline, and
/// scheduled runs act with the owner's rights (PLAN-005, question 1): moving a schedule keeps the owner,
/// any other edit moves it to the editor. So the answer is yes only when it is proved, and every doubt
/// is a no, which is what happened before this decision:
/// <list type="bullet">
/// <item>both texts are one document of one mapping, without a repeated key at any depth;</item>
/// <item>every other line is the same, to the byte, in the same order, line ending included;</item>
/// <item>the two lines hold a scalar on that line alone (no anchor, tag, block, flow or open quote, which
/// could reach the lines after them), and keep the comment they had; an added line has none;</item>
/// <item>the parsed documents are equal without these two keys: the parser's own reading of the same
/// fact, so a line that does not mean what it reads is not trusted to the text comparison alone;</item>
/// <item>the trigger is <c>manual</c> or <c>schedule</c> on both sides: a webhook lets someone else
/// start the pipeline, so entering or leaving that state is an edit of the pipeline.</item>
/// </list>
/// </summary>
internal static class PipelineScheduleOnlyEdit
{
    private const string ScheduleKey = "schedule";
    private const string TriggerKey = "trigger";
    private static readonly string[] Keys = [ScheduleKey, TriggerKey];

    public static bool IsScheduleOnly(string? before, string? after)
    {
        if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after)
            || !IsOneDocumentWithoutRepeatedKeys(before) || !IsOneDocumentWithoutRepeatedKeys(after))
        {
            return false;
        }

        var oldText = RootMappingText.Create(before, out _);
        var newText = RootMappingText.Create(after, out _);
        return oldText is not null && newText is not null && oldText.RootIndent == newText.RootIndent
            && LinesDifferOnlyByTheTwoKeys(oldText, newText)
            && DocumentsAreEqualWithoutTheTwoKeys(before, after);
    }

    /// <summary>Whether <paramref name="after"/> is scheduled for the engine and <paramref name="before"/> was not,
    /// whatever else differs: a definition that cannot be read before counts as not scheduled, so the right
    /// is asked, and one that cannot be read after schedules nothing, the validation refuses it. It reads the
    /// trigger as the save does, and it is the one rule of the routes that write a definition.</summary>
    public static bool TurnsScheduleOn(string? before, string? after) =>
        PipelineScheduleReader.Read(after)?.IsScheduled == true
        && PipelineScheduleReader.Read(before)?.IsScheduled != true;

    private static bool IsOneDocumentWithoutRepeatedKeys(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
            return stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlMappingNode;
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool LinesDifferOnlyByTheTwoKeys(RootMappingText before, RootMappingText after)
    {
        var (oldIndex, newIndex) = (0, 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = before.Lines.Select(line => KeyOf(line.Text, before.RootIndent)).ToList();
        while (newIndex < after.Lines.Count)
        {
            var newLine = after.Lines[newIndex];
            var key = KeyOf(newLine.Text, after.RootIndent);
            if (key is not null && !seen.Add(key))
            {
                return false;
            }

            if (oldIndex < before.Lines.Count && SameEntry(before, oldIndex, kept[oldIndex], newLine, key))
            {
                oldIndex++;
            }
            else if (key is null || kept.Contains(key) || CommentOf(newLine.Text, after.RootIndent, key).Length > 0)
            {
                return false;
            }

            newIndex++;
        }

        return oldIndex == before.Lines.Count;
    }

    // The same line, or the same key with its value changed and all the rest of the line kept.
    private static bool SameEntry(
        RootMappingText before, int oldIndex, string? oldKey, (string Text, string Ending) newLine, string? newKey)
    {
        var oldLine = before.Lines[oldIndex];
        if (oldKey != newKey)
        {
            return false;
        }

        // The last line of a file without a final line break takes one when a line is added after it.
        return oldKey is null
            ? oldLine == newLine
            : (oldLine.Ending == newLine.Ending || oldLine.Ending.Length == 0)
                && CommentOf(oldLine.Text, before.RootIndent, oldKey) == CommentOf(newLine.Text, before.RootIndent, oldKey);
    }

    // The key a top-level line writes, when its value is a scalar held on that line.
    private static string? KeyOf(string text, int rootIndent) =>
        Keys.FirstOrDefault(key => LocateValue(text, rootIndent, key) is not null);

    private static string CommentOf(string text, int rootIndent, string key) =>
        text[LocateValue(text, rootIndent, key)!.End..].Trim();

    private static ValueSpan? LocateValue(string text, int rootIndent, string key)
    {
        var afterColon = rootIndent + key.Length + 1;
        var isKey = text.Length >= afterColon
            && text.AsSpan(0, rootIndent).IndexOfAnyExcept(' ') < 0
            && string.CompareOrdinal(text, rootIndent, key + ":", 0, key.Length + 1) == 0
            && (text.Length == afterColon || text[afterColon] is ' ' or '\t');
        return isKey ? ValueSpan.Locate(text, afterColon, out _) : null;
    }

    private static bool DocumentsAreEqualWithoutTheTwoKeys(string before, string after)
    {
        var oldDocument = PipelineScheduleYamlEditor.Parse(before, out _);
        var newDocument = PipelineScheduleYamlEditor.Parse(after, out _);
        return oldDocument is not null && newDocument is not null
            && IsPlainTrigger(oldDocument) && IsPlainTrigger(newDocument)
            && IsScalarSchedule(oldDocument) && IsScalarSchedule(newDocument)
            && YamlTree.AreEqual(WithoutTheTwoKeys(oldDocument), WithoutTheTwoKeys(newDocument));
    }

    private static bool IsPlainTrigger(Dictionary<object, object?> document) =>
        !document.TryGetValue(TriggerKey, out var trigger)
        || trigger is PipelineScheduleYamlEditor.ManualTrigger or PipelineScheduleYamlEditor.ScheduleTrigger;

    private static bool IsScalarSchedule(Dictionary<object, object?> document) =>
        !document.TryGetValue(ScheduleKey, out var schedule) || schedule is null or string;

    private static Dictionary<object, object?> WithoutTheTwoKeys(Dictionary<object, object?> document)
    {
        var rest = new Dictionary<object, object?>(document);
        rest.Remove(ScheduleKey);
        rest.Remove(TriggerKey);
        return rest;
    }
}
