// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Aetheus.Back.Components.PackageRegistry;

/// <summary>
/// Reads a deployed POM (XXE-safe) and checks that it describes the coordinates of the path it is
/// deployed to, so a POM can never claim another artifact's identity. A value still holding an
/// unresolved <c>${...}</c> property is not compared: Maven left it to the consumer.
/// </summary>
internal static class MavenPomInspector
{
    public const int MaxPomBytes = 4 * 1024 * 1024;

    public static MavenPom Inspect(byte[] content, MavenRepositoryPath path)
    {
        XDocument document;
        try
        {
            document = SafeXml.Load(Encoding.UTF8.GetString(content).TrimStart('﻿'));
        }
        catch (XmlException ex)
        {
            throw new BadRequestException($"The POM is not well-formed XML: {ex.Message}");
        }

        var project = document.Root;
        if (project is null || project.Name.LocalName != "project")
            throw new BadRequestException("The POM has no <project> root element.");

        var parent = Child(project, "parent");
        var groupId = Text(project, "groupId") ?? (parent is null ? null : Text(parent, "groupId"));
        var artifactId = Text(project, "artifactId");
        var version = Text(project, "version") ?? (parent is null ? null : Text(parent, "version"));

        Require("artifactId", artifactId, path.ArtifactId);
        Require("groupId", groupId, path.GroupId);
        Require("version", version, path.Version!);

        var description = Text(project, "description") ?? Text(project, "name");
        if (description is { Length: > 1000 })
            description = description[..1000];
        return new MavenPom(description);
    }

    private static void Require(string field, string? declared, string expected)
    {
        if (declared is null)
            throw new BadRequestException($"The POM declares no {field}.");
        if (!declared.Contains("${", StringComparison.Ordinal)
            && !string.Equals(declared, expected, StringComparison.Ordinal))
            throw new BadRequestException(
                $"The POM {field} '{declared}' does not match the deployment path ('{expected}').");
    }

    private static XElement? Child(XElement element, string name)
        => element.Elements().FirstOrDefault(child => child.Name.LocalName == name);

    private static string? Text(XElement element, string name)
        => Child(element, name)?.Value.Trim() is { Length: > 0 } value ? value : null;
}

internal sealed record MavenPom(string? Description);
