using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Rita;

/// <summary>How to start one RITA analysis.</summary>
/// <param name="CancelCommand">argv run when the job is cancelled / times out (kills the pipeline script, rita and ClickHouse in the distro).</param>
public sealed record RitaInvocation(string Exe, IReadOnlyList<string> Args, IReadOnlyList<string>? CancelCommand, IReadOnlyDictionary<string, string>? Environment);

/// <summary>
/// The one-call pipeline of an analysis (<c>wsl -d EzForensics -u root -e sh -c &lt;script&gt;</c>, LF only):
/// <list type="number">
/// <item><c>flock</c> on <see cref="RitaSetup.LockFile"/> (one analysis at a time: they share one ClickHouse);</item>
/// <item>the Zeek logs listed in <see cref="InputsFile"/> are linked under their original names, one folder per capture;</item>
/// <item><c>clickhouse server</c> starts in the background with our config (127.0.0.1:19000; on a WSL1 distro the blob-storage
/// variant of <see cref="RitaSetup.ClickHouseConfigXmlFor"/>), a <c>trap</c> stops it;</item>
/// <item>ping until ready; <c>rita import --database --logs --rebuild</c> (bounded by the <c>#timeout</c> of the inputs file); on
/// failure RITA's log tail goes to stderr and its last FTL / ERR line ends the "rita import failed" line (the job's reason);</item>
/// <item><c>rita view --stdout</c> → <see cref="ViewFile"/>;</item>
/// <item>enrichment (first / last seen and captures per src/dst, per src/fqdn, per DNS domain; data span) with <c>clickhouse client</c>,
/// FORMAT CSVWithNames, into the work files read by <see cref="RitaResultConverter"/>.</item>
/// </list>
/// Every work file ends in <c>.tmp</c> (never published) and lives in the job's output (staging) folder.
/// </summary>
public static class RitaCommandBuilder
{
    public const string InputsFile = "rita_inputs.tmp", ViewFile = "rita_view.csv.tmp", PairsFile = "rita_pairs.csv.tmp",
        SniFile = "rita_sni.csv.tmp", DnsFile = "rita_dns.csv.tmp", SpanFile = "rita_span.csv.tmp", InfoFile = "rita_info.tmp";

    public static readonly IReadOnlyList<string> WorkFiles = new[] { InputsFile, ViewFile, PairsFile, SniFile, DnsFile, SpanFile, InfoFile };

    /// <summary>Marker in the pipeline script's command line (the cancel command kills the script by it).</summary>
    public const string ScriptMarker = "EZFORENSICS-RITA-PIPELINE";

    /// <summary>The result limit passed to <c>rita view --limit</c> (its default is 100).</summary>
    public const int ViewLimit = 1_000_000;

    /// <summary>Logs RITA reads (it ignores every other file).</summary>
    public static readonly IReadOnlyList<string> RitaLogNames = new[] { "conn", "open_conn", "dns", "http", "open_http", "ssl", "open_ssl" };

