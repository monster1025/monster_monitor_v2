# Monster Monitor v2

Windows-приложение (WinForms, .NET Framework 4.7.2) для мониторинга процесса `ss.exe`, поддержки SSH-туннеля и контроля состояния сервисов с логированием в UI.

## Возможности

- мониторинг и автоподдержка процесса `ss.exe`;
- управление SSH-туннелем;
- предотвращение засыпания системы во время работы;
- логирование событий в консоль приложения;
- хранение настроек в локальном профиле пользователя;
- автообновление через GitHub Releases (с проверкой раз в час);
- поддержка proxy для запросов к GitHub API и скачивания релизов.

## Требования

- Windows 10+;
- .NET Framework 4.7.2 (target `net472`);
- доступ к GitHub (напрямую или через proxy).

## Сборка и запуск

Из корня репозитория:

```powershell
dotnet build .\src\MonsterMonitor\MonsterMonitor.csproj
dotnet run --project .\src\MonsterMonitor\MonsterMonitor.csproj
```

Или откройте `src/MonsterMonitor.sln` в Visual Studio и запустите проект `MonsterMonitor`.

## Настройки

Настройки сохраняются в:

`%LOCALAPPDATA%\MonsterMonitor\settings.ini`

Основные параметры:

- `SshHost`, `SshPort`, `SshUsername`, `SshPasswordProtected`;
- `RemotePort`, `LocalPort`;
- `MaxPingFailures`, `ReconnectTimeoutSec`;
- `SsProcessPath`, `SsArguments`;
- `Proxy` — адрес proxy для GitHub-запросов (пример: `http://host:port`).

Часть паролей хранится в защищенном виде через DPAPI.

## Автообновление

Приложение проверяет новые версии в GitHub репозитории:

- owner: `monster1025`
- repo: `monster_monitor_v2`

Логика:

1. Определяется текущая версия приложения.
2. Получается последняя версия релиза из GitHub.
3. Если версия новее текущей — скачивается `.zip`-ассет релиза.
4. Обновление подготавливается в директории приложения:
   - `.\update\<version>\update.zip`
   - `.\update\<version>\extracted`
   - `.\update\<version>\apply_update.bat`
5. Пользователю предлагается перезапустить приложение для применения обновления.

Периодичность проверки: каждые 60 минут (и первичная проверка после старта приложения).

## Интерфейс

- Главное окно:
  - кнопка `Настройки`;
  - кнопка `Выход`;
  - консоль логов (уровни: Debug/Info/Warn/Error).
- При сворачивании окно уходит в трей.

## Примечания

- Для автообновления релиз должен содержать `.zip`-ассет с файлами приложения.
- Если `/releases/latest` недоступен, используется fallback на список релизов `/releases`.
