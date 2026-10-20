// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// A Docker daemon held in memory for the pipeline functions that drive the Docker CLI: containers,
/// volumes, networks and images, answered for exactly the argv the functions (and the scripts they
/// port) send, and mutated by the removals. Every command is recorded; <see cref="Override"/> answers a
/// command first (a failure, a race) when it returns a value.
/// </summary>
internal sealed class FakeDockerHost
{
    private const string ProjectFilter = "label=com.docker.compose.project";
    private const string ProjectFormat = "{{.Label \"com.docker.compose.project\"}}";

    public List<FakeContainer> Containers { get; } = [];
    public List<(string Name, string? Project)> Volumes { get; } = [];
    public List<(string Id, string? Project)> Networks { get; } = [];

    /// <summary>Tags, newest first, as <c>docker image ls</c> lists them.</summary>
    public List<(string Id, string Repository, string Tag)> Images { get; } = [];
    public List<string> Commands { get; } = [];
    public Func<string, (int, string[])?> Override { get; set; } = _ => null;

    public Task<(int, string[])> AnswerAsync(ProcessStartInfo process, CancellationToken _)
    {
        var arguments = process.ArgumentList.ToArray();
        var command = string.Join(' ', arguments);
        lock (Commands)
            Commands.Add(command);
        return Task.FromResult(Override(command) ?? Answer(arguments));
    }

    private (int, string[]) Answer(string[] a) =>
        AnswerContainers(a) ?? AnswerVolumes(a) ?? AnswerNetworks(a) ?? AnswerImages(a) ?? AnswerBuildx(a) ?? (127, []);

    private (int, string[])? AnswerContainers(string[] a) => a switch
    {
        ["ps", "-aq", "--filter", var filter] => Ok(Containers.Where(c => Labelled(filter, c.Project)).Select(c => c.Id)),
        ["ps", "-a", "--format", "{{.ID}} {{.Names}}"] => Ok(Containers.Select(c => $"{c.Id} {c.Name}")),
        ["ps", "-a", "--format", "{{.Names}}"] => Ok(Containers.Select(c => c.Name)),
        ["ps", "-a", "--filter", ProjectFilter, "--format", ProjectFormat] => Ok(Containers.Select(c => c.Project).OfType<string>()),
        ["ps", "-aq", "--no-trunc"] => Ok(Containers.Select(c => c.Id)),
        ["ps", "-aq", "--no-trunc", "--filter", var id] => Ok(Containers.Where(c => "id=" + c.Id == id).Select(c => c.Id)),
        ["inspect", "--format", "{{.Name}}", .. var ids] => Ok(ids.Select(id => "/" + Containers.Single(c => c.Id == id).Name)),
        ["inspect", "--format", "{{.Image}}", var id] => Containers.FirstOrDefault(c => c.Id == id) is { } c ? Ok([c.Image]) : (1, []),
        ["rm", "-f", .. var ids] => Remove(ids, Containers, (c, id) => c.Id == id),
        _ => null
    };

    private (int, string[])? AnswerVolumes(string[] a) => a switch
    {
        ["volume", "ls", "-q"] => Ok(Volumes.Select(v => v.Name)),
        ["volume", "inspect", var name] => Volumes.Any(v => v.Name == name) ? Ok([name]) : (1, []),
        ["volume", "ls", "-q", "--filter", var filter] => Ok(Volumes.Where(v => Labelled(filter, v.Project)).Select(v => v.Name)),
        ["volume", "ls", "--filter", ProjectFilter, "--format", ProjectFormat] => Ok(Volumes.Select(v => v.Project).OfType<string>()),
        ["volume", "rm", .. var names] => Remove(names, Volumes, (v, name) => v.Name == name),
        _ => null
    };

    private (int, string[])? AnswerNetworks(string[] a) => a switch
    {
        ["network", "ls", "-q", "--filter", var filter] => Ok(Networks.Where(n => Labelled(filter, n.Project)).Select(n => n.Id)),
        ["network", "ls", "--filter", ProjectFilter, "--format", ProjectFormat] => Ok(Networks.Select(n => n.Project).OfType<string>()),
        ["network", "rm", .. var ids] => Remove(ids, Networks, (n, id) => n.Id == id),
        _ => null
    };

    private (int, string[])? AnswerImages(string[] a) => a switch
    {
        ["image", "ls", "-q", var repository] => Ok(Images.Where(i => i.Repository == repository).Select(i => i.Id)),
        ["image", "ls", var repository, "--no-trunc", "--format", var format] =>
            Ok(Images.Where(i => i.Repository == repository).Select(i => format.EndsWith("|{{.CreatedAt}}", StringComparison.Ordinal)
                ? $"{i.Id}|{i.Repository}:{i.Tag}|2026-10-13 12:00:00 +0000 UTC"
                : $"{i.Id}|{i.Repository}:{i.Tag}")),
        ["image", "inspect", "--format", "{{.Id}}", var tag] =>
            Images.FirstOrDefault(i => $"{i.Repository}:{i.Tag}" == tag) is { Id: not null } image ? Ok([image.Id]) : (1, []),
        ["image", "rm", "-f", .. var ids] => Remove(ids, Images, (i, id) => i.Id == id),
        ["image", "rm", var tag] => Remove([tag], Images, (i, t) => $"{i.Repository}:{i.Tag}" == t),
        _ => null
    };

    private static (int, string[])? AnswerBuildx(string[] a) => a switch
    {
        ["buildx", "prune", ..] => (0, ["Total: 0B"]),
        _ => null
    };

    private static bool Labelled(string filter, string? project) =>
        project is not null && filter == $"{ProjectFilter}={project}";

    private static (int, string[]) Ok(IEnumerable<string> lines) => (0, [.. lines]);

    private static (int, string[]) Remove<T>(IEnumerable<string> keys, List<T> items, Func<T, string, bool> matches)
    {
        var removed = new List<string>();
        foreach (var key in keys)
            if (items.RemoveAll(item => matches(item, key)) > 0)
                removed.Add(key);
        return (0, [.. removed]);
    }
}

/// <summary>One container of <see cref="FakeDockerHost"/>.</summary>
internal sealed record FakeContainer(string Id, string Name, string? Project, string Image);
