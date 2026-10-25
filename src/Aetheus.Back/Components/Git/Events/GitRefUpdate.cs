// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git.Events;

/// <summary>A branch update requested by the client and confirmed by receive-pack.</summary>
/// <param name="OldObjectId">What the ref pointed at before this push, as git announced it. All
/// zeroes for a branch this push creates, in which case there is no range to diff. Kept because a
/// path filter has to look at the whole push, not at its tip commit: a push of three commits whose
/// last one only touches a README still changed code.</param>
public sealed record GitRefUpdate(string Reference, string NewObjectId, string OldObjectId = "");
