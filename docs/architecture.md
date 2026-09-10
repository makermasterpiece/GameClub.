# Архитектура GameClub до Stage 8

Stage 8 сохраняет границы слоёв: extension/transfer и billing находятся в Application, сегменты и журнал операций — в Domain, транзакции и миграции — в Infrastructure. Server проверяет employee policies и публикует уведомления после commit. Agent содержит только два фиксированных native power handler после проверки подписанной команды. Подробности: [Stage 8](stage8.md).

Модульный монолит на .NET 8 / PostgreSQL 16, Windows Worker Agent, непривилегированный WPF Client и отдельная React/TypeScript-панель сотрудников. Server — источник истины о станции, авторизации, оплаченном времени и деньгах. Agent отвечает за локальный транспорт и выполнение только разрешённых подписанных команд; Client и Admin отображают подтверждённое состояние.

```text
WPF Client ── bounded Named Pipe ── Agent ── HMAC HTTPS ──┐
                                      ↕ Agent SignalR  │
React Admin ── Employee JWT HTTPS / Admin SignalR ────── Server
                                                        │
                                                   Application
                                                        │ IClubData
                                                  Infrastructure
                                                        │ EF Core/Npgsql
                                                    PostgreSQL
```

Две SignalR-границы независимы: `/hubs/stations` принимает Agent JWT, `/hubs/admin` — Employee JWT. Ни один UI не имеет прямого соединения с PostgreSQL. WPF Client не обращается к Server и не хранит station credential.

## Проекты и зависимости

| Проект | Ответственность | Project references |
| --- | --- | --- |
| `GameClub.Domain` | Сущности, состояния, ограничения, денежная арифметика | Нет |
| `GameClub.Contracts` | DTO IPC/gaming, безопасные снимки и display-only проекция таймера | Нет |
| `GameClub.Application` | Каталог, кошелёк, gaming, смена группы; `IClubData` и events ports | Domain, Contracts |
| `GameClub.Infrastructure` | `GameClubDbContext`, mappings/migrations, PostgreSQL unit of work | Domain, Application |
| `GameClub.Server` | REST, HMAC/JWT, SignalR, employee/player authentication, hosted monitors | Domain, Infrastructure; Application/Contracts транзитивно |
| `GameClub.Agent` | Worker, HttpClientFactory, подписанные команды, DPAPI/SQLite, локальный IPC | Domain, Contracts |
| `GameClub.Client` | WPF player shell, login UI и монотонный таймер | Contracts |
| `GameClub.Admin` | Панель сотрудников, REST и invalidation/polling; отдельный pnpm build | Не .NET-проект |

Domain не зависит от Server, Infrastructure или Agent. Application использует provider-independent EF Core async LINQ extensions, но не конкретный DbContext, Npgsql, HTTP или UI. Это осознанная минимальная граница без дополнительного универсального repository framework. Действующие enrollment/command/player security services находятся в Server; их перенос ради симметрии слоёв не выполнялся.

`GameClub.sln` содержит .NET-проекты и `GameClub.Server.Tests`; `dotnet build` не собирает Node frontend. Для Admin нужен отдельный `pnpm build`. Server может раздавать готовый Admin как same-origin `/admin`; порядок сборки и копирования описан в [deployment.md](deployment.md).

## Основная модель

- `Station` хранит ONLINE/OFFLINE/Maintenance, timestamps, группу и отдельный отчёт здоровья Client. ONLINE Agent не означает подключённый WPF Client.
- `StationCredential`, `EnrollmentToken`, `AgentCommand`, `SecurityAuditEvent` задают существующую границу доверия станций и журнал операций.
- `User` и `PlayerAuthSession` описывают игрока и его вход на конкретной станции. `Employee` — отдельная identity сотрудника, не вид пользовательской сессии.
- `GamingSession` и `SessionEvent` описывают оплаченное время и переходы; `PlayerAuthSession` сама по себе времени не покупает.
- `StationGroup`, `Tariff`, `TariffPackage` задают каталог. `Wallet`, `WalletTransaction`, `WalletReservation` обеспечивают баланс, проводки и резерв postpaid.

PostgreSQL partial unique indexes запрещают две активные авторизации либо две Active/Paused gaming sessions одного игрока или станции. Последняя миграция этапа — `20260905130313_AddEmployeeAdministrationAndClientHealth`; схема создаётся migrations, не `EnsureCreated` и не ручными SQL-правками.

## Поток состояния и времени

1. Новый Agent проходит одноразовый enrollment по доверенному HTTPS, сохраняет уникальный credential через Windows DPAPI и закрепляет открытый ключ Server.
2. Heartbeat каждые 10 секунд проходит HMAC-проверку и обновляет `LastSeenAtUtc`. Server примерно каждые 10 секунд переводит Online-станции в Offline при возрасте heartbeat больше 30 секунд.
3. Успешный heartbeat запускает сверку gaming и player auth через station-bound HTTP endpoints. `GamingSessionChanged(stationId)` ускоряет ту же сверку, не заменяет её.
4. Player login создаёт только авторизацию. Пока оплаченной gaming session нет, Client показывает ожидание игрового времени. Покупка сотрудником создаёт сессию и списание/резерв на Server.
5. Server отдаёт timestamps, elapsed и remaining. WPF использует `Stopwatch` лишь для интерполяции отображения: paused не уменьшается, ноль не даёт самостоятельной команды завершения.
6. При end/expiry/logout Server рассчитывает postpaid и завершает связанную авторизацию. Client возвращается в Available или сохраняет более строгую базовую Locked/Maintenance policy.

