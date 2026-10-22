using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

// Voice messages receive an inline button. Whisper runs only after the button is clicked.
// Pending button -> voice file_id mappings survive a bot restart; no audio is stored.

var root = Directory.GetCurrentDirectory();
var configFile = Path.Combine(root, "settings.local.json");

if (!File.Exists(configFile))
{
    Console.Error.WriteLine("Нет settings.local.json. Скопируйте settings.example.json и укажите токен.");
    return;
}

var config = JsonSerializer.Deserialize<BotSettings>(
    await File.ReadAllTextAsync(configFile),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Не удалось прочитать настройки.");

if (string.IsNullOrWhiteSpace(config.BotToken) || config.BotToken == "PASTE_YOUR_BOT_TOKEN")
    throw new InvalidOperationException("Укажите BotToken в settings.local.json.");

string whisperExe = ResolvePath(config.WhisperExe);
string whisperModel = ResolvePath(config.WhisperModel);
string ffmpegExe = ResolvePath(config.FfmpegExe);

if (!File.Exists(whisperExe))
    throw new FileNotFoundException("Не найден whisper-cli.exe. Запустите setup-windows.ps1.", whisperExe);
if (!File.Exists(whisperModel))
    throw new FileNotFoundException("Не найдена модель Whisper. Запустите setup-windows.ps1.", whisperModel);

var dataDir = Path.Combine(root, "data");
Directory.CreateDirectory(dataDir);
var pendingFile = Path.Combine(dataDir, "pending-voices.json");

Dictionary<string, VoiceJob> pending = File.Exists(pendingFile)
    ? JsonSerializer.Deserialize<Dictionary<string, VoiceJob>>(File.ReadAllText(pendingFile)) ?? new()
    : new();

using var cts = new CancellationTokenSource();
using var whisperGate = new SemaphoreSlim(1, 1);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var bot = new TelegramBotClient(config.BotToken, cancellationToken: cts.Token);
var identity = await bot.GetMe();

bot.OnMessage += OnMessage;
bot.OnUpdate += OnUpdate; // callback button presses arrive here, not in OnMessage.
Console.WriteLine($"Бот @{identity.Username} запущен. Ctrl+C — остановить.");
Console.WriteLine("После голосового появится кнопка «Показать транскрипцию».");

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { /* normal shutdown */ }

async Task OnMessage(Message message, UpdateType updateType)
{
    if (updateType != UpdateType.Message) return;

    if (message.Text is { } command &&
        (command.StartsWith("/start", StringComparison.OrdinalIgnoreCase) ||
         command.StartsWith("/help", StringComparison.OrdinalIgnoreCase)))
    {
        await SafeSend(message, "🎙 Отправьте голосовое сообщение. Под ним появится кнопка «📝 Показать транскрипцию». " +
            "Нажмите кнопку, чтобы получить текст. В группах нужно отключить Privacy Mode у бота.");
        return;
    }

    if (message.Voice is not { } voice) return;

    if (voice.Duration > config.MaxVoiceSeconds)
    {
        await SafeSend(message, $"⚠️ Пока поддерживаются голосовые до {config.MaxVoiceSeconds} секунд.");
        return;
    }

    if (voice.FileSize is > 20_000_000)
    {
        await SafeSend(message, "⚠️ Голосовое больше 20 МБ — Telegram Bot API не даёт его скачать.");
        return;
    }

    try
    {
        // A bot cannot attach a button to another user's voice message.
        // Instead it sends a short reply with the inline button.
        var sent = await bot.SendMessage(
            message.Chat,
            "🎙 Голосовое сообщение\nНажмите кнопку, чтобы увидеть его расшифровку:",
            replyParameters: message.Id,
            replyMarkup: ShowButton(),
            messageThreadId: message.MessageThreadId,
            cancellationToken: cts.Token);

        SavePending(new VoiceJob
        {
            ChatId = message.Chat.Id,
            ButtonMessageId = sent.Id,
            OriginalMessageId = message.Id,
            MessageThreadId = message.MessageThreadId,
            FileId = voice.FileId
        });
        Console.WriteLine($"Кнопка chat={message.Chat.Id}, voice={message.Id}, button={sent.Id}");
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Ошибка создания кнопки chat={message.Chat.Id}, msg={message.Id}: {ex}");
        await SafeSend(message, "❌ Не удалось создать кнопку. Проверьте журнал бота.");
    }
}

async Task OnUpdate(Update update)
{
    if (update.CallbackQuery is not { Data: "show_transcript" } query) return;
    if (query.Message is not { } clickedMessage)
    {
        await bot.AnswerCallbackQuery(query.Id, "Сообщение больше недоступно.", cancellationToken: cts.Token);
        return;
    }

    long chatId = clickedMessage.Chat.Id;
    int buttonMessageId = clickedMessage.Id;
    var key = Key(chatId, buttonMessageId);

    // Important: Telegram needs an answer to the callback promptly, BEFORE Whisper starts.
    bool exists;
    lock (pending) exists = pending.ContainsKey(key);
    await bot.AnswerCallbackQuery(query.Id,
        exists ? "Начинаю распознавание…" : "Эта кнопка уже обработана или устарела.",
        showAlert: !exists,
        cancellationToken: cts.Token);
    if (!exists) return;

    await whisperGate.WaitAsync(cts.Token);
    try
    {
        VoiceJob? job;
        lock (pending) pending.TryGetValue(key, out job);
        if (job is null)
        {
            // Already processed, or data was removed.
            return;
        }

        await bot.EditMessageText(chatId, buttonMessageId,
            "⏳ Распознаю голосовое… Это может занять немного времени.",
            replyMarkup: ShowButton(), // still clickable if the bot restarts mid-transcription
            cancellationToken: cts.Token);

        string transcript = await TranscribeVoice(job.FileId, cts.Token);
        if (string.IsNullOrWhiteSpace(transcript))
            transcript = "Речь не распознана. Попробуйте записать сообщение чётче.";

        var chunks = SplitText("📝 Расшифровка:\n\n" + transcript, 3500).ToArray();

        // Editing our own button message removes the keyboard and reveals the transcription.
        await bot.EditMessageText(chatId, buttonMessageId, chunks[0], cancellationToken: cts.Token);

        for (int i = 1; i < chunks.Length; i++)
        {
            await bot.SendMessage(chatId, chunks[i],
                replyParameters: job.OriginalMessageId,
                messageThreadId: job.MessageThreadId,
                cancellationToken: cts.Token);
        }

        RemovePending(key);
        Console.WriteLine($"OK chat={chatId}, voice={job.OriginalMessageId}, chars={transcript.Length}");
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Ошибка расшифровки chat={chatId}, button={buttonMessageId}: {ex}");
        try
        {
            await bot.EditMessageText(chatId, buttonMessageId,
                "❌ Не удалось распознать голосовое. Проверьте PowerShell и попробуйте ещё раз.",
                replyMarkup: ShowButton(),
                cancellationToken: cts.Token);
        }
        catch (Exception editEx)
        {
            Console.Error.WriteLine($"Не удалось вернуть кнопку: {editEx.Message}");
        }
    }
    finally { whisperGate.Release(); }
}

static InlineKeyboardMarkup ShowButton() => new(new[]
{
    new[] { InlineKeyboardButton.WithCallbackData("📝 Показать транскрипцию", "show_transcript") }
});

static string Key(long chatId, int botMessageId) => $"{chatId}:{botMessageId}";

void SavePending(VoiceJob job)
{
    lock (pending)
    {
        pending[Key(job.ChatId, job.ButtonMessageId)] = job;
        PersistPending();
    }
}

void RemovePending(string key)
{
    lock (pending)
    {
        pending.Remove(key);
        PersistPending();
    }
}

// Called only under lock(pending); atomic replace avoids half-written JSON files.
void PersistPending()
{
    string tmpPath = pendingFile + ".tmp";
    File.WriteAllText(tmpPath, JsonSerializer.Serialize(pending));
    File.Move(tmpPath, pendingFile, overwrite: true);
}

async Task<string> TranscribeVoice(string fileId, CancellationToken ct)
{
    string tempDir = Path.Combine(root, "tmp", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);

    try
    {
        string oggPath = Path.Combine(tempDir, "voice.ogg");
        string wavPath = Path.Combine(tempDir, "voice.wav");
        string resultBase = Path.Combine(tempDir, "transcript");

        var fileInfo = await bot.GetFile(fileId, cancellationToken: ct);
        await using (var fileStream = File.Create(oggPath))
            await bot.DownloadFile(fileInfo, fileStream, cancellationToken: ct);

        await RunProcess(ffmpegExe, [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", oggPath, "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wavPath
        ], ct);

        await RunProcess(whisperExe, [
            "-m", whisperModel, "-f", wavPath, "-l", config.Language,
            "-otxt", "-of", resultBase, "-nt"
        ], ct);

        string resultFile = resultBase + ".txt";
        if (!File.Exists(resultFile))
            throw new IOException("Whisper завершился, но файл расшифровки не найден.");

        return (await File.ReadAllTextAsync(resultFile, Encoding.UTF8, ct)).Trim();
    }
    finally
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch (Exception ex) { Console.Error.WriteLine($"Не удалось удалить временные файлы: {ex.Message}"); }
    }
}

