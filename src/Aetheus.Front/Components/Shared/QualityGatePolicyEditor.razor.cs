// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Microsoft.AspNetCore.Components.Forms;

namespace Aetheus.Front.Components.Shared;

public partial class QualityGatePolicyEditor
{
    [Parameter] public AnalysisPolicyScope Scope { get; set; }
    [Parameter] public int? OrganizationId { get; set; }
    [Parameter] public int? ProjectId { get; set; }
    [Parameter] public bool CanEdit { get; set; }
    [Parameter] public IReadOnlyList<AnalysisPolicyDto>? InitialPolicies { get; set; }
    [Parameter] public EventCallback<List<AnalysisPolicyDto>> PoliciesChanged { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private const string YamlExample = """
        - name: Quality gate
          type: analysis-gate
          analysis_scope: quality
          analysis_preset: recommended
          analysis_rules:
            - key: quality.coverage.line
              minimum: 82
          analysis_grading:
            version: 1
            minimum_grade: C
            required_domains: [reliability]
            rules:
              - key: grade.coverage.line
                domain: reliability
                metric: coverage.line.percent
                category: coverage
                direction: higher-is-better
                a: 75
                b: 65
                c: 55
                d: 40
                e: 20
        """;

    private List<LocalizedOption<AnalysisCategory>> _categories = [];
    private List<LocalizedOption<AnalysisSeverity>> _severities = [];
    private List<LocalizedOption<AnalysisPolicyOperator>> _operators = [];
    private List<LocalizedOption<AnalysisGateBehavior>> _behaviors = [];
    private List<RuleKindOption> _ruleKinds = [];
    private List<AnalysisPolicyDto> _policies = [];
    private PolicyFormModel _form = new();
    private AnalysisPolicySetPreviewDto? _preview;
    private List<AnalysisPolicyRevisionDto> _revisions = [];
    private AnalysisPolicyDto? _historyPolicy;
    private static readonly IReadOnlyList<string> JsonContentTypes = ["application/json", "text/json"];
    private string? _loadedKey;
    private int? _editingPolicyId;
    private bool _lockPolicyKey;
    private bool _showForm;
    private bool _loading = true;
    private bool _showImporter;
    private bool _saving;
    private bool _previewing;
    private bool _applying;

    // The header filters name origins, behaviors and states as the cells do; built once so the
    // columns see the same delegates on every render.
    private Func<string, string>? _scopeText;
    private Func<string, string>? _behaviorText;
    private Func<string, string>? _effectiveText;
    private Func<string, string> ScopeText => _scopeText ??= value =>
        Enum.TryParse<AnalysisPolicyScope>(value, ignoreCase: true, out var scope) ? ScopeLabel(scope) : value;
    private Func<string, string> BehaviorText => _behaviorText ??= value =>
        Enum.TryParse<AnalysisGateBehavior>(value, ignoreCase: true, out var behavior) ? BehaviorLabel(behavior) : value;
    private Func<string, string> EffectiveText => _effectiveText ??= value =>
        bool.TryParse(value, out var effective) ? L[effective ? "Enabled" : "Disabled"].Value : value;

    /// <summary>
    /// Set once this editor has written: the seeded <see cref="InitialPolicies"/> is stale from then
    /// on, so every later load goes to the server. Kept as a local flag rather than nulling the
    /// parameter - a parent that binds it would restore it on its next render and overwrite the
    /// freshly saved list with the pre-save one.
    /// </summary>
    private bool _seedIsStale;

    private IReadOnlyList<AnalysisPolicyDto>? Seed => _seedIsStale ? null : InitialPolicies;

    protected override void OnInitialized()
    {
        // The enums never change between renders, so these lists are built once instead of on every
        // parameter pass (five allocations and ~28 localizer lookups each time).
        _ruleKinds =
        [
            new(QualityGateRuleKind.Finding, L["QualityGateFindingRule"]),
            new(QualityGateRuleKind.Metric, L["QualityGateMetricRule"])
        ];
        _categories = LocalizedOptions<AnalysisCategory>("QualityGateCategory");
        _severities = LocalizedOptions<AnalysisSeverity>("QualityGateSeverity");
        _operators = LocalizedOptions<AnalysisPolicyOperator>("QualityGateOperator");
        _behaviors = LocalizedOptions<AnalysisGateBehavior>("QualityGateBehavior");
    }

    protected override async Task OnParametersSetAsync()
    {
        var key = $"{Scope}:{OrganizationId}:{ProjectId}";
        if (_loadedKey == key)
        {
            if (Seed is { } seeded)
                _policies = [.. seeded];
            return;
        }

        _loadedKey = key;
        _seedIsStale = false;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _policies = Seed is { } seed
                ? [.. seed]
                : Scope switch
                {
                    AnalysisPolicyScope.Global => await Api.Analysis.GetGlobalAnalysisPoliciesAsync(),
                    AnalysisPolicyScope.Organization =>
                        await Api.Analysis.GetOrganizationAnalysisPoliciesAsync(OrganizationId!.Value),
                    AnalysisPolicyScope.Project => await Api.Analysis.GetAnalysisPoliciesAsync(ProjectId!.Value),
                    _ => []
                };
            await PoliciesChanged.InvokeAsync(_policies);
        }
        catch (HttpRequestException)
        {
            _policies = [];
        }
        finally
        {
            _loading = false;
        }
    }

