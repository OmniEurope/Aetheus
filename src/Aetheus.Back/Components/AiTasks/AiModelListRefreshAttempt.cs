// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AiTasks;

/// <summary>The outcome of a "read now" of a profile's model list: the profile as stored afterwards
/// (list, read time, last error), or, when <see cref="RetryAfter"/> is set, as it was, because the list
/// was read too recently to call the provider again.</summary>
public sealed record AiModelListRefreshAttempt(AiRunnerProfileDto Profile, TimeSpan? RetryAfter);
