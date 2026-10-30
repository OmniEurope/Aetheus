// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>Recette R-97: one kind of resource a project deployed on a server, which deleting the
/// project removes too (ADR-058). The order is the order of the removal on the agent: containers before
/// the volumes and networks they hold, virtual hosts before the certificates they name, containers before the
/// images they run, virtual hosts before the blue-green upstream file whose <c>Define</c> they use.</summary>
public enum ProjectServerResourceKind
{
    Container = 0,
    Volume = 1,
    Network = 2,
    VirtualHost = 3,
    Certificate = 4,
    StateDirectory = 5,
    BackupArchives = 6,

    /// <summary>An image the Compose project built (label <c>com.docker.compose.project</c>).</summary>
    Image = 7,

    /// <summary>The blue-green upstream file of <c>sites-available</c> the library names in <c>UPSTREAM_CONF</c>.</summary>
    UpstreamConfiguration = 8,

    /// <summary>The copy of the repository a deployment pipeline unpacks in <c>.deployments/&lt;Compose project&gt;</c> of the
    /// agent's working directory (<c>DEPLOY_DIR</c> of the generic deploy templates), source code included (recette R-85).</summary>
    DeploymentDirectory = 9
}

/// <summary>What the agent says of one resource: found by a discovery, or the result of its removal.</summary>
public enum ProjectServerResourceOutcome
{
    /// <summary>Present on the server (discovery).</summary>
    Found = 0,

    /// <summary>Removed, and no longer present.</summary>
    Removed = 1,

    /// <summary>Listed for removal but already gone when the removal ran: nothing is left of it.</summary>
    Absent = 2,

    /// <summary>The removal was attempted or refused and the resource is still there.</summary>
    Failed = 3,

    /// <summary>Present on the server but not in the list the user confirmed (it appeared after the
    /// preview): left as it is.</summary>
    NotListed = 4
}

/// <summary>The phase of a server task of a project deletion.</summary>
public enum ProjectServerTaskPhase
{
    Discovery = 0,
    Removal = 1
}

/// <summary>Where a server task of a project deletion stands.</summary>
public enum ProjectServerTaskState
{
    /// <summary>Queued or running on the agent.</summary>
    Running = 0,

    /// <summary>Finished: every resource read (discovery), or every confirmed resource gone (removal).</summary>
    Succeeded = 1,

    /// <summary>Finished without that proof: the agent failed, timed out, could not read something or
    /// could not remove something.</summary>
    Failed = 2
}

/// <summary>Whether Aetheus can ask a server's agent about the project's resources.</summary>
public enum ProjectDeletionServerState
{
    Ready = 0,
    Offline = 1,

    /// <summary>The agent does not publish the <c>project.resources</c> capability: update it.</summary>
    AgentTooOld = 2,

    /// <summary>The user deleting the project has no Write permission on this server: it is neither read nor
    /// cleaned, and what the project deployed there stays.</summary>
    NoWriteAccess = 3
}

/// <summary>Why a value of the project's libraries is not used to find or remove anything.</summary>
public enum ProjectDeletionRefusal
{
    /// <summary>Not a valid Compose project, host name or path.</summary>
    InvalidName = 0,

    /// <summary>A state directory outside <c>/var/lib/aetheus-agent/projects/&lt;project slug&gt;[-...]</c> and
    /// <c>/var/lib/&lt;project slug&gt;[-...]</c>.</summary>
    OutsideProjectBase = 1,

    /// <summary>A <c>$(NAME)</c> reference no library entry defines.</summary>
    UnresolvedReference = 2,

    /// <summary>Another project's library names the same value: removing it would touch that project.</summary>
    SharedWithAnotherProject = 3,

    /// <summary>The environment has no server Aetheus could ask.</summary>
    NoServer = 4,

    /// <summary>A Compose project not named after the project (<c>&lt;slug&gt;</c>, <c>&lt;slug&gt;-...</c> or
    /// <c>&lt;slug&gt;_...</c>): nothing ties it to this project.</summary>
    NotNamedAfterProject = 5,

    /// <summary>Named in the control plane namespace (<c>aetheus</c>, <c>aetheus-...</c>): it may be Aetheus itself.</summary>
    ReservedForControlPlane = 6,

    /// <summary>A public host of the Aetheus control plane (its API or front-end).</summary>
    ControlPlaneHost = 7
}
