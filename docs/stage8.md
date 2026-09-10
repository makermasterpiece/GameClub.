# Stage 8 — продление, перенос и операции станции

Дата: 2026-09-06. Реализован только Stage 8; Stage 9–12 не начаты. Первоначальная настройка — в [README](../README.md).

## Продление

Admin предлагает +15/+30/+60 и произвольные целые минуты. UI запрашивает quote у Server и показывает доплату либо дополнительный резерв. Quote — предварительный расчёт: окончательная проверка выполняется в POST-транзакции.

- Prepaid использует исходный `HourlyPriceSnapshot`. Округляется совокупная стоимость минут, затем вычитается уже списанная сумма: дробление запросов не обходит округление.
- Доплата пакета пропорциональна исходной цене/длительности: `PackagePriceSnapshot × добавленные минуты / PackageDurationMinutesSnapshot`, с накопленным округлением. Изменение каталога не переоценивает начатую игру.
- Postpaid увеличивает лимит секунд и необходимый резерв. Требуемый общий резерв округляется вверх до цента; существующее обеспечение учитывается. Settlement остаётся по фактическому активному времени.
- Active получает поздний `ExpectedEndAtUtc`; Paused остаётся на паузе, растёт бюджет, deadline пересчитывается при Resume.
- Ночное `WindowEndsAtUtc` и 12-часовая авторизация не сдвигаются. Нельзя оплатить время за этими границами или продлить истёкшую игру. Общий бюджет ограничен 10080 минутами. Legacy без цены получает `EXTENSION_PRICING_UNAVAILABLE`.

SessionExtended, ledger/reservation, параметры сессии, SessionOperation и actor audit сохраняются атомарно. Новое действие получает новый UUID operationId; сетевой повтор использует прежние UUID и параметры. Другие payload/session/тип/сотрудник дают IDEMPOTENCY_CONFLICT. ID нельзя повторно использовать для deposit/purchase/transfer. UI сохраняет ID и блокирует параметры после отправки: при неизвестном результате повторите запрос в том же диалоге, не создавайте новое действие.

## Перенос

Server использует SERIALIZABLE. Destination: Online, heartbeat не старше 30 секунд, не Maintenance, без auth/game и защитной паузы питания. Разрешена только та же группа, совместимая с исходным тарифом/пакетом; автоматического перерасчёта между группами нет. Исходный ПК может быть Offline для пересадки с неисправной станции.

Сохраняются ID game/auth, UserId, цена, резерв, остаток, Active/Paused и TTL. StationId меняется у game и PlayerAuthSession. Старый StationSessionSegment закрывается, новый открывается; SessionTransferred и audit фиксируют станции и сотрудника. Unique index допускает один открытый сегмент. Сегменты включают паузы, их длительности не используются как billable time.

После commit обе станции получают SignalR invalidation. Agent повторяет HMAC current-read, WPF получает проекцию через Named Pipe. Потерю SignalR компенсирует polling. Перенос не снимает ClientLocked; отсутствие Client отображается отдельно.

`POST /api/agent/player/logout` теперь требует `{ "expectedSessionId": "<PlayerAuthSession.Id>" }` в HMAC body. Несовпадение ID на подписанной станции — успешный no-op; отсутствующий/пустой ID — 400. Старый logout не завершит вход нового посетителя после пересадки. Agent берёт подтверждённую auth identity, компенсация login — ID из login response; после logout сверяет Server, а не безусловно рисует Available.

## REST API

Все administrative routes требуют Employee JWT.

| Route | Policy / body |
| --- | --- |
| `GET /api/admin/gaming-sessions/{id}/extension-quote?minutes=15` | ClubRead; ответ `{sessionId,minutes,billingMode,charge,additionalReservation,expectedEndAtUtc}` |
| `POST /api/admin/gaming-sessions/{id}/extend` | ClubOperate; `{operationId,minutes}` |
| `POST /api/admin/gaming-sessions/{id}/transfer` | ClubOperate; `{operationId,destinationStationId}` |
| `GET /api/admin/gaming-sessions/{id}/segments` | ClubRead; название станции, StartedAtUtc/EndedAtUtc |
| `GET /api/admin/gaming-sessions/{id}/events` | ClubRead; включая SessionExtended/SessionTransferred |
| `POST /api/stations/{id}/commands` | ClubOperate + ClubPower для питания; `{type:"RestartStation"}` или `{type:"ShutdownStation"}`, без payload |