Auth живёт 12 часов от login, пауза этот срок не продлевает. Prepaid сверх оставшегося auth TTL отвергается до списания; postpaid ограничен резервом и auth TTL. Hard deadline ночного пакета также не переносится паузой. Подробные правила округления, окон, отрицательного баланса и отсутствия автоматического prepaid refund — в [billing.md](billing.md).

| Механизм | Интервал / граница |
| --- | --- |
| Agent heartbeat и штатная auth/gaming reconciliation | 10 секунд |
| Offline monitor / порог отсутствия heartbeat | Проверка около 10 секунд / строго больше 30 секунд |
| Gaming expiry monitor | Около 5 секунд; current-session запрос также проверяет expiry |
| Свежесть Client health в dashboard | Не старше 15 секунд и только при Online Agent |
| IPC read / write timeout | 15 / 5 секунд; JSON line максимум 16 KiB |
| Dashboard change scan / browser polling fallback | Около 5 / 15 секунд |
| Agent JWT / Employee JWT | 5 минут / 4 часа |
| Revalidation открытых employee hub connections | Около 10 секунд; ошибка проверки закрывает соединение |

При restart Agent и недоступном Server сохранённый snapshot не авторизует доступ: Client показывает Offline до подтверждения Server. Restart Client запрашивает актуальную Agent projection. Сетевой сбой не удаляет пригодные для восстановления локальные credentials и базовое состояние.

## События и доставка

| Транспорт | Событие | Смысл |
| --- | --- | --- |
| Agent Hub `/hubs/stations` | `StationStatusChanged` | stationId, name, status, lastSeenAtUtc |
| Agent Hub `/hubs/stations` | `GamingSessionChanged` | Guid stationId; повторить HMAC reconciliation |
| Agent Hub `/hubs/stations` | `ExecuteCommand` | Подписанный allowlisted command envelope |
| Admin Hub `/hubs/admin` | `DashboardChanged` | `{ stationId }`, nullable; перечитать REST dashboard |

События после commit — подсказки, а не durable message queue. Нельзя предполагать exactly-once SignalR delivery. Для потерянных уведомлений есть periodic reconciliation/polling. Команды отдельно используют signature, TTL и persistent SQLite ID/nonce ledger; наличие HTTP 201/202 не доказывает исполнение команды или Client ACK.

## Карта REST endpoints

| Метод и путь | Доступ / назначение |
| --- | --- |
| `POST /api/admin/auth/login` | Анонимный employee login с rate limit |
| `GET /api/admin/auth/me`, `POST /api/admin/auth/logout` | Employee Read; identity / отзыв сессий сотрудника |
| `GET, POST /api/admin/employees` | Administrator; безопасный список / создание |
| `PUT /api/admin/employees/{id}/access`, `.../{id}/password` | Administrator; смена роли/активности / пароля |
| `GET /api/admin/dashboard` | Employee Read; согласованная проекция станций/игроков/gaming |
| `GET /api/stations`, `GET /api/stations/{id}` | Employee Read |
| `GET /api/stations/{id}/commands`, `GET /api/commands/{id}` | Employee Read; история / статус команды |
| `POST /api/stations/{id}/commands` | Employee Operate; только allowlisted команды |
| `POST /api/admin/enrollment-tokens` | Employee Security; одноразовый token |
| `POST /api/stations/{id}/credentials/revoke` | Employee Security; отзыв station credential |
| `GET, POST /api/admin/users` | Read / Operate; поиск до 100 игроков / создание |
| `PUT /api/admin/users/{id}/status` | Employee Money; Active/Disabled/Banned |
| `/api/admin/catalog`, `/api/admin/users/{id}/wallet`, `/api/admin/gaming-sessions` | Точные методы, DTO и права в [billing.md](billing.md) |
| `POST /api/stations/enroll` | Одноразовый enrollment token, доверенный TLS |
| `POST /api/stations/{id}/heartbeat` | Station HMAC; обновление Agent и Client health |
| `POST /api/agent/session-token` | Station HMAC; короткий Agent JWT только для Agent Hub |
| `POST /api/agent/player/login`, `.../logout` | Station HMAC; identity станции не из request body |
| `GET /api/agent/player/session/current` | Station HMAC; auth snapshot или 204 |
| `GET /api/agent/gaming/session/current` | Station HMAC; `{ session, serverTimeUtc }`, session nullable |

Legacy `POST /api/stations/register` по умолчанию выключен и доступен только при отдельном Development opt-in; secure Agent его не использует. Compatibility `POST /api/dev/users` существует только в Development и также требует `ClubOperate`. Основной путь создания игроков — `/api/admin/users`.

