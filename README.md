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
- `InvalidState`/`InvalidParam` — терминальные статусы, лицензия
  помечается и больше не трогается (Steam стабильно отказывает на
  некоторых sub'ах — ретраить их бессмысленно); есть общий предохранитель
  `MaxAttempts = 5` на случай ещё каких-то статусов, чтобы один "залипший"
  SubID не блокировал всю очередь навсегда;
- можно защитить конкретные SubID от удаления списком исключений
  (`FreeLicenseCleanerExcludeSubIds`) — на случай, если среди
  "удаляемых" Steam окажется что-то, что вы хотите оставить;
- можно защитить всё, во что реально играли — `FreeLicenseCleanerMinPlaytimeToExcludeMinutes`
  сверяет playtime (через Steam) и содержимое пакета (через PICS, как
  делает сама ASF) и не трогает то, что наиграно больше порога;
- состояние (pending/processed/duplicate/invalid_state/invalid_param/
  excluded/failed) хранится в JSON-файле на бота:
  `plugins/FreeLicenseCleaner/data/<BotName>.json`;
- умеет автообновляться из GitHub Releases этого репозитория
  (`IGitHubPluginUpdates`) — так же, как это делают другие плагины ASF:
  ASF сам проверяет новые версии по своему обычному расписанию
  обновлений, плюс по команде `update`/`updateplugins`.

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
- **Релиз с готовым zip** — запушьте тег вида `X.Y.Z`, **БЕЗ** буквы `v`
  спереди (например через GitHub → Releases → Draft a new release, поле
  "Choose a tag" → `0.0.5`), не забыв синхронно поднять `<Version>` в
  `Directory.Build.props` до того же числа (`X.Y.Z.0`) — иначе внутри
  плагина останется старая версия, хотя тег уже новый.

  **Важно про префикс `v`:** `IGitHubPluginUpdates` в самой ASF делает
  `new Version(тег_релиза)` без какой-либо обработки префиксов — тег вида
  `v0.0.5` ломает автопроверку обновлений ASF с
  `System.FormatException: The input string 'v0' was not in a correct
  format.` Сама ASF всегда тегает релизы без `v` (`6.3.9.6`, не
  `v6.3.9.6`) — здесь нужно делать так же. (Теги `v0.0.3`/`v0.0.4` в этом
  репозитории созданы неправильно по этой же причине — автообновление на
  них не сработает, используйте только новые теги без `v`.)

  После пуша тега workflow [`Release`](.github/workflows/release.yml) сам
  соберёт Release-конфигурацию и прикрепит `FreeLicenseCleaner.zip` (уже
  пригодный для `<ASF>/plugins/FreeLicenseCleaner/`) к новому GitHub
  Release. Версия сейчас `0.0.9` — подтверждено, что плагин реально
  сканирует и удаляет лицензии на живом боте, но пока это была лишь
  ограниченная обкатка, так что до `1.0.0` ещё рано.

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
конфиге `config/<BotName>.json`. Всё, включая таймеры и лимиты, читается
оттуда же (`JsonExtensionDataAttribute` — ASF просто передаёт плагину
любые незнакомые ему ключи) — никакого отдельного файла настроек и
пересборки не нужно, только правка JSON и перезапуск ASF:

```json
{
	"Enabled": true,
	"...": "...",

	"FreeLicenseCleanerEnabled": true,
	"FreeLicenseCleanerDryRun": true,
	"FreeLicenseCleanerMaxAttempts": 5,
	"FreeLicenseCleanerSuccessDelaySeconds": 30,
	"FreeLicenseCleanerRateLimitDelaySeconds": 660,
	"FreeLicenseCleanerErrorDelaySeconds": 30,
	"FreeLicenseCleanerIdleDelaySeconds": 300,
	"FreeLicenseCleanerFullScanIntervalMinutes": 1440,
	"FreeLicenseCleanerStorePageDelayMilliseconds": 500,
	"FreeLicenseCleanerMaxStorePages": 1000,
	"FreeLicenseCleanerExcludeSubIds": [12345, 67890],
	"FreeLicenseCleanerMinPlaytimeToExcludeMinutes": 0
}
```

Все ключи опциональны — что не указано, остаётся с дефолтом (см. таблицу).
Значение с неверным типом или `<= 0` для чисел просто игнорируется (в лог
уйдёт warning), дефолт не ломается. Исключение — `MinPlaytimeToExcludeMinutes`:
там допустим и `0` (это и есть "выключено"), отвергается только
отрицательное значение.

| Ключ | По умолчанию | Что означает |
|---|---|---|
| `FreeLicenseCleanerEnabled` | `false` | Включить плагин для этого бота |
| `FreeLicenseCleanerDryRun` | `true` | Только логировать, что было бы удалено, ничего не трогая |
| `FreeLicenseCleanerMaxAttempts` | `5` | Сколько раз ретраить SubID с непонятным статусом, прежде чем навсегда пометить как failed |
| `FreeLicenseCleanerSuccessDelaySeconds` | `30` | Пауза после успеха / дубликата / invalid_state |
| `FreeLicenseCleanerRateLimitDelaySeconds` | `660` | Пауза после `RateLimitExceeded` от Steam |
| `FreeLicenseCleanerErrorDelaySeconds` | `30` | Пауза после сетевой ошибки / нераспознанного статуса |
| `FreeLicenseCleanerIdleDelaySeconds` | `300` | Пауза, когда очередь пуста и полный скан пока не нужен |
| `FreeLicenseCleanerFullScanIntervalMinutes` | `1440` | Как часто пересканировать всю страницу лицензий заново |
| `FreeLicenseCleanerStorePageDelayMilliseconds` | `500` | Пауза между запросами страниц пагинации во время полного скана |
| `FreeLicenseCleanerMaxStorePages` | `1000` | Предохранитель — максимум страниц пагинации за один скан |
| `FreeLicenseCleanerExcludeSubIds` | `[]` | SubID, которые никогда не будут удалены, даже если Steam помечает их как удаляемые |
| `FreeLicenseCleanerMinPlaytimeToExcludeMinutes` | `0` | `0` — выключено. `1` — защищать всё, во что хоть раз заходили. `30` — защищать всё, во что играли 30+ минут |

`FreeLicenseCleanerDryRun` по умолчанию `true` — проверьте лог/`flc status`
и только потом ставьте `false`.

### До версии `0.0.9` обновление плагина могло стереть всю накопленную историю

Это гораздо серьёзнее предыдущего пункта: не "пересканить заново", а
**полностью потерять** `processed`/`excluded`/`invalid_state`/
`invalid_param`/`failed_max_attempts` — после обновления полный скан
показывал `New: <все кандидаты>` и все счётчики по `0`, как будто плагин
никогда не запускался, даже если он реально отработал неделю и больше.

Причина — в самом механизме обновления плагинов ASF
(`Utilities.UpdateFromArchive`/`MoveAllUpdateFiles` в
`ArchiSteamFarm/Core/Utilities.cs`): применяя новую версию, ASF перемещает
**всё содержимое** папки плагина (`plugins/FreeLicenseCleaner/`) в
служебную папку `_old`, а затем на её место распаковывает файлы из
скачанного релиза. Не переносятся в `_old` только несколько
зарезервированных ASF'ом имён подпапок (`config`, `logs`, `debug`,
`plugins`, `_new`, `_old`) — до `0.0.9` плагин хранил своё состояние в
подпапке `data/`, которой в этом списке нет, а в самом zip-релизе такой
папки, естественно, тоже нет (она создаётся заново на пустом месте). В
итоге на каждое обновление плагина накопленный `data/*.json` уезжал в
`_old` и терялся безвозвратно — сам список обработанных SubID'ов, а не
только флаг "нужен ли пересканинг" (это был отдельный, куда менее
болезненный баг, см. следующий пункт).

