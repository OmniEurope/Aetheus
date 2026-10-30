// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Shared;

/// <summary>
/// One refusal as data rather than as a sentence: a stable <see cref="Code"/> the front translates
/// (resource <c>LaunchProblem_{Code}</c>) and the values that sentence names. The parameters are user
/// and repository data (stage names, paths, hosts): a reader renders them as text, never as markup.
/// The parameter <c>chain</c>, when present, names the triggered pipeline the problem belongs to.
/// </summary>
public sealed record ProblemDto(string Code, Dictionary<string, string> Parameters);
