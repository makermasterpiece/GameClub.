# Client IPC security

## Boundary

IPC используется только между локальным Windows Agent и визуальным WPF Client. Сетевой TCP listener не создаётся. Pipe name: `GameClub.Agent.ClientState`.

Agent является server и единственной стороной, которая отправляет `ClientStateMessage`. Client protocol принимает только:

- `GetState`;
- heartbeat;
- ACK конкретной state revision;
- `PlayerLoginRequest` с bounded username/password;
- `PlayerLogoutRequest` без session identity override.

Client не может прислать `Locked`, `Available` или другой новый state. Переход к разрешающему состоянию опирается на проверенную подписанную Server command и подтверждённую HTTP-сверку/login state от Server; один IPC request сам по себе state не устанавливает. Logout/end/expiry обрабатываются Server, а потеря связи переводит локальную проекцию в Offline без права восстановить игру из кэша.

Password присутствует только во входящем login request и никогда не включается в `ClientStateMessage`, login result, логи или SQLite. Максимальный размер любого line-delimited JSON сообщения — 16 KiB; username ограничен 32, password — 128 символами. Agent сам определяет station identity и current auth session.

Размер ограничивается во время чтения, до безразмерного накопления строки. Reader продолжает принимать ACK/heartbeat, пока отдельная единственная login/logout операция ожидает HTTP; повторный player request при busy получает безопасный отказ, не становится очередью паролей. Read timeout — 15 секунд, write timeout — 5 секунд; ошибка heartbeat отменяет ожидающий read. Неверный JSON логируется только по типу exception, не вместе с request content.

`ClientStateMessage` может содержать безопасный `GamingSessionSnapshot`, без credentials и готовых команд. WPF `Stopwatch` интерполирует remaining только для отображения; Paused не уменьшается, duplicate snapshot не перезапускает отсчёт. Достижение нуля не создаёт локального разрешения/завершения: нужна новая подтверждённая Agent projection.

## Windows ACL

`WindowsClientPipeServerFactory` создаёт pipe с `FirstPipeInstance` и explicit ACL:

- Local System — Full Control;
- Built-in Administrators — Full Control;
- текущий Agent identity — Full Control;
- Interactive SID — Read/Write для локального UI Client.

Remote/network logon не получает Interactive SID. Client всегда подключается к server name `.`.

## Remaining limitation

Windows ACL различает security principals, но не два процесса одного interactive user. Поэтому вредоносный процесс под тем же user SID теоретически может подключиться раньше официального Client и отправить ложный ACK; когда Agent не запущен, он также может попытаться создать pipe с тем же именем для подмены визуального состояния.

`FirstPipeInstance` блокирует второй server, пока Agent владеет pipe. Для production hardening installer должен запускать Agent до Client и ограничивать запуск произвольных программ. На следующем security этапе можно добавить проверку server process identity/signature через Windows APIs или отдельный authenticated IPC handshake. Это не заменяется локальным TCP и не требует keyboard hooks/system policy hacks.
