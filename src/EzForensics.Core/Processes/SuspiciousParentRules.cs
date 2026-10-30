namespace EzForensics.Core.Processes;

/// <summary>
/// Parent/child rules behind <see cref="ProcessMarkers.SuspiciousParent"/>. Names are lower case image names.
/// A rule fires when the parent is in <see cref="Rule.Parents"/> and the child in <see cref="Rule.Children"/> (empty = any child),
/// or, for rules with <see cref="Rule.RequiredParents"/>, when the child is named in <see cref="Rule.Children"/> and its parent is NOT one of the required ones.
/// </summary>
public static class SuspiciousParentRules
{
    public sealed record Rule(string Why, string[] Parents, string[] Children, string[] RequiredParents)
    {
        public bool Matches(string parent, string child)
        {
            if (RequiredParents.Length > 0)
                return Children.Contains(child) && !RequiredParents.Contains(parent);
            return Parents.Contains(parent) && (Children.Length == 0 || Children.Contains(child));
        }
    }

    private static readonly string[] None = Array.Empty<string>();
    private static readonly string[] Office = { "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe" };
    private static readonly string[] Shells = { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe" };

    /// <summary>Office → shell / script host and "lsass starts no children" stay fixed rules; the "should be started by" rules come from the <c>parents</c> of roles.json.</summary>
    public static readonly IReadOnlyList<Rule> Rules = Build();

    private static IReadOnlyList<Rule> Build()
    {
        var rules = new List<Rule> { new("Office application started a shell or script host", Office, Shells, None) };
        foreach (var r in ProcessRoles.Images.Values.Where(i => i.Parents.Count > 0))
            rules.Add(new Rule($"{r.Name} should be started by {string.Join(" or ", r.Parents)}", None, new[] { r.Name }, r.Parents.ToArray()));
        rules.Add(new Rule("lsass.exe does not normally start child processes", new[] { "lsass.exe" }, None, None));
        return rules;
    }

    /// <summary>The first rule matching this parent/child pair (names in any case), or null.</summary>
    public static Rule? Find(string parentName, string childName)
    {
        var p = (parentName ?? "").Trim().ToLowerInvariant();
        var c = (childName ?? "").Trim().ToLowerInvariant();
        foreach (var r in Rules) if (r.Matches(p, c)) return r;
        return null;
    }
}
