<!-- SPDX-License-Identifier: EUPL-1.2 -->
# Auth

Authentication and authorization: JWT login, refresh-token rotation, agent registration tokens, RBAC roles and resource permissions, TOTP 2FA, account lockout, and external login providers.

## API Surface

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/auth/public-demo` | GET | Anonymous | Report whether public-demo mode is enabled |
| `/api/auth/login` | POST | Anonymous | User login (rate-limited) |
| `/api/auth/register` | POST | Anonymous | Agent server registration |
| `/api/auth/renew` | POST | User | Renew JWT before expiry |
| `/api/auth/token/refresh` | POST | Anonymous | Refresh-token rotation |
| `/api/auth/logout` | POST | User (rate-limited `auth-token`) | The client logs out: the server forgets what it holds in memory for the user (the typed mailbox passwords, R1-37); revokes no token |
| `/api/auth/session-ended` | POST | Anonymous (rate-limited `auth-token`) | Client reports why it ended a session (known reason code + correlation id); one log line, Information for normal ends, Warning for `refresh_replay` and `refresh_rejected` (R2-018) |
| `/api/auth/external-login` | POST | Trusted gateway | External provider login; requires the shared `X-Aetheus-External-Auth` gateway secret |
| `/api/auth/registration-tokens` | GET | Admin | List registration tokens |
| `/api/auth/registration-tokens/{id}` | GET | Admin | Single registration token (wizard verify-step poll) |
| `/api/auth/registration-tokens` | POST | Admin | Create registration token |
| `/api/auth/servers/{id}/rotate-token` | POST | Admin | Rotate agent token |
| `/api/auth/totp/setup` | POST | User | Begin TOTP 2FA setup |
| `/api/auth/totp/verify` | POST | User | Verify and enable TOTP |
| `/api/auth/totp/disable` | POST | User | Disable TOTP |
| `/api/auth/users/{id}/unlock` | POST | Admin | Unlock locked-out user |
| `/api/roles` | GET | Admin | List roles |
| `/api/roles/{id}` | GET | Admin | Role detail |
| `/api/roles` | POST | Admin | Create role |
| `/api/roles/{id}` | PUT | Admin | Update role |
| `/api/roles/{id}` | DELETE | Admin | Delete role |
| `/api/roles/{id}/permissions` | GET | Admin | Role permissions |
| `/api/roles/{id}/permissions` | PUT | Admin | Set role permissions |
| `/api/roles/{id}/clone` | POST | Admin | Clone role |
| `/api/roles/{id}/users` | GET | Admin | Lister les utilisateurs du role |
| `/api/roles/{id}/available-users` | GET | Admin | Lister les utilisateurs disponibles pour le role |
| `/api/roles/{id}/users` | POST | Admin | Ajouter un utilisateur au role |
| `/api/roles/{id}/users/{userId}` | DELETE | Admin | Retirer un utilisateur du role |
| `/api/users/{id}/effective-permissions` | GET | Admin | User effective permissions |
| `/api/users/me/permissions` | GET | User | Current user permissions; for the bootstrap identity (deployment smoke account or configured bootstrap admin, no user row), built from the token's role claims (R2-014) |

## Key Classes

- `AuthController` -- login, registration, token, TOTP, lockout
- `RolesController` -- RBAC role + permission management
- `IAuthService` / `AuthService` -- auth logic, token issuance, agent renewal (token-building helpers extracted to the static `AuthTokenHelper`)
- `IServerEnrollmentService` / `ServerEnrollmentService` -- agent server enrollment (`POST /api/auth/register`), extracted from `AuthService`
- `IAuthRepository` / `AuthRepository` -- user/token EF access
- `IRoleService` / `RoleService` -- role CRUD, permission resolution
- `IRoleRepository` / `RoleRepository` -- role EF access
- `ITotpService` / `TotpService` -- TOTP secret generation and verification
- `AgentTokenAuthenticationHandler` -- validates agent bearer tokens
- `JwtOptions` (in `Components/Shared/`) -- JWT configuration binding

## Cross-Module Dependencies

- Depends on: Audit, Users, Shared
- Depended on by: Servers (token renewal), Git (smart HTTP basic auth), AgentInstaller
