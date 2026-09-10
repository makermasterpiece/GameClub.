# Security architecture

## Trust boundaries

`Client ↔ local IPC ↔ Agent ↔ network ↔ Server ↔ PostgreSQL` и `Admin browser ↔ Server` — разные границы доверия. Ни station ID из route/header, ни network location сами по себе identity не являются. Начиная со Stage 7 management plane имеет отдельные Employee JWT и серверные permission policies; он не использует Agent JWT. Stage 8 добавляет отдельную policy команд питания и identity-bound logout; Stage 9 — ограниченный запуск Playnite; Stage 10 — атомарный POS и смены с Employee permissions. Stage 11–12 и OS hardening не выполнены. Ограничения: [Stage 9](stage9.md) и [Stage 10](stage10.md).

## Enrollment and identity

Server генерирует 32 random bytes через `RandomNumberGenerator`, отдаёт token один раз и сохраняет только SHA-256 hash. TTL — 15 минут. `UsedAtUtc` и `Revoked` являются EF concurrency tokens; условное обновление в транзакции `SaveChanges` не позволяет двум конкурентным запросам успешно употребить один token.

`POST /api/stations/enroll` создаёт/привязывает Station, отзывает прежние active credentials при re-enrollment и создаёт уникальный 256-bit station secret. Agent получает `StationId`, secret и server ECDSA public key только в response enrollment.

HMAC требует, чтобы проверяющая сторона располагала ключевым материалом: необратимого `SecretHash` математически недостаточно. Поэтому PostgreSQL содержит одновременно обязательный SHA-256 `SecretHash` и `ProtectedSecret`, зашифрованный ASP.NET Core Data Protection. Plaintext secret в БД не записывается. Data Protection key ring и его OS protection входят в server trust boundary.

## Agent secret storage

Agent сериализует identity только в памяти, затем защищает весь credential blob через `ProtectedData.Protect(..., LocalMachine)`. Файл по умолчанию: `%ProgramData%\GameClub\Agent\credentials.dat`. Secret не попадает в `appsettings.json`, registry, URL или logs.

Ожидаемый production ACL каталога:

- `SYSTEM`: Full Control;
- `Administrators`: Full Control;
- service identity: необходимые Read/Write права;
- обычные `Users`: No Access к `credentials.dat` и `agent_state.db` (Read допустим только для несекретных файлов, если это требуется политикой клуба).

MVP не меняет ACL автоматически: неверное изменение прав может заблокировать службу. ACL должен устанавливать installer после проверки service identity.

## HTTP authentication and replay protection

Agent подписывает exact request body и отправляет:

- `X-GameClub-Station-Id`;
- `X-GameClub-Timestamp` (Unix seconds);
- `X-GameClub-Nonce` (16 cryptographically random bytes, base64url);
- `X-GameClub-Signature` (HMAC-SHA256, base64url).

Canonical request v1:

```text
HTTP_METHOD
PATH
TIMESTAMP
NONCE
BODY_SHA256
```

Server ищет active credential по signed station identity, расшифровывает key, вычисляет HMAC и сравнивает через `CryptographicOperations.FixedTimeEquals`. Подпись проверяется до помещения nonce в replay store. Timestamp принимается в окне ±60 секунд. `IReplayProtectionStore` атомарно резервирует nonce на 2 минуты; текущая реализация process-local, следующая масштабируемая реализация может использовать Redis.

## SignalR authentication and authorization

`POST /api/agent/session-token` принимает только HMAC-authenticated request. Ответ — ES256 JWT с TTL 5 минут, audience `gameclub-agent`, claims `sub`, `station_id`, `agent`, `jti`.

Agent Hub `/hubs/stations` защищён схемой `AgentJwt`. Hub не принимает station ID из client argument/header как authority: ID извлекается только из validated `station_id` claim. На connect и перед ACK/COMPLETED/FAILED дополнительно проверяется, что credential не отозван. Agent не передаёт station secret или station ID в query string.

Admin Hub `/hubs/admin` защищён схемой `EmployeeJwt` и policy `ClubRead`. Browser SignalR может передавать Bearer access token через query только на этом hub path; обработчик не принимает query token для произвольного REST endpoint. TLS обязателен, proxy/application access logs должны исключать значение `access_token` и заголовок Authorization. Token нельзя печатать в console или копировать в diagnostic URL.

