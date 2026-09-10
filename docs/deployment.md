# Развёртывание MVP до Stage 10

Этот runbook описывает имеющиеся компоненты до Stage 10, а не автоматический installer или готовый hardened deployment. Stage 11–12, автообновление, OS kiosk policies и автоматизированные backup/restore не входят в реализацию.

## Предварительные условия

Server требует .NET 8/ASP.NET Core runtime и PostgreSQL 16. Полная разработка и сборка требуют .NET SDK 8; Admin — Node.js 24+ и pnpm 11.19.0. Agent/Client запускаются на Windows; для WPF необходим .NET 8 Desktop Runtime при framework-dependent публикации.

Времена игровых сессий хранятся в UTC. Настройте синхронизацию часов Server и станций: HMAC принимает timestamp только в пределах ±60 секунд. `Club:TimeZoneId` задаёт локальную зону окон пакетов, по умолчанию UTC; она должна существовать в ОС Server. Пауза не переносит абсолютное окончание ночного окна или срок авторизации игрока.

## Конфигурация и доверие

| Переменная | Назначение |
| --- | --- |
| `ConnectionStrings__GameClubDb` | Полное подключение к PostgreSQL; секрет |
| `Security__CommandSigningPrivateKeyPath` | Внешний постоянный ECDSA P-256 PEM |
| `Security__CommandSigningCertificatePath` | Альтернатива PEM: PFX с ECDSA private key |
| `Security__CommandSigningCertificatePassword` | Пароль PFX, только из секретного provider |
| `Security__DataProtectionKeysPath` | Постоянный защищённый key ring вне release-каталога |
| `Club__TimeZoneId` | Часовая зона клуба, например UTC |
| `Club__AllowNegativeBalance` | По умолчанию false; разрешение долга — явная бизнес-политика |
| `Club__OperatorCanPower` | По умолчанию false; явно разрешает Operator команды питания |
| `Server__BaseUrl` | На Agent: HTTPS origin Server, совпадающая с DNS-именем сертификата |
| `Station__Name` | Человекочитаемое имя станции |

TLS-сертификат HTTPS и ECDSA signing key выполняют разные задачи. HTTPS защищает канал и enrollment; pinned ECDSA key подтверждает команды. Не заменяйте signing key при каждом release: все существующие Agent закрепили соответствующий public key. При сознательной замене ключа требуется управляемый re-enrollment, не отключение проверки подписей.

Data Protection key ring нужен для чтения сохранённых station secrets. Не помещайте его в временный каталог контейнера/release. Ограничьте доступ к нему и private key для Server identity, SYSTEM/Administrators. На Windows используется DPAPI-защита key ring; на других ОС необходимо отдельно обеспечить защищённое хранение ключей. Бэкап одной БД без необходимых ключей недостаточен для восстановления действующих credentials.

Compose поставляет **только PostgreSQL**. `.env` подставляет `POSTGRES_PASSWORD` в Compose; ASP.NET Core не читает этот файл автоматически. В поставляемом compose порт 5432 опубликован на host: ограничьте его firewall/локальным bind или закрытой сетью перед использованием вне dev-станции. Не выставляйте PostgreSQL в публичный интернет.

## Первое развёртывание

Для обновления Stage 7 → 8 сначала остановите обслуживание и сохраните БД/ключи. Server и Agent обновляются вместе: новый logout требует expectedSessionId. Примените AddSessionExtensionsTransfersAndStationHistory, обновите Admin; сохраните credentials, replay ledger и ключи. Подробнее: [Stage 8](stage8.md).