    private static readonly Regex DbNameRx = new("^[a-z][A-Za-z0-9_]+[A-Za-z0-9]$", RegexOptions.CultureInvariant);
    private static readonly Regex OriginalNameRx = new(@"^(?<n>conn|open_conn|dns|http|open_http|ssl|open_ssl)(\.[^\\/]*)?\.log$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex V2NameRx = new(@"^Zeek_(?<n>Conn|OpenConn|Dns|Http|OpenHttp|Ssl|OpenSsl)(_[A-Za-z0-9]+)*\.log$", RegexOptions.CultureInvariant);

    /// <summary>True when RITA accepts <paramref name="name"/> as a database name (its own rules: ≤ 63, no hyphen, not reserved).</summary>
    public static bool IsValidDatabaseName(string? name) =>
        name != null && name.Length <= 63 && DbNameRx.IsMatch(name) && name is not ("default" or "system" or "information_schema" or "metadatabase");

    /// <summary>
    /// <c>ez_&lt;case&gt;_&lt;host|case&gt;_&lt;hash6&gt;</c>: lower-case alphanumeric parts joined by '_', at most 63 characters; the hash
    /// (of the case path and host) keeps two cases with the same name apart.
    /// </summary>
    public static string DatabaseName(string caseDir, string? host)
    {
        var full = Path.GetFullPath(caseDir).TrimEnd('\\', '/');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant() + "|" + (host ?? "<case>").ToUpperInvariant())))[..6].ToLowerInvariant();
        var c = Part(Path.GetFileName(full), 22);
        var h = host == null ? "case" : Part(host, 22);
        var name = string.Join("_", new[] { "ez", c, h, hash }.Where(p => p.Length > 0));
        return name.Length <= 63 ? name : name[..56].TrimEnd('_') + "_" + hash;
    }

    private static string Part(string? value, int max)
    {
        var sb = new StringBuilder();
        foreach (var ch in (value ?? "").ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        return s.Length > max ? s[..max].TrimEnd('_') : s;
    }

    /// <summary>
    /// The original Zeek log name RITA expects for a log file: <c>conn.log</c> stays, layout v2 <c>Zeek_Conn_cap.log</c> → <c>conn.log</c>,
    /// <c>Zeek_OpenConn_cap.log</c> → <c>open_conn.log</c>. Null for logs RITA does not read (and for CSV files).
    /// </summary>
    public static string? OriginalLogName(string path)
    {
        var name = Path.GetFileName(path);
        var m = OriginalNameRx.Match(name);
        if (m.Success) return m.Groups["n"].Value.ToLowerInvariant() + ".log";
        m = V2NameRx.Match(name);
        if (!m.Success) return null;
        return m.Groups["n"].Value switch
        {
            "OpenConn" => "open_conn.log", "OpenHttp" => "open_http.log", "OpenSsl" => "open_ssl.log",
            var v => v.ToLowerInvariant() + ".log",
        };
    }

    /// <summary>
    /// One folder per capture (<c>c01</c>, <c>c02</c>, ...: plain names, so RITA's hour / date parsing of paths never misreads a
    /// capture name), the logs under their original names; logs RITA does not read are dropped, a second log of the same type in one
    /// capture is skipped.
    /// </summary>
    public static IReadOnlyList<RitaStagedLog> Stage(IEnumerable<RitaCaptureLogs> captures)
    {
        var list = new List<RitaStagedLog>();
        int n = 0;
        foreach (var c in captures)
        {
            var folder = "c" + (n + 1).ToString("00", CultureInfo.InvariantCulture);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var logs = new List<RitaStagedLog>();
            foreach (var f in c.LogFiles)
            {
                var orig = OriginalLogName(f);
                if (orig == null || !seen.Add(orig)) continue;
                logs.Add(new RitaStagedLog(folder, orig, c.Capture, f));
            }
            if (logs.Count == 0) continue;
            list.AddRange(logs);
            n++;
        }
        return list;
    }

    /// <summary>
    /// Inputs file: <c>#timeout &lt;seconds&gt;</c>, then one line per log, tab-separated: folder, original name, capture, path as
    /// the engine sees it (<paramref name="toEnginePath"/>: <c>/mnt/...</c> for WSL). LF line ends.
    /// </summary>
    public static string InputsText(IEnumerable<RitaStagedLog> logs, Func<string, string?> toEnginePath, TimeSpan importTimeout)
    {
        var sb = new StringBuilder();
        sb.Append("#timeout ").Append(((long)Math.Max(60, importTimeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var l in logs)
        {
            var p = toEnginePath(l.SourcePath) ?? throw new ArgumentException($"{l.SourcePath} is not visible inside WSL (UNC or relative path). Copy the case to a local drive.");
            sb.Append(l.Folder).Append('\t').Append(l.Name).Append('\t').Append(Clean(l.Capture)).Append('\t').Append(p).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// A field of the tab-separated inputs file: no tab / line break / backslash (awk -v and TSV escapes), and never empty: tab is an IFS
    /// white-space character for the script's <c>read</c>, so an empty field would collapse and shift the path into the wrong variable.
    /// </summary>
    private static string Clean(string s)
    {
        var c = new string((s ?? "").Select(ch => ch is '\t' or '\r' or '\n' or '\\' || char.IsControl(ch) ? '_' : ch).ToArray());
        return c.Trim().Length == 0 ? "_" : c;
    }

    /// <summary>Parses <see cref="InputsText"/> (fake tool, tests).</summary>
    public static (TimeSpan? Timeout, IReadOnlyList<RitaStagedLog> Logs) ParseInputs(string text)
    {
        TimeSpan? timeout = null;
        var logs = new List<RitaStagedLog>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("#timeout ", StringComparison.Ordinal) && long.TryParse(line[9..], NumberStyles.None, CultureInfo.InvariantCulture, out var s))
                timeout = TimeSpan.FromSeconds(s);
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split('\t');
            if (p.Length >= 4) logs.Add(new RitaStagedLog(p[0], p[1], p[2], string.Join("\t", p.Skip(3))));
        }
        return (timeout, logs);
    }

    // ------------------------------------------------------------------ enrichment SQL (RITA v5.1.2 tables, verified against its source)

    private const string Ip = "replaceRegexpOne(IPv6NumToString({0}), '^::ffff:', '')";
    private const string CaptureJoin = "LEFT JOIN (SELECT MD5(uid) AS h, capture FROM ezforensics_uid_capture) AS c ON t.zeek_uid = c.h";
    private const string CaptureList = "arrayStringConcat(arraySort(groupUniqArrayIf(c.capture, c.capture != '')), '|') AS captures";

    /// <summary>First / last seen and captures per (src, dst) of the findings (conn, http, ssl and their open_* tables; zeek_uid = MD5(uid)).</summary>
    public static string PairsSql => $"""
        SELECT {string.Format(CultureInfo.InvariantCulture, Ip, "t.src")} AS src, {string.Format(CultureInfo.InvariantCulture, Ip, "t.dst")} AS dst,
          toUnixTimestamp(min(t.ts)) AS first_ts, toUnixTimestamp(max(t.ts)) AS last_ts, {CaptureList}
        FROM (
          SELECT src, dst, ts, zeek_uid FROM conn UNION ALL SELECT src, dst, ts, zeek_uid FROM openconn
          UNION ALL SELECT src, dst, ts, zeek_uid FROM http UNION ALL SELECT src, dst, ts, zeek_uid FROM openhttp
          UNION ALL SELECT src, dst, ts, zeek_uid FROM ssl UNION ALL SELECT src, dst, ts, zeek_uid FROM openssl
        ) AS t {CaptureJoin}
        WHERE (t.src, t.dst) IN (SELECT src, dst FROM threat_mixtape)
        GROUP BY t.src, t.dst
        FORMAT CSVWithNames
        """;

    /// <summary>Per (src, fqdn) of SNI / HTTP host findings (http.host, ssl.server_name).</summary>
    public static string SniSql => $"""
        SELECT {string.Format(CultureInfo.InvariantCulture, Ip, "t.src")} AS src, t.fqdn AS fqdn,
          toUnixTimestamp(min(t.ts)) AS first_ts, toUnixTimestamp(max(t.ts)) AS last_ts, {CaptureList}
        FROM (
          SELECT src, host AS fqdn, ts, zeek_uid FROM http UNION ALL SELECT src, host, ts, zeek_uid FROM openhttp
          UNION ALL SELECT src, server_name, ts, zeek_uid FROM ssl UNION ALL SELECT src, server_name, ts, zeek_uid FROM openssl
        ) AS t {CaptureJoin}
        WHERE t.fqdn != '' AND (t.src, t.fqdn) IN (SELECT src, fqdn FROM threat_mixtape WHERE fqdn != '')
        GROUP BY t.src, t.fqdn
        FORMAT CSVWithNames
        """;

    /// <summary>Per domain of DNS findings (C2 over DNS): queries for the domain or any sub-domain of it.</summary>
    public static string DnsSql => $"""
        SELECT t.d AS fqdn, toUnixTimestamp(min(t.ts)) AS first_ts, toUnixTimestamp(max(t.ts)) AS last_ts, {CaptureList}
        FROM (
          SELECT ts, zeek_uid, arrayJoin(arrayMap(i -> arrayStringConcat(arraySlice(splitByChar('.', query), i), '.'), range(1, length(splitByChar('.', query))))) AS d
          FROM dns WHERE query != ''
        ) AS t {CaptureJoin}
        WHERE t.d IN (SELECT fqdn FROM threat_mixtape WHERE fqdn != '')
        GROUP BY t.d
        FORMAT CSVWithNames
        """;

    /// <summary>First and last timestamp of the imported data per capture (the data span RITA scored; '' = records not linked to a capture).</summary>
    public static string SpanSql => $"""
        SELECT c.capture AS capture, toUnixTimestamp(min(t.ts)) AS first_ts, toUnixTimestamp(max(t.ts)) AS last_ts
        FROM (
          SELECT ts, zeek_uid FROM conn UNION ALL SELECT ts, zeek_uid FROM openconn UNION ALL SELECT ts, zeek_uid FROM dns
          UNION ALL SELECT ts, zeek_uid FROM http UNION ALL SELECT ts, zeek_uid FROM ssl
        ) AS t {CaptureJoin}
        GROUP BY c.capture
        FORMAT CSVWithNames
        """;

    // ------------------------------------------------------------------ script

    /// <summary>
    /// The pipeline script (pure). <paramref name="outDir"/> = the job's output (staging) folder as the distro sees it.
    /// Exit codes: 0 ok (also "no data": <see cref="InfoFile"/> says <c>nodata=1</c>), 2 no readable input, 70 ClickHouse did not
    /// start, 71 import failed, 72 view failed, 124 import timed out.
    /// </summary>
    public static string PipelineScript(string outDir, string database, RitaOptions? options = null)
    {
        if (!IsValidDatabaseName(database)) throw new ArgumentException("Invalid RITA database name: " + database, nameof(database));
        var o = WslRuntime.ShQuote(outDir);
        var extra = string.Concat((options?.ExtraImportArgs ?? Array.Empty<string>()).Select(a => " " + WslRuntime.ShQuote(a)));
        const string eof = "EZFORENSICS_EOF_7f3c";
        string Sql(string sql, string file) =>
            $"if ! \"$R/bin/clickhouse\" client --config-file {RitaSetup.ClientConfig} --port {RitaSetup.TcpPort} --database \"$DB\" > \"$OUT/{file}\" 2>> \"$RUN/enrich.err\" <<'{eof}'\n{sql}\n{eof}\nthen echo \"EZFORENSICS: enrichment query for {file} failed (first / last seen may be missing)\"; tail -n 3 \"$RUN/enrich.err\"; rm -f \"$OUT/{file}\"; fi\n";
        var sb = new StringBuilder();
        sb.Append($$"""
            # {{ScriptMarker}} {{database}}
            R={{RitaSetup.Root}}
            DB={{database}}
            OUT={{o}}
            RUN={{RitaSetup.WorkRoot}}/run/$DB
            TAB=$(printf '\t')
            [ -x "$R/bin/rita" ] && [ -x "$R/bin/clickhouse" ] || { echo "EZFORENSICS: RITA is not installed in this distro (Tools → RITA → Install)" >&2; exit 3; }
            [ -s {{RitaSetup.PasswordFile}} ] && [ -f {{RitaSetup.ClientConfig}} ] || { echo "EZFORENSICS: the RITA installation is outdated (Tools → RITA → Update)" >&2; exit 3; }
            mkdir -p {{RitaSetup.WorkRoot}} {{RitaSetup.DataDir}}
            exec 9>{{RitaSetup.LockFile}}
            if ! flock -n 9; then echo "EZFORENSICS: another RITA analysis is running; waiting for it to finish"; flock 9; fi
            CH=
            cleanup() {
              if [ -n "$CH" ]; then kill "$CH" 2>/dev/null; wait "$CH" 2>/dev/null; fi
              rm -rf "$RUN"
              rm -f {{RitaSetup.OwnerFile}}
            }
            trap cleanup EXIT
            trap 'exit 130' INT TERM HUP
            echo "$DB" > {{RitaSetup.OwnerFile}}
            pkill -f '[/]opt/ezhub/rita/bin/clickhouse server' 2>/dev/null && sleep 1
            rm -rf "$RUN"
            mkdir -p "$RUN/logs"
            rm -f "$OUT/{{ViewFile}}" "$OUT/{{PairsFile}}" "$OUT/{{SniFile}}" "$OUT/{{DnsFile}}" "$OUT/{{SpanFile}}" "$OUT/{{InfoFile}}"
            [ -f "$OUT/{{InputsFile}}" ] || { echo "EZFORENSICS: the input list $OUT/{{InputsFile}} is missing" >&2; exit 2; }
            TO=$(sed -n 's/^#timeout \([0-9][0-9]*\)$/\1/p' "$OUT/{{InputsFile}}" | head -n 1)
            [ -n "$TO" ] || TO=3600
            n=0
            while IFS="$TAB" read -r dir name cap src; do
              case "$dir" in ''|'#'*) continue ;; esac
              if [ ! -r "$src" ]; then echo "EZFORENSICS: cannot read $src (skipped)"; continue; fi
              mkdir -p "$RUN/logs/$dir"
              ln -sf "$src" "$RUN/logs/$dir/$name"
              n=$((n+1))
            done < "$OUT/{{InputsFile}}"
            [ "$n" -gt 0 ] || { echo "EZFORENSICS: none of the Zeek logs could be read" >&2; exit 2; }
            echo "EZFORENSICS: $n Zeek log(s) of $(ls "$RUN/logs" | wc -l) capture(s) staged for database $DB"
            cat > "$RUN/config.hjson" <<'{{eof}}'
            {{RitaSetup.RitaConfigHjson(options).TrimEnd('\n')}}
            {{eof}}
            CHCONF={{RitaSetup.ClickHouseConfig}}
            if grep -q Microsoft /proc/sys/kernel/osrelease 2>/dev/null; then
              CHCONF="$RUN/clickhouse-wsl1.xml"
              mkdir -p {{RitaSetup.DataDirWsl1}}
              cat > "$CHCONF" <<'{{eof}}'
            {{RitaSetup.ClickHouseConfigXmlFor(wsl1: true).TrimEnd('\n')}}
            {{eof}}
              echo "EZFORENSICS: WSL1 distro: ClickHouse keeps its data as blobs in {{RitaSetup.DataDirWsl1}} (WSL1 cannot rename folders with open files)"
            fi
            implog() { sed -e 's/\x1b\[[0-9;?]*[A-Za-z]//g' "$RUN/import.log" | tr '\r' '\n' | grep -v -e '░' -e '█' -e '^[[:space:]]*$'; }
            cd {{RitaSetup.EtcDir}}
            "$R/bin/clickhouse" server --config-file="$CHCONF" > "$RUN/clickhouse.log" 2>&1 &
            CH=$!
            i=0
            until "$R/bin/clickhouse" client --config-file {{RitaSetup.ClientConfig}} --port {{RitaSetup.TcpPort}} --query 'SELECT 1' >/dev/null 2>&1; do
              i=$((i+1))
              if [ "$i" -gt 240 ] || ! kill -0 "$CH" 2>/dev/null; then echo "EZFORENSICS: ClickHouse did not start" >&2; tail -n 20 "$RUN/clickhouse.log" >&2; exit 70; fi
              sleep 0.5
            done
            echo "EZFORENSICS: ClickHouse ready; rita import (timeout ${TO}s)"
            timeout "$TO" "$R/bin/rita" import --config "$RUN/config.hjson" --database "$DB" --logs "$RUN/logs" --rebuild{{extra}} > "$RUN/import.log" 2>&1
            rc=$?
            if [ "$rc" = 0 ]; then implog | tail -n 200; else implog | tail -n 40 >&2; fi
            if [ "$rc" = 124 ]; then echo "EZFORENSICS: rita import timed out after ${TO}s" >&2; exit 124; fi
            if [ "$rc" != 0 ]; then
              ERR=$(implog | grep -E ' (FTL|ERR|PNC) |^(Error|error|panic)' | tail -n 1 | sed -e 's/^[0-9][0-9TZ:.+-]* //' | cut -c 1-400)
              echo "EZFORENSICS: rita import failed (exit $rc)${ERR:+: $ERR}" >&2
              exit 71
            fi
            NODATA=0
            grep -q 'could not find imported data' "$RUN/import.log" && NODATA=1
            if ! "$R/bin/rita" view --config "$RUN/config.hjson" --stdout --limit {{ViewLimit}} "$DB" > "$OUT/{{ViewFile}}" 2> "$RUN/view.err"; then
              if [ "$NODATA" = 1 ] || grep -q -i 'not found' "$RUN/view.err"; then
                NODATA=1
                : > "$OUT/{{ViewFile}}"
                echo "EZFORENSICS: RITA found no data to analyse (no connections between the internal subnets and outside)"
              else
                sed -e 's/\x1b\[[0-9;?]*[A-Za-z]//g' "$RUN/view.err" | tail -n 20 >&2
                echo "EZFORENSICS: rita view failed" >&2
                exit 72
              fi
            fi
            if [ "$NODATA" = 0 ]; then
              "$R/bin/clickhouse" client --config-file {{RitaSetup.ClientConfig}} --port {{RitaSetup.TcpPort}} --database "$DB" --query 'CREATE TABLE IF NOT EXISTS ezforensics_uid_capture (uid String, capture String) ENGINE = Memory' 2>> "$RUN/enrich.err"
              while IFS="$TAB" read -r dir name cap src; do
                case "$name" in conn.log|open_conn.log) ;; *) continue ;; esac
                [ -r "$src" ] || continue
                awk -F"$TAB" -v cap="$cap" '/^#fields/ { for (i = 2; i <= NF; i++) if ($i == "uid") c = i - 1; next } /^#/ { next } c { print $c "\t" cap }' "$src" \
                  | "$R/bin/clickhouse" client --config-file {{RitaSetup.ClientConfig}} --port {{RitaSetup.TcpPort}} --database "$DB" --query 'INSERT INTO ezforensics_uid_capture FORMAT TSV' 2>> "$RUN/enrich.err"
              done < "$OUT/{{InputsFile}}"

            """);
        sb.Append(Sql(PairsSql, PairsFile));
        sb.Append(Sql(SniSql, SniFile));
        sb.Append(Sql(DnsSql, DnsFile));
        sb.Append(Sql(SpanSql, SpanFile));
        sb.Append($$"""
            fi
            {
              echo "rita.version=$("$R/bin/rita" --version 2>/dev/null | head -n 1)"
              echo "clickhouse.version=$("$R/bin/clickhouse" local --version 2>/dev/null | head -n 1)"
              echo "rita.db=$DB"
              echo "nodata=$NODATA"
            } > "$OUT/{{InfoFile}}"
            echo "EZFORENSICS: done ($(grep -c . "$OUT/{{ViewFile}}" 2>/dev/null || echo 0) line(s) of RITA results)"
            exit 0

            """);
        return EzDistro.Lf(sb.ToString());

    }

    /// <summary>
    /// The cancel command (WSL): kills the pipeline script of <paramref name="database"/>; rita and ClickHouse only when that script holds the
    /// analysis lock (<see cref="RitaSetup.OwnerFile"/> names its database): a script still waiting for the lock must not stop another analysis.
    /// </summary>
    public static IReadOnlyList<string> WslCancelArgs(string database)
    {
        if (!IsValidDatabaseName(database)) throw new ArgumentException("Invalid RITA database name: " + database, nameof(database));
        return EzDistro.ScriptArgs(
            $"pkill -f '[{ScriptMarker[0]}]{ScriptMarker[1..]} {database}'; " +
            $"if [ \"$(cat {RitaSetup.OwnerFile} 2>/dev/null)\" = '{database}' ]; then " +
            "pkill -f '[/]opt/ezhub/rita/bin/rita'; sleep 1; pkill -f '[/]opt/ezhub/rita/bin/clickhouse server'; fi; true");
    }

    /// <summary>
    /// Builds the process for <paramref name="install"/>. <paramref name="outputDir"/> is the job's (staging) folder on Windows: the
    /// inputs file is read from it and every work file written to it.
    /// </summary>
    public static RitaInvocation Build(RitaInstall install, string outputDir, string database, RitaOptions? options = null)
    {
        options ??= RitaOptions.Default;
        switch (install.Engine)
        {
            case RitaEngine.Wsl:
            {
                var wslOut = WslRuntime.ToWslPath(outputDir) ?? throw new ArgumentException($"{outputDir} is not visible inside WSL (UNC or relative path). Use a case folder on a local drive.");
                return new RitaInvocation(install.Exe, EzDistro.ScriptArgs(PipelineScript(wslOut, database, options)),
                    new[] { install.Exe }.Concat(WslCancelArgs(database)).ToList(), WslRuntime.Utf8Env);
            }
            case RitaEngine.Direct:
            {
                var args = new List<string> { "--fake-rita", "--out", outputDir, "--db", database, "--subnets", string.Join(",", RitaOptions.NormalizeSubnets(options.InternalSubnets)) };
                return new RitaInvocation(install.Exe, args, null, null);
            }
            default:
                throw new NotSupportedException(RitaSetup.DockerNotSupportedMessage);
        }
    }

    /// <summary>Path of a staged log as the engine sees it.</summary>
    public static Func<string, string?> EnginePath(RitaInstall install) => install.Engine == RitaEngine.Wsl ? p => WslRuntime.ToWslPath(p) : p => Path.GetFullPath(p);
}
