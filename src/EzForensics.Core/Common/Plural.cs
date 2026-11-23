namespace EzForensics.Core.Common;

/// <summary>"1 document", "2 documents": count + noun with the right number.</summary>
public static class Plural
{
    /// <summary>"{n:N0} {noun}" with an "s" added unless n is 1; pass <paramref name="plural"/> for irregular nouns.</summary>
    public static string Of(long n, string noun, string? plural = null) =>
        $"{n:N0} {(n == 1 ? noun : plural ?? noun + "s")}";
}