Сервер закрывает hub при истечении authentication. Уже подключённые employee connections повторно проверяются примерно раз в 10 секунд: DB role/status/security stamp и expiry должны оставаться действительными. Нельзя утверждать мгновенный отзыв уже открытого соединения: это bounded periodic revalidation. При невозможности проверить текущие права соединения закрываются, а не продолжают получать данные.

`GamingSessionChanged(stationId)` и `DashboardChanged({stationId?})` — invalidation hints, не authoritative данные. Agent повторяет HMAC current-session запрос; Admin повторяет Employee-authenticated REST. Отсутствие гарантированной доставки SignalR компенсируется polling, не скрывается обещанием exactly-once events.

## Employees and management authorization

Первый Administrator создаётся только CLI-веткой `--bootstrap-admin` при пустой таблице сотрудников. Username/password берутся из явных `Bootstrap__Username` / `Bootstrap__Password`; нормальный startup не создаёт аккаунты, HTTP bootstrap отсутствует. Пароль 12–128 символов хешируется `PasswordHasher<Employee>`; секретные ENV после bootstrap следует очистить.

`POST /api/admin/auth/login` возвращает ES256 JWT на 4 часа с audience `gameclub-employee`, `employee_id`, `security_stamp`, role и permission claims. Login имеет ограничение 20 попыток/минуту/IP и максимум 5 failures/in-flight; неизвестный аккаунт проходит dummy hash verification, неуспех не раскрывает существование/активность аккаунта.

Для каждого нового валидируемого JWT запрос проверяет Employee в БД: активность, stamp и роль. Permissions пересчитываются из текущей роли/конфигурации, а не слепо принимаются из старого JWT. Смена доступа/пароля и logout отзывают старые сессии. Последнего активного Administrator нельзя отключить или понизить. Frontend хранит access token в памяти, не в localStorage; UI permissions не заменяют серверную authorization.

| Policy | Operator | Manager | Administrator |
| --- | --- | --- | --- |
| `ClubRead` | Да | Да | Да |
| `ClubOperate` | Да | Да | Да |
| `ClubManageCatalog`, `ClubManageMoney` | Нет | Да | Да |
| `ClubManageEmployees`, `ClubManageSecurity` | Нет | Нет | Да |
| `ClubPower` | Только при `Club:OperatorCanPower=true` | Да | Да |

Restart/Shutdown дополнительно проверяют `ClubPower` после разбора allowlist-типа, до создания команды. По умолчанию Operator не имеет этого права. JSON payload запрещён: нет пути executable, аргументов, удалённого hostname или скрипта. Agent проверяет pinned signature, station binding, срок ≤30 секунд, nonce и persistent replay ledger до отдельного handler. Local Win32 API запрашивает только graceful reboot/shutdown с фиксированными параметрами. Автотесты подменяют native boundary и не выключают ОС.

При переносе auth session сохраняет ID и TTL, но меняет станцию атомарно вместе с игрой и сегментами. Agent logout подписывает `{ "expectedSessionId": "<auth-session-guid>" }`; несовпадение текущей auth на подписанной станции даёт безопасный no-op. Запрос старого ПК не завершит игру нового посетителя. Отсутствующий/пустой ID отвергается. Изменения платы/сегментов, события и успешный audit сохраняются в одной транзакции.

`ClubPower` используется командами и кнопками Restart/Shutdown Stage 8. Отдельной read-only роли нет. `GET /api/stations`, чтение команд/кошельков/игроков защищены `ClubRead`; deposit, player creation и игровые действия — `ClubOperate`; корректировка баланса и статуса игрока — `ClubManageMoney`; enrollment/revoke — `ClubManageSecurity`. Agent credential нельзя предъявить вместо employee identity.

Значения role в JSON API: `Administrator`, `Manager`, `Operator`.

## Command integrity and authorization

Server подписывает каждую доставку ECDSA P-256/SHA-256. Private key существует только на Server и загружается из внешнего PEM или X.509 PFX; Agent хранит только pinned SubjectPublicKeyInfo.

Canonical command v1:

```text
version
commandId
stationId
type
payloadSha256
createdAtUnixMilliseconds
expiresAtUnixMilliseconds
nonce
```

`PayloadJson` не подписывается как raw canonical JSON; подписывается его SHA-256 hash, поэтому порядок полей внешнего envelope не влияет на проверку. Перед ACK Agent проверяет non-empty command ID, собственный station ID, creation/expiry, nonce, exact allowlist, payload size/schema, ECDSA signature и persistent replay ledger.

