# Stage 4 — targeted authentication fixes

This change preserves the existing player-authentication endpoints and schema.

- Failed login attempts are cumulative within the one-minute station window. A successful login no longer clears earlier failures; the counter resets only when the window expires.
- An unknown username runs password verification against an in-memory dummy Identity V3 hash before returning the same `INVALID_CREDENTIALS` response as a wrong password. The dummy identity is never persisted or authenticated. This removes the obvious missing password-hash work; it does not claim identical wall-clock response times. The dummy hash uses the same default Identity hasher configuration currently registered in the server.
- Current-session reconciliation ends sessions belonging to disabled or banned users. A new login also releases a station occupied by such an invalid session. Expiration still has precedence and keeps the `Expired` status.
- Only PostgreSQL unique violations for `IX_users_normalized_username` are translated into `DuplicateUsername`. Connection failures, unrelated constraints and serialization failures propagate as server/database errors instead of a misleading duplicate-user result.
- Concurrent-login handling is restricted to the two active-session unique indexes. Raw database exceptions are no longer logged for these expected conflicts, avoiding database-detail leakage.
- Both IPC peers use `Contracts/Client/BoundedPipeReader.cs`, enforcing the frame limit while reading, before an oversized line can be allocated in full.
- Invalid IPC JSON logs only the exception type, not the exception payload. Agent retries failed pipe connections after one second.
- Client connection initialization and its first send are inside `try/finally`, so an initial-send failure cannot leave a stale active session reference.

Regression coverage is in `PlayerAuthenticationServiceTests`, `UserProvisioningDatabaseErrorTests` and five `BoundedPipeReaderTests`. Database-error classification tests inject synthetic Npgsql exceptions through an EF Core interceptor; they do not replace a real PostgreSQL concurrency/migration integration check. Existing-session invalidation happens when the server processes reconciliation or a new login, not as an immediate push on a status mutation.
