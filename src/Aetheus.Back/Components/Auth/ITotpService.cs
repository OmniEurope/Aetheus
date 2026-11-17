// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Auth;

public interface ITotpService
{
    Task<TotpSetupResponse> SetupTotpAsync(int userId, CancellationToken ct = default);
    Task<bool> VerifyAndEnableTotpAsync(int userId, string code, CancellationToken ct = default);
    Task<bool> DisableTotpAsync(int userId, TotpDisableRequest request, CancellationToken ct = default);

    /// <summary>Validates a TOTP code against the user's encrypted secret.</summary>
    bool ValidateTotpCode(string encryptedSecret, string code);

    /// <summary>Consumes a one-time recovery code. Returns true if the code was valid.</summary>
    Task<bool> ConsumeRecoveryCodeAsync(int userId, string code, CancellationToken ct = default);
}
