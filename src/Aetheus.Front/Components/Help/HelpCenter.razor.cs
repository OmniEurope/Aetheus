// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Help;

public partial class HelpCenter
{
    [Inject] private HelpService Help { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;

    private IReadOnlyList<Aetheus.Front.Components.Shared.HelpArticle> _articles = [];
    private IReadOnlyList<Aetheus.Front.Components.Shared.HelpArticle> _filtered = [];
    private string? _search;
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        Breadcrumb.Set(new BreadcrumbItem(L["HelpCenter"]));
        _articles = await Help.GetAllArticlesAsync();
        _filtered = _articles;
        _loading = false;
    }

    private async Task FilterAsync()
    {
        _filtered = await Help.SearchAsync(_search ?? "");
    }

    private async Task OnSearchInputAsync(string? value)
    {
        _search = value;
        await FilterAsync();
    }
}
