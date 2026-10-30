using System.Diagnostics;

namespace EzForensics.Core.Persistence;

/// <summary>
/// Fills the Autoruns Description / Company / Version columns from the PE version resource of the image file found inside the
/// evidence. The file is only inspected (<see cref="FileVersionInfo"/> reads the resource section, nothing is loaded or run);
/// entries whose image is not in the evidence keep the columns empty.
/// </summary>
public static class PeVersionInfo
{
    public static void Enrich(IEnumerable<PersistenceEntry> entries, string systemRoot)
    {
        var cache = new Dictionary<string, (string Desc, string Company, string Version)?>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.ImagePath)) continue;
            var file = ResolveFile(e.ImagePath, systemRoot, e.User);
            if (file == null) continue;
            if (!cache.TryGetValue(file, out var info)) cache[file] = info = Read(file);
            if (info is not { } i) continue;
            if (e.Description.Length == 0) e.Description = i.Desc;
            if (e.Company.Length == 0) e.Company = i.Company;
            if (e.Version.Length == 0) e.Version = i.Version;
        }
    }

    /// <summary>Maps an image path as written in the evidence (variables, drive letters, bare names) to an existing file under the system root.</summary>
    public static string? ResolveFile(string imagePath, string systemRoot, string? user = null)
    {
        try
        {
            var p = imagePath.Trim().Trim('"');
            if (p.Length == 0) return null;
            var resolved = OfflinePath.Resolve(p, systemRoot, user);
            if (resolved != null) return File.Exists(resolved) ? resolved : null;
            if (p.IndexOfAny(new[] { '\\', '/', '%', ':' }) < 0)
            {
                // Bare name: the loader looks in System32, then the Windows folder, then SysWOW64.
                foreach (var dir in new[] { Path.Combine("Windows", "System32"), "Windows", Path.Combine("Windows", "SysWOW64") })
                {
                    var f = Path.Combine(systemRoot, dir, p);
                    if (File.Exists(f)) return f;
                    if (Path.GetExtension(p).Length == 0 && File.Exists(f + ".dll")) return f + ".dll";
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static (string, string, string)? Read(string file)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(file);
            var desc = (v.FileDescription ?? "").Trim();
            var company = (v.CompanyName ?? "").Trim();
            var version = (v.FileVersion ?? "").Trim();
            if (desc.Length == 0 && company.Length == 0 && version.Length == 0) return null;
            return (desc, company, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or ArgumentException) { return null; }
    }
}
