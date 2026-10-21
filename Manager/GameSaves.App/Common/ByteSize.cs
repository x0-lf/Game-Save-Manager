using System;

namespace GameSaves.App.Common;

/// <summary>
/// The one human-readable byte size format every page shows. Invariant, like
/// the rest of the English UI: a Windows regional format with a comma decimal
/// would otherwise show "1,5 KB" beside "1,000 files".
/// </summary>
internal static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        double kb = bytes / 1024.0;

        if (kb < 1024)
            return FormattableString.Invariant($"{kb:0.##} KB");

        double mb = kb / 1024.0;

        if (mb < 1024)
            return FormattableString.Invariant($"{mb:0.##} MB");

        double gb = mb / 1024.0;

        return FormattableString.Invariant($"{gb:0.##} GB");
    }
}
