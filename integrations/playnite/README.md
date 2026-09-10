# Playnite: локальная установка Stage 9

Это расширение Playnite, не удалённый PowerShell endpoint. Оно читает публичный
`PlayniteApi.Database.Games` и экспортирует **Game.Id** (GUID Playnite), имя и
`IsInstalled`; внутренние файлы БД Playnite не читаются. `Game.GameId` провайдера
(например Steam App ID) не подходит.

1. Администратор устанавливает Playnite на станции и добавляет игры обычным способом.
2. Копирует `GameClub.LibraryExporter` в каталог `Extensions` установленного Playnite.
3. Создаёт `%ProgramData%\GameClub\Playnite\config.json`, доступный игроку и Agent
   только для чтения. Родительские каталоги, Client, исполняемые файлы Playnite,
   расширение и библиотека/настройки запуска игр должны быть защищены от изменения
   игроком. Local Administrators/SYSTEM сохраняют управление. Не выдавайте игроку
   Modify на весь `%ProgramData%\GameClub\Playnite`.
4. Создаёт отдельный `%ProgramData%\GameClub\Playnite\Inventory` и выдаёт **только**
   на него Modify интерактивной учётной записи Playnite; Agent получает Read.
5. Указывает абсолютные локальные пути и сопоставления идентификаторов:

```json
{
  "fullscreenExecutable": "C:\\Program Files\\Playnite\\Playnite.FullscreenApp.exe",
  "libraryManifestPath": "C:\\ProgramData\\GameClub\\Playnite\\Inventory\\library.json",
  "closeOnSessionEnd": true,
  "allowedGames": [
    { "gameId": "11111111-1111-4111-8111-111111111111", "playniteGameId": "22222222-2222-4222-8222-222222222222" }
  ]
}
```

Замените пример GUID на GameClub Game.Id и локальный Playnite Game.Id. Максимум
500 игр. Экспорт атомарный UTF-8, schemaVersion=1, UTC; запуск конкретной игры
требует свежести до двух минут, Installed=true и точного allowlist. Agent сообщает
инвентарь каждые 30 секунд. Расширение экспортирует при старте/обновлении/установке
и каждые 20 секунд. Для другой папки ProgramData используйте её фактический путь.
Периодический экспорт работает в отдельном PowerShell runspace с постоянно активным
pipeline: он не зависит от обработки событий простаивающим extension host. При
выходе Playnite worker останавливается и освобождает runspace.
ИГРЫ может открыть Fullscreen до появления свежего экспорта (bootstrap); конкретная
LaunchGame до экспорта отклоняется. После остановки Playnite экспорт устаревает.

Не запускайте эту psm1 вне Playnite: API предоставляет его extension host.
Стандартный пользователь может менять собственные данные Playnite: allowlist
GameClub не заменяет ACL и kiosk/application-control политику Windows. Экспорт не
криптографически доверенный: он не может расширить allowlist, но его подмена игроком
может исказить Installed. Не выдавайте игроку административные права.

Ручная проверка на реальной Windows станции обязательна: загрузка расширения в
Playnite, обновление timestamp каждые 20 секунд, install/uninstall, stale manifest,
ИГРЫ в оплаченной сессии, LaunchGame и корректное закрытие Fullscreen. Без установленного
Playnite автоматические тесты проверяют контракты/валидацию, а не его runtime.

`Test-ExporterHost.ps1` дополнительно проверяет экспорт в реальном embedded
Windows PowerShell 5.1 с mock SDK: после возврата startup handler host простаивает
22 секунды, а timestamp должен обновиться. Все тестовые файлы остаются в `work`,
реальный ProgramData не меняется. Это не заменяет проверку в самом Playnite.

Первичные источники SDK:
- https://api.playnite.link/docs/tutorials/extensions/scripting.html
- https://api.playnite.link/docs/tutorials/extensions/extensionsManifest.html
- https://api.playnite.link/docs/tutorials/extensions/library.html
- https://api.playnite.link/docs/tutorials/extensions/events.html
