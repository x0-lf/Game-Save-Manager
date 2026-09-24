namespace GameSaves.App.Common;

/// <summary>The one human-readable byte size format every page shows.</summary>
internal static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        double kb = bytes / 1024.0;

        if (kb < 1024)
            return $"{kb:0.##} KB";

        double mb = kb / 1024.0;

        if (mb < 1024)
            return $"{mb:0.##} MB";

        double gb = mb / 1024.0;

        return $"{gb:0.##} GB";
    }
}