    private void BeginAdd()
    {
        _editingPolicyId = null;
        _lockPolicyKey = false;
        _form = new PolicyFormModel();
        _preview = null;
        _showForm = true;
    }

    private void BeginEdit(AnalysisPolicyDto policy)
    {
        _editingPolicyId = policy.Id;
        _lockPolicyKey = true;
        _form = PolicyFormModel.From(policy);
        _preview = null;
        _showForm = true;
    }

    private void BeginOverride(AnalysisPolicyDto policy)
    {
        _editingPolicyId = null;
        _lockPolicyKey = true;
        _form = PolicyFormModel.From(policy);
        _preview = null;
        _showForm = true;
    }

    private void BeginDuplicate(AnalysisPolicyDto policy)
    {
        _editingPolicyId = null;
        _lockPolicyKey = false;
        _form = PolicyFormModel.From(policy);
        _form.PolicyKey = NextCopyKey(policy.PolicyKey);
        _form.Name = $"{policy.Name} ({L["Copy"]})";
        _preview = null;
        _showForm = true;
    }

    private void CancelEdit()
    {
        _showForm = false;
        _preview = null;
    }

    private async Task PreviewAsync()
    {
        _previewing = true;
        try
        {
            _preview = await Api.Analysis.PreviewAnalysisPolicySetAsync(
                Scope,
                OrganizationId,
                ProjectId,
                new PreviewAnalysisPolicySetRequest
                {
                    PolicyId = _editingPolicyId,
                    Candidate = _form.ToRequest()
                });
        }
        finally
        {
            _previewing = false;
        }
    }

    private async Task SaveAsync()
    {
        // The submit path is reachable by Enter as well as by the button, whose busy veil only blocks
        // the pointer (STD-BUSY): without this, a double submit creates a duplicate quality-gate rule.
        if (_saving) return;
        _saving = true;
        try
        {
            var request = _form.ToRequest();
            var saved = await SaveRequestAsync(_editingPolicyId, request);
            if (saved is null) return;
            _showForm = false;
            _preview = null;
            _seedIsStale = true;
            await LoadAsync();
            Toast.Success("Saved", "AnalysisPolicySaved");
        }
        finally
        {
            _saving = false;
        }
    }

    private Task<AnalysisPolicyDto?> SaveRequestAsync(
        int? policyId,
        UpsertAnalysisPolicyRequest request) =>
        Scope switch
        {
            AnalysisPolicyScope.Global => policyId.HasValue
                ? Api.Analysis.UpdateGlobalAnalysisPolicyAsync(policyId.Value, request)
                : Api.Analysis.CreateGlobalAnalysisPolicyAsync(request),
            AnalysisPolicyScope.Organization => policyId.HasValue
                ? Api.Analysis.UpdateOrganizationAnalysisPolicyAsync(OrganizationId!.Value, policyId.Value, request)
                : Api.Analysis.CreateOrganizationAnalysisPolicyAsync(OrganizationId!.Value, request),
            AnalysisPolicyScope.Project => policyId.HasValue
                ? Api.Analysis.UpdateAnalysisPolicyAsync(ProjectId!.Value, policyId.Value, request)
                : Api.Analysis.CreateAnalysisPolicyAsync(ProjectId!.Value, request),
            _ => Task.FromResult<AnalysisPolicyDto?>(null)
        };

