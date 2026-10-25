# TelegramVoiceTranscriber — бесплатная расшифровка голосовых сообщений

.NET 8 консольный Telegram-бот. Работает в группах и личных сообщениях.
После голосового бот отправляет кнопку «Показать транскрипцию». Распознавание
начинается по нажатию; результат появляется в сообщении бота.
Распознаёт голосовые локально через whisper.cpp, без платного AI API.

## Требования

- Windows 10/11, .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
- FFmpeg в PATH (в PowerShell: `winget install --id Gyan.FFmpeg -e` и перезапустить терминал)
- whisper.cpp Windows build и мультиязычная модель, скачиваются скриптом ниже
- Интернет для Telegram API и первоначальной загрузки модели

## Установка (PowerShell)

1. Распакуйте архив **в папку с латинским путём**, например `C:\VoiceBot`.
2. Перейдите в папку с файлом `TelegramVoiceTranscriber.csproj` (после распаковки внутри `C:\VoiceBot\TelegramVoiceTranscriber`, если распаковывали архив в `C:\VoiceBot`). Откройте в ней PowerShell и выполните:

   ```powershell
   winget install --id Gyan.FFmpeg -e
   # Перезапустите PowerShell после установки FFmpeg.
   powershell -ExecutionPolicy Bypass -File .\setup-windows.ps1
   Copy-Item settings.example.json settings.local.json
   ```

3. В Telegram откройте [@BotFather](https://t.me/BotFather):
   - `/newbot` → создайте бота и получите токен.
   - `/mybots` → выберите уже созданного бота → Bot Settings → Group Privacy → **Turn off**.
   - Убедитесь, что **Allow Groups** включён.
4. Откройте `settings.local.json` и впишите токен в `BotToken`.
5. В PowerShell проверьте:

   ```powershell
   ffmpeg -version
   dotnet --version
   Test-Path .\tools\whisper\Release\whisper-cli.exe # True
   Test-Path .\tools\models\ggml-base.bin      # True
   dotnet restore
   dotnet run
   ```

6. Добавьте бота в группу (после смены Group Privacy удалите/добавьте заново).
7. Отправьте голосовое сообщение и нажмите «Показать транскрипцию» под ответом бота.

**Важно:** не публикуйте `settings.local.json` или токен. Он даёт полный доступ к боту.

## Настройки

`settings.local.json`:

- `BotToken` — секретный токен бота.
- `Language`: `auto` (определение языка) или `ru` (русский, часто стабильнее).
- `MaxVoiceSeconds`: 180 (макс. 3 минуты на голосовое).
- `MaxQueuedTranscriptions`: 20 (включая выполняемое распознавание). При заполнении
  очереди бот предложит повторить нажатие позже. Повторные нажатия на одну кнопку
  не создают дубликаты; одновременно выполняется одно распознавание.
- `WhisperExe`, `WhisperModel`, `FfmpegExe`: пути к исполняемым файлам и модели.

Токен можно передать через переменную окружения `TELEGRAM_BOT_TOKEN` вместо
сохранения в JSON. Переменная имеет приоритет над `BotToken`; не добавляйте её
значение в Git или журналы.

Проверка локальных зависимостей без подключения к Telegram:

```sh
dotnet run -- --check
```

Сначала создайте `settings.local.json` из примера. Команда проверяет настройки,
наличие модели и запуск FFmpeg/Whisper. Она не проверяет точность модели или токен.
Относительные пути считаются от текущего рабочего каталога: запускайте из папки
проекта. Для исполняемых файлов поддерживаются абсолютные пути и поиск в `PATH`.

## Linux

Установите .NET 8 SDK, FFmpeg и соберите `whisper-cli` по инструкции whisper.cpp.
Скачайте мультиязычную модель и создайте `settings.local.json` из примера, заменив
Windows-путь `WhisperExe` на Linux-путь или имя `whisper-cli` из `PATH`.
Укажите `WhisperModel`, передайте токен через `TELEGRAM_BOT_TOKEN`, выполните
`dotnet run -- --check`, затем `dotnet run`.

Ctrl+C и SIGTERM на Linux отменяют текущую обработку, завершают FFmpeg/Whisper
и ожидают очистки временных файлов. Сохранённые кнопки остаются доступны после
перезапуска; незавершённую задачу нужно запустить повторным нажатием.

## Проверка изменений

```sh
dotnet test TelegramVoiceTranscriber.sln
```

Тесты проверяют очередь, повторы, отмену и завершение обработки, поиск файлов,
разбиение текста с эмодзи. Проверки дочерних процессов используют POSIX shell
и на Windows пропускаются. Токен Telegram и рабочая модель для тестов не нужны.

Для лучшей точности можете скачать мультиязычную модель `ggml-small.bin` (около 466 МБ)
из https://huggingface.co/ggerganov/whisper.cpp/tree/main и поменять `WhisperModel`.
Используйте `base` (не `base.en`, потому что `.en` только для английского).

## Приватность и ограничения

- При выключенном Privacy Mode Telegram позволяет боту получать сообщения группы, хотя этот код обрабатывает только voice и команды `/start`, `/help`. Предупредите участников группы.
- Голосовые загружаются во временную папку `tmp/` и удаляются после обработки; база данных не используется.
- `data/pending-voices.json` хранит идентификаторы сообщений и файлов Telegram для
  восстановления кнопок. Этот каталог исключён из Git; аудио в нём не хранится.
- Стандартный Telegram Bot API позволяет скачивать файлы размером до 20 МБ.
- Транскрибирование происходит на машине, где запущен бот. Если выключить компьютер или закрыть программу, бот перестанет отвечать.
- Для круглосуточной работы нужен постоянно работающий ПК/VPS. Модель на CPU может расшифровывать медленно.
- Если бот не получает голосовые в группе, перепроверьте Group Privacy и повторное добавление в группу.
- Если `ffmpeg` не найден, закройте и заново откройте терминал после `winget install`.
- Для авторасшифровки других типов (audio/video) код нужно расширить: версия MVP обрабатывает `Message.Voice`.

## Как устроен проект

```
Telegram Voice → кнопка → ограниченная очередь → Telegram.Bot.GetFile / DownloadFile
               ↓
            FFmpeg (ogg/opus → mono 16kHz wav)
               ↓
            whisper.cpp (локальное распознавание)
               ↓
            Telegram.Bot.SendMessage (reply на исходное)
```

Зависимости: https://www.nuget.org/packages/Telegram.Bot , https://github.com/ggml-org/whisper.cpp
