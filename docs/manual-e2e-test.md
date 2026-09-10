# Ручная E2E-проверка до Stage 7

Продолжение checklist для продления, переноса и команд питания — в [Stage 8](stage8.md#ручная-приёмка). Исторические результаты Stage 7 ниже сохранены отдельно от нового этапа.

Это воспроизводимый checklist, а не утверждение, что все физические/UI шаги уже выполнены. Фактические результаты фиксируются отдельно в `implementation-report.md`. Используйте отдельную тестовую БД и тестовые аккаунты, не реальные деньги посетителей.

Статус последней проверки 2026-09-05: backend/PostgreSQL, Admin login/realtime/таймер, players/wallet/ledger, catalog/employees, Operator UI permissions, pause/resume/logout и read-only WebMCP проверены. Подтверждение EndSession в browser **не проверено**: автоматическая проверка безопасности отклонила финансово значимое подтверждение; modal отменён без нажатия подтверждающей кнопки и без обхода ограничения. Server end/settlement tests пройдены. Полный сценарий двух физических Windows-станций с WPF также **не проверен**; пункты ниже остаются инструкцией для отдельного стенда.

## Подготовка

1. Выполните setup README: PostgreSQL, migrations, постоянный ECDSA key, Data Protection key ring, явный bootstrap Administrator.
2. Соберите .NET и Admin: `dotnet restore`, `dotnet build`, `dotnet test`; затем `pnpm install --frozen-lockfile`, `pnpm build`, `pnpm test` в `src/GameClub.Admin`.
3. Запустите Server по HTTPS и Admin на `/admin/`. Для Vite используйте доверенный публичный dev certificate через `NODE_EXTRA_CA_CERTS`, не отключение TLS.
4. Подготовьте две отдельные Windows-станции/VM с разными MachineName. На каждой запустите один Agent и один WPF Client. Не запускайте два Agent на одном ПК для имитации двух станций: identity и Named Pipe у них конфликтуют.
5. Для каждого Agent создайте отдельный одноразовый enrollment token через Administrator. Не копируйте credential-файлы между машинами.

## A. Сотрудники, роли и анонимный доступ

1. Войдите первым Administrator через Admin или `POST /api/admin/auth/login`.
2. Проверьте `GET /api/admin/auth/me`. Ответ содержит безопасные id/username/role/permissions, но не password hash.
3. Создайте Operator и Manager через `POST /api/admin/employees` с полями `username`, `password`, `role`. Пароли 12–128 символов задавайте сами, не используйте опубликованные тестовые пароли.
4. Без Bearer token вызовите `GET /api/stations` и `POST /api/admin/enrollment-tokens`: ожидается 401, не успешная операция.
5. Войдите Operator: dashboard, чтение игроков/каталога/кошелька, создание игрока, deposit и операции игровых сессий разрешены.
6. Тем же Operator token вызовите `POST /api/admin/enrollment-tokens`, `POST /api/stations/{id}/credentials/revoke`, `POST /api/admin/users/{id}/wallet/adjustment` и изменение каталога: ожидается 403. Недостаточно скрыть кнопку — проверяйте прямой REST-запрос. Отдельной read-only роли нет; здесь проверяется доступ Operator к чтению и запрет privileged mutation.
7. Manager может менять каталог/баланс/статус игрока, но не создавать сотрудников, enrollment tokens или отзывать station credentials.
8. Administrator меняет роль/пароль либо отключает тестового сотрудника; старый JWT должен перестать проходить проверки. Последнего действующего Administrator нельзя отключить или понизить.
9. Logout сотрудника (`POST /api/admin/auth/logout`) отзывает его сессии. Перезагрузка Admin не должна восстанавливать JWT из localStorage.

## B. Регистрация, heartbeat и два вида offline

1. Убедитесь, что PC-01 и PC-02 представлены разными StationId, именами и MachineName.
2. Дождитесь ONLINE обеих станций; `LastSeenAtUtc` должен обновляться от heartbeat.
3. Откройте WPF Client: dashboard должен показывать отдельно `agentOnline` и `clientConnected`, а не выводить связь Client только из статуса станции.
4. Закройте только WPF Client на PC-02. Agent остаётся ONLINE; в пределах следующего heartbeat/health timeout Admin показывает отсутствие Client. Откройте Client снова — связь восстанавливается.
5. Остановите Agent на PC-02 более чем на 30 секунд. Server monitor с интервалом примерно 10 секунд переводит станцию в Offline; dashboard обновляется через событие/повторное чтение.
6. Запустите Agent снова без нового token: тот же StationId должен восстановиться из DPAPI credential. Отсутствие Server не должно приводить к завершению процесса Agent.

## C. Подписанный Lock/Unlock и вход игрока

1. Отправьте `LockStation` с `payload:null`, затем `UnlockStation` через `POST /api/stations/{id}/commands` с Employee token. Следите за persisted status команды через `GET /api/commands/{id}` или dashboard.
2. Проверяйте итоговую доставку: принятие POST не означает, что Client уже подтвердил визуальное состояние. Для подключённого Client ожидается ACK и дальнейший `Completed`.
3. Locked скрывает форму входа. Available показывает login/password. Maintenance — отдельное безопасное визуальное состояние, но полного maintenance-workflow в UI нет.
4. Создайте двух игроков через `POST /api/admin/users`, например `player-a` и `player-b`, с собственными паролями. GET-поиск не должен возвращать password/hash.
5. На PC-01 войдите player-a. Client покажет приветствие и ожидание игровой сессии, но не выдуманный баланс/тариф/таймер.
6. Неверный пароль и неизвестный username дают одинаковый внешний `INVALID_CREDENTIALS`. После пяти неудачных попыток за минуту следующая попытка ограничена; успешный вход внутри того же окна не стирает накопленные ошибки.
7. Попытка player-a войти на PC-02 при действующей авторизации на PC-01 отвергается. Player-b может войти на PC-02 независимо.

## D. Prepaid на 3 часа

1. Узнайте StationGroupId PC-01. Создайте активный тариф для этой группы с `hourlyPrice:600.00` либо используйте существующий подходящий.
2. Пополните кошелёк player-a на 5000.00 новым `operationId` через `/api/admin/users/{userId}/wallet/deposit`.
3. Покупайте в свежей авторизации: до её 12-часового окончания должно оставаться больше трёх часов.
4. Выполните `POST /api/admin/gaming-sessions`:

```json
{
  "operationId": "NEW_GUID",
  "userId": "PLAYER_A_GUID",
  "stationId": "PC01_GUID",
  "mode": "Prepaid",
  "tariffId": "TARIFF_GUID",
  "packageId": null,
  "purchasedMinutes": 180,
  "postpaidLimit": null
}
```

5. Ожидается одно списание 1800.00, баланс 3200.00 и одна игровая сессия. Client/панель показывают примерно 03:00:00 с уменьшением по серверному состоянию.
6. Повторите **то же тело с тем же operationId**: второго списания и второй сессии нет. Измените тело, сохранив operationId: ожидается `IDEMPOTENCY_CONFLICT`.
7. Измените цену каталога. Купленная сессия сохраняет свою цену; новую цену увидит только новая покупка.
8. Pause останавливает расход оплачиваемого времени, Resume продолжает его. Проверяйте не только цифры UI, но и `SessionEvent`/Server snapshot. Пауза не продлевает авторизацию и абсолютное ночное окно.
9. Досрочно End: авторизация игрока закрывается, Client возвращается в Available либо более строгую сохранённую базовую политику Locked/Maintenance. Автоматического возврата unused prepaid минут нет; FinalPrice остаётся 1800.00.

## E. Перезапуски и разрывы связи

1. Создайте новую тестовую prepaid/postpaid сессию после повторного входа игрока.
2. Перезапустите только WPF Client. Он получает текущую проекцию Agent; покупка и списание не повторяются.
3. Перезапустите Agent при работающем Server. До authenticated reconciliation Client показывает Offline, затем получает актуальные player/gaming state. Таймер не начинается заново с полной длительности.
4. Перезапустите Server с **тем же** ECDSA private key и Data Protection key ring. Agent переживает ошибки и восстанавливает heartbeat/SignalR. Повторный enrollment не должен требоваться.
5. На тестовом стенде временно остановите Server. Agent остаётся в retry-loop, Client показывает Offline и не разрешает доступ только по SQLite snapshot. После восстановления Server состояние перечитывается.
6. Если пакет/сессия истекли во время недоступности, Server завершает её при проверке. Поздняя проверка не добавляет оплату сверх сохранённого deadline.

## F. Postpaid, деньги и окна

1. Авторизуйте игрока заново. Запустите postpaid по тарифу с `postpaidLimit`, подходящим доступному балансу: balance пока не уменьшается, reserved увеличивается, available уменьшается.
2. Убедитесь, что повторная покупка/коррекция не может потратить зарезервированные деньги при `Club:AllowNegativeBalance=false`.
3. Проведите часть времени Active, часть Paused, затем End. Резерв освобождается; одна GamingCharge списывает фактически накопленное активное время со снимком тарифа.
4. Повторный End не создаёт второе списание. Чужая группа станции и неактивный тариф отвергаются до финансовых изменений.
5. Проверьте пакет 22:00–08:00 в настроенной локальной зоне. Поздняя покупка ограничена концом окна, но цена остаётся фиксированной. В 08:00 старт запрещён; pause/resume не позволяют выйти за абсолютную границу окна.
6. Для границ окон/DST используйте автоматические тесты с управляемым временем. Не меняйте часы рабочей станции/Server ради проверки: это ломает HMAC и другие сессии.
7. Prepaid, превышающий оставшийся срок авторизации, отвергается до списания. Postpaid funding-time ограничивается остатком авторизации. После её истечения сессия закрывается даже на паузе.

## G. Real-time, безопасность и replay

Admin слушает `/hubs/admin` → `DashboardChanged` и перечитывает REST. Agent слушает `/hubs/stations` → `GamingSessionChanged`, затем получает HMAC snapshot. Разорвите SignalR-соединение без остановки HTTP: polling должен восстановить данные, событие не является единственным источником истины.

Не воспроизводите forged/replayed подписанные команды на рабочих станциях. В изолированной тестовой среде выполните:

```powershell
dotnet test GameClub.sln --no-build --filter "FullyQualifiedName~Security|FullyQualifiedName~AgentCommandHandlerTests|FullyQualifiedName~EndpointPermissionTests"
```

Проверьте cases: неверная подпись, другой station ID, просрочка, изменённый payload, повтор command ID/nonce после SQLite restart. Повторная доставка не должна исполнять действие второй раз; Agent повторяет безопасный результат из persistent replay ledger. Authenticated Agent JWT с любыми station claims не должен проходить Employee endpoints.

Administrator может проверить revoke на специально выделенной тестовой станции: `POST /api/stations/{id}/credentials/revoke`. После этого старый HMAC/JWT не должен работать. Для восстановления остановите Agent, обеспечьте новый enrollment token и контролируемое удаление/замену только её старого credential; не удаляйте общую папку или ключи Server.

## Фиксация результата

Запишите версию/commit, время UTC, схему migrations, список тестов passed/failed/skipped, ОС двух станций и отдельно отмеченные ручные/UI шаги. Не прикладывайте JWT, пароли, enrollment tokens, private keys или расшифрованный credential. Непройденный физический/визуальный шаг нельзя заменять фразой «unit tests зелёные». Дополнительные сценарии: [Stage 8](stage8.md), [Stage 9 Playnite](stage9.md) и [Stage 10 POS](stage10.md). Stage 11–12 не входят в этот checklist.

## Stage 10 — отдельная тестовая кассовая смена

На изолированной БД выполните checklist из Stage 10: каталог и запрет изменения Operator, собственная смена, Cash/Card/Wallet-корзины, price-change guard, недостаточный остаток/баланс с полным rollback, retry одного operationId, полный возврат Manager и повтор, закрытие и запрет новых операций. Сверьте Cash/Card/Wallet нетто, ExpectedCash и CashDifference; отдельно исключение system gaming-проводок и стабильность GamingSalesSnapshot после закрытия. Реальный банковский платёж или фискальный чек не ожидаются: интеграции нет. Не используйте реальные деньги/продажи для smoke.