    private async Task ApplyPresetAsync(bool strict)
    {
        if (_applying) return;
        _applying = true;
        try
        {
            var saved = await SaveBatchAsync(Preset(strict));
            if (saved is null) return;
            _seedIsStale = true;
            await LoadAsync();
            Toast.Success("Saved", strict ? "QualityGateStrictApplied" : "QualityGateRecommendedApplied");
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task ExportAsync()
    {
        var export = new QualityGateExport
        {
            Rules = _policies.Where(CanEditDirectly)
                .Select(item => PolicyFormModel.From(item).ToRequest())
                .ToList()
        };
        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
        await Js.InvokeVoidAsync(
            "downloadFile",
            $"quality-gates-{Scope.ToString().ToLowerInvariant()}.json",
            json,
            "application/json");
    }

    private Task OpenImportAsync()
    {
        _showImporter = !_showImporter;
        return Task.CompletedTask;
    }

    private async Task ImportAsync(IReadOnlyList<IBrowserFile> files)
    {
        if (_applying) return;
        const long maxBytes = 1_048_576;
        var file = files.Single();
        if (file.Size is 0 or > maxBytes)
        {
            Toast.Error("Error", "QualityGateImportInvalid");
            return;
        }

        QualityGateExport? import;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream(maxBytes));
            import = JsonSerializer.Deserialize<QualityGateExport>(
                await reader.ReadToEndAsync(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            Toast.Error("Error", "QualityGateImportInvalid");
            return;
        }

        if (!IsValidImport(import))
        {
            Toast.Error("Error", "QualityGateImportInvalid");
            return;
        }

        _applying = true;
        try
        {
            var saved = await SaveBatchAsync(import!.Rules);
            if (saved is null) return;
            _seedIsStale = true;
            _showImporter = false;
            await LoadAsync();
            Toast.Success("Imported", "QualityGateImported", import.Rules.Count);
        }
        finally
        {
            _applying = false;
        }
    }

    private Task<List<AnalysisPolicyDto>?> SaveBatchAsync(
        IReadOnlyCollection<UpsertAnalysisPolicyRequest> requests)
    {
        var batch = new ApplyAnalysisPolicyBatchRequest
        {
            Items = requests.Select(request =>
            {
                var local = _policies.FirstOrDefault(item =>
                    CanEditDirectly(item)
                    && string.Equals(item.PolicyKey, request.PolicyKey, StringComparison.OrdinalIgnoreCase));
                return new AnalysisPolicyBatchItemRequest
                {
                    PolicyId = local?.Id,
                    Policy = request
                };
            }).ToList()
        };
        return Scope switch
        {
            AnalysisPolicyScope.Global => Api.Analysis.ApplyGlobalAnalysisPolicyBatchAsync(batch),
            AnalysisPolicyScope.Organization =>
                Api.Analysis.ApplyOrganizationAnalysisPolicyBatchAsync(OrganizationId!.Value, batch),
            AnalysisPolicyScope.Project => Api.Analysis.ApplyAnalysisPolicyBatchAsync(ProjectId!.Value, batch),
            _ => Task.FromResult<List<AnalysisPolicyDto>?>(null)
        };
    }

    private async Task ShowHistoryAsync(AnalysisPolicyDto policy)
    {
        _historyPolicy = policy;
        _revisions = await Api.Analysis.GetAnalysisPolicyRevisionsAsync(
            Scope,
            OrganizationId,
            ProjectId,
            policy.Id);
    }

    private async Task RollbackAsync(AnalysisPolicyRevisionDto revision)
    {
        if (_historyPolicy is null || _applying) return;
        _applying = true;
        try
        {
            await Api.Analysis.RollbackAnalysisPolicyAsync(
                Scope,
                OrganizationId,
                ProjectId,
                _historyPolicy.Id,
                revision.Version);
            _seedIsStale = true;
            await LoadAsync();
            var refreshed = _policies.FirstOrDefault(item => item.Id == _historyPolicy.Id);
            if (refreshed is not null) await ShowHistoryAsync(refreshed);
            Toast.Success("Saved", "QualityGateRevisionRestored", revision.Version);
        }
        finally
        {
            _applying = false;
        }
    }

    private string NextCopyKey(string key)
    {
        var root = $"{key}.copy";
        var candidate = root;
        var suffix = 2;
        var keys = _policies.Select(item => item.PolicyKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (keys.Contains(candidate)) candidate = $"{root}.{suffix++}";
        return candidate;
    }

    /// <summary>An imported gate: schema 1, 1 to 100 rules, each valid, with a unique key, a target and,
    /// for a metric rule, its operator and threshold.</summary>
    internal static bool IsValidImport(QualityGateExport? import)
    {
        if (import is not { SchemaVersion: 1, Rules.Count: > 0 and <= 100 }) return false;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return import.Rules.All(rule => IsValidImportedRule(rule, keys));
    }

    private static bool IsValidImportedRule(UpsertAnalysisPolicyRequest rule, HashSet<string> keys) =>
        Validator.TryValidateObject(rule, new ValidationContext(rule), new List<ValidationResult>(), true)
        && !string.IsNullOrWhiteSpace(rule.PolicyKey)
        && keys.Add(rule.PolicyKey)
        && HasImportTarget(rule)
        && HasCompleteMetric(rule);

    /// <summary>A rule targets something: a severity, a rule, a metric, a category or a scanner.</summary>
    private static bool HasImportTarget(UpsertAnalysisPolicyRequest rule) =>
        rule.SeverityThreshold.HasValue
        || !string.IsNullOrWhiteSpace(rule.RuleId)
        || !string.IsNullOrWhiteSpace(rule.MetricKey)
        || rule.Category.HasValue
        || !string.IsNullOrWhiteSpace(rule.ScannerKey);

    /// <summary>A metric rule carries its operator and threshold.</summary>
    private static bool HasCompleteMetric(UpsertAnalysisPolicyRequest rule) =>
        string.IsNullOrWhiteSpace(rule.MetricKey) || (rule.Operator.HasValue && rule.Threshold.HasValue);

    private static List<UpsertAnalysisPolicyRequest> Preset(bool strict) =>
    [
        MetricPreset("quality.coverage.line", "Line coverage", "coverage.line.percent",
            AnalysisPolicyOperator.LessThan, strict ? 85 : 75, strict),
        MetricPreset("quality.duplication.percentage", "Duplication", "duplication.percentage",
            AnalysisPolicyOperator.GreaterThan, strict ? 3 : 5, strict),
        MetricPreset("quality.complexity.maximum", "Maximum complexity", "complexity.cyclomatic.maximum",
            AnalysisPolicyOperator.GreaterThan, strict ? 15 : 25, strict),
        MetricPreset("quality.architecture.cycles", "Architecture cycles", "architecture.cycles*",
            AnalysisPolicyOperator.GreaterThan, 0, strict)
    ];

    private static UpsertAnalysisPolicyRequest MetricPreset(
        string key,
        string name,
        string metricKey,
        AnalysisPolicyOperator policyOperator,
        double threshold,
        bool strict) => new()
        {
            PolicyKey = key,
            Name = name,
            MetricKey = metricKey,
            Operator = policyOperator,
            Threshold = threshold,
            Behavior = strict ? AnalysisGateBehavior.Block : AnalysisGateBehavior.Warn,
            Enabled = true
        };

    private bool CanEditDirectly(AnalysisPolicyDto policy) =>
        policy.Id > 0 && policy.Scope == Scope && !policy.IsInherited;

    private string ScopeLabel(AnalysisPolicyScope scope) => scope switch
    {
        AnalysisPolicyScope.System => L["QualityGateScopeSystem"],
        AnalysisPolicyScope.Global => L["QualityGateScopeGlobal"],
        AnalysisPolicyScope.Organization => L["QualityGateScopeOrganization"],
        AnalysisPolicyScope.Project => L["QualityGateScopeProject"],
        AnalysisPolicyScope.Pipeline => L["QualityGateScopePipeline"],
        _ => scope.ToString()
    };

    private string BehaviorLabel(AnalysisGateBehavior behavior) =>
        L[$"QualityGateBehavior{behavior}"];

    private List<LocalizedOption<T>> LocalizedOptions<T>(string prefix)
        where T : struct, Enum =>
        Enum.GetValues<T>()
            .Select(value => new LocalizedOption<T>(value, L[$"{prefix}{value}"]))
            .ToList();

    private string Criterion(AnalysisPolicyDto policy)
    {
        if (!string.IsNullOrWhiteSpace(policy.MetricKey))
            return $"{policy.MetricKey} {policy.Operator} {policy.Threshold:0.##}";
        var parts = new List<string>();
        if (policy.Category.HasValue) parts.Add(policy.Category.Value.ToString());
        if (!string.IsNullOrWhiteSpace(policy.ScannerKey)) parts.Add(policy.ScannerKey);
        if (!string.IsNullOrWhiteSpace(policy.RuleId)) parts.Add(policy.RuleId);
        if (policy.SeverityThreshold.HasValue) parts.Add($">= {policy.SeverityThreshold}");
        if (policy.NewFindingsOnly) parts.Add(L["QualityGateNewOnlyShort"]);
        return string.Join(" / ", parts);
    }

    private enum QualityGateRuleKind
    {
        Finding,
        Metric
    }

    private sealed record RuleKindOption(QualityGateRuleKind Value, string Text);
    private sealed record LocalizedOption<T>(T Value, string Text);

    internal sealed class QualityGateExport
    {
        public int SchemaVersion { get; init; } = 1;
        public List<UpsertAnalysisPolicyRequest> Rules { get; init; } = [];
    }

    private sealed class PolicyFormModel
    {
        public QualityGateRuleKind Kind { get; set; }
        [Required, StringLength(200, MinimumLength = 3)] public string Name { get; set; } = string.Empty;
        [StringLength(200)] public string? PolicyKey { get; set; }
        public AnalysisCategory? Category { get; set; }
        [StringLength(100)] public string? ScannerKey { get; set; }
        [StringLength(300)] public string? RuleId { get; set; }
        public AnalysisSeverity? SeverityThreshold { get; set; }
        public bool NewFindingsOnly { get; set; }
        [StringLength(300)] public string? MetricKey { get; set; }
        public AnalysisPolicyOperator? Operator { get; set; }
        public double? Threshold { get; set; }
        [StringLength(300)] public string? BranchPattern { get; set; }
        [StringLength(300)] public string? EnvironmentPattern { get; set; }
        public AnalysisGateBehavior Behavior { get; set; } = AnalysisGateBehavior.Warn;
        [Range(-10_000, 10_000)] public int Priority { get; set; }
        public bool Enabled { get; set; } = true;

        public UpsertAnalysisPolicyRequest ToRequest() => new()
        {
            PolicyKey = PolicyKey,
            Name = Name,
            Category = Category,
            ScannerKey = ScannerKey,
            RuleId = Kind == QualityGateRuleKind.Finding ? RuleId : null,
            SeverityThreshold = Kind == QualityGateRuleKind.Finding ? SeverityThreshold : null,
            NewFindingsOnly = Kind == QualityGateRuleKind.Finding && NewFindingsOnly,
            MetricKey = Kind == QualityGateRuleKind.Metric ? MetricKey : null,
            Operator = Kind == QualityGateRuleKind.Metric ? Operator : null,
            Threshold = Kind == QualityGateRuleKind.Metric ? Threshold : null,
            BranchPattern = BranchPattern,
            EnvironmentPattern = EnvironmentPattern,
            Behavior = Behavior,
            Priority = Priority,
            Enabled = Enabled
        };

        public static PolicyFormModel From(AnalysisPolicyDto policy) => new()
        {
            Kind = string.IsNullOrWhiteSpace(policy.MetricKey)
                ? QualityGateRuleKind.Finding
                : QualityGateRuleKind.Metric,
            PolicyKey = policy.PolicyKey,
            Name = policy.Name,
            Category = policy.Category,
            ScannerKey = policy.ScannerKey,
            RuleId = policy.RuleId,
            SeverityThreshold = policy.SeverityThreshold,
            NewFindingsOnly = policy.NewFindingsOnly,
            MetricKey = policy.MetricKey,
            Operator = policy.Operator,
            Threshold = policy.Threshold,
            BranchPattern = policy.BranchPattern,
            EnvironmentPattern = policy.EnvironmentPattern,
            Behavior = policy.Behavior,
            Priority = policy.Priority,
            Enabled = policy.Enabled
        };
    }
}