Extend/transfer возвращают актуальный GamingSessionSnapshot. Бизнес-ошибки — HTTP 409 с code, валидация — 400, недостаточно прав — 403. Основные коды: STATION_OCCUPIED, STATION_GROUP_MISMATCH, STATION_UNAVAILABLE, STATION_POWER_PENDING, INSUFFICIENT_FUNDS, SESSION_EXPIRED, SESSION_EXCEEDS_AUTH_LIFETIME, SESSION_EXCEEDS_PACKAGE_WINDOW, IDEMPOTENCY_CONFLICT.

## Команды питания

LogoutPlayer сохранён. RestartStation/ShutdownStation имеют разные handler после signature/station/nonce/expiry/replay validation. Нет generic shell/process API, произвольных аргументов, executable path или remote hostname.

Manager/Administrator имеют ClubPower. Operator получает его только при `Club__OperatorCanPower=true` на Server; default=false. Server проверяет права независимо от UI. Admin запрашивает отдельное подтверждение.

Разрешён только свободный Online-ПК без auth/game, повторная проверка выполняется при dispatch. Power creation, player login и назначение станции защищены SERIALIZABLE. Команда действует 30 секунд; до ExpiresAtUtc+60 секунд запрещены новый login/start/transfer на станцию, включая Completed. Failed/Expired освобождают защитную паузу. Это ограниченная пауза, не подтверждение завершения Windows.

Agent дополнительно требует подтверждённое idle-состояние. Native boundary вызывает локальный InitiateSystemShutdownExW: задержка 30 секунд, forceAppsClosed=false, фиксированная planned-причина. Процесс временно включает уже предоставленную SeShutdownPrivilege, проверяет ERROR_NOT_ALL_ASSIGNED и восстанавливает прежний privilege state. MVP не назначает права и не меняет Windows security policy. При недостатке прав команда Failed; service identity настраивается вручную.

Persistent Acknowledged записывается до native вызова; повтор commandId не исполняется снова, включая restart Agent. Crash между Acknowledged и native вызовом может оставить команду невыполненной: это at-most-once попытка, не exactly-once. Completed означает только принятие запроса Windows; приложение может задержать/отклонить shutdown. Окончание защитной паузы не доказывает отмену или завершение OS-запроса: при неопределённом результате оператор проверяет ПК перед новым занятием. Audit связывает employee, commandId, station, тип и переходы; секреты не добавляются в логи.

## Миграция и обновление

`20260906105627_AddSessionExtensionsTransfersAndStationHistory` добавляет две таблицы, package snapshot-поля и индексы. Для Stage 7 сессий с StartedAtUtc создаётся исходный сегмент; для Active/Paused — открытый. Цена берётся из InitialPrice (fallback на цену пакета), длительность — из неизменяемого пакета. Старые migrations сохранены.

Перед обновлением остановите обслуживание/вход игроков и Agent, сохраните БД и ключи. Обновите Server и Agent вместе: старый Agent несовместим с обязательным expectedSessionId logout. Примените migration и соберите Admin. Не удаляйте credentials.dat, agent_state.db и key ring. Downgrade удаляет новую историю/журнал; не понижайте рабочую БД без согласованного восстановления backup.

```powershell
dotnet restore GameClub.sln
dotnet build GameClub.sln --no-restore
dotnet ef database update --project src/GameClub.Infrastructure --startup-project src/GameClub.Server
dotnet test GameClub.sln --no-build
Push-Location src/GameClub.Admin
pnpm install --frozen-lockfile
pnpm build
pnpm test
Pop-Location
dotnet run --project src/GameClub.Server --launch-profile https
# В отдельных консолях после конфигурации:
dotnet run --project src/GameClub.Agent
dotnet run --project src/GameClub.Client
```

PG-тестам нужен GAMECLUB_TEST_POSTGRES на изолированном сервере; иначе они явно пропускаются. Connection string, enrollment, trust и dev Admin — в README.

## Ключевые файлы

