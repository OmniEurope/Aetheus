// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>A file a function downloads and trusts only when its bytes match the pinned SHA-256
/// (lowercase hex).</summary>
internal sealed record PinnedDownload(Uri Source, string Sha256);
