# Player authentication security

## Trust boundary

WPF Client никогда не открывает соединение с GameClub.Server. Login проходит по цепочке Client → local Named Pipe → Agent → HTTPS → Server. Agent подписывает каждый player API request существующим station HMAC credential. Login body не содержит `stationId`; Server использует только station id, полученный после проверки HMAC signature, timestamp, nonce и replay protection.

## Password handling

- Client использует `PasswordBox` и очищает его сразу после постановки IPC-запроса на отправку.
- IPC содержит password только в `PlayerLoginRequest`; state/result messages его не содержат.
- Agent не сохраняет password и не добавляет username/password в structured logs.
- Server ограничивает password request 128 символами.
- PostgreSQL хранит только versioned ASP.NET Core Identity `PasswordHasher<User>` hash (PBKDF2), но не plaintext password.
- API DTO и station projection не имеют `Password`/`PasswordHash` полей.

## Enumeration and abuse controls

Unknown username и неверный password возвращают одинаковый `INVALID_CREDENTIALS`; для неизвестного username выполняется проверка dummy hash. Disabled и banned accounts получают отдельные UI-safe codes только после правильного password. Per-station in-memory limiter допускает до 10 запросов и до 5 неуспешных попыток в фиксированном минутном окне; превышение даёт HTTP 429 и `PlayerLoginRateLimited` audit event. Успешный вход не стирает накопленные failures в текущем окне.

Ограничитель сбрасывается при restart Server и не является permanent lockout. Для распределённого production deployment его нужно заменить общим Redis/PostgreSQL-backed limiter, но это не расширяется в текущем self-hosted MVP.

## Session consistency

`PlayerAuthSession` живёт 12 часов и не является billing session. Partial unique indexes разрешают не более одной строки `Active` для одного User и одной Station. Service завершает истёкшие или принадлежащие disabled/banned игроку строки при сверке и перед новой попыткой, обрабатывает database uniqueness race. Смена статуса игрока требует Employee `ClubManageMoney`; gaming monitor закрывает связанную игровую сессию после обнаружения недействующей авторизации.

Agent сохраняет только безопасный session snapshot. После restart snapshot не считается доказательством активной server session: Client видит `Offline`, пока HMAC-authenticated current-session запросы не подтвердят auth и gaming. Logout очищает Agent snapshot только после успешного server response. Signed `LogoutPlayer` использует тот же порядок. В Stage 5–7 logout и истечение auth завершают также связанную gaming session с расчётом postpaid; ранний prepaid logout не делает автоматического возврата. Пауза gaming не продлевает 12-часовой auth TTL.

Игрок создаётся через Employee-защищённый `POST /api/admin/users`, не через публичную регистрацию. Пароль создания проходит тот же hash boundary и не возвращается в DTO. Employee authentication отделена от player identity; пароль игрока не даёт management permissions.

## Audit data

Security audit использует `UserId`, `StationId`, timestamp, result и при наличии session id. События: `PlayerLoginSucceeded`, `PlayerLoginFailed`, `PlayerLoginRateLimited`, `DuplicateLoginRejected`, `PlayerLogout`. Password, hash, Agent secret и raw authorization data не записываются.
