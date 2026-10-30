using System.Runtime.InteropServices;

namespace EzForensics.Core.Rita;

/// <summary>
/// The rita binary of one architecture inside the official image <c>ghcr.io/activecm/rita:v5.1.2</c>: the per-platform manifest
/// (pinned by digest), the layer that holds <c>/rita</c> (pinned by digest and size) and the binary itself (SHA-256 and size).
/// The binary is extracted from that layer without Docker (<see cref="OciBlobFetcher"/>); EZ Forensics never hosts it.
/// </summary>
public sealed record RitaImagePin(string Architecture, string ManifestDigest, string LayerDigest, long LayerBytes,
    string BinaryPath, string BinarySha256, long BinaryBytes);

/// <summary>The ClickHouse static build of one architecture (GitHub release tarball, SHA-512 from its <c>.sha512</c> asset).</summary>
public sealed record ClickHousePin(string Architecture, string Version, string Url, string Sha512, long Bytes)
{
    public string FileName => Path.GetFileName(new Uri(Url).AbsolutePath);
}

/// <summary>
/// Version pins for RITA (resolved 2026-10-11 from the registry API and the GitHub releases) and its links. RITA is GPL-3.0
/// (Active Countermeasures); ClickHouse is Apache-2.0.
/// </summary>
public static class RitaRelease
{
    public const string Version = "5.1.2";
    public const string Tag = "v5.1.2";
    public const string Registry = "ghcr.io";
    public const string Repository = "activecm/rita";
    public const string Image = Registry + "/" + Repository;
    /// <summary>Digest of the multi-platform image index of <c>ghcr.io/activecm/rita:v5.1.2</c>.</summary>
    public const string IndexDigest = "sha256:a2bb0ef6185e33780dbc3ce7d86e38ac7a65c98e729e17510fe29717eb34b76a";
    public const string ImageReference = Image + "@" + IndexDigest;

    public static readonly RitaImagePin Amd64 = new("amd64",
        "sha256:c53629801508b4a0903e278993c40dc0a99a73fa3a4abe0504027b3d1d348b9b",
        "sha256:f3f8820ca2890f6d86a21f5ea04db7c5013ed0fbd0e0fb846d1394cb54825286", 14_575_853,
        "rita", "d82d51a9fdf5ec9ee0f329caa383fb4611c3d61d36619783b51b315bdb5d4c21", 29_631_327);

    public static readonly RitaImagePin Arm64 = new("arm64",
        "sha256:30a82a74ed8097b3f1eb02436b4cd2f941fdaf6c0c7e83758d8f852ef5a9b9a5",
        "sha256:b919e7a2fbb36da0c6575712b00e23b46d95253981f004be5690f184f580f5fa", 13_665_993,
        "rita", "4025ba99f73c80f6ca58a88a28ee1e8404a158635b25f032fc3fa27aad1912bb", 28_264_262);

    public static RitaImagePin Pin => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? Arm64 : Amd64;

    /// <summary>RITA v5 requires ClickHouse 24.1.6 (its .env: CLICKHOUSE_VERSION=24.1.6).</summary>
    public const string ClickHouseVersion = "24.1.6.52";

    public static readonly ClickHousePin ClickHouseAmd64 = new("amd64", ClickHouseVersion,
        "https://github.com/ClickHouse/ClickHouse/releases/download/v24.1.6.52-stable/clickhouse-common-static-24.1.6.52-amd64.tgz",
        "25b0302ca4eb4ce1fbeda225b5d569ca1cb67856448edcfa84528299c758be19515e31bf65005babb4f3ff2b52d9f30e68b9b9f323e4e7c61f0c47c6de2dc89c",
        260_679_829);

    public static readonly ClickHousePin ClickHouseArm64 = new("arm64", ClickHouseVersion,
        "https://github.com/ClickHouse/ClickHouse/releases/download/v24.1.6.52-stable/clickhouse-common-static-24.1.6.52-arm64.tgz",
        "65f2683e81bd04a1a3429911eab09ec69e2312ae6a76c7aa28f8e10ec714719c6b9ea029143880706e50441aaa6be6a65e961c9dd71be5d450047303bfc922b9",
        245_156_995);

    public static ClickHousePin ClickHouse => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? ClickHouseArm64 : ClickHouseAmd64;

    /// <summary>RITA's MIME type / URI extension list (required by <c>rita</c> at start-up; from the source tree at the same tag).</summary>
    public const string HttpExtensionsUrl = "https://raw.githubusercontent.com/activecm/rita/v5.1.2/deployment/http_extensions_list.csv";
    public const string HttpExtensionsSha256 = "4e2d16f3f0f54967f392797ce75eaa14d992c836016c282e5f9a2b727637a940";

    /// <summary>
    /// Measured on the EZ Forensics distro (amd64): ≈14 MB image layer + ≈250 MB ClickHouse tarball downloaded; ≈30 MB rita +
    /// ≈480 MB clickhouse binary on disk, plus the databases (grow with the analysed logs).
    /// </summary>
    public const int DownloadMB = 265, DiskMB = 520;

    public const string License = "GPL-3.0";
    public const string LicenseNotice =
        "RITA (Real Intelligence Threat Analytics) is © Active Countermeasures and licensed under the GNU General Public License v3.0. " +
        "EZ Forensics does not ship or modify RITA: it downloads the unmodified official binary from ghcr.io/activecm/rita (pinned, SHA-256 verified) " +
        "into its own WSL environment. The complete source code is available from the RITA project.";
}

/// <summary>Official RITA / ClickHouse links.</summary>
public static class RitaLinks
{
    public const string Website = "https://www.activecountermeasures.com/free-tools/rita/";
    public const string Source = "https://github.com/activecm/rita";
    public const string SourceAtTag = "https://github.com/activecm/rita/tree/v5.1.2";
    public const string License = "https://github.com/activecm/rita/blob/v5.1.2/LICENSE";
    public const string Releases = "https://github.com/activecm/rita/releases";
    public const string ImagePackage = "https://github.com/activecm/rita/pkgs/container/rita";
    public const string ClickHouse = "https://clickhouse.com/";
    public const string ClickHouseLicense = "https://github.com/ClickHouse/ClickHouse/blob/master/LICENSE";
}