1. Подготовьте PostgreSQL, секреты и постоянные ключи по README.
2. Выполните `dotnet tool restore`, `dotnet restore GameClub.sln`, `dotnet build GameClub.sln --no-restore`.
3. Примените migrations: `dotnet ef database update --project src/GameClub.Infrastructure --startup-project src/GameClub.Server`.
4. Из доверенной консоли выполните явный bootstrap первого Administrator с `Bootstrap__Username` и `Bootstrap__Password` (12–128 символов): `dotnet run --project src/GameClub.Server -- --bootstrap-admin`.
5. Немедленно очистите Bootstrap-переменные. Bootstrap работает только при пустой таблице сотрудников и завершается без запуска HTTP-listener; при обычном старте сотрудники автоматически не создаются.
6. Соберите Admin и опубликуйте Server командами ниже.
7. Настройте HTTPS и запустите Server; войдите Administrator, создайте сотрудников и enrolment token отдельно для каждой станции.
8. На станциях запустите Agent, затем WPF Client в интерактивной Windows-сессии; выполните manual E2E.

## Admin: dev и publish

Из корня репозитория:

```powershell
Push-Location src/GameClub.Admin
pnpm install --frozen-lockfile
pnpm build
pnpm test
Pop-Location
dotnet publish src/GameClub.Server -c Release -o artifacts/server-stage8
New-Item -ItemType Directory -Force artifacts/server-stage8/wwwroot/admin
Copy-Item -Path 'src/GameClub.Admin/dist/*' -Destination 'artifacts/server-stage8/wwwroot/admin' -Recurse
```

Используйте отдельный release-каталог, не директорию с данными PostgreSQL/Agent/ключами. Admin не включается в .NET solution build: `dotnet build` не заменяет `pnpm build`. Сначала сформируйте `dist`, затем копируйте его; не публикуйте исходный dev-server Vite как production frontend.

При `dotnet run` из source Server ищет сначала `wwwroot/admin`, затем соседний `src/GameClub.Admin/dist`. Опубликованному Server нужен каталог `wwwroot/admin`: путь к source checkout не предполагается. После размещения файлов URL — `/admin/`; клиентские маршруты получают SPA fallback. Наличие static-файлов само по себе не даёт доступ к API: серверные endpoints защищены Employee JWT.

Vite использует `http://127.0.0.1:5173/admin/` только для локальной разработки. Его `/api` и `/hubs` proxy направлены на `https://localhost:5001`, проверка сертификата включена (`secure: true`). Для доверия Node экспортируйте только публичную часть ASP.NET dev certificate:

```powershell
New-Item -ItemType Directory -Force 'C:\ProgramData\GameClub\Development'
dotnet dev-certs https --trust
dotnet dev-certs https --export-path 'C:\ProgramData\GameClub\Development\aspnet-dev-public.pem' --format PEM
$env:NODE_EXTRA_CA_CERTS = 'C:\ProgramData\GameClub\Development\aspnet-dev-public.pem'
Push-Location src/GameClub.Admin
pnpm dev
Pop-Location
```

Не добавляйте `--password` или `--no-password` к этому экспорту: нужен только public certificate, не private key. Семантика экспорта описана в [dotnet dev-certs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-dev-certs). `NODE_EXTRA_CA_CERTS` должен быть установлен **до** запуска Node/Vite. Не отключайте TLS-validation через `secure:false`, `NODE_TLS_REJECT_UNAUTHORIZED=0` или `curl -k`.

## Server вне Development

