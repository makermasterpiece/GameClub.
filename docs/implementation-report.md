# Implementation report — GameClub до Stage 10

## Реализация Stage 10 — 2026-09-08

Добавлены POS-каталог, остатки и аудит корректировок, атомарная корзина со снимками SaleItem, Wallet ProductPurchase/Refund, внутренний Cash/Card Payment, полный возврат, собственные смены и сводка. Миграция: `20260908103342_AddPosProductsSalesAndEmployeeShifts`. API и правила: [Stage 10](stage10.md). Нового Agent/Client протокола и SignalR event нет.

Cash/Card не интегрированы с терминалом или фискализацией. Возврат только полный и отдельной записью, без редактирования исходной продажи. GamingSales ограничен сотрудником и UTC-интервалом; system-проводки без EmployeeId исключены, закрытая цифра фиксируется snapshot. Это не общая финансовая отчётность. Stage 11–12 не реализованы.

Фактическая проверка Stage 10:

- `dotnet restore` / `dotnet build`: успешно, 0 предупреждений и ошибок; локальный SDK .NET 8 и NuGet feed.
- Полный прогон: **462 .NET-теста пройдены, 0 ошибок, 0 пропусков**, включая PostgreSQL и управляемые гонки последнего товара, повторных продаж/возвратов и игрового списания против закрытия смены. Изолированные БД создаются с применением migrations и удаляются после проверки.
- `dotnet ef migrations has-pending-model-changes`: изменений нет. Зависимости проектов проверены, Domain/Contracts независимы от Infrastructure/Server.
- Admin: production build успешен, **35 тестов пройдены**. Sites использован для расширения существующей панели с сохранением self-hosted размещения; облачной публикации и интерактивной браузерной проверки не было.
- Реальный локальный HTTPS/SignalR smoke с новой БД: предыдущие Stage 8/9 сценарии и POS проходят. Проверены Employee RBAC, запрет неизвестных полей/отсутствующего paymentMethod, PRICE_CHANGED, Cash/Card/Wallet, replay, полный возврат, остаток, wallet balance, Payment-записи, личные смены, сверка итогов и запрет продажи после закрытия. Временный Server и его БД очищены; реальные игры, платежи и команды питания не выполнялись.

Ручной checklist остаётся инструкцией, а не заявлением о проверке UI на рабочем месте кассира. Перед эксплуатацией нужна отдельная проверка на тестовом развёртывании клуба. Приведённые ниже counts относятся к историческому Stage 9.

## Проверка Stage 9 — 2026-09-08

Реализованы каталог Game/StationGame, EF migration `20260908095534_AddGameCatalogAndStationInventory`, Employee CRUD/read, HMAC inventory/авторизация, подписанный LaunchGame, локальный allowlist и WPF Playnite bridge, Admin «Игры», SDK-экспортёр. Подробные интерфейсы, настройка и ограничения: [Stage 9](stage9.md).

- `dotnet restore` и `dotnet build`: успешно, 0 warnings/errors. Проверено локальным SDK .NET 8 с локальным NuGet feed; обычные команды из README остаются стандартными.
- Полный .NET/PostgreSQL run: **426 passed, 0 failed, 0 skipped**. PostgreSQL 16 на изолированном loopback; suite создаёт/удаляет отдельные тестовые БД, применяя все десять migrations.
- `dotnet ef migrations has-pending-model-changes`: изменений относительно snapshot нет. Domain/Contracts не имеют ProjectReference; Client зависит только от Contracts; разделение слоёв сохранено.
- Admin production build и **30 frontend tests passed**. Стиль, компоненты и self-hosted размещение сохранены; Sites применялся только к расширению существующего UI, без облачного развёртывания. Интерактивная браузерная проверка Stage 9 не выполнялась.
- Реальный локальный HTTPS/SignalR smoke: новый Server и новая БД, проверенные TLS chain/hostname, Employee/Agent изоляция, каталог/RBAC, HMAC inventory, отказ неверной пары GUID, paid authorization/paused denial, подписанный LaunchGame с тремя GUID и TTL30, синтетические ACK/Complete сохранены в PostgreSQL. Повторно прошли сценарии Stage 8. Временный Server остановлен, созданная smoke-БД и её секретные файлы удалены штатной очисткой helper.
- Windows PowerShell 5.1 embedded-host test: mock Playnite API, первоначальный экспорт и повторный после 22 секунд простоя host, корректные GUID/Installed, штатное завершение. Исправлены отдельный runspace для периодики и PS5.1 NullString для атомарного File.Replace. Тест: `integrations/playnite/Test-ExporterHost.ps1`; не пишет в настоящий ProgramData и не запускает Playnite.

