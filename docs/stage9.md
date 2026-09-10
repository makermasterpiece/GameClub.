# Stage 9 — каталог игр и Playnite

## Реализовано

- Domain `Game`: Id, Name, nullable PlayniteGameId/Executable/CoverUrl, IsActive. PlayniteGameId — каноническая строка GUID **Game.Id библиотеки**, не идентификатор провайдера Game.GameId.
- `StationGame`: составной ключ StationId/GameId, Installed, LastDetectedAtUtc. Полный отчёт Agent сбрасывает отсутствующие игры в Installed=false; remap каталога сбрасывает старые подтверждения установки.
- PostgreSQL: миграция `20260908095534_AddGameCatalogAndStationInventory`, две таблицы, внешние ключи, уникальный PlayniteGameId. Существующие migrations неизменны.
- Admin: раздел «Игры», создание/редактирование/отключение, ID для настройки станций, свежесть установки на выбранном ПК, подтверждение отправки LaunchGame. Обновление REST каждые 15 секунд. Изображения CoverUrl не скачиваются сервером и не загружаются автоматически панелью.
- Agent: отдельный inventory worker каждые 30 секунд; ограниченные файлы конфигурации и SDK manifest; строгий локальный allowlist.
- WPF: «ИГРЫ» доступна только во время активной оплаченной сессии. Fullscreen запускается в интерактивной сессии Windows, не в Session 0 службы Agent.

## REST и разрешения

| Endpoint | Доступ / назначение |
| --- | --- |
| GET /api/admin/games | Employee ClubRead |
| POST /api/admin/games | ClubManageCatalog: Administrator/Manager |
| PUT /api/admin/games/{id} | ClubManageCatalog; выключение через IsActive=false |
| GET /api/admin/stations/{stationId}/games | ClubRead, nullable timestamp если отчёта не было |
| POST /api/agent/games/inventory | Station HMAC, до 500 записей; только известные точные пары GUID |
| POST /api/agent/games/authorize | Station HMAC, ожидаемая игровая сессия и optional GameId |
| POST /api/stations/{stationId}/commands | ClubOperate; type=LaunchGame, payload содержит **только gameId** |

Создание/изменение каталога, запросы и отказы запуска аудируются. Нельзя задавать executable, arguments, shell или рабочую папку в LaunchGame. Поле Game.Executable — только справочные метаданные.

```json
{ "type": "LaunchGame", "payload": { "gameId": "11111111-1111-4111-8111-111111111111" } }
```

Server выбирает активную игру и текущую сессию, формирует подписанный payload из GameId, GamingSessionId, PlayniteGameId. Команда действует максимум 30 секунд, привязана к станции, сохраняет nonce/signature/replay ledger. При dispatch и ACK проверки повторяются. Agent дополнительно получает свежую HTTPS/HMAC авторизацию максимум на 10 секунд, сверяет сессию и локальную установку, затем передаёт только типизированные GUID и срок в Named Pipe. WPF повторно сверяет состояние, allowlist и срок непосредственно перед запуском. Completed означает принятие запроса локальным адаптером, не доказательство готовности или успешного старта самой игры внутри Playnite.

Для конкретной игры требуются Active paid session, действующий вход того же игрока, свежие heartbeat Agent/Client, отсутствие pending power, активная запись каталога, Installed=true и отчёт не старше 90 секунд. Локальный SDK manifest должен быть не старше двух минут. Offline, Paused, истёкшая или перенесённая сессия запрещают запуск.

Для первого открытия Fullscreen свежий inventory не требуется: закрытый Playnite ещё не может запустить экспортёр. Требуются оплаченная сессия и валидная локальная конфигурация с непустым allowlist. **Сначала игрок нажимает «ИГРЫ»; затем доступен LaunchGame.** Уже запущенный посторонний Playnite автоматически не присваивается GameClub.

## Команды запуска и настройки

Из корня solution, после обычной настройки PostgreSQL, HTTPS и ключей согласно README:

```powershell
dotnet restore GameClub.sln
dotnet build GameClub.sln --no-restore
dotnet ef database update --project src/GameClub.Infrastructure --startup-project src/GameClub.Server
dotnet run --project src/GameClub.Server
# В других консолях на игровой Windows-станции:
dotnet run --project src/GameClub.Agent
dotnet run --project src/GameClub.Client
```

