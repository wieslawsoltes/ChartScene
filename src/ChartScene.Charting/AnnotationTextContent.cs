namespace ChartScene.Charting;

/// <summary>
/// CR, LF and CRLF are equivalent annotation line separators. Other characters,
/// blank lines and trailing separators are preserved. The model itself never
/// rewrites supplied text; editors use these helpers at their input boundary.
/// </summary>
public static class AnnotationTextContent
{
    /// <summary>Returns canonical LF text, reusing an already canonical input.</summary>
    public static string NormalizeLineEndings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOf('\r') < 0) return text;
        int length = text.Length;
        for (int i = 0; i + 1 < text.Length; i++)
            if (text[i] == '\r' && text[i + 1] == '\n') { length--; i++; }
        return string.Create(length, text, static (output, input) =>
        {
            int written = 0;
            for (int i = 0; i < input.Length; i++)
            {
                char value = input[i];
                if (value == '\r')
                {
                    value = '\n';
                    if (i + 1 < input.Length && input[i + 1] == '\n') i++;
                }
                output[written++] = value;
            }
        });
    }

    /// <summary>Compares visible text without allocating normalized copies.</summary>
    public static bool Equivalent(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (ReferenceEquals(first, second)) return true;
        int a = 0, b = 0;
        while (a < first.Length && b < second.Length)
            if (Read(first, ref a) != Read(second, ref b)) return false;
        return a == first.Length && b == second.Length;
    }
    private static char Read(string text, ref int index)
    {
        char value = text[index++];
        if (value != '\r') return value;
        if (index < text.Length && text[index] == '\n') index++;
        return '\n';
    }
}