Allowlist: `Ping`, `TestMessage`, `LockStation`, `UnlockStation`, `LogoutPlayer`, `RestartStation`, `ShutdownStation`. Все типы, кроме TestMessage, не имеют payload. Lock/Unlock изменяют persisted visual ClientShellState; LogoutPlayer завершает конкретную auth/game с расчётом через HTTPS. Только Restart/Shutdown обращаются к фиксированному local Win32 power API; security policies Windows не меняются. TestMessage принимает единственное Message до 1000 символов. Payload — до 16 KiB, error — до 2000 символов. Arbitrary executable/shell/download-and-execute API отсутствует.

Создание команды требует Employee ClubOperate, для питания дополнительно ClubPower. Command record и audit с EmployeeId сохраняются в одной транзакции до dispatch; audit не содержит произвольный payload. Отклонённые power-запросы busy/offline/pending/concurrency аудируются после rollback, permission-denied — до создания команды. HTTP-ответ о создании не доказывает исполнение: статус команды и Client ACK отслеживаются отдельно.

## Local Client IPC

Agent — единственный Named Pipe Server `GameClub.Agent.ClientState`; WPF Client подключается только к локальной машине (`.`). Общий `GameClub.Contracts` задаёт состояния и line-delimited JSON protocol. Agent сохраняет состояние до публикации и не принимает state mutation от Client. При `LockStation` подключённый Client должен подтвердить revision; при отсутствии Client persisted state будет выдан при следующем `GetState`.

Windows pipe использует `FirstPipeInstance` и ACL для SYSTEM, Administrators, service/current identity и Interactive SID. Это исключает remote network logon и не даёт обычному клиенту выдавать state от имени Agent через существующий pipe. Процессы одного interactive identity нельзя надёжно различить одним ACL; остаточный риск и будущий hardening описаны в `docs/client-ipc-security.md`.

Линия IPC ограничивается во время чтения, до накопления безразмерного сообщения; предел — 16 KiB. Одна login/logout операция выполняется параллельно reader, чтобы HTTP-ожидание не блокировало ACK и heartbeat; новые player-запросы при занятой операции отвергаются без неограниченной очереди паролей. Read timeout — 15 секунд, write timeout — 5 секунд. Heartbeat отвечает текущим безопасным state. Ошибка heartbeat отменяет ожидающий read, разрыв даёт Offline и reconnect. JSON-ошибки логируют тип исключения, не содержимое запроса.

## Player authentication

Client передаёт login только локальному Agent. Agent отправляет request по HTTPS с HMAC station authentication; station id отсутствует в body и выводится Server из проверенных headers. Password хешируется `PasswordHasher<User>`, username хранится вместе с normalized unique value. Active auth sessions защищены per-user и per-station partial unique indexes, имеют TTL 12 часов и не содержат billing данных.

Локальный player snapshot не является authority. После restart Agent и после каждого успешного heartbeat он сверяет auth/gaming через authenticated current-session endpoints; до проверки и при потере Server Client показывает Offline. Login/logout/reconciliation сериализованы. Disabled/banned/expired auth не даёт восстановить игровую сессию. Детальная модель описана в `docs/player-auth-security.md`.

## Billing integrity

Покупка и списание/резерв, события сессии и итоговый расчёт выполняются в PostgreSQL SERIALIZABLE unit of work. `operationId` и unique constraints защищают повторные покупки/проводки; одинаковый ID с другим телом даёт конфликт, а не новое списание. UI не задаёт готовую стоимость и не изменяет balance напрямую. Ledger не является tamper-proof журналом против администратора БД: доступ к БД входит в границу доверия.

Снимок тарифа защищает купленное время от последующей смены цены каталога. Prepaid сверх оставшегося auth TTL отвергается до оплаты; postpaid ограничен резервом и auth TTL. Ночное окно сохраняет абсолютный UTC deadline, который нельзя перенести pause/resume. Подробная политика округления, отрицательного баланса и отсутствия автоматического prepaid refund — в `billing.md`.

## Persistent command replay ledger

`SqliteProcessedCommandStore` создаёт `%ProgramData%\GameClub\Agent\agent_state.db` без EF migration. Таблица `processed_commands` имеет unique `command_id` и `nonce`, status/error/timestamp. Записи сохраняются минимум 24 часа и переживают restart. Повторная доставка не исполняет command повторно, а идемпотентно повторяет server report согласно локальному status.

## Revocation

