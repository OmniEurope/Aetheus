// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>A mail domain served by a server whose mail stack Aetheus reports, and the host to submit to.</summary>
public sealed record ManagedMailDomain(int ServerId, string DomainName, string SubmissionHost);
