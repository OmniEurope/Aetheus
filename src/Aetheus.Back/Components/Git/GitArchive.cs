// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

/// <summary>A repository snapshot as a zip being streamed out of <c>git archive</c>, with the file
/// name the download offers.</summary>
public sealed record GitArchive(Stream Content, string FileName);
