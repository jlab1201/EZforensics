using System.Globalization;
using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Rita;

/// <summary>
/// Install / update / uninstall of RITA inside the private EZ Forensics WSL distro (<see cref="EzDistro"/>), as root, no
/// Windows admin rights:
/// <list type="bullet">
/// <item>program files in <c>/opt/ezhub/rita/{bin,etc}</c>: <c>bin/rita</c> (official binary from the pinned image, fetched on
/// the Windows side by <see cref="OciBlobFetcher"/> and SHA-256 verified again inside the distro), <c>bin/clickhouse</c> (the
/// 24.1.6 static build, downloaded inside the distro and SHA-512 verified), <c>etc/</c> (our ClickHouse config + users, RITA's
/// <c>.env</c>, <c>config.hjson</c>, <c>http_extensions_list.csv</c>, an empty <c>threat_intel_feeds/</c>);</item>
/// <item>databases in <c>/var/lib/ezhub/clickhouse</c> (ext4 inside the distro's disk).</item>
/// </list>
/// ClickHouse never runs as a service: each analysis starts it in the background and stops it (<see cref="RitaCommandBuilder"/>).
/// </summary>
public static class RitaSetup
{
    // Legacy name (EZ Hub), kept on purpose: /opt/ezhub and /var/lib/ezhub hold existing installs and databases inside the distro.
    public const string Root = "/opt/ezhub/rita";
    public const string BinDir = Root + "/bin";
    public const string EtcDir = Root + "/etc";
    public const string RitaBin = BinDir + "/rita";
    public const string ClickHouseBin = BinDir + "/clickhouse";
    public const string ClickHouseConfig = EtcDir + "/clickhouse-config.xml";
    public const string ClickHouseUsers = EtcDir + "/clickhouse-users.xml";
    public const string RitaConfig = EtcDir + "/config.hjson";
    /// <summary>Random ClickHouse password (root-only, 0600), generated at install / update when missing. Never logged.</summary>
    public const string PasswordFile = EtcDir + "/clickhouse.pass";
    /// <summary><c>clickhouse client</c> config (user + password, 0600): the client gets the password from it, never from its command line.</summary>
    public const string ClientConfig = EtcDir + "/clickhouse-client.xml";
    /// <summary>Placeholders the install script replaces (sed) with the password / its SHA-256 after writing the files.</summary>
    public const string PasswordPlaceholder = "@@EZHUB_CH_PASSWORD@@", PasswordShaPlaceholder = "@@EZHUB_CH_SHA256@@";
    public const string MarkerPath = EtcDir + "/installed";
    public const string DataDir = "/var/lib/ezhub/clickhouse";
    /// <summary>ClickHouse data on WSL1 (<see cref="ClickHouseConfigXmlFor"/>), apart from <see cref="DataDir"/> whose default-disk tables WSL1 cannot write.</summary>
    public const string DataDirWsl1 = "/var/lib/ezhub/clickhouse-wsl1";
    /// <summary>Storage disk of the WSL1 ClickHouse config.</summary>
    public const string Wsl1DiskName = "ezblob";
    public const string WorkRoot = "/var/lib/ezhub/rita";
    public const string LockFile = WorkRoot + "/rita.lock";
    /// <summary>Database name of the analysis holding <see cref="LockFile"/> (the cancel command stops rita / ClickHouse only for it).</summary>
    public const string OwnerFile = WorkRoot + "/rita.owner";
    public const int TcpPort = 19000, HttpPort = 18123;
    /// <summary>Bump when the files written by <see cref="InstallScript"/> change in a way existing installs must get.</summary>
    public const int SchemaVersion = 2;

    public const string ToolIdInstall = "rita-install", ToolIdUninstall = "rita-uninstall", ToolIdDeleteDatabases = "rita-delete-databases";

    public const string DockerNotSupportedMessage =
        "RITA runs in the EZ Forensics WSL environment. A Docker engine (two containers: rita + clickhouse/clickhouse-server:24.1.6 on a private " +
        "network) is not supported yet; enable WSL and use the EZ Forensics distro.";

    /// <summary>Marker content: "rita 5.1.2 / clickhouse 24.1.6.52 / schema 2".</summary>
    public static string MarkerText => $"rita {RitaRelease.Version} / clickhouse {RitaRelease.ClickHouseVersion} / schema {SchemaVersion}";

