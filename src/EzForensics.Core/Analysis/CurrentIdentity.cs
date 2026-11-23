namespace EzForensics.Core.Analysis;

/// <summary>
/// Who is working: the name written to <see cref="HistoryEntry.Who"/>. Defaults to the Windows user; the app sets <see cref="Provider"/>
/// to <c>Options.ExaminerName</c>. This is the single place a later account system plugs into.
/// </summary>
public static class CurrentIdentity
{
    private static Func<string?> _provider = () => Environment.UserName;

    /// <summary>Returns the examiner name; null or blank falls back to the Windows user.</summary>
    public static Func<string?> Provider
    {
        get => _provider;
        set => _provider = value ?? (() => Environment.UserName);
    }

    public static string Name
    {
        get
        {
            string? n = null;
            try { n = _provider(); } catch { }
            return string.IsNullOrWhiteSpace(n) ? Environment.UserName : n.Trim();
        }
    }
}
