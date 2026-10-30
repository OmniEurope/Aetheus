// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Projects;

namespace Aetheus.Back.Components.Shared;

/// <summary>
/// The prefix of the resources a project owns by convention: <c>&lt;slug&gt;.analytics</c> for its
/// analytics vault, <c>&lt;slug&gt;.share</c> or <c>&lt;slug&gt;.prod</c> for the libraries a generic
/// pipeline template reads (recette R3-67). One calculation, so a template and the analytics vault
/// never disagree on what a project is called; it lives in <see cref="ProjectSlugs"/>, which the front shares.
/// </summary>
public static class ProjectResourceNames
{
    /// <inheritdoc cref="ProjectSlugs.Of"/>
    public static string Slug(string projectName, int projectId) => ProjectSlugs.Of(projectName, projectId);
}
