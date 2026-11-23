using System.Text.Json;

namespace EzForensics.Core.Knowledge;

/// <summary>Finds the JSON array in an AI reply (which may carry prose around it).</summary>
internal static class JsonArrayText
{
    /// <summary>The first <c>[</c>…<c>]</c> as a parsed document (caller disposes), with a balanced-bracket fallback; null when none parses.</summary>
    public static JsonDocument? Extract(string? reply)
    {
        if (string.IsNullOrEmpty(reply)) return null;
        var a = ArrayStart(reply); var b = reply.LastIndexOf(']');
        if (a < 0 || b <= a) return null;
        try { return JsonDocument.Parse(reply[a..(b + 1)]); }
        catch (JsonException)
        {
            // prose with brackets after the array: take the first balanced array instead
            var end = BalancedEnd(reply, a);
            if (end < 0) return null;
            try { return JsonDocument.Parse(reply[a..(end + 1)]); } catch (JsonException) { return null; }
        }
    }

    /// <summary>The first <c>[</c> whose next non-space char is <c>{</c> or <c>]</c> (skips prose markers like "[0]").</summary>
    private static int ArrayStart(string s)
    {
        for (var i = s.IndexOf('['); i >= 0; i = s.IndexOf('[', i + 1))
        {
            var j = i + 1;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j < s.Length && (s[j] == '{' || s[j] == ']')) return i;
        }
        return -1;
    }

    private static int BalancedEnd(string s, int start)
    {
        int depth = 0; var inStr = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return i;
        }
        return -1;
    }
}
