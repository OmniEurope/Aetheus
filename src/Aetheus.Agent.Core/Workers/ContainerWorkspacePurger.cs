// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Workers;

internal static class ContainerWorkspacePurger
{
    internal static void Purge(
        string workDirectory,
        int pipelineRunId,
        ILogger logger)
    {
        foreach (var parent in new[] { "cw", "container-state" })
        {
            var target = Path.Combine(workDirectory, parent, pipelineRunId.ToString());
            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    ex,
                    "Could not purge container workspace state {Path} for pipeline run {RunId}",
                    target,
                    pipelineRunId);
            }
        }
        ContainerWorkspaceMaterializer.ForgetWorkspace(pipelineRunId);
    }
}
