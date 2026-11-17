// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>The affinity runner of a run as a caller already read it, so the system task factory does not read it
/// again. A null <see cref="ServerId"/> is an answer (the run has no affinity yet), unlike no
/// <see cref="KnownRunAffinity"/> at all, which means the caller did not read it.</summary>
public readonly record struct KnownRunAffinity(int? ServerId);
