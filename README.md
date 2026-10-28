# Driver Updater

Портативная Windows-утилита (WPF, .NET 8): сканирует устройства, находит обновления
драйверов сразу в 10 источниках, делает бэкап перед установкой и ставит обновления.

Запускается без установки: single-file exe рядом с собой хранит настройки и данные
(`Data/`). Для работы требует запуска от администратора (манифест уже настроен).

## Возможности

- Сканирование PnP-устройств и оценка состояния драйверов
- Поиск обновлений в 10 источниках (см. ниже), включая анонсы для устройств
  без установленного драйвера (driverless: AMD PSP/GPIO и другие платформенные устройства)
- Резервное копирование драйверов перед установкой
- Установка обновлений с флагом «требуется перезагрузка» (pnputil)
- Проверка файлов: подпись Authenticode и контрольные суммы (NVIDIA/Lenovo)
- Переносимые настройки рядом с exe, атомарное сохранение, ротация логов

## Источники обновлений

| Источник | Что ищет |
|---|---|
| Microsoft Catalog | Microsoft Update Catalog |
| Windows Update | драйверы через клиент Windows Update |
| Windows Update Full | расширенный проход Windows Update (в т.ч. driverless-устройства) |
| Winget | обновления программ через winget |
| Chocolatey | обновления программ через choco |
| SDI | пакеты Snappy Driver Installer |
| DriverPack | пакеты DriverPack |
| Vendor | сайты вендоров |
| NVIDIA GeForce | драйверы NVIDIA |
| Lenovo | дескрипторы/каталог Lenovo (HWID, даты сборок) |

## Скачивание готовой версии

GitHub Actions при каждом пуше в `main` автоматически собирает portable-версию,
прогоняет тесты и публикует zip-архив во вкладке **Releases**
(`DriverUpdater-portable-build-N.zip`). Достаточно распаковать архив в любую
папку и запустить `DriverUpdater.exe` — установщик не нужен.

## Сборка

```
dotnet build DriverUpdater.sln
```

Публикация portable-версии (single-file, self-contained):

```
dotnet publish DriverUpdater/DriverUpdater.csproj -c Release -r win-x64
```

## Тесты

xUnit, 95 тестов (парсеры NVIDIA/Lenovo, сопоставление версий, проверка файлов,
конвертеры WPF, настройки, DI-обвязка источников):

```
dotnet test DriverUpdater.sln
```