## Транзакции и права

Use cases каталога, денег и gaming работают через PostgreSQL SERIALIZABLE transaction с ограниченными повторами serialization/deadlock конфликтов. Gaming event, списание/резерв и session mutation входят в одну операцию. `operationId` делает повтор покупки/проводки идемпотентным; новое тело со старым ID отвергается. Read-only dashboard собирает связанные проекции в RepeatableRead transaction, чтобы не смешивать части разных состояний.

REST management endpoints требуют explicit Employee policy. Operator имеет чтение и операционные действия; Manager дополнительно управляет каталогом/деньгами; Administrator — сотрудниками и security. Подробная матрица и проверка текущего security stamp — в [security-architecture.md](security-architecture.md). Frontend visibility не заменяет authorization; Agent JWT нельзя использовать вместо Employee JWT.

## Ключевые файлы

| Область | Входные точки |
| --- | --- |
| Server startup, DI, hub/static routes | `src/GameClub.Server/Program.cs` |
| Модель и схема | `src/GameClub.Domain/`, `src/GameClub.Infrastructure/Persistence/GameClubDbContext.cs`, `src/GameClub.Infrastructure/Persistence/Migrations/` |
| Транзакционные use cases | `src/GameClub.Application/Gaming/GamingSessionService.cs`, `src/GameClub.Application/Billing/CatalogService.cs`, `src/GameClub.Application/Billing/WalletService.cs` |
| Management endpoints | `src/GameClub.Server/Controllers/BillingController.cs`, `GamingSessionsController.cs`, `AdminPlayersController.cs`, `AdminDashboardController.cs` |
| Employee identity | `src/GameClub.Server/Security/Employees/`, `Controllers/EmployeeAuthController.cs`, `Controllers/EmployeesController.cs` |
| Player identity | `src/GameClub.Server/Services/Players/`, `Controllers/AgentPlayerController.cs` |
| Agent orchestration | `src/GameClub.Agent/Worker.cs`, `src/GameClub.Agent/Services/Players/PlayerLoginService.cs`, `src/GameClub.Agent/Services/Client/ClientStateCoordinator.cs` |
| Gaming transport и таймер | `src/GameClub.Contracts/Gaming/`, `src/GameClub.Contracts/Client/BoundedPipeReader.cs`, `src/GameClub.Client/` |
| Admin | `src/GameClub.Admin/src/App.tsx`, `src/GameClub.Admin/src/Dashboard.tsx`, `src/GameClub.Admin/vite.config.ts` |
| Регрессии | `tests/GameClub.Server.Tests/{Security,Players,Client,Gaming,Billing,Employees,Admin,Integration}/` |

Точные команды первого запуска — в [README](../README.md). Unit/InMemory tests не заменяют PostgreSQL constraints/concurrency tests; последние создают отдельные временные БД с проверенными именами. Manual Windows/двухстанционный сценарий отдельно описан в [manual-e2e-test.md](manual-e2e-test.md).

## Граница этапа

Stage 8 включает transfer/extend и фиксированные Windows reboot/shutdown. Stage 9 добавляет [каталог и Playnite](stage9.md): Application проверяет paid session/инвентарь, Agent — локальный allowlist, WPF — интерактивный процесс через фиксированные CLI флаги. Domain не зависит от SDK; SDK-экспортёр находится отдельно в `integrations/playnite`. Новых проектов и Framework-зависимостей в solution нет. Stage 10 добавляет [POS и смены](stage10.md). Настоящий kiosk lockdown, бронирование, общие отчёты Stage 11, installer/update pipeline Stage 12 и production hardening не реализованы. Текущий Client — визуальная оболочка, не защита ОС от локального администратора.

## POS — Stage 10

`Domain/Pos` хранит ProductCategory, Product, EmployeeShift, Sale, SaleItem, Payment, SaleRefund, StockMovement и PosOperation. `Application/Pos` разделяет каталог, смены и продажу; PosSaleService использует существующий WalletService внутри той же SERIALIZABLE unit of work. EF-конфигурации и миграция `20260908103342_AddPosProductsSalesAndEmployeeShifts` принадлежат Infrastructure. Server PosController извлекает сотрудника из Employee JWT; Admin является интерфейсом оператора. Agent/Client, новые команды и hubs для POS не нужны.

Sale/SaleItem неизменяемы: название, цена и сумма фиксируются на момент покупки. Возврат — отдельная полная компенсирующая операция с восстановлением товара и исторической суммы, без редактирования продажи. Cash/Card Payment является только внутренним учётом; Wallet использует ProductPurchase/Refund ledger и доступный баланс с учётом резервов. Журнал operationId/fingerprint защищает от повтора и пересечения POS/wallet/gaming идентификаторов.

Сводка смены имеет узкую область: товары по ShiftId, возвраты по смене возврата; GamingSales — проводки GamingCharge сотрудника по UTC-интервалу. На закрытии GamingSalesSnapshot фиксируется, поэтому системный поздний settlement не дописывает закрытую смену. Это не общая выручка клуба и не Stage 11 reporting.