    /// <summary>Windows folder that holds the fetched rita binary until it is copied into the distro (<c>&lt;DataRoot&gt;\cache\rita\5.1.2</c>).</summary>
    public static string DownloadDir => Path.Combine(AppPaths.CacheDir, "rita", RitaRelease.Version);

    // ------------------------------------------------------------------ configuration files (pure)

    /// <summary>
    /// Stand-alone ClickHouse server config: 127.0.0.1 only on our own ports (never clashes with another ClickHouse), half the
    /// distro's RAM at most, small caches, no system log tables (query / part / metric / trace logs), UTC.
    /// </summary>
    public static string ClickHouseConfigXml => ClickHouseConfigXmlFor(wsl1: false);

    /// <summary>
    /// The server config; <paramref name="wsl1"/> = the variant the pipeline writes for a WSL1 distro. WSL1 cannot rename a directory
    /// while a file inside it is open (EACCES, also on tmpfs), and ClickHouse's MergeTree renames each new part directory
    /// (<c>tmp_insert_*</c> → <c>all_*</c>) with files still open, so the first INSERT of <c>rita import</c> fails with "filesystem error:
    /// in rename: Permission denied" and rita exits 1. The WSL1 variant keeps its data in <see cref="DataDirWsl1"/> and makes the default
    /// storage policy a <c>local_blob_storage</c> disk, whose part directories only hold small metadata files that are closed before the
    /// rename (verified on WSL1 with RITA 5.1.2 / ClickHouse 24.1).
    /// </summary>
    public static string ClickHouseConfigXmlFor(bool wsl1)
    {
        var data = wsl1 ? DataDirWsl1 : DataDir;
        var storage = !wsl1 ? "" : $"""

                <storage_configuration>
                    <disks>
                        <{Wsl1DiskName}>
                            <type>local_blob_storage</type>
                            <path>{DataDirWsl1}/blob/</path>
                        </{Wsl1DiskName}>
                    </disks>
                    <policies>
                        <default>
                            <volumes>
                                <main><disk>{Wsl1DiskName}</disk></main>
                            </volumes>
                        </default>
                    </policies>
                </storage_configuration>
            """;
        return EzDistro.Lf($"""
        <!-- Written by EZ Forensics (RITA). Overwritten on install / update. -->
        <clickhouse>
            <logger>
                <level>warning</level>
                <console>1</console>
            </logger>
            <listen_host>127.0.0.1</listen_host>
            <tcp_port>{TcpPort}</tcp_port>
            <http_port>{HttpPort}</http_port>
            <path>{data}/</path>
            <tmp_path>{data}/tmp/</tmp_path>
            <user_files_path>{data}/user_files/</user_files_path>
            <format_schema_path>{data}/format_schemas/</format_schema_path>
            <user_directories>
                <users_xml>
                    <path>{ClickHouseUsers}</path>
                </users_xml>
            </user_directories>{storage}
            <timezone>UTC</timezone>
            <max_server_memory_usage_to_ram_ratio>0.5</max_server_memory_usage_to_ram_ratio>
            <mark_cache_size>268435456</mark_cache_size>
            <index_mark_cache_size>67108864</index_mark_cache_size>
            <uncompressed_cache_size>134217728</uncompressed_cache_size>
            <mmap_cache_size>256</mmap_cache_size>
            <compiled_expression_cache_size>67108864</compiled_expression_cache_size>
            <max_concurrent_queries>50</max_concurrent_queries>
            <mlock_executable>false</mlock_executable>
            <send_crash_reports><enabled>false</enabled></send_crash_reports>
        </clickhouse>
        """);
    }

    /// <summary>User <c>default</c>, password as SHA-256 (placeholder replaced at install), 127.0.0.1 only; RITA's profile settings (mutations_sync, optimize_aggregation_in_order), no query log.</summary>
    public static string ClickHouseUsersXml => EzDistro.Lf($"""
        <!-- Written by EZ Forensics (RITA). Overwritten on install / update. -->
        <clickhouse>
            <profiles>
                <default>
                    <mutations_sync>1</mutations_sync>
                    <optimize_aggregation_in_order>1</optimize_aggregation_in_order>
                    <log_queries>0</log_queries>
                </default>
            </profiles>
            <users>
                <default>
                    <password_sha256_hex>{PasswordShaPlaceholder}</password_sha256_hex>
                    <networks>
                        <ip>127.0.0.1</ip>
                        <ip>::1</ip>
                    </networks>
                    <profile>default</profile>
                    <quota>default</quota>
                    <access_management>1</access_management>
                </default>
            </users>
            <quotas>
                <default>
                    <interval>
                        <duration>3600</duration>
                        <queries>0</queries>
                        <errors>0</errors>
                        <result_rows>0</result_rows>
                        <read_rows>0</read_rows>
                        <execution_time>0</execution_time>
                    </interval>
                </default>
            </quotas>
        </clickhouse>
        """);

