// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.Sections;

public partial class Monitoring
{
    /// <summary> The application the telemetry panel opens on. A link from the supervision of all
    /// projects, or from the project's own list of applications, names it here, so there is one
    /// supervision page per project and no second page per application. </summary>
    [SupplyParameterFromQuery(Name = "app")] public int? App { get; set; }
}
