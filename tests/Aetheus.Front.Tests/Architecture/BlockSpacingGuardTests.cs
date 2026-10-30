// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Every block (card, alert, fieldset, grid, tabs...) is separated from the next block of its container
/// by the standard spacing (recette R1-43, <c>docs/contracts/ui-patterns.md</c>, "Spacing between
/// blocks"): the container is an <c>OmniStack</c> whose Gap is Medium or more, or a custom container
/// whose stylesheet rule sets a gap, or one of the two blocks carries an <c>omni-u-mt-*</c>/<c>omni-u-mb-*</c>
/// utility or a margin class.
/// </summary>
public sealed class BlockSpacingGuardTests
{
    private static readonly Regex CssRule = new(
        @"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.CultureInvariant);

    private static readonly Regex GapDeclaration = new(
        @"(?<![\w-])(?:row-|column-)?gap\s*:\s*(?!0\s*(?:;|$))", RegexOptions.CultureInvariant);

    private static readonly Regex MarginDeclaration = new(
        @"(?<![\w-])margin(?:-top|-bottom|-block|-block-start|-block-end)?\s*:\s*(?!0\s*(?:;|$)|auto)",
        RegexOptions.CultureInvariant);

    private static readonly Regex ClassName = new(@"\.(?<c>-?[_a-zA-Z][\w-]*)", RegexOptions.CultureInvariant);

    [Fact]
    public void Blocks_AreSeparatedByTheStandardSpacing()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var css = LoadCss(front);
        var violations = new List<string>();
        var blocks = 0;

        foreach (var file in RepositoryScan.Enumerate(front, "*.razor"))
        {
            var source = File.ReadAllText(file);
            blocks += BlockSpacingScanner.CountBlocks(source);
            violations.AddRange(BlockSpacingScanner.Scan(source, css).Select(v =>
                $"{Path.GetRelativePath(front, file)}:{v.Line}  {v.First} then {v.Second} in {(v.Parent.Length == 0 ? "the page root" : v.Parent)}"));
        }

        // A scan that finds no block protects nothing.
        Assert.True(blocks >= 300, $"Expected the front to hold many blocks, found {blocks}.");
        Assert.True(violations.Count == 0,
            "Two blocks follow each other with no standard spacing. Put them in an OmniStack (Gap Medium or "
            + "more), or give the first omni-u-mb-md / the second omni-u-mt-md "
            + "(docs/contracts/ui-patterns.md, \"Spacing between blocks\"):\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Pages already moved to the strict form of the rule (STD-SPACE as rewritten on 2026-10-24, recette R-14):
    /// their blocks are separated only by the gap of their container and none carries a margin of its own.
    /// The page of a run and the components it is made of come first; a page migrated later is added here, and
    /// the end state is every <c>.razor</c> file of the front.
    /// </summary>
    // Every .razor of Components/Pipelines (R-14), except Pipelines.razor (its file name is shared with two other folders; the list is by name).
    private static readonly string[] ContainerSpacedFiles =
    [
        "PipelineRun.razor",
        "PipelineStepStopAlert.razor",
        "PipelineRunPreflightRecord.razor",
        "PipelineRunAssuranceBanner.razor",
        "PipelineRunWaitingAlert.razor",
        "PipelineRunToEndAlert.razor",
        "PipelineRunAiResultsCard.razor",
        "PipelineRunApprovalPanel.razor",
        "PipelineRunResumePanel.razor",
        "DryRunResultDialog.razor",
        "ExtractPipelineTemplateDialog.razor",
        "ImportJsonDialog.razor",
        "MonacoEditor.razor",
        "PipelineAiProfileField.razor",
        "PipelineDefinitionPeek.razor",
        "PipelineEdit.razor",
        "PipelineFleet.razor",
        "PipelineFleetUpdateDialog.razor",
        "PipelineInfoTip.razor",
        "PipelineLaunchDialog.razor",
        "PipelineOwnerPanel.razor",
        "PipelineOwnerTransferDialog.razor",
        "PipelineProductionPanel.razor",
        "PipelineProductionRefusedAlert.razor",
        "PipelineProjectCopyStep.razor",
        "PipelineReadinessCheckBody.razor",
        "PipelineReadinessChecks.razor",
        "PipelineReadinessEmptyValues.razor",
        "PipelineRunBlockingTests.razor",
        "PipelineRunCoverageFiles.razor",
        "PipelineRunGateFindings.razor",
        "PipelineRunGateOverview.razor",
        "PipelineRunHeader.razor",
        "PipelineRunLineageDialog.razor",
        "PipelineRunLineageTile.razor",
        "PipelineRunLiveDuration.razor",
        "PipelineRunLogLines.razor",
        "PipelineRunMigrationGate.razor",
        "PipelineRunParametersDialog.razor",
        "PipelineRunPreflightDialog.razor",
        "PipelineRunResultsTabs.razor",
        "PipelineRunSchemaTab.razor",
        "PipelineRunSourceTile.razor",
        "PipelineRunVariablesDialog.razor",
        "PipelineSaveDialog.razor",
        "PipelineSelectDialog.razor",
        "PipelineSetupReadinessPanel.razor",
        "PipelineSetupRecap.razor",
        "PipelineSetupStartFrom.razor",
        "PipelineSetupWizard.razor",
        "PipelineSetupWizardStep.razor",
        "PipelineTemplateEdit.razor",
        "PipelineTemplateExtract.razor",
        "PipelineTemplateHistory.razor",
        "PipelineTemplatePromote.razor",
        "PipelineTemplateUpdate.razor",
        "PipelineTemplates.razor",
        "PipelinesList.razor",
        "ProjectPipelinesReadiness.razor",
        "PromotePipelineTemplateDialog.razor",
        "ReleaseGateVerdict.razor",
        "ReleasePickerGrid.razor",
        "RunGanttStrip.razor",
        "RunParameterFields.razor",
        "RunParametersDialog.razor",
        "RunTimelineTree.razor",
        "SchemaPendingContractions.razor",
        "StageEditPanel.razor",
        "StageNode.razor",
        "StepEditDialog.razor",
        "StepStatusBadge.razor",
        "TemplateEditDialog.razor",
        "TemplateVersionHistoryDialog.razor",
        "TextExportDialog.razor",
        "VisualPipelineEditor.razor",
        // R-110: Components/Shared, Components/Users, Components/Settings (every .razor but _Imports.razor,
        // a markup-free file whose name exists in two folders and so cannot be looked up by name).
        // AetheusDataGrid.razor is in the list below: its root is an OmniStack that IS the grid host
        // (aetheus-grid-host), so the load-failure alert and the grid are separated by its gap and the
        // full-height chain (.app-body-wrapper > .aetheus-grid-host) is unchanged.
        "AdminIdentityNav.razor",
        "AdminSettings.razor",
        "Administration.razor",
        "AetheusLoader.razor",
        "AetheusVirtualList.razor",
        "AppDatabaseSizeView.razor",
        "AppErrorDetailDialog.razor",
        "AppErrorsView.razor",
        "AppIngestionSettings.razor",
        "AppMetricsView.razor",
        "AppPerformanceView.razor",
        "AppVisitorsView.razor",
        "AppWebAnalyticsPagesView.razor",
        "AppWebAnalyticsView.razor",
        "BrowserBackButton.razor",
        "CountBadge.razor",
        "DailyRecapSettingsPanel.razor",
        "DialogFooter.razor",
        "EmailSenderSettingsPanel.razor",
        "EmptyState.razor",
        "EntityAuditTrail.razor",
        "GradeBadge.razor",
        "HelpLink.razor",
        "InheritedFromBadge.razor",
        "MonitoredAppFormDialog.razor",
        "MonitoredAppsStatusGrid.razor",
        "NotFound.razor",
        "NotificationBody.razor",
        "NotificationDetailDialog.razor",
        "NotificationListDialog.razor",
        "OwnerTypeSelector.razor",
        "PageLoadProgressBar.razor",
        "PermissionsChangedDialog.razor",
        "PersonalAccessTokens.razor",
        "PipelineDependencyGrid.razor",
        "PipelineOwnerBadge.razor",
        "PipelineRunHierarchy.razor",
        "PipelineRunHistory.razor",
        "PipelineRunStatusBadge.razor",
        "PipelineRunTableLiveDuration.razor",
        "PipelineRunsGrid.razor",
        "PortCheckDialog.razor",
        "PortVerdictBadge.razor",
        "QualityGatePolicyEditor.razor",
        "ReleaseStatusBadge.razor",
        "ResourceUsageSummary.razor",
        "RoleAssignmentPicker.razor",
        "RoleCreateDialog.razor",
        "RoleEdit.razor",
        "Roles.razor",
        "Settings.razor",
        "ShortId.razor",
        "SiteAppearanceWindow.razor",
        "StatusCountBadge.razor",
        "TelemetrySendIntervalNote.razor",
        "UrlSyncedTabs.razor",
        "UserCreate.razor",
        "UserEdit.razor",
        "UserOwnedPipelines.razor",
        "UserPipelineHandoverDialog.razor",
        "Users.razor",
        "VariableLibrariesList.razor",
        "VaultsList.razor",
        "WizardDialog.razor",
        // R-110 (rest): every other migrated Components folder. Backups.razor, Logs.razor and Servers.razor
        // stay out: each file name exists in two folders, so it cannot be looked up by name.
        "AetheusDataGrid.razor",
        "AgentOfflineNotice.razor",
        "AgentUpdateProgressCard.razor",
        "AiContextFilePicker.razor",
        "AiResultDialog.razor",
        "AiRunnerProfileDialog.razor",
        "AiRunnerProfiles.razor",
        "AiTaskResults.razor",
        "AiTasks.razor",
        "AlertOccurrenceDetail.razor",
        "Alerts.razor",
        "AnalysisFindingDetail.razor",
        "ApiReference.razor",
        "AppLogsView.razor",
        "AppTelemetryPanel.razor",
        "ArtifactDetail.razor",
        "AuditDetailDialog.razor",
        "CommitDetail.razor",
        "DashboardEdit.razor",
        "DockerComposeTab.razor",
        "DockerContainersTab.razor",
        "DockerImagesTab.razor",
        "DockerNetworksTab.razor",
        "DockerProjectDialog.razor",
        "DockerRelationsGraph.razor",
        "DockerVolumesTab.razor",
        "Edit.razor",
        "EnvironmentEdit.razor",
        "EnvironmentSchemaTracking.razor",
        "GitCommitDetail.razor",
        "GitRepositoryDetail.razor",
        "Home.razor",
        "LogEntryDialog.razor",
        "Login.razor",
        "MailComplianceBanner.razor",
        "MailLeftoversCheck.razor",
        "MailTaskBanner.razor",
        "MailTemplates.razor",
        "MainLayout.razor",
        "ManageableServiceGrid.razor",
        "ModuleLinksTab.razor",
        "MonitoredAppsList.razor",
        "NotificationsAdmin.razor",
        "NotificationsInbox.razor",
        "PackageFeedPackagesDialog.razor",
        "PackageFeedsAdmin.razor",
        "PendingApprovalsPanel.razor",
        "Performance.razor",
        "ProjectDataRetentionSection.razor",
        "ProjectDeleteDialog.razor",
        "ProjectEditSection.razor",
        "ProjectOverviewSection.razor",
        "ProjectQualitySection.razor",
        "ReclaimSpaceView.razor",
        "ReleaseDetail.razor",
        "ReleaseProvenancePanel.razor",
        "RetiredServersDialog.razor",
        "ServerApacheSection.razor",
        "ServerAppsSection.razor",
        "ServerCertbotSection.razor",
        "ServerConfigSection.razor",
        "ServerDetailLayout.razor",
        "ServerDisksCard.razor",
        "ServerLogsSection.razor",
        "ServerMailDeliveryPanel.razor",
        "ServerMailLogsTab.razor",
        "ServerMailQueueTab.razor",
        "ServerMailReinstall.razor",
        "ServerMailSection.razor",
        "ServerMailSetupAssistant.razor",
        "ServerMailStatusList.razor",
        "ServerModulesSection.razor",
        "ServerOverviewSection.razor",
        "ServerPackageInventory.razor",
        "ServerPortsSection.razor",
        "ServerServicesSection.razor",
        "ServerSshConfigCard.razor",
        "ServiceActionsInFlightBanner.razor",
        "Supervision.razor",
        "SupervisionProjectsGrid.razor",
        "SystemLogs.razor",
        "TaskDetail.razor",
        "TaskListView.razor",
        "VariableLibraryEdit.razor",
        "VaultEdit.razor",
        "VersionHistoryDialog.razor",
        "WizardCombinedCopyAll.razor",
        "WizardConfigureStep.razor",
        "WizardInstallInstructions.razor",
    ];

    [Fact]
    public void ContainerSpacedFiles_BlocksCarryNoOuterMargin()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var css = LoadCss(front);
        var files = RepositoryScan.Enumerate(front, "*.razor").ToList();
        var violations = new List<string>();

        foreach (var name in ContainerSpacedFiles)
        {
            var file = Assert.Single(files, f => Path.GetFileName(f) == name);
            violations.AddRange(BlockSpacingScanner.ScanBlockMargins(File.ReadAllText(file), css).Select(v =>
                $"{name}:{v.Line}  {v.First} carries {v.Second} in {v.Parent}"));
        }

        Assert.True(violations.Count == 0,
            "A block carries a margin of its own. Blocks are separated only by the gap of their container "
            + "(an OmniStack at Gap Medium or more), never by omni-u-mt-*/omni-u-mb-* or a margin class "
            + "(STD-SPACE, docs/contracts/ui-patterns.md):\n  " + string.Join("\n  ", violations));
    }

    [Theory]
    [InlineData("<div><OmniAlert Class=\"omni-u-mt-sm\"/></div>")]
    [InlineData("<div><OmniCard Class=\"omni-u-p-md omni-u-mb-lg\"/></div>")]
    [InlineData("<OmniStack><OmniFieldset Class=\"omni-u-my-md\"/></OmniStack>")]
    [InlineData("<div><OmniAlert style=\"margin-top: 1rem\"/></div>")]
    public void StrictScanner_FlagsABlockThatCarriesAMargin_EvenAloneInAStack(string markup) =>
        Assert.Single(BlockSpacingScanner.ScanBlockMargins(markup, BlockSpacingScanner.CssSpacing.None));

    [Theory]
    [InlineData("<OmniStack><OmniAlert/><OmniCard Class=\"omni-u-p-md\"/></OmniStack>")]
    [InlineData("<OmniStack><OmniText Class=\"omni-u-mb-sm\"/><OmniCard/></OmniStack>")]
    [InlineData("<OmniStack Class=\"omni-u-mt-md\"><OmniCard/></OmniStack>")]
    [InlineData("<OmniCard Class=\"omni-u-mt-0\"/>")]
    public void StrictScanner_AcceptsBlocksWithoutAMargin_AndMarginsOnNonBlocks(string markup) =>
        Assert.Empty(BlockSpacingScanner.ScanBlockMargins(markup, BlockSpacingScanner.CssSpacing.None));

    [Fact]
    public void StrictScanner_FlagsABlockWhoseClassTheStylesheetGivesAMargin()
    {
        var css = new BlockSpacingScanner.CssSpacing(new HashSet<string>(), new HashSet<string> { "spaced" });

        Assert.Single(BlockSpacingScanner.ScanBlockMargins("<div><OmniCard Class=\"spaced\"/></div>", css));
        Assert.Empty(BlockSpacingScanner.ScanBlockMargins("<div><OmniCard Class=\"plain\"/></div>", css));
    }

    [Fact]
    public void Scanner_FlagsTwoBareSiblingBlocks()
    {
        var found = Scan("<div><OmniCard>a</OmniCard><OmniAlert>b</OmniAlert></div>");

        var violation = Assert.Single(found);
        Assert.Equal(("OmniCard", "OmniAlert", "div"), (violation.First, violation.Second, violation.Parent));
    }

    [Theory]
    [InlineData("<OmniStack><OmniCard/><OmniCard/></OmniStack>")]
    [InlineData("<OmniStack Gap=\"OmniSpacing.Large\"><OmniCard/><OmniCard/></OmniStack>")]
    [InlineData("<OmniStack Gap=\"@gap\"><OmniCard/><OmniCard/></OmniStack>")]
    [InlineData("<OmniTileGrid><OmniCard/><OmniCard/></OmniTileGrid>")]
    [InlineData("<OmniTileGrid Gap=\"OmniSpacing.Large\"><OmniCard/><OmniCard/></OmniTileGrid>")]
    [InlineData("<div><OmniCard Class=\"omni-u-mb-md\"/><OmniCard/></div>")]
    [InlineData("<div><OmniCard/><OmniCard Class=\"omni-u-mt-md\"/></div>")]
    [InlineData("<div><OmniCard/><OmniText>between</OmniText><OmniCard/></div>")]
    [InlineData("<div><OmniCard/><div class=\"spacer\"></div><OmniAlert/></div>")]
    [InlineData("<div><OmniCard/><OmniButton/></div>")]
    public void Scanner_AcceptsSeparatedOrNonAdjacentBlocks(string markup) =>
        Assert.Empty(Scan(markup));

    [Theory]
    [InlineData("<OmniStack Gap=\"OmniSpacing.None\"><OmniCard/><OmniCard/></OmniStack>")]
    [InlineData("<OmniStack Gap=\"OmniSpacing.XSmall\"><OmniCard/><OmniCard/></OmniStack>")]
    [InlineData("<OmniStack Gap=\"OmniSpacing.Small\"><OmniCard/><OmniCard/></OmniStack>")]
    [InlineData("<OmniTileGrid Gap=\"OmniSpacing.Small\"><OmniCard/><OmniCard/></OmniTileGrid>")]
    [InlineData("<div><OmniCard/><OmniCard Class=\"omni-u-mt-sm\"/></div>")]
    [InlineData("<div><OmniCard Class=\"omni-u-mb-0\"/><OmniCard/></div>")]
    [InlineData("<OmniCard/><OmniCard/>")]
    [InlineData("<div><OmniDataGrid TItem=\"X\"></OmniDataGrid><OmniTabs></OmniTabs></div>")]
    public void Scanner_FlagsBlocksWithNoVisibleSpacing(string markup) =>
        Assert.Single(Scan(markup));

    [Fact]
    public void Scanner_TreatsTheTwoBranchesOfOneIfAsExclusive()
    {
        var exclusive = Scan("<div>@if (a) { <OmniAlert/> } else { <OmniCard/> }</div>");
        var together = Scan("<div>@if (a) { <OmniAlert/> } @if (b) { <OmniCard/> }</div>");

        Assert.Empty(exclusive);
        Assert.Single(together);
    }

    [Fact]
    public void Scanner_FlagsBlocksRepeatedByALoopOutsideAGappedContainer()
    {
        var bare = Scan("<div>@foreach (var x in xs) { <OmniCard/> }</div>");
        var stacked = Scan("<OmniStack>@foreach (var x in xs) { <OmniCard/> }</OmniStack>");

        Assert.Single(bare);
        Assert.Empty(stacked);
    }

    [Fact]
    public void Scanner_AcceptsAContainerOrAnElementTheStylesheetSpaces()
    {
        var css = new BlockSpacingScanner.CssSpacing(
            new HashSet<string> { "gapped" }, new HashSet<string> { "margined" });

        Assert.Empty(BlockSpacingScanner.Scan("<div class=\"gapped\"><OmniCard/><OmniCard/></div>", css));
        Assert.Empty(BlockSpacingScanner.Scan("<div><OmniCard class=\"margined\"/><OmniCard/></div>", css));
        Assert.Single(BlockSpacingScanner.Scan("<div class=\"other\"><OmniCard/><OmniCard/></div>", css));
    }

    private static List<BlockSpacingScanner.Violation> Scan(string markup) =>
        BlockSpacingScanner.Scan(markup, BlockSpacingScanner.CssSpacing.None);

    private static BlockSpacingScanner.CssSpacing LoadCss(string front)
    {
        var gaps = new HashSet<string>(StringComparer.Ordinal);
        var margins = new HashSet<string>(StringComparer.Ordinal);
        var sheets = RepositoryScan.Enumerate(Path.Combine(front, "wwwroot", "css"), "*.css")
            .Concat(RepositoryScan.EnumerateOptional(front, "*.razor.css"));
        foreach (var sheet in sheets)
        {
            foreach (Match rule in CssRule.Matches(File.ReadAllText(sheet)))
            {
                AddClasses(rule, gaps, margins);
            }
        }

        return new BlockSpacingScanner.CssSpacing(gaps, margins);
    }

    private static void AddClasses(Match rule, HashSet<string> gaps, HashSet<string> margins)
    {
        var body = rule.Groups["body"].Value;
        var isGap = GapDeclaration.IsMatch(body);
        var isMargin = MarginDeclaration.IsMatch(body);
        if (!isGap && !isMargin)
        {
            return;
        }

        foreach (var selector in rule.Groups["sel"].Value.Split(','))
        {
            var subject = selector.Trim().Split([' ', '>', '+', '~'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            foreach (Match name in ClassName.Matches(subject ?? string.Empty))
            {
                (isGap ? gaps : margins).Add(name.Groups["c"].Value);
                if (isGap && isMargin)
                {
                    margins.Add(name.Groups["c"].Value);
                }
            }
        }
    }
}
