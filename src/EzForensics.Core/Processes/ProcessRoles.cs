using System.Text.Json;

namespace EzForensics.Core.Processes;

/// <summary>How a <see cref="ProcessNode.Role"/> was assigned.</summary>
public enum RoleBasis
{
    /// <summary>No role: unknown image name, or a known name running from a directory that is not an expected one.</summary>
    None,
    /// <summary>The image name is known and its stated path is one of the expected directories.</summary>
    NamePath,
    /// <summary>The image name is known; there is no stated path to check (or the image has no fixed location).</summary>
    NameOnly,
}

/// <summary>One entry of <c>Resources/processes/roles.json</c>.</summary>
public sealed record ImageRole(string Name, string Role, IReadOnlyList<string> Dirs, IReadOnlyList<string> Parents, bool Singleton, bool Session0);

/// <summary>Role of a process from <c>roles.json</c> (embedded): the image name plus, when stated, its path. Facts only, nothing is guessed.</summary>
public static class ProcessRoles
{
    public const string OtherRole = "Other";

    private static readonly Lazy<IReadOnlyDictionary<string, ImageRole>> Embedded = new(LoadEmbedded);

    public static IReadOnlyDictionary<string, ImageRole> Images => Embedded.Value;

    private sealed class RolesFile { public Dictionary<string, Entry> Images { get; set; } = new(); }
    private sealed class Entry
    {
        public string Role { get; set; } = OtherRole;
        public List<string>? Dirs { get; set; }
        public List<string>? Parents { get; set; }
        public bool Singleton { get; set; }
        public bool Session0 { get; set; }
    }

    private static IReadOnlyDictionary<string, ImageRole> LoadEmbedded()
    {
        var asm = typeof(ProcessRoles).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("processes.roles.json", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        return Load(s);
    }

    public static IReadOnlyDictionary<string, ImageRole> Load(Stream json)
    {
        var file = JsonSerializer.Deserialize<RolesFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   ?? throw new InvalidDataException("roles.json is empty");
        var d = new Dictionary<string, ImageRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, e) in file.Images ?? new())
        {
            if (e == null) continue;   // "name": null
            d[name] = new ImageRole(name.ToLowerInvariant(), string.IsNullOrWhiteSpace(e.Role) ? OtherRole : e.Role,
                (e.Dirs ?? new()).Where(x => x != null).Select(x => x.ToLowerInvariant()).ToList(),
                (e.Parents ?? new()).Where(x => x != null).Select(x => x.ToLowerInvariant()).ToList(), e.Singleton, e.Session0);
        }
        return d;
    }

    /// <summary>Lower case image name of a node: its Name, else the file name of its path.</summary>
    public static string ImageName(ProcessNode n)
    {
        var name = n.Name.Trim();
        if (name.Length == 0) name = ProcessLogText.FileNameOf(ProcessLogText.EffectivePath(n));
        return name.ToLowerInvariant();
    }

    public static ImageRole? Find(ProcessNode n) => n.IsGhost ? null : Images.GetValueOrDefault(ImageName(n));

    /// <summary>Directory part of a <see cref="ProcessLogText.PathKey"/> (with the trailing backslash); empty when the path has no directory.</summary>
    public static string DirOf(string pathKey)
    {
        int i = pathKey.LastIndexOf('\\');
        return i >= 0 ? pathKey[..(i + 1)] : "";
    }

    /// <summary>True when the known image runs from a stated path outside its expected directories (the <see cref="IndicatorKind.UnexpectedLocation"/> case).</summary>
    public static bool IsUnexpectedLocation(ProcessNode n, ImageRole role, out string actualPath)
    {
        actualPath = ProcessLogText.EffectivePath(n);
        if (role.Dirs.Count == 0) return false;
        var key = ProcessLogText.PathKey(actualPath);
        return key.Length > 0 && !role.Dirs.Contains(DirOf(key), StringComparer.Ordinal);
    }

    /// <summary>Suffix of <see cref="RoleLabel"/> when the roles.json <c>parents</c> rule is broken.</summary>
    public const string UnexpectedParentSuffix = " · unexpected parent";

    /// <summary>
    /// The known image has a roles.json <c>parents</c> rule and its captured parent is not one of them (svchost.exe started by explorer.exe). No parent, a ghost
    /// parent (not captured) or a parent without a name: nothing is known, so false.
    /// </summary>
    public static bool HasUnexpectedParent(ProcessNode n) => UnexpectedParentRole(n) != null;

    /// <summary>The image's roles.json entry when <see cref="HasUnexpectedParent"/>, else null.</summary>
    public static ImageRole? UnexpectedParentRole(ProcessNode n)
    {
        if (n.IsGhost || n.Parent is not { IsGhost: false } p) return null;
        var role = Find(n);
        if (role == null || role.Parents.Count == 0) return null;
        var parent = ImageName(p);
        if (parent.Length == 0) return null;
        return role.Parents.Contains(parent, StringComparer.Ordinal) ? null : role;
    }

    /// <summary>The role shown on the card and the node: <see cref="ProcessNode.Role"/> (Other when empty) plus <see cref="UnexpectedParentSuffix"/> when the parent rule is broken; empty for a ghost.</summary>
    public static string RoleLabel(ProcessNode n)
    {
        if (n.IsGhost) return "";
        var role = string.IsNullOrEmpty(n.Role) ? OtherRole : n.Role;
        return HasUnexpectedParent(n) ? role + UnexpectedParentSuffix : role;
    }

    /// <summary>
    /// Known name + path in an expected directory: the role, <see cref="RoleBasis.NamePath"/>. Known name + no stated path: the role, <see cref="RoleBasis.NameOnly"/>.
    /// Known name running elsewhere, or an unknown name: <see cref="OtherRole"/>, <see cref="RoleBasis.None"/>.
    /// </summary>
    public static (string Role, RoleBasis Basis) Classify(ProcessNode node)
    {
        var role = Find(node);
        if (role == null) return (OtherRole, RoleBasis.None);
        if (role.Dirs.Count == 0) return (role.Role, RoleBasis.NameOnly);
        var key = ProcessLogText.PathKey(ProcessLogText.EffectivePath(node));
        if (key.Length == 0) return (role.Role, RoleBasis.NameOnly);
        return role.Dirs.Contains(DirOf(key), StringComparer.Ordinal) ? (role.Role, RoleBasis.NamePath) : (OtherRole, RoleBasis.None);
    }
}