Endpoint `POST /api/stations/{id}/credentials/revoke` требует Employee `ClubManageSecurity`, устанавливает `RevokedAtUtc` и сохраняет audit с EmployeeId вместе с изменением credentials. После этого HMAC heartbeat/session-token не проходят, новый SignalR connect отвергается, а методы уже открытого Agent Hub проверяют revocation повторно. Возврат станции выполняется только новым enrollment token и re-enrollment; старый локальный credential заменяется администратором станции. Создание enrollment token также требует `ClubManageSecurity` и сохраняет actor audit в той же транзакции.

Будущая rotation может создавать overlap credentials с идентификаторами key version, но текущий MVP сознательно реализует revoke + re-enroll.

## TLS and server trust

Agent принимает HTTPS по умолчанию. HTTP допустим только при `DOTNET_ENVIRONMENT=Development` и `Security:AllowInsecureDevelopmentHttp=true`; вне Development Agent логирует critical и не начинает network activity. Certificate validation использует стандартный OS trust store и нигде не отключается.

Server command public key pinning даёт независимую от TLS проверку command integrity после enrollment. Оно не заменяет TLS, поскольку JWT, metadata и enrollment response также требуют confidentiality/authenticity.

## Database, Server and operator trust

- Server process доверен хранить decrypted key только кратковременно в memory и подписывать allowlisted commands.
- PostgreSQL доверен хранить state/audit, но leakage БД не должна сразу раскрывать plaintext station secret; кража Data Protection key ring вместе с БД снимает эту защиту.
- Employee имеет отдельную application identity и минимальные права. Bootstrap и управление signing/Data Protection keys остаются операциями доверенного администратора host, не браузерными возможностями Operator.
- Development legacy registration по умолчанию выключен. Совместимый `/api/dev/users`, если включён Development routing, тоже требует `ClubOperate`; основной endpoint — `/api/admin/users`.

## Audit and logging

Stage 10 POS использует только Employee policies: Operator продаёт в своей открытой смене; каталог и остатки доступны Manager/Administrator; полный возврат требует ClubManageMoney и собственной открытой смены. Operator не читает чужие продажи/смены; Agent JWT не подходит. Сотрудник не задаётся JSON. DTO отклоняют неизвестные поля; суммы/строки пересчитывает Server. Остатки, ledger, Payment, журнал operationId/fingerprint и аудит записываются атомарно; сериализация и unique constraints не допускают двойного расхода/возврата. Нельзя обойти postpaid reserves покупкой товара с кошелька.

Продажа не редактируется; полный возврат сохраняет причину, сотрудника и отдельный след StockMovement/Payment либо WalletTransaction. Cash/Card не являются доказательством банковского платежа: это локальный учёт без хранения card PAN/CVV, эквайринга и фискализации. Система не должна получать банковские реквизиты в причинах или логах. GamingSalesSnapshot закрытой смены имеет явно ограниченную actor/time семантику, не гарантирует полноту общей финансовой отчётности. Подробности: [Stage 10](stage10.md).

Stage 9 Playnite: только подписанный LaunchGame с GUID игры/сессии, TTL30s и дополнительной свежей авторизацией10s. Путь executable никогда не поступает с Server в исполнение. Локальный allowlist и фиксированный Fullscreen executable настраивает администратор; Named Pipe не аутентифицирует конкретный WPF binary. Интерактивный manifest — недоверенный сигнал Installed, не право расширять allowlist. CLI закрывает только Playnite, не саму игру; Windows ACL/application control остаются обязательными предпосылками. Полный перечень ограничений — [Stage 9](stage9.md).

`SecurityAuditEvent` хранит event type, optional station/user ID, UTC timestamp, source IP и bounded details. Помимо enrollment/HMAC/replay, аудит включает player login/logout, создание/статус игрока, каталог/группы, employee bootstrap/login/logout/access/password и employee-initiated команды/revoke. ActorEmployeeId/EmployeeId фиксируется безопасным GUID; баланс имеет отдельные проводки с EmployeeId, игровая сессия — SessionEvent. Agent локально пишет `SecurityWarning` для invalid/tampered/wrong-station commands.

Прикладной код не должен логировать enrollment token, station secret, full JWT, private key, database password или secret-bearing signature input. Не включайте HTTP body/Authorization/query-token logging в reverse proxy, APM и debug-инструментах. Реальная эксплуатационная защита включает ACL, TLS, firewall, хранение секретов и проверку логов; наличие auth policies не означает, что все эти меры автоматически настроены.
