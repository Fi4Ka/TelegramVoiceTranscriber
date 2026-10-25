namespace TelegramVoiceTranscriber;

public static class TextChunks
{
    public static IEnumerable<string> Split(string text, int chunkSize)
    {
        if (chunkSize < 2) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        for (int start = 0; start < text.Length;)
        {
            int length = Math.Min(chunkSize, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])
                && char.IsLowSurrogate(text[start + length])) length--;
            yield return text.Substring(start, length);
            start += length;
        }
    }
}
