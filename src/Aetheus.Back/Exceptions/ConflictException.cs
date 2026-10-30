// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Exceptions;

/// <summary>A 409. <see cref="Exception.Message"/> stays English (logs, audit, API clients);
/// <see cref="Code"/>, when given, names the refusal for the front to say in the user's language.</summary>
public sealed class ConflictException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}