**С `0.0.9` состояние хранится не внутри папки плагина**, а в
`<ASF>/config/plugins/FreeLicenseCleanerPlugin/<BotName>.json` — эта
папка принадлежит самой ASF, механизм обновления плагина её не трогает
(и она же переживает обновления самой ASF). Плагин также один раз
попробует сам перенести файлы из старой папки `data/` в новую при первой
загрузке после обновления — но это поможет только если с момента потери
истории ещё не было **следующего** обновления плагина (сама потеря
данных при апдейте на `0.0.9` уже, скорее всего, необратима для тех, кто
уже словил эту проблему раньше — старые файлы к моменту загрузки `0.0.9`
уже либо переехали в уже перезаписанную `_old`, либо будут перезаписаны
именно этим обновлением). Если у вас это ещё не случилось повторно —
самый надёжный вариант: **до** обновления на `0.0.9` вручную скопировать
`<ASF>/plugins/FreeLicenseCleaner/_old/data/*.json` (если папка ещё
существует) в `<ASF>/config/plugins/FreeLicenseCleanerPlugin/` — плагин
подхватит их сам.

### Обновление плагина не запускает пересканинг заново

До версии `0.0.8` полный пересканинг всех страниц (`~500+`, может занимать
десяток+ минут) форсировался при **любом** обновлении версии плагина —
потому что триггер был завязан на версию сборки, а не на реальные
изменения логики скана. Внешне это выглядело так, будто плагин "забыл"
всё, что было сделано раньше (на самом деле нет — уже обработанные
SubID'ы (`processed`/`excluded`/`invalid_state`/`failed`) никогда не
сбрасываются повторным сканом, пересканивался только сам процесс поиска
кандидатов). С `0.0.8` пересканирование при обновлении больше не
форсируется — только по расписанию (`FullScanIntervalMinutes`) или по
`flc scan` вручную.

### Про `MinPlaytimeToExcludeMinutes`

Проверяется **не на каждой попытке удаления**, а раз за полный скан (по
умолчанию — раз в сутки, либо сразу при `flc scan`) — чтобы не дёргать
Steam-запросами лишний раз. Механизм: берём реальный playtime всех ваших
игр через Steam (`playtime_forever`), для каждого ожидающего удаления
SubID через PICS узнаём, какие AppID в него входят (тот же механизм,
которым пользуется сама ASF), и если у любого из них playtime достиг
порога — весь пакет помечается как `excluded` и больше не трогается.

Если ASF ещё не успела получить access token на какой-то пакет (редкий
случай, обычно сразу после того как лицензия впервые появилась) — в этом
цикле он просто не проверяется и остаётся pending; проверится на
следующем скане.

Перезапустите ASF после изменения конфига.

## Команды бота

Нужен Master-доступ — как и у нативного `rmlicense`.

```
flc status       — счётчики очереди + текущие действующие настройки
flc scan         — немедленный полный пересканинг licenses-страницы
flc list         — реальные SubID/имена по каждой корзине (pending/invalid/excluded/failed),
                    не только счётчики
flc retry subid  — вернуть конкретный SubID в pending вне зависимости от текущего статуса
                    (сбрасывает счётчик попыток)
```

## Лицензия

[Apache License 2.0](LICENSE) — как и сама ArchiSteamFarm.
