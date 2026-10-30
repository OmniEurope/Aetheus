// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>The names of the variables the welcome mail template inserts (R-14), written <c>{{Name}}</c>.
/// No variable holds a password: the mail carries a link to choose one, never a password itself.</summary>
public static class UserWelcomeMailVariables
{
    /// <summary>The account's username.</summary>
    public const string UserName = "UserName";

    /// <summary>The address the person signs in with (PLAN-003 P3-2), the one the mail is sent to.</summary>
    public const string SignInEmail = "SignInEmail";

    /// <summary>The single-use link to the page where the person chooses their password.</summary>
    public const string SetPasswordUrl = "SetPasswordUrl";

    /// <summary>How long the welcome link stays usable, in days.</summary>
    public const string ValidDays = "ValidDays";

    /// <summary>How long the forgotten-password link stays usable, in minutes.</summary>
    public const string ValidMinutes = "ValidMinutes";

    /// <summary>The sign-in page.</summary>
    public const string SignInUrl = "SignInUrl";
}
