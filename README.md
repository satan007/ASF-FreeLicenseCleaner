# ASF-FreeLicenseCleaner

[![CI](https://github.com/satan007/ASF-FreeLicenseCleaner/actions/workflows/ci.yml/badge.svg)](https://github.com/satan007/ASF-FreeLicenseCleaner/actions/workflows/ci.yml)

Плагин [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) (ASF),
который сам находит и удаляет бесплатные ("on demand") лицензии Steam —
по одной, с паузами, без внешних скриптов и IPC.

Работает целиком внутри процесса ASF:

- сканирует `store.steampowered.com/account/licenses/` через уже
  авторизованную сессию бота (`Bot.ArchiWebHandler`) — отдельно логиниться
  или таскать cookies не нужно;
- удаляет лицензии через `Bot.Actions.RemoveLicensePackage(subID)` —
  публичный API самой ASF (та же функция, что стоит за встроенной командой
  `rmlicense`/`RL`, начиная с ASF 6.1.7.8), который сразу отдаёт
  `SteamKit2.EResult` — без парсинга текста регулярками;
- по одной лицензии за раз, с паузами: 30 сек после успеха/дубликата/
  invalid state, 660 сек после `RateLimitExceeded`;
- `InvalidState` — терминальный статус, лицензия помечается и больше не
  трогается (Steam стабильно отказывает на некоторых sub'ах — ретраить их
  бессмысленно); есть общий предохранитель `MaxAttempts = 5` на случай ещё
  каких-то статусов, чтобы один "залипший" SubID не блокировал всю
  очередь навсегда;
- состояние (pending/processed/duplicate/invalid_state/failed) хранится в
  JSON-файле на бота: `plugins/FreeLicenseCleaner/data/<BotName>.json`.

## Статус проверки

Код собран и проверен (`dotnet build`, 0 ошибок/предупреждений) против:

- точных сигнатур ASF, сверенных построчно с исходником релиза
  `6.3.9.6` — `Bot`, `Actions.RemoveLicensePackage`,
  `ArchiWebHandler.UrlGetToHtmlDocumentWithSession`,
  `HtmlDocumentResponse.Content`, интерфейсы
  `IASF`/`IBot`/`IBotModules`/`IBotCommand2`, `EAccess`;
- настоящих пакетов `SteamKit2 3.4.0` и `AngleSharp 1.7.1` — тех же
  версий, что закреплены в `Directory.Packages.props` самой ASF.

Собрать этот плагин целиком (со ссылкой на реальный `ArchiSteamFarm.csproj`,
подключённый ниже как submodule) в песочнице, где писался этот плагин, не
вышло: с SDK `10.0.111` из apt-репозитория Ubuntu сама ASF на этом теге не
компилируется — `[with(...)]` в коллекционных литералах в её собственном
коде (`NLog/Targets/HistoryTarget.cs` и др.) требует более новой сборки
компилятора C#, чем оказалась в этом пакете. При этом собственный CI
JustArchiNET собирает этот же тег через `actions/setup-dotnet` с обычным
`dotnet-version: 10.0` (без preview-квалификатора) — то есть, скорее всего,
дело именно в устаревшем apt-пакете, а не в реальной потребности в
недо-выпущенном тулчейне. Смотрите бейдж CI выше и вкладку
[Actions](../../actions) — они сами покажут, собирается ли текущий код на
официальных раннерах GitHub.

## Готовые сборки

- **Автоматически на каждый пуш/PR** — workflow [`CI`](.github/workflows/ci.yml)
  собирает Debug и Release на Ubuntu и Windows и прикладывает Release-сборку
  как artifact к каждому запуску (вкладка Actions → выбранный run →
  Artifacts).
- **Релиз с готовым zip** — запушьте тег вида `v1.0.0`
  (`git tag v1.0.0 && git push origin v1.0.0`), и workflow
  [`Release`](.github/workflows/release.yml) сам соберёт Release-конфигурацию
  и прикрепит `FreeLicenseCleaner.zip` (уже пригодный для
  `<ASF>/plugins/FreeLicenseCleaner/`) к новому GitHub Release.

## Сборка руками

1. Клонируйте репозиторий вместе с submodule (в нём — ArchiSteamFarm,
   закреплённый на теге `6.3.9.6`):

   ```
   git clone --recursive https://github.com/satan007/ASF-FreeLicenseCleaner.git
   cd ASF-FreeLicenseCleaner
   ```

   Уже клонировали без `--recursive`? `git submodule update --init`.

2. Поставьте [.NET SDK](https://dotnet.microsoft.com/download) той версии,
   которую требует `ArchiSteamFarm/Directory.Build.props`
   (`TargetFramework`/`LangVersion`). Если у вас release-версия SDK
   ругается на `preview`-синтаксис — нужен соответствующий preview SDK
   с сайта Microsoft, релизный `dotnet install` его не поставит.

   Хотите собрать против более старого, точно стабильного тега ASF —
   `cd ArchiSteamFarm && git checkout <нужный_тег> && cd ..`
   (только не забудьте, что сигнатуры API могут отличаться от указанных
   выше — тогда сверьтесь с исходником этого тега).

3. Соберите:

   ```
   dotnet build FreeLicenseCleaner -c Release
   ```

   Если что-то не резолвится по `using` — это, скорее всего, мелкое
   расхождение версий ASF (namespace/сигнатура чуть сдвинулись между
   релизами). IDE (Rider/VS Code) подскажет правильный `using` одной
   кнопкой quick-fix; сами имена классов/методов проверены и стабильны.

4. Скопируйте содержимое `FreeLicenseCleaner/bin/Release/net*/` (всю
   папку, не только `FreeLicenseCleaner.dll`) в
   `<ASF>/plugins/FreeLicenseCleaner/`.

## Настройка

Плагин выключен по умолчанию — включается **на бота**, в его собственном
конфиге `config/<BotName>.json`:

```json
{
	"Enabled": true,
	"...": "...",
	"FreeLicenseCleanerEnabled": true,
	"FreeLicenseCleanerDryRun": true
}
```

`FreeLicenseCleanerDryRun` по умолчанию `true` — плагин только логирует,
что бы он удалил, ничего реально не трогая. Проверьте лог/`flc status`,
и только потом ставьте `false`.

Перезапустите ASF.

## Команды бота

Нужен Master-доступ — как и у нативного `rmlicense`.

```
flc status   — счётчики очереди (pending/processed/duplicate/...)
flc scan     — немедленный полный пересканинг licenses-страницы
```

## Настройки таймеров

Захардкожены в `FreeLicenseCleaner/CleanerWorker.cs`:
`SuccessDelaySeconds=30`, `RateLimitDelaySeconds=660`, `ErrorDelaySeconds=30`,
`FullScanIntervalMinutes=1440`, `MaxAttempts=5`. Поменять — правкой констант
и пересборкой.

## Лицензия

[Apache License 2.0](LICENSE) — как и сама ArchiSteamFarm.