**Не подтверждено:** реальный Playnite/SDK внутри установленного Playnite, WPF foreground на игровой станции, реальные игры, закрытие Fullscreen в Windows, две физические станции. Process-adapter тесты используют fake; серверный Completed не доказывает запуска самой игры. Закрытие Playnite не завершает игру. ACL и управляемая библиотека — предпосылки развёртывания, не автоматически выполненное OS-hardening. Stage 10–12 не начинались.

## Исторический отчёт до Stage 7

Исторический отчёт Stage 1–7. Продолжение от 2026-09-06 и актуальные проверки находятся в [Stage 8](stage8.md); ограничения «Stage 8 не реализован» ниже относятся к состоянию на 2026-09-05.

Дата: 2026-09-05. Граница работы: Stage 1–7 включительно. Этот отчёт отделяет реализованный код, выполненные автоматические проверки и не подтверждённые физические/UI сценарии. Итоговые .NET, PostgreSQL, frontend и перечисленные browser проверки после runtime-исправлений пройдены. UI-подтверждение EndSession и полный сценарий двух физических Windows-станций не проверены; причины указаны ниже.

## Реализованный результат

| Область | Результат |
| --- | --- |
| Станции | PostgreSQL/EF migrations, одноразовый enrollment, persisted identity, heartbeat, ONLINE/OFFLINE monitor и REST |
| Доверие Agent | Уникальный HMAC credential, replay protection, Agent JWT, pinned ECDSA signed commands, revoke/re-enroll |
| Windows boundary | Worker/HttpClientFactory/retry, DPAPI credential, SQLite replay/state, bounded Named Pipe и WPF visual shell |
| Игроки | Хеширование паролей, ограничение login, авторизация на конкретной станции, запрет параллельного входа, logout/expiry |
| Игровое время | Модель Created/Active/Paused/Completed/Cancelled; server-authoritative time, persisted events, restart/reconciliation, display-only Client timer |
| Биллинг | Группы/тарифы/пакеты, prepaid, postpaid reserve/settlement, ledger, idempotency, serializable money/session mutations |
| Сотрудники | Явный первый CLI bootstrap, Employee JWT, Administrator/Manager/Operator policies, access/password/logout revocation и actor audit |
| Панель | React/TypeScript Admin с dashboard, Employee-authenticated REST, отдельный Admin Hub и polling fallback |
| Документация | Новый developer setup, deployment, security, billing и пошаговый manual E2E до Stage 7 |

Для проверки схемы используется существующая последняя migration `20260905130313_AddEmployeeAdministrationAndClientHealth`. Domain не ссылается на Infrastructure/Server/Agent. Admin имеет собственный pnpm build и не скрыт за утверждением, что `dotnet build` собирает frontend.

## Исправления, найденные проверками

В Stage 4 закрыты накопление login failures, отсутствие dummy hash для unknown player, восстановление disabled/banned auth и слишком широкая классификация DB uniqueness ошибок. Bounded IPC reader ограничивает сообщение до роста буфера; исключения JSON не публикуют пароль через текст exception.

В Stage 5 исправлены IPC ACK/heartbeat starvation во время HTTP login, снятие UI busy не связанным с запросом state update и зависание reader при разрыве heartbeat. Login/logout/reconciliation сериализованы; SignalR используется как invalidation hint, кэш после restart не авторизует игру.

В Stage 6 добавлены absolute UTC deadline ночного пакета, expiry также на паузе и ограничение покупки сроком 12-часовой авторизации. Prepaid-покупка за пределами auth TTL отвергается до charge; postpaid funding-time ограничен оставшимся auth. Wallet snapshot читается согласованно, а не несколькими несвязанными запросами.

Final QA Stage 7 обнаружила дополнительные обязательные runtime-баги:

- ASP.NET Core MVC positional-record request validation: DataAnnotations должны относиться к constructor parameters, а не сгенерированным properties. Исправление предотвращает HTTP 500 на реальных JSON-запросах; проверяется HTTP-тестами, не только прямым вызовом controller method.
- Windows CNG/PFX: загрузка signing key не должна экспортировать private parameters. Используются принадлежащие provider ECDSA handles из двух ephemeral PFX imports для command/JWT signing, а validation key содержит только public parameters. Сам по себе флаг Exportable не решает проблему; regression test проверяет реальный путь PFX.
- Денежный midpoint: при тарифе 6.00/час и трёх секундах активного времени точная стоимость 0.005 должна стать 0.01. Умножение до деления сохраняет нужную точность перед единственным `AwayFromZero` округлением; добавлена регрессия.
- Глобальная ownership `operationId`: ID покупки не может конфликтовать с ledger-проводкой, включая период открытого postpaid до settlement. Проверены оба порядка создания и конкурентный deposit/postpaid с одним ID; проигравшая операция не оставляет зависшую оплачиваемую сессию.
- Dashboard разделяет Agent OFFLINE и Client OFFLINE; отсутствие WPF при живом Agent не выдаётся за потерю всей станции.
- Development compatibility player-creation endpoint тоже требует Employee `ClubOperate`; он не остаётся обходом основной administrative authorization.

Эти изменения исправляют уже включённые функции Stage 1–7. Они не означают реализацию Stage 12 hardening.

## Files Added / Modified — Stage 4–7

Основные файлы Stage 4–6 (добавления и изменения существующей реализации):

| Слой | Файлы / каталоги и результат |
| --- | --- |
| Domain | `src/GameClub.Domain/Gaming/` — session/events/state; `Billing/` — StationGroup, Tariff, TariffPackage, Wallet, WalletTransaction, WalletReservation, Money, BillingMode; `Stations/Station.cs` — группа станции |
| Application | Новый `src/GameClub.Application/`; `Abstractions/IClubData.cs` — unit of work/events ports; `Gaming/GamingSessionService.cs`; `Billing/{CatalogService,SessionBillingService,WalletService}.cs`; `Stations/StationManagementService.cs` |
| EF/PostgreSQL | `src/GameClub.Infrastructure/Persistence/GameClubDbContext.cs`, `ClubData.cs`, `Configurations/{GamingConfiguration,BillingConfiguration,StationConfiguration}.cs`, migrations и snapshot |
| Contracts | `src/GameClub.Contracts/Client/BoundedPipeReader.cs`, `ClientStateMessage.cs`; `Gaming/{GamingSessionSnapshot,GamingSessionTimeProjection}.cs` |
| Agent | `src/GameClub.Agent/Worker.cs`, `Services/{StationApiClient,AgentSignalRClient}.cs`, `Services/Players/{PlayerLoginService,StationSessionSyncSignal}.cs`, `Services/Client/{ClientPipeServer,ClientStateCoordinator}.cs` — сверка, fail-closed, IPC cancellation/ACK |
| WPF Client | `src/GameClub.Client/Services/ClientPipeConnection.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs` — guarded reconnect, busy lifecycle и display-only Stopwatch |
| Server | `Services/Players/{PlayerAuthenticationService,PlayerLoginRateLimiter,UserProvisioningService}.cs`, `Services/{GamingSessionMonitor,ClubEvents}.cs`, player/gaming/billing controllers и DI в Program |
| Регрессии | `tests/GameClub.Server.Tests/Players/`, `Client/`, `Gaming/`, `Billing/`, `Integration/`; `docs/stage4-fixes.md` описывает узкие auth/IPC исправления |

Основные файлы Stage 7:

| Область | Файлы / каталоги |
| --- | --- |
| Employee model/security | `src/GameClub.Domain/Employees/`, `src/GameClub.Server/Security/Employees/`, `Controllers/EmployeeAuthController.cs`, `Controllers/EmployeesController.cs` |
| Защита существующих API и actor audit | Server `Controllers/StationsController.cs`, `StationCommandsController.cs`, `CommandsController.cs`, `AdminEnrollmentTokensController.cs`, `AdminPlayersController.cs`, `BillingController.cs`, `GamingSessionsController.cs`, `Endpoints/DevelopmentUserEndpoints.cs` |
| Dashboard и health | Server `Controllers/AdminDashboardController.cs`, `Services/Admin/`, `Hubs/AdminHub.cs`; Domain Station, heartbeat contract/Agent и `20260905130313_AddEmployeeAdministrationAndClientHealth` |
| Startup/static Admin | `src/GameClub.Server/Program.cs`, `src/GameClub.Admin/package.json`, `pnpm-lock.yaml`, `vite.config.ts`, `src/` и `tests/` |
| PFX runtime fix | `src/GameClub.Server/Security/ServerSigningKeyProvider.cs`, `tests/GameClub.Server.Tests/Security/ServerSigningKeyProviderTests.cs` |
| MVC request fix | Server positional request records; `tests/GameClub.Server.Tests/Employees/RequestRecordValidationTests.cs` |
| Финансовые регрессии | `src/GameClub.Domain/Billing/Money.cs`, `src/GameClub.Application/Billing/SessionBillingService.cs`, `WalletService.cs`, `src/GameClub.Application/Gaming/GamingSessionService.cs`, `tests/GameClub.Server.Tests/Billing/WalletTests.cs`, `SessionBillingTests.cs` |
| Policy/DB регрессии | `tests/GameClub.Server.Tests/Employees/`, `Admin/DashboardTests.cs`, `Integration/DashboardIntegrationTests.cs` |
| Runbooks | `README.md`, `docs/architecture.md`, `billing.md`, `security-architecture.md`, `deployment.md`, `manual-e2e-test.md`, этот отчёт; актуализированы player/IPC threat и secrets references |

Это навигационная карта основных изменений, не утверждение о наличии Git commit: окончательная версия/commit фиксируется при передаче репозитория.

## Database Migrations

Четыре новые migrations текущего продолжения находятся в `src/GameClub.Infrastructure/Persistence/Migrations/` вместе с Designer-файлами и обновлённым `GameClubDbContextModelSnapshot.cs`:

| Migration | Изменение схемы |
| --- | --- |
| `20260905122328_AddGamingSessions` | Gaming sessions, session events, уникальные ограничения активных сессий |
| `20260905123726_AddTariffsAndWalletBilling` | Каталог, STANDARD/VIP/BOOTCAMP seed, группа станции, wallet/ledger/reservations, billing snapshots |
| `20260905124137_EnforcePackageWindowDeadline` | Абсолютный `WindowEndsAtUtc` для hard end окна пакета |
| `20260905130313_AddEmployeeAdministrationAndClientHealth` | Employee identity/roles/stamp и отдельные Client health поля станции |

Предыдущие InitialCreate, AgentCommands, StationSecurity и Users/PlayerAuthSessions migrations сохранены. Stage 4 targeted fixes не требуют отдельной новой migration. Полный порядок применения и команды — в [README](../README.md); финальная проверка модели и применения Stage 7 прошла.

## Endpoints, SignalR Events и Agent Commands

| Основная группа REST | Реальные routes и доступ |
| --- | --- |
| Employee auth | `POST /api/admin/auth/login`; Employee `GET /api/admin/auth/me`, `POST /api/admin/auth/logout` |
| Employees | Administrator: `GET/POST /api/admin/employees`, `PUT /api/admin/employees/{id}/access`, `.../{id}/password` |
| Dashboard/stations | Read: `GET /api/admin/dashboard`, `/api/stations`, `/api/stations/{id}` |
| Игроки | Read `GET /api/admin/users`; Operate `POST /api/admin/users`; Money `PUT /api/admin/users/{id}/status` |
| Каталог | Read `GET /api/admin/catalog`; Catalog: POST `groups`, `tariffs`, `packages`; PUT `tariffs/{id}/price`, `tariffs/{id}/active`, `packages/{id}/active`, `stations/{id}/group` под этим prefix |
| Wallet | Под `/api/admin/users/{id}/wallet`: Read GET корня/`transactions`; Operate POST `deposit`; Money POST `adjustment` |
| Gaming | Operate `POST /api/admin/gaming-sessions`, `.../{id}/pause`, `.../{id}/resume`, `.../{id}/end`; Read GET `.../{id}/events` |
| Команды | Operate `POST /api/stations/{id}/commands`; Read GET того же route и `/api/commands/{id}` |
| Enrollment/revoke | Security `POST /api/admin/enrollment-tokens`, `/api/stations/{id}/credentials/revoke`; одноразовый token для `POST /api/stations/enroll` |
| Agent | HMAC `POST /api/stations/{id}/heartbeat`, `/api/agent/session-token`, `/api/agent/player/login`, `/api/agent/player/logout`; GET `/api/agent/player/session/current`, `/api/agent/gaming/session/current` |

