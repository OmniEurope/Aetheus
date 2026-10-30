// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// One launch refusal or launch warning: a stable <see cref="Code"/> from <see cref="PipelineProblemCodes"/>,
/// the values its sentence names, and the same sentence in <see cref="English"/> for logs, audit, the run's
/// warnings and API clients. The front translates the code (resource <c>LaunchProblem_{Code}</c>) and falls
/// back to the English when it does not know it.
/// </summary>
public sealed record PipelineProblem(string Code, IReadOnlyDictionary<string, string> Parameters, string English)
{
    /// <summary>The parameter naming the triggered pipeline (the chain that reaches it) a problem belongs to.</summary>
    public const string ChainParameter = "chain";

    /// <summary>A problem whose parameters are given as name/value pairs, formatted invariantly.</summary>
    public static PipelineProblem Of(string code, string english, params (string Name, object? Value)[] parameters)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
            values[name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return new PipelineProblem(code, values, english);
    }

    /// <summary>The same problem, attributed to the triggered pipeline reached by <paramref name="chain"/>.</summary>
    public PipelineProblem InChain(string chain)
    {
        var values = new Dictionary<string, string>(Parameters, StringComparer.Ordinal);
        values[ChainParameter] = values.TryGetValue(ChainParameter, out var inner) ? $"{chain}: {inner}" : chain;
        return new PipelineProblem(Code, values, $"{chain}: {English}");
    }

    public ProblemDto ToDto() => new(Code, new Dictionary<string, string>(Parameters, StringComparer.Ordinal));

    /// <summary>The problem as a string (a warning stored on a run, an advisory warning of the preview): the front
    /// says it in the user's language, an API client or a log reader reads the English inside it. <paramref
    /// name="inner"/> is the refusal of another place this one wraps, written where the sentence names
    /// <c>{detail}</c>.</summary>
    public string ToStored(IReadOnlyList<ProblemDto>? inner = null) => StoredProblem.Compose(ToDto(), English, inner);

    /// <summary>Several problems as one string: the problem itself when there is one, else a wrapper that
    /// writes each of them where its sentence names <c>{detail}</c>; <c>null</c> when there are none.</summary>
    public static string? StoredTogether(IReadOnlyCollection<PipelineProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (problems.Count == 0) return null;
        if (problems.Count == 1) return problems.First().ToStored();
        var english = string.Join(" ", problems.Select(problem => problem.English));
        return Of(PipelineProblemCodes.ProblemsTogether, english, ("detail", english))
            .ToStored([.. problems.Select(problem => problem.ToDto())]);
    }

    /// <summary>A problem carried by a refusal from elsewhere (<see cref="BadRequestException.Problems"/>),
    /// with the English of that refusal.</summary>
    public static PipelineProblem FromDto(ProblemDto problem, string english)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new PipelineProblem(problem.Code, new Dictionary<string, string>(problem.Parameters, StringComparer.Ordinal), english);
    }

    /// <summary>The 400 that refuses with these problems: the English sentences joined as the message, the codes
    /// next to it for the front.</summary>
    public static BadRequestException Refusal(IReadOnlyCollection<PipelineProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return new BadRequestException(
            string.Join(" ", problems.Select(problem => problem.English)), [.. problems.Select(problem => problem.ToDto())]);
    }

    /// <summary>The 400 that refuses with this one problem.</summary>
    public BadRequestException ToRefusal() => Refusal([this]);

    public override string ToString() => English;

    /// <summary>Names quoted and joined as the English sentences quote them: <c>'a', 'b'</c>.</summary>
    internal static string QuotedList(IEnumerable<string> names) => string.Join(", ", names.Select(name => $"'{name}'"));
}

/// <summary>Everything the blocking preflight concluded: what it refused, what it verified, and what
/// it noted without refusing. <see cref="Warnings"/> are kept apart from <see cref="Problems"/> so an
/// observation (an undeclared listener under <c>observed_port_policy: warning</c>) cannot be mistaken
/// for a refusal.</summary>
public sealed record PipelinePreflightOutcome
{
    public List<PipelineProblem> Problems { get; init; } = [];
    public List<PreflightCheckDto> Checks { get; init; } = [];
    public List<PipelineProblem> Warnings { get; init; } = [];
}
