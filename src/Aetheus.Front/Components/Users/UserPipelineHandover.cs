// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

/// <summary> What the deactivation or deletion dialog decided for the pipelines the user owns: the
/// active user that takes them over, or <see cref="Force"/> to leave them with an inactive owner.
/// Both empty when the user owns no pipeline. </summary>
public sealed record UserPipelineHandover(string? TransferTo, bool Force);