Legacy `/api/stations/register` выключен по умолчанию, требует Development opt-in; secure Agent использует enrollment. Development compatibility `/api/dev/users` также требует Employee Operate. Точные DTO/финансовые примеры — в [billing.md](billing.md), полная карта — в [architecture.md](architecture.md).

| Транспорт / функция | Событие или allowlist | Назначение |
| --- | --- | --- |
| Agent Hub `/hubs/stations` | `StationStatusChanged` | stationId, name, status, lastSeenAtUtc |
| Agent Hub `/hubs/stations` | `GamingSessionChanged(Guid stationId)` | Invalidation: повторить station-bound HMAC current запрос |
| Agent Hub `/hubs/stations` | `ExecuteCommand` | Подписанный ECDSA envelope, Agent JWT transport |
| Agent Hub methods | `AcknowledgeCommand`, `CompleteCommand`, `FailCommand` | Отчёт authenticated станции о конкретной команде |
| Employee Hub `/hubs/admin` | `DashboardChanged({stationId})` | Nullable stationId; перечитать Employee-authenticated dashboard |
| Agent Commands | `Ping`, `TestMessage`, `LockStation`, `UnlockStation`, `LogoutPlayer` | Только фиксированный allowlist; visual shell/login state, без OS execution |

SignalR не является durable queue; snapshots перечитываются через HTTP с polling fallback. Command creation не равна execution; подпись, station binding, expiry и persistent replay ledger проверяются до side effect.

## Подтверждённые gates

| Gate | Подтверждённый результат |
| --- | --- |
| Stage 4 | 76 tests passed |
| Stage 5 | 131 tests passed |
| Stage 6 | 206 tests passed; PostgreSQL integration suite действительно выполнена, не заменена skipped tests |
| Stage 7 .NET | Restore: все проекты актуальны; build: 0 warnings / 0 errors; test: 297 passed / 0 failed / 0 skipped, около 39 секунд |
| Stage 7 Admin | После WebMCP stale-handle fix: `pnpm install --frozen-lockfile` и build passed (50 modules, JS bundle 287.52 kB); 15 tests passed / 0 failed / 0 skipped |
| Stage 7 HTTPS/WSS smoke | Пройден реальный HTTP/WebSocket сценарий с PostgreSQL, перечисленный ниже |
| Migration/DB | `database update` применил Stage 7 migration; `has-pending-model-changes`: No changes. Real PostgreSQL 16 suite включает свежие БД/migrations, без skipped cases |
| Stage 7 browser | Administrator login, realtime dashboard, реальная станция/countdown/details, players/wallet/ledger, catalog/employees, Operator UI permissions, pause/resume и employee logout passed |
| WebMCP | Стабильная регистрация; `get_gameclub_station_status` вернул реальную станцию без JWT; unregister после logout passed |

Предыдущие gates — история выполненных проверок, а итоговый счёт Stage 7 .NET — 297 без skips. Reviewer не запускал параллельную сборку поверх основного gate, чтобы не смешивать артефакты. Browser и backend результаты учитываются отдельно.

В данной среде .NET проверялся локальным SDK `work/.dotnet` и NuGet local-feed через `scripts/verify.ps1`; restore/build/test действительно выполнены успешно. Online vulnerability audit зависимостей не выполнялся и не подтверждён. Исходная настройка NuGetAudit не изменялась для скрытия предупреждений. Перед эксплуатацией отдельно проверьте актуальные security updates .NET 8, EF Core, Npgsql и JavaScript-зависимостей; успешный build не является проверкой отсутствия известных уязвимостей.

Реальный HTTPS/WSS smoke подтвердил: PFX bootstrap; MVC request validation; 401/403 и роли; создание игрока, deposit, тариф и enrollment; HMAC-запросы; изоляцию Agent/Employee JWT; покупку gaming session, current и dashboard; `DashboardChanged` по живому SignalR; проверку подписи Ping, ACK и Completed с сохранением в PostgreSQL. Это transport/backend smoke, а не запуск двух физических WPF-станций.