    /// <summary>RITA's <c>.env</c> (read from the working directory by <c>rita</c>): our ClickHouse, no syslog, info level.</summary>
    public static string RitaEnv => EzDistro.Lf($"""
        # Written by EZ Forensics. rita reads ./.env from its working directory ({EtcDir}).
        DB_ADDRESS=127.0.0.1:{TcpPort}
        CLICKHOUSE_USERNAME=default
        CLICKHOUSE_PASSWORD={PasswordPlaceholder}
        CONFIG_DIR={EtcDir}
        CONFIG_FILE={RitaConfig}
        LOGGING_ENABLED=false
        LOG_LEVEL=1
        SYSLOG_ADDRESS=127.0.0.1:514
        APP_LOGS={WorkRoot}/logs

        """);

    /// <summary>Config of <c>clickhouse client</c> (read with <c>--config-file</c>): user and password; placeholder replaced at install.</summary>
    public static string ClickHouseClientXml => EzDistro.Lf($"""
        <!-- Written by EZ Forensics (RITA). Contains the ClickHouse password: root-only. -->
        <clickhouse>
            <user>default</user>
            <password>{PasswordPlaceholder}</password>
        </clickhouse>
        """);

    /// <summary>
    /// RITA <c>config.hjson</c>: internal subnets from the settings, online feeds only when <see cref="RitaOptions.OnlineThreatIntel"/>,
    /// no update check; every other value is RITA's default (scoring and modifiers are filled in by rita from its defaults).
    /// </summary>
    public static string RitaConfigHjson(RitaOptions? options = null)
    {
        options ??= RitaOptions.Default;
        var subnets = RitaOptions.NormalizeSubnets(options.InternalSubnets);
        if (subnets.Count == 0) subnets = RitaOptions.DefaultInternalSubnets;
        static string Arr(IEnumerable<string> v) => string.Join(", ", v.Select(x => "\"" + x.Replace("\\", "").Replace("\"", "") + "\""));
        var feeds = options.OnlineThreatIntel ? RitaOptions.DefaultOnlineFeeds : Array.Empty<string>();
        return EzDistro.Lf($$"""
            // Written by EZ Forensics for RITA {{RitaRelease.Version}}. Regenerated for every analysis from Settings.
            {
                "update_check_enabled": false,
                "threat_intel": {
                    "online_feeds": [{{Arr(feeds)}}]
                },
                "filtering": {
                    "internal_subnets": [{{Arr(subnets)}}],
                    "always_included_subnets": [],
                    "always_included_domains": [],
                    "never_included_subnets": [],
                    "never_included_domains": [],
                    "filter_external_to_internal": true
                }
            }

            """);
    }

    // ------------------------------------------------------------------ scripts (pure)

    /// <summary>Prints EZRITA=&lt;marker&gt;, RITAVER=, CHVER=, DATABYTES= (exit 0; no EZRITA line = not installed).</summary>
    public static string ProbeScript => EzDistro.Lf($$"""
        if [ -f {{MarkerPath}} ] && [ -x {{RitaBin}} ] && [ -x {{ClickHouseBin}} ]; then
          echo "EZRITA=$(cat {{MarkerPath}})"
          echo "RITAVER=$(cd {{EtcDir}} && {{RitaBin}} --version 2>/dev/null | head -n 1)"
          echo "CHVER=$({{ClickHouseBin}} local --version 2>/dev/null | head -n 1)"
        fi
        if [ -d {{DataDir}} ] || [ -d {{DataDirWsl1}} ]; then echo "DATABYTES=$(du -scb {{DataDir}} {{DataDirWsl1}} 2>/dev/null | tail -n 1 | cut -f1)"; fi
        true
        """);