async Task RunProcess(string executable, IReadOnlyList<string> args, CancellationToken ct)
{
    var info = new ProcessStartInfo
    {
        FileName = executable,
        WorkingDirectory = Path.IsPathRooted(executable) ? Path.GetDirectoryName(executable)! : root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var arg in args) info.ArgumentList.Add(arg);

    using var proc = new Process { StartInfo = info };
    if (!proc.Start()) throw new IOException($"Не удалось запустить {executable}");

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromMinutes(10));
    Task<string> outTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
    Task<string> errTask = proc.StandardError.ReadToEndAsync(timeout.Token);

    try { await proc.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException)
    {
        if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        throw new TimeoutException($"Процесс {Path.GetFileName(executable)} остановлен или превысил лимит 10 минут.");
    }

    string output = await outTask;
    string error = await errTask;
    if (proc.ExitCode != 0)
        throw new IOException($"{Path.GetFileName(executable)} вернул код {proc.ExitCode}:\n{Trim(error + "\n" + output, 2000)}");
}

async Task SafeSend(Message original, string text)
{
    try
    {
        await bot.SendMessage(original.Chat, text,
            replyParameters: original.Id,
            messageThreadId: original.MessageThreadId,
            cancellationToken: cts.Token);
    }
    catch (Exception e) { Console.Error.WriteLine($"Не удалось ответить в чат: {e.Message}"); }
}

string ResolvePath(string value)
{
    if (Path.IsPathRooted(value)) return value;
    if (value.Contains('/') || value.Contains('\\') || value.StartsWith('.'))
        return Path.GetFullPath(Path.Combine(root, value));
    return value;
}

static string Trim(string text, int length) => text.Length <= length ? text : text[..length];

static IEnumerable<string> SplitText(string text, int chunkSize)
{
    for (int start = 0; start < text.Length; start += chunkSize)
        yield return text.Substring(start, Math.Min(chunkSize, text.Length - start));
}

sealed class VoiceJob
{
    public long ChatId { get; init; }
    public int ButtonMessageId { get; init; }
    public int OriginalMessageId { get; init; }
    public int? MessageThreadId { get; init; }
    public string FileId { get; init; } = "";
}

sealed class BotSettings
{
    public string BotToken { get; init; } = "";
    public string WhisperExe { get; init; } = "tools/whisper/Release/whisper-cli.exe";
    public string WhisperModel { get; init; } = "tools/models/ggml-base.bin";
    public string FfmpegExe { get; init; } = "ffmpeg";
    public string Language { get; init; } = "auto";
    public int MaxVoiceSeconds { get; init; } = 180;
}