Browser QA подтвердил отображение кошелька и ledger: deposit 100.00 − gaming charge 30.00 = balance 70.00. У Operator нет вкладки Employees и кнопок изменения каталога; чтение доступно, а ограничения прямых REST-запросов дополнительно проверены HTTP-тестами. Pause остановил отображаемый отсчёт, Resume продолжил его. Проверены вход/выход Administrator и Operator. Read-only WebMCP tool зарегистрирован стабильно, возвращает данные станции без токена и удаляется при logout.

После проверки остановлены созданные для неё test Server, Vite и PostgreSQL; собственные временные БД, PFX и Data Protection/DPAPI-артефакты удалены. Пользовательские данные не затронуты.

Команды для воспроизведения:

```powershell
dotnet tool restore
dotnet restore GameClub.sln
dotnet build GameClub.sln --no-restore
dotnet ef database update --project src/GameClub.Infrastructure --startup-project src/GameClub.Server
dotnet ef migrations has-pending-model-changes --project src/GameClub.Infrastructure --startup-project src/GameClub.Server
dotnet test GameClub.sln --no-build
Push-Location src/GameClub.Admin
pnpm install --frozen-lockfile
pnpm build
pnpm test
Pop-Location
```

Connection string и signing/Data Protection configuration задаются по README. Для PostgreSQL suite требуется `GAMECLUB_TEST_POSTGRES` с изолированным тестовым сервером и правом создавать/удалять только тестовые БД. Без этой переменной соответствующие tests пропускаются; это нельзя записывать как подтверждённую PostgreSQL проверку.

## Что ещё требует отдельной ручной фиксации

UI EndSession confirmation — **NOT VERIFIED**. Автоматическая проверка безопасности не разрешила подтверждение финансово значимого действия. Подтверждающая кнопка не нажата, modal отменён; ограничение не обходилось. Server end/settlement покрыт прошедшими автоматическими тестами, но это не подтверждает весь browser confirmation flow.

Две физические Windows-станции и реальный WPF manual E2E — **NOT VERIFIED**. [Manual E2E](manual-e2e-test.md) содержит сценарий разных станций, трёхчасовой покупки, restart Agent/Client/Server, UI таймера и offline. Наличие checklist или unit test не подтверждает, что два физических игровых ПК были использованы. Browser QA не заменяет native WPF/Windows Service сценарий.

До эксплуатации отдельно проверяются trusted TLS/DNS, ACL локальных credential/replay/key files, отсутствие секретов в proxy/APM logs, воспроизводимое восстановление БД и ключей, поведение после реальных разрывов питания/сети. Готовый installer, backup/restore pipeline и production hardening в продукт не включены.

## Важные бизнес-ограничения

- Авторизация игрока живёт 12 часов от login; pause не продлевает её. Это ограничение текущего продукта, не обещание неограниченной сессии.
- При раннем prepaid end/logout нет автоматического refund. Ручная adjustment — отдельная проводка с правами Manager/Administrator.
- Отрицательный баланс по умолчанию запрещён. Разрешение долга требует явного `Club:AllowNegativeBalance`; это не автоматический кредитный сервис.
- Ночное окно пакета закрывается по настроенной локальной зоне с сохранённой абсолютной UTC-границей. Поздняя покупка уменьшает доступное время, но не цену пакета.
- Нулевая индикация Client не решает самостоятельно вопрос доступа. Server остаётся authority; Client — визуальная оболочка, не OS security boundary.

## NOT IMPLEMENTED — не реализовано и не заявляется

Stage 8–12; transfer/extend сессий; reboot/shutdown и remote execution; настоящий kiosk/Windows lock; Playnite/запуск игр; POS и кассовые смены; бронирование; финансовые отчёты; платёжные интеграции; installer/updater; автоматизированный backup/restore; production-ready/hardening certification. Отсутствующие возможности не должны иметь ложных рабочих кнопок или описываться как завершённые.

Ключевые файлы и dependency map: [architecture.md](architecture.md). Быстрый запуск: [README](../README.md). Точная финансовая политика: [billing.md](billing.md). Матрица прав и границы доверия: [security-architecture.md](security-architecture.md).