    /// <summary>
    /// Root install / update script. <paramref name="ritaSourceWslPath"/> = the verified binary on the Windows side (<c>/mnt/...</c>).
    /// Re-runnable: an already verified clickhouse binary of the pinned version is kept. Exit 0 = installed; output lines
    /// prefixed <c>EZFORENSICS:</c> describe each step.
    /// </summary>
    public static string InstallScript(string ritaSourceWslPath, RitaOptions? options = null, RitaImagePin? ritaPin = null, ClickHousePin? chPin = null)
    {
        ritaPin ??= RitaRelease.Pin;
        chPin ??= RitaRelease.ClickHouse;
        var q = WslRuntime.ShQuote(ritaSourceWslPath);
        var sb = new StringBuilder();
        sb.Append($$"""
            set -e
            echo "EZFORENSICS: installing RITA {{RitaRelease.Version}} + ClickHouse {{chPin.Version}} into {{Root}}"
            for c in curl tar sha256sum sha512sum flock pkill; do command -v $c >/dev/null 2>&1 || { echo "EZFORENSICS: $c is missing in this distro" >&2; exit 3; }; done
            pkill -f '[/]opt/ezhub/rita/bin/clickhouse server' 2>/dev/null || true
            mkdir -p {{BinDir}} {{EtcDir}}/threat_intel_feeds {{DataDir}} {{WorkRoot}}
            chmod 0700 {{EtcDir}}
            # ClickHouse (static build, SHA-512 pinned)
            CHOK=0
            if [ -x {{ClickHouseBin}} ] && grep -q 'clickhouse {{chPin.Version}}' {{MarkerPath}} 2>/dev/null && {{ClickHouseBin}} local --version 2>/dev/null | grep -q '{{chPin.Version}}'; then CHOK=1; echo "EZFORENSICS: ClickHouse {{chPin.Version}} already installed"; fi
            if [ $CHOK = 0 ]; then
              T=/var/tmp/ezforensics-{{chPin.FileName}}
              echo "EZFORENSICS: downloading {{chPin.Url}} ({{(chPin.Bytes / 1_000_000).ToString(CultureInfo.InvariantCulture)}} MB)"
              curl -fsSL --proto '=https' --proto-redir '=https' --retry 3 --retry-delay 5 -o "$T" '{{chPin.Url}}'
              echo "{{chPin.Sha512}}  $T" | sha512sum -c - || { rm -f "$T"; echo "EZFORENSICS: SHA-512 mismatch for {{chPin.FileName}}" >&2; exit 4; }
              echo "EZFORENSICS: SHA-512 verified; extracting usr/bin/clickhouse"
              tar -xzf "$T" -O --wildcards '*/usr/bin/clickhouse' > {{ClickHouseBin}}.new
              rm -f "$T"
              chmod 0755 {{ClickHouseBin}}.new
              mv -f {{ClickHouseBin}}.new {{ClickHouseBin}}
            fi
            # rita (official image binary, verified on Windows and again here)
            echo "{{ritaPin.BinarySha256}}  "{{q}} | sha256sum -c - >/dev/null || { echo "EZFORENSICS: the rita binary does not match its pinned SHA-256" >&2; exit 5; }
            cp -f {{q}} {{RitaBin}}.new
            echo "{{ritaPin.BinarySha256}}  {{RitaBin}}.new" | sha256sum -c - >/dev/null
            chmod 0755 {{RitaBin}}.new
            mv -f {{RitaBin}}.new {{RitaBin}}
            echo "EZFORENSICS: rita binary SHA-256 verified ({{ritaPin.BinarySha256}})"
            # RITA data files from its source tree at {{RitaRelease.Tag}}
            if ! echo "{{RitaRelease.HttpExtensionsSha256}}  {{EtcDir}}/http_extensions_list.csv" | sha256sum -c - >/dev/null 2>&1; then
              curl -fsSL --proto '=https' --proto-redir '=https' --retry 3 -o {{EtcDir}}/http_extensions_list.csv.new '{{RitaRelease.HttpExtensionsUrl}}'
              echo "{{RitaRelease.HttpExtensionsSha256}}  {{EtcDir}}/http_extensions_list.csv.new" | sha256sum -c - >/dev/null || { echo "EZFORENSICS: http_extensions_list.csv does not match its pinned SHA-256" >&2; exit 6; }
              mv -f {{EtcDir}}/http_extensions_list.csv.new {{EtcDir}}/http_extensions_list.csv
            fi

            """);
        // ClickHouse password: generated once (kept on update), only ever written into root-only 0600 files; no xtrace, no echo.
        sb.Append($$"""
            umask 077
            if [ ! -s {{PasswordFile}} ]; then
              tr -dc 'A-Za-z0-9' < /dev/urandom | head -c 40 > {{PasswordFile}}.new || true
              [ "$(wc -c < {{PasswordFile}}.new)" -ge 32 ] || { rm -f {{PasswordFile}}.new; echo "EZFORENSICS: could not generate the ClickHouse password" >&2; exit 7; }
              mv -f {{PasswordFile}}.new {{PasswordFile}}
              echo "EZFORENSICS: generated the ClickHouse password (stored root-only)"
            fi
            chmod 0600 {{PasswordFile}}
            CHPW=$(cat {{PasswordFile}})
            CHSHA=$(printf %s "$CHPW" | sha256sum | cut -d' ' -f1)

            """);
        AppendFile(sb, ClickHouseConfig, ClickHouseConfigXml);
        AppendFile(sb, ClickHouseUsers, ClickHouseUsersXml, secret: true);
        AppendFile(sb, EtcDir + "/.env", RitaEnv, secret: true);
        AppendFile(sb, ClientConfig, ClickHouseClientXml, secret: true);
        sb.Append("umask 022\n");
        AppendFile(sb, RitaConfig, RitaConfigHjson(options));
        sb.Append($$"""
            echo "{{MarkerText}}" > {{MarkerPath}}
            echo "EZFORENSICS: rita $(cd {{EtcDir}} && {{RitaBin}} --version 2>/dev/null | head -n 1)"
            echo "EZFORENSICS: $({{ClickHouseBin}} local --version 2>/dev/null | head -n 1)"
            echo "EZFORENSICS: installed ($(du -sh {{Root}} | cut -f1) program files)"

            """);
        return EzDistro.Lf(sb.ToString());
    }

