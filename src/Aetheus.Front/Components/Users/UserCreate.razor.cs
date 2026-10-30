// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Forms;

namespace Aetheus.Front.Components.Users;

/// <summary>
/// Creates an account. No password is typed here (owner decision 2026-10-28, R-14): the welcome mail carries
/// the link with which the person chooses theirs, and the page says what became of that mail.
/// </summary>
public partial class UserCreate
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private AdministrationScopeService Scope { get; set; } = default!;

    private readonly UserModel _model = new();
    private readonly EditContext _editContext;
    private bool _busy;
    private string? _error;

    /// <summary>The account created, held when its welcome mail did not leave so the page can say so.</summary>
    private UserDto? _created;

    /// <summary>R-10 (d): for an organization's administrator, the organizations where it holds org.members.manage,
    /// the only ones it creates an account in; null for the site Admin, who chooses among every organization.</summary>
    private IReadOnlyList<AdministeredOrganizationDto>? _administered;
    private int? _initialOrganizationId;
    private bool _ready;

    public UserCreate() => _editContext = new EditContext(_model);

    protected override async Task OnInitializedAsync()
    {
        if (!Auth.IsAdmin && !Permissions.Can(PermissionKeys.OrganizationMembersManage))
        {
            Nav.NavigateTo("/");
            return;
        }

        if (!Auth.IsAdmin)
        {
            await Scope.EnsureLoadedAsync();
            _administered = Scope.OrganizationsFor(PermissionKeys.OrganizationMembersManage);
            _initialOrganizationId = Scope.SelectedOrFirstFor(PermissionKeys.OrganizationMembersManage);
        }
        _ready = true;

        Breadcrumb.Set(
            new BreadcrumbItem(L["Administration"], "/admin"),
            new BreadcrumbItem(L["Users"], "/admin/users"),
            new BreadcrumbItem(L["NewUser"]));
    }

    // The account page is the site Admin's; an organization's administrator goes back to its users' list.
    private string CreatedPage(UserDto created) => _administered is null ? $"/users/{created.Id}" : "/admin/users";

    private void OpenCreated()
    {
        if (_created is { } created)
            Nav.NavigateTo(CreatedPage(created));
    }

    private async Task OnSubmit()
    {
        _busy = true;
        _error = null;

        try
        {
            var outcome = await Api.Auth.CreateUserAsync(new CreateUserRequest
            {
                Username = _model.Username,
                Email = string.IsNullOrWhiteSpace(_model.Email) ? null : _model.Email,
                // Only the site Admin gives a site-wide role; an organization's administrator never sends one.
                Roles = _administered is null ? _model.Roles : [],
                OrganizationRoles = _model.OrganizationRoles
            });

            if (outcome.Value is { } created)
            {
                if (created.WelcomeMail is { Status: WelcomeMailStatus.Sent } sent)
                {
                    Toast.Success("Created", "UserCreatedWelcomeSent", sent.To ?? created.Email ?? string.Empty);
                    Nav.NavigateTo(CreatedPage(created));
                    return;
                }

                // Created, but the welcome mail did not leave (or the answer does not say it did): the page
                // keeps the outcome on screen instead of a success toast.
                _created = created with
                {
                    WelcomeMail = created.WelcomeMail ?? new WelcomeMailResultDto
                    {
                        Status = WelcomeMailStatus.Failed,
                        To = created.Email,
                        Message = L["WelcomeMailUnknown"].Value
                    }
                };
                _busy = false;
                return;
            }

            _error = outcome.Error?.Code == UserErrorCodes.EmailInUse
                ? L["UserEmailInUse"].Value
                : string.IsNullOrWhiteSpace(outcome.Error?.Message)
                    ? L["SaveFailed"].Value
                    : outcome.Error.Message;
        }
        catch (HttpRequestException)
        {
            _error = L["SaveFailed"].Value;
            Toast.Error("Error", "SaveFailed");
        }
        _busy = false;
    }

    private sealed class UserModel
    {
        [Required(ErrorMessage = "UsernameRequired")]
        [StringLength(50, ErrorMessage = "UsernameTooLong")]
        public string Username { get; set; } = string.Empty;

        // PLAN-003 P3-2: the sign-in identifier, and where the welcome mail goes (R-14).
        [Required(ErrorMessage = "EmailRequired")]
        [StringLength(200)]
        [EmailAddress(ErrorMessage = "EmailInvalid")]
        public string? Email { get; set; }

        public List<string> Roles { get; set; } = [];
        public List<OrganizationRoleAssignmentDto> OrganizationRoles { get; set; } = [];
    }
}
