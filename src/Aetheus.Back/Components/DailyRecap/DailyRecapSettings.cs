// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Mail;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// The daily recap settings as the scheduler uses them. A missing row takes its default: the recap
/// is on, at 07:00 Europe/Paris, in French, with every section. <see cref="Recipients"/> is null until
/// an administrator saves a list: the recap then goes to the active administrators.
/// </summary>
internal sealed record DailyRecapSettings(
    bool Enabled,
    TimeOnly SendTime,
    string TimeZoneId,
    DailyRecapLanguage Language,
    IReadOnlySet<DailyRecapSection> Sections,
    IReadOnlyList<string>? Recipients)
{
    /// <summary>No timezone setting exists elsewhere in Aetheus, so the recap carries its own, Paris by default.</summary>
    public const string DefaultTimeZoneId = "Europe/Paris";

    public static readonly TimeOnly DefaultSendTime = new(7, 0);

    private const int MaxRecipients = 50;

    public static IReadOnlySet<DailyRecapSection> AllSections { get; } =
        Enum.GetValues<DailyRecapSection>().ToHashSet();

    public static DailyRecapSettings Default { get; } =
        new(true, DefaultSendTime, DefaultTimeZoneId, DailyRecapLanguage.French, AllSections, null);

    public CultureInfo Culture => Language == DailyRecapLanguage.English
        ? CultureInfo.GetCultureInfo("en")
        : CultureInfo.GetCultureInfo("fr-FR");

    public static DailyRecapSettings Parse(IReadOnlyDictionary<string, string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var enabled = !rows.TryGetValue(DailyRecapSettingKeys.Enabled, out var enabledText)
            || !bool.TryParse(enabledText, out var parsedEnabled) || parsedEnabled;
        var sendTime = rows.TryGetValue(DailyRecapSettingKeys.SendTime, out var timeText)
            && TimeOnly.TryParseExact(timeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime)
            ? parsedTime : DefaultSendTime;
        var timeZone = rows.TryGetValue(DailyRecapSettingKeys.TimeZone, out var zoneText) && !string.IsNullOrWhiteSpace(zoneText)
            ? zoneText.Trim() : DefaultTimeZoneId;
        var language = rows.TryGetValue(DailyRecapSettingKeys.Language, out var languageText)
            && Enum.TryParse<DailyRecapLanguage>(languageText, ignoreCase: true, out var parsedLanguage)
            && Enum.IsDefined(parsedLanguage) ? parsedLanguage : DailyRecapLanguage.French;
        // A row that was never written means every section; a saved empty list means none.
        var sections = rows.TryGetValue(DailyRecapSettingKeys.Sections, out var sectionsText)
            ? Split(sectionsText)
                .Select(name => Enum.TryParse<DailyRecapSection>(name, ignoreCase: true, out var section) && Enum.IsDefined(section) ? section : (DailyRecapSection?)null)
                .OfType<DailyRecapSection>()
                .ToHashSet()
            : AllSections;
        var recipients = rows.TryGetValue(DailyRecapSettingKeys.Recipients, out var recipientsText)
            ? Split(recipientsText).ToList() : null;
        return new DailyRecapSettings(enabled, sendTime, timeZone, language, sections, recipients);
    }

    /// <summary>Checks an update and returns the settings it describes; a wrong value is a 400 that names it.</summary>
    public static DailyRecapSettings FromRequest(UpdateDailyRecapSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeZoneId = (request.TimeZoneId ?? string.Empty).Trim();
        if (!DailyRecapClock.TryResolveTimeZone(timeZoneId, out _))
            throw new BadRequestException($"The timezone '{timeZoneId}' is not known to this server (an IANA name such as Europe/Paris is expected).");
        if (!Enum.IsDefined(request.Language))
            throw new BadRequestException("The recap language is not supported.");

        var sections = CheckedSections(request.Sections);
        var recipients = CheckedRecipients(request.Recipients);
        if (recipients.Count > MaxRecipients)
            throw new BadRequestException($"At most {MaxRecipients} recipients can receive the recap.");
        if (request.Enabled && recipients.Count == 0)
            throw new BadRequestException("The recap is switched on but has no recipient: add an address or switch it off.");

        var sendTime = new TimeOnly(request.SendTime.Hour, request.SendTime.Minute);
        return new DailyRecapSettings(request.Enabled, sendTime, timeZoneId, request.Language, sections, recipients);
    }

    private static HashSet<DailyRecapSection> CheckedSections(IEnumerable<DailyRecapSection>? requested)
    {
        var sections = new HashSet<DailyRecapSection>();
        foreach (var section in requested ?? [])
        {
            if (!Enum.IsDefined(section))
                throw new BadRequestException($"The recap section '{(int)section}' does not exist.");
            sections.Add(section);
        }
        return sections;
    }

    private static List<string> CheckedRecipients(IEnumerable<string?>? requested)
    {
        var recipients = new List<string>();
        foreach (var raw in requested ?? [])
        {
            var address = (raw ?? string.Empty).Trim();
            if (address.Length == 0) continue;
            if (!MailAddress.TryCreate(address, out var parsed) || !string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase))
                throw new BadRequestException($"'{address}' is not a valid e-mail address.");
            if (recipients.Contains(address, StringComparer.OrdinalIgnoreCase))
                throw new BadRequestException($"'{address}' is listed twice.");
            recipients.Add(address);
        }
        return recipients;
    }

    public IReadOnlyDictionary<string, string> ToRows() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DailyRecapSettingKeys.Enabled] = Enabled ? "true" : "false",
        [DailyRecapSettingKeys.SendTime] = SendTime.ToString("HH:mm", CultureInfo.InvariantCulture),
        [DailyRecapSettingKeys.TimeZone] = TimeZoneId,
        [DailyRecapSettingKeys.Language] = Language.ToString(),
        [DailyRecapSettingKeys.Sections] = string.Join(',', Enum.GetValues<DailyRecapSection>().Where(Sections.Contains)),
        [DailyRecapSettingKeys.Recipients] = string.Join(',', Recipients ?? [])
    };

    private static IEnumerable<string> Split(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