    /// <summary>Stops ClickHouse and removes the program files and every RITA database.</summary>
    public static string UninstallScript => EzDistro.Lf($$"""
        mkdir -p {{WorkRoot}}
        exec 9>{{LockFile}}
        flock -w 30 9 || { echo "EZFORENSICS: a RITA analysis is running; try again when it has finished" >&2; exit 75; }
        pkill -f '[/]opt/ezhub/rita/bin/rita' 2>/dev/null || true
        pkill -f '[/]opt/ezhub/rita/bin/clickhouse server' 2>/dev/null || true
        sleep 1
        rm -rf {{Root}} {{DataDir}} {{DataDirWsl1}} {{WorkRoot}}
        rmdir /opt/ezhub /var/lib/ezhub 2>/dev/null || true
        echo "EZFORENSICS: RITA removed"
        """);

    /// <summary>Stops ClickHouse and deletes every database (the program files stay).</summary>
    public static string DeleteDatabasesScript => EzDistro.Lf($$"""
        mkdir -p {{WorkRoot}}
        exec 9>{{LockFile}}
        flock -w 30 9 || { echo "EZFORENSICS: a RITA analysis is running; try again when it has finished" >&2; exit 75; }
        pkill -f '[/]opt/ezhub/rita/bin/clickhouse server' 2>/dev/null || true
        sleep 1
        B=$(du -scb {{DataDir}} {{DataDirWsl1}} 2>/dev/null | tail -n 1 | cut -f1)
        rm -rf {{DataDir}} {{DataDirWsl1}} {{WorkRoot}}/run
        mkdir -p {{DataDir}}
        echo "EZFORENSICS: deleted the RITA databases (${B:-0} bytes)"
        """);

    private static void AppendFile(StringBuilder sb, string path, string content, bool secret = false)
    {
        const string eof = "EZFORENSICS_EOF_7f3c";
        sb.Append($"cat > {path} <<'{eof}'\n").Append(content.TrimEnd('\n')).Append($"\n{eof}\n");
        if (secret)
            sb.Append($"chmod 0600 {path}\nsed -i -e \"s/{PasswordPlaceholder}/$CHPW/g\" -e \"s/{PasswordShaPlaceholder}/$CHSHA/g\" {path}\n");
    }

    // ------------------------------------------------------------------ steps (JobQueue jobs)

    public static RuntimeStep InstallStep(string wslExe, string ritaSourceWslPath, RitaOptions? options = null) =>
        new(ToolIdInstall, wslExe, EzDistro.ScriptArgs(InstallScript(ritaSourceWslPath, options)),
            $"Install RITA {RitaRelease.Version} + ClickHouse {RitaRelease.ClickHouseVersion} in the {EzDistro.Name} WSL distro", TimeSpan.FromMinutes(60), WslRuntime.Utf8Env);

