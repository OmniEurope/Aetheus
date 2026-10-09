// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Docker;

/// <summary>
/// Names of the Docker agent tasks whose output a page waits for. The backend names the task and the
/// front recognises its completion by the same name, so both read it from here.
/// </summary>
public static class DockerTaskNames
{
    /// <summary>The task that reads the compose file of one stack (<c>docker compose -p {stack} config</c>).</summary>
    public static string ComposeFile(string stackName) => $"Docker compose file - {stackName}";
}
