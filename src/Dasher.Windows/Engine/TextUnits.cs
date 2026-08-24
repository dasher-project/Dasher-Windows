namespace Dasher.Windows.Engine;

public static class TextUnits
{
    /// <summary>
    /// Counts Unicode code points in a UTF-16 string. Surrogate pairs count
    /// as one code point — keyboard-mode deletes must inject one backspace
    /// per character, never per UTF-16 unit or byte (RFC 0015 §5).
    /// </summary>
    public static int CountCodePoints(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLowSurrogate(text[i])) continue;
            count++;
        }
        return count;
    }
}