Используйте настоящий TLS certificate с доверенной цепочкой и DNS-именем, по которому обращаются станции. Пример переменных для прямого Kestrel HTTPS-listener:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ASPNETCORE_URLS = 'https://0.0.0.0:5001'
$env:Kestrel__Certificates__Default__Path = 'C:\ProgramData\GameClub\Server\https.pfx'
# Kestrel__Certificates__Default__Password, DB connection и signing key — из secret provider.
Push-Location artifacts/server-stage7
dotnet GameClub.Server.dll
Pop-Location
```

Рабочим каталогом процесса должен быть каталог опубликованного Server, чтобы `appsettings.json` и `wwwroot/admin` разрешались из release, а не из произвольного cwd. При запуске через service manager задайте этот working directory явно.

Не копируйте localhost development certificate на игровые станции как постоянное production-решение. При TLS termination на reverse proxy нужны отдельные корректные HTTPS/forwarded-headers/firewall настройки и проверка WebSocket/SSE/LongPolling; готовой proxy-конфигурации в MVP нет. Swagger включён только в Development. Employee-protected management API работает и вне Development; анонимный доступ от этого не появляется.

## Windows Agent и Client

```powershell
dotnet publish src/GameClub.Agent -c Release -r win-x64 --self-contained false -o artifacts/agent-stage7
dotnet publish src/GameClub.Client -c Release -r win-x64 --self-contained false -o artifacts/client-stage7
```

Для каждой станции нужны собственные MachineName, StationId, secret и replay DB. Не клонируйте `credentials.dat`/`agent_state.db` на другой ПК. Новый enrollment token создаёт Administrator; передавайте его как `Security__EnrollmentToken` только для первого запуска. После успешной регистрации уберите token из окружения последующих запусков.

Agent имеет интеграцию `Microsoft.Extensions.Hosting.WindowsServices`, но автоматическая установка службы и запуск Client при входе в Windows не реализованы. WPF Client должен работать в интерактивной пользовательской сессии, а не в Session 0 службы. Не рассчитывайте на визуальную оболочку как на границу защиты ОС.

DPAPI `LocalMachine` требует правильных ACL: SYSTEM/Administrators/service identity должны иметь необходимые права, обычные пользователи не должны читать или изменять credential/replay files. Один DPAPI machine scope без ACL не защищает от другого процесса на той же машине. Политика Named Pipe описана в `client-ipc-security.md`.

Для проверки двух станций используйте два Windows-PC/VM. Два экземпляра Agent на одной машине имеют одинаковый `Environment.MachineName`, конкурируют за фиксированный Named Pipe и не являются корректным E2E двух станций.

## Обновление и проверки

Stage 10: приостановите приём POS-операций, сделайте восстановимую копию БД и примените `20260908103342_AddPosProductsSalesAndEmployeeShifts` стандартной командой EF из README. После этого обновите Server и Admin dist. Новых настроек Agent/Client, ключей, портов, фоновых сервисов или внешнего терминала не требуется. Проверьте роли сотрудника, каталог, тестовую смену и продажу на отдельной тестовой БД по [Stage 10](stage10.md). Cash/Card обозначают только учёт вручную подтверждённой сотрудником оплаты; продукт не обеспечивает эквайринг, фискализацию или автоматическую сверку терминала. Нельзя проверять возврат на реальной продаже без согласованной операции клуба.

Stage 9 требует согласованного обновления Server, Agent и Client: новая команда и IPC несовместимы со старым allowlist. Сначала примените `20260908095534_AddGameCatalogAndStationInventory`, затем обновите бинарные файлы и Admin dist. Playnite устанавливается отдельно в интерактивной сессии; локальный config/ACL и SDK-расширение описаны в [установке Playnite](../integrations/playnite/README.md). Без config интеграция отключена. Не запускайте GUI из Windows Service и не предоставляйте игроку права изменять allowlist или executable.

Перед обновлением остановите приём новых игровых сессий, обеспечьте восстановимую копию БД и ключей штатными средствами вашей эксплуатации, проверьте SQL migrations на тестовой БД. Автоматической процедуры безопасного отката схемы/backup restore продукт пока не предоставляет.

Применение migrations, смена бинарных файлов и перезапуск Server — отдельные управляемые шаги. Persisted ключи и данные Agent остаются вне release-каталогов. После обновления проверьте вход сотрудника, heartbeat, HMAC-current, подписанную команду, завершение billing и Admin real-time/fallback polling.

`GAMECLUB_TEST_POSTGRES` разрешён только для изолированного тестового PostgreSQL: integration suite создаёт/удаляет БД `gameclub_test_<guid>`. Не выдавайте тестовому процессу административные права на production PostgreSQL. Полный сценарий проверки находится в `manual-e2e-test.md`; фактическое покрытие последнего запуска — в `implementation-report.md`.