Admin: `pnpm --dir src/GameClub.Admin build`, затем `/admin/` на Server. Секреты и станции создаются существующим защищённым enrollment workflow, не заменяются тестовыми значениями.

Установите Playnite и расширение из [integrations/playnite](../integrations/playnite/README.md). Администратор вручную задаёт `%ProgramData%\GameClub\Playnite\config.json`: фиксированный локальный Fullscreen executable, путь manifest, CloseOnSessionEnd и пары GameClub/Playnite GUID. Отсутствие конфигурации выключает интеграцию, не ломая heartbeat/login/billing. Отсутствующий или повреждённый manifest не разрешает запуск конкретной игры.

## Ручной E2E на отдельной Windows-станции

1. Добавить игру в Playnite и Admin, настроить точную пару GUID и права файлов. Проверить загрузку SDK-расширения в Playnite, затем закрыть его.
2. Войти игроком без покупки: «ИГРЫ» недоступна. Создать paid session — кнопка появляется.
3. Нажать «ИГРЫ»: открывается управляемый Fullscreen, Client уступает экран. Убедиться, что timestamp manifest обновляется периодически, затем установка появляется в Admin в течение очередного 30-секундного отчёта.
4. Отправить LaunchGame через Admin и проверить реальный запуск игры, подпись/ACK/Completed и отсутствие executable в переданном payload.
5. Изменить GameId на неизвестный, передать поле exe, поставить сессию на паузу, отключить игру, удалить локальную пару, остановить экспортёр до устаревания manifest — запуск должен быть отклонён.
6. Завершить/перенести сессию, отключить Server/Agent или разорвать Pipe: Client возвращается на экран. При CloseOnSessionEnd=true он запрашивает корректное закрытие только принадлежащего ему экземпляра Playnite. При false закрытие не запрашивается.
7. Открыть Playnite вручную до GameClub: программа не должна захватывать и закрывать посторонний экземпляр. Повтор signed command ID/nonce не должен повторно запускать процесс.

## Безопасность и ограничения

- Локальная конфигурация, её родители, бинарные файлы, расширение и настройки запуска библиотеки требуют административных ACL. Код отвергает UNC/device/ADS/relative/reparse paths, но **не устанавливает ACL и не является kiosk-hardening**.
- SDK inventory доступен интерактивной учётной записи для записи: это сигнал наличия, не криптографическое доказательство установки. Он не расширяет защищённый allowlist.
- Allowlist закрывает запросы GameClub, но не ручной выбор других игр в интерфейсе Playnite. Клуб должен использовать подготовленную библиотеку и отдельную Windows application-control политику.
- `--shutdown` закрывает Playnite, **не останавливает уже запущенную игру**. Kill-by-name, произвольные команды, остановка деревьев игр и удалённый shell не добавлялись. Ошибка закрытия не отменяет возврат оболочки; остановку игры нельзя считать подтверждённой.
- Реальный Playnite, WPF foreground и две физические станции требуют ручного E2E. Автотесты и simulated SDK host не заменяют эту проверку.
- SignalR hubs не менялись: `/hubs/stations` ExecuteCommand/GamingSessionChanged и `/hubs/admin` DashboardChanged; каталог/инвентарь читаются через REST. Деньги, тарифы, перенос и продление остаются сценариями Stage 1–8.
- Stage 10 POS/смены, Stage 11 отчёты/бронирование, Stage 12 installer/update/production hardening не реализовывались.

## Ключевые файлы

Domain `Games/`; Application `Games/GameCatalogService.cs`, `GameLaunchService.cs`; Server `Controllers/GamesController.cs`, `Services/Commands/AgentCommandService.cs`; Contracts `Games/GameContracts.cs`; Agent `Services/Games/`, `Services/Client/ClientPipeServer.cs`; Client `Services/Playnite/`, `MainWindow.xaml`; Admin `src/GamesPage.tsx`; SDK `integrations/playnite/GameClub.LibraryExporter/`.

Верификация: `scripts/verify.ps1` выполняет restore/build/test. PostgreSQL opt-in — только изолированная БД через `GAMECLUB_TEST_POSTGRES`; миграции применяются к новым тестовым базам. Итог последнего прогона см. [implementation-report](implementation-report.md).
