namespace TelegramVoiceTranscriber;

public static class RuntimePaths
{
    public static string ResolveExecutable(string value, string root)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Не указан путь к исполняемому файлу.");

        if (Path.IsPathRooted(value) || value.Contains('/') || value.Contains('\\'))
            return RequireFile(Path.GetFullPath(value, root));

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : Array.Empty<string>();
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), value);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            foreach (var extension in extensions)
                if (File.Exists(candidate + extension)) return Path.GetFullPath(candidate + extension);
        }
        throw new FileNotFoundException($"Не найден {value}. Укажите полный путь или добавьте программу в PATH.");
    }

    private static string RequireFile(string path) => File.Exists(path)
        ? path : throw new FileNotFoundException("Не найден исполняемый файл.", path);
}