    public static RuntimeStep UninstallStep(string wslExe) =>
        new(ToolIdUninstall, wslExe, EzDistro.ScriptArgs(UninstallScript), $"Remove RITA from the {EzDistro.Name} WSL distro", TimeSpan.FromMinutes(10), WslRuntime.Utf8Env);

    public static RuntimeStep DeleteDatabasesStep(string wslExe) =>
        new(ToolIdDeleteDatabases, wslExe, EzDistro.ScriptArgs(DeleteDatabasesScript), "Delete the RITA databases", TimeSpan.FromMinutes(10), WslRuntime.Utf8Env);

    /// <summary>
    /// Install or update: needs the EZ Forensics distro ready (<see cref="EzDistro.EnsureAsync"/> is the caller's step). Fetches the
    /// pinned rita binary (Windows side, verified), runs <see cref="InstallStep"/> through <paramref name="runStep"/> (a JobQueue job
    /// in the app), then deletes the Windows copy. Never touches another distro.
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(HttpClient http, Func<RuntimeStep, CancellationToken, Task<Job>> runStep,
        Action<string> log, RitaOptions? options = null, IProgress<double>? progress = null, IRuntimeProbe? probe = null, CancellationToken ct = default)
    {
        probe ??= SystemRuntimeProbe.Instance;
        var info = await Task.Run(() => EzDistro.Inspect(probe), ct).ConfigureAwait(true);
        if (info.WslExe == null || !info.WslAvailable) return (false, "WSL is not enabled. Run setup (Network) to enable it, then install RITA.");
        if (!info.Ready) return (false, $"The {EzDistro.Name} WSL distro is not set up yet. Install it first (Tools → Zeek / Network setup).");
        string rita;
        try
        {
            rita = await OciBlobFetcher.FetchRitaAsync(http, RitaRelease.Pin, DownloadDir, new Progress<double>(p => progress?.Report(p * 0.2)), log, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (false, "Downloading RITA from ghcr.io failed: " + ex.Message);
        }
        var wslPath = WslRuntime.ToWslPath(rita);
        if (wslPath == null) return (false, $"The download folder {DownloadDir} is not visible inside WSL (it must be on a local drive).");
        progress?.Report(0.25);
        var job = await runStep(InstallStep(info.WslExe, wslPath, options), ct).ConfigureAwait(true);
        progress?.Report(1);
        if (job.State != JobState.Succeeded) return (false, $"Installing RITA failed: {job.StatusReason}. Check the internet connection (github.com is needed for ClickHouse), then retry.");
        try { File.Delete(rita); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var after = await Task.Run(() => RitaLocator.Discover(probe), ct).ConfigureAwait(true);
        return after.Installed ? (true, $"RITA {after.RitaVersion} is installed (ClickHouse {after.ClickHouseVersion}).")
            : (false, "RITA was installed but does not answer. See the job log.");
    }

    public static async Task<(bool Ok, string Message)> UninstallAsync(Func<RuntimeStep, CancellationToken, Task<Job>> runStep, IRuntimeProbe? probe = null, CancellationToken ct = default)
    {
        probe ??= SystemRuntimeProbe.Instance;
        var info = await Task.Run(() => EzDistro.Inspect(probe, checkProvisioned: false), ct).ConfigureAwait(true);
        if (info.WslExe == null || !info.Exists) return (true, "RITA is not installed.");
        var job = await runStep(UninstallStep(info.WslExe), ct).ConfigureAwait(true);
        return job.State == JobState.Succeeded ? (true, "RITA and its databases were removed.") : (false, $"Removing RITA failed: {job.StatusReason}.");
    }

    public static async Task<(bool Ok, string Message)> DeleteDatabasesAsync(Func<RuntimeStep, CancellationToken, Task<Job>> runStep, IRuntimeProbe? probe = null, CancellationToken ct = default)
    {
        probe ??= SystemRuntimeProbe.Instance;
        var info = await Task.Run(() => EzDistro.Inspect(probe, checkProvisioned: false), ct).ConfigureAwait(true);
        if (info.WslExe == null || !info.Exists) return (true, "There are no RITA databases.");
        var job = await runStep(DeleteDatabasesStep(info.WslExe), ct).ConfigureAwait(true);
        return job.State == JobState.Succeeded ? (true, "The RITA databases were deleted.") : (false, $"Deleting the RITA databases failed: {job.StatusReason}.");
    }
}