- Domain: GamingSession, SessionOperation, StationSessionSegment, PlayerAuthSession, StationPowerPolicy.
- Application: GamingSessionService, SessionExtensionQuote, SessionBillingService, WalletService.
- Infrastructure: ClubData, GamingConfiguration, Stage 8 migration и snapshot.
- Server: GamingSessionsController, AgentPlayerController, StationCommandsController, PlayerAuthenticationService, AgentCommandService.
- Agent: AgentCommandHandler, SystemPowerCommands, WindowsSystemPowerApi, PlayerLoginService, StationApiClient.
- Admin: src/Dashboard.tsx, SessionOperations.tsx, models.ts, api.ts, tests/models.test.mjs.

## Ручная приёмка

1. Поднимите две тестовые станции одной группы, проверьте heartbeat/ClientConnected. Войдите тестовым игроком, пополните тестовый баланс и начните prepaid.
2. Проверьте quote +15/+30/+60/custom. Продление меняет время/баланс/SessionExtended. Повтор operationId не списывает снова; недостаток средств и выход за окно не оставляют изменений.
3. Перенесите Active, затем Paused. Источник очистится, цель получит того же игрока/игру, таймер/пауза сохранятся, история покажет сегменты. Offline/Maintenance/занятый ПК и другая группа отвергаются.
4. Войдите новым игроком на освобождённом ПК. Повторите старый logout: новый вход не завершится. Проверьте восстановление после отключения исходного Agent.
5. Для postpaid проверьте рост резерва, фактический settlement после переноса и освобождение остатка; на паузе billable time не растёт.
6. Operator с policy=false получает 403 для питания. Manager/Administrator создают команды только на свободном Online-ПК. Проверьте audit и запрет нового назначения во время защитной паузы.
7. Реальный Restart/Shutdown проверяйте вручную на выделенной Windows-станции, закрыв приложения и сохранив данные: signed delivery, OS acceptance, Offline, восстановление Agent после reboot. Не используйте рабочую станцию разработки.

Автотесты подменяют native API: физическое выключение и два реальных WPF-ПК не подтверждены. Финальное UI-подтверждение денежных операций требует ручной приёмки; Server проверяется на disposable PostgreSQL.

## Выполненные проверки

- Финальный `dotnet restore` и `dotnet build`: успешно, 0 warnings / 0 errors. Для изолированной среды использован локальный NuGet feed; внешний NuGet vulnerability audit этим прогоном не подтверждается.
- `dotnet test`: **379 passed, 0 failed, 0 skipped**, с настоящим PostgreSQL 16. Проверены конкурентные операции, rollback, глобальная идемпотентность, prepaid/postpaid/package, hard window/auth TTL, перенос/logout, security/replay и power-versus-login.
- Fresh PostgreSQL migrations выполняются интеграционными тестами; отдельно пройден populated Stage 7 → Stage 8 upgrade с 6 legacy-сессиями. `ef database update` успешен, `has-pending-model-changes` не обнаружил расхождений.
- Admin: `pnpm install --frozen-lockfile --offline`, TypeScript/Vite build и **27 tests passed**. Зависимости не обновлялись. Существующая React-панель расширена без смены self-hosted архитектуры или облачного размещения.
- Реальный HTTPS/WSS smoke: два synthetic Agent identity, HMAC, employee/agent JWT isolation, quote/extend/replay/conflict, transfer/replay, два сегмента и единичные события, старый logout против нового посетителя, Operator power 403 (включая свободный ПК), Admin busy 409, invalidation обеих станций и signed Ping ACK/Complete. Private CA проверяется с hostname/pinning; TLS validation не отключалась. Ни Windows Agent, ни native power API в этом smoke не запускались.
- Browser QA на локальном стенде: login/realtime, quote +15/+30/+60 и custom=7 с серверными суммами, zero-minutes validation, история двух ПК, фильтр/выбор destination, отдельные Restart/Shutdown confirmation dialogs и отмена, disabled power на занятом ПК, отсутствие power-кнопок у Operator. Внешний вид диалога проверен скриншотом. Из браузера extension/transfer/power не подтверждались; операции Server проверены HTTP/PG-тестами, не выдаются за полный UI E2E.

В baseline выявлен редкий PostgreSQL 40001 при COMMIT: явный Rollback уже завершённой Npgsql-транзакции маскировал retryable exception. ClubData теперь освобождает транзакцию перед bounded retry; deterministic deferred-trigger regression подтверждает единственную успешную запись сотрудника и audit после повтора.
