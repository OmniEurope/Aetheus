// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

public sealed class MailTemplateRepository(AppDbContext db) : IMailTemplateRepository
{
    public Task<List<MailTemplate>> ListAsync(CancellationToken ct = default) =>
        db.MailTemplates.AsNoTracking().OrderBy(template => template.Kind).ThenBy(template => template.Language).ToListAsync(ct);

    public Task<MailTemplate?> FindAsync(MailTemplateKind kind, string language, CancellationToken ct = default) =>
        db.MailTemplates.AsNoTracking().FirstOrDefaultAsync(template => template.Kind == kind && template.Language == language, ct);

    public async Task<MailTemplate> UpsertAsync(
        MailTemplateKind kind, string language, string subject, string body, string? updatedBy, CancellationToken ct = default)
    {
        var row = await db.MailTemplates
            .FirstOrDefaultAsync(template => template.Kind == kind && template.Language == language, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new MailTemplate { Kind = kind, Language = language };
            db.MailTemplates.Add(row);
        }

        row.Subject = subject;
        row.Body = body;
        row.UpdatedBy = updatedBy;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    public async Task<bool> DeleteAsync(MailTemplateKind kind, string language, CancellationToken ct = default)
    {
        var row = await db.MailTemplates
            .FirstOrDefaultAsync(template => template.Kind == kind && template.Language == language, ct).ConfigureAwait(false);
        if (row is null) return false;
        db.MailTemplates.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<(string? LanguageJson, string? AppearanceJson)> FindRecipientPreferencesAsync(string email, CancellationToken ct = default)
    {
        var address = email.Trim().ToLowerInvariant();
        var userId = await db.Users.AsNoTracking()
            .Where(user => user.Email != null && user.Email.ToLower() == address)
            .OrderByDescending(user => user.IsActive)
            .ThenBy(user => user.Id)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (userId is not { } id) return (null, null);

        var preferences = await db.UserPreferences.AsNoTracking()
            .Where(preference => preference.UserId == id
                && (preference.Key == MailRecipientStyle.LanguagePreferenceKey || preference.Key == MailRecipientStyle.AppearancePreferenceKey))
            .Select(preference => new { preference.Key, preference.Value })
            .ToListAsync(ct).ConfigureAwait(false);
        return (
            preferences.FirstOrDefault(preference => preference.Key == MailRecipientStyle.LanguagePreferenceKey)?.Value,
            preferences.FirstOrDefault(preference => preference.Key == MailRecipientStyle.AppearancePreferenceKey)?.Value);
    }

    public Task<string?> FindUserEmailAsync(int userId, CancellationToken ct = default) =>
        db.Users.AsNoTracking()
            .Where(user => user.Id == userId && user.Email != null && user.Email != string.Empty)
            .Select(user => user.Email)
            .FirstOrDefaultAsync(ct);
}
