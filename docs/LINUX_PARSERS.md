# Linux artifact parsers: design

Status: design + abstraction (issue #24). Implemented so far: `Core/Platforms` (`OsFamily`, `OsFamilyDetector`, `EvidencePath`,
`IArtifactParser`, `ArtifactParserRegistry`, `ArtifactCsvWriter`, `ArtifactOutputLayout`) and one reference parser,
`Platforms/Linux/LinuxPasswdParser` (`etc/passwd` -> `<Host>\Accounts\Users.csv`). Nothing is wired into the Ingest page yet.

## 1. Goals

- Ingest Linux evidence (mounted images, extracted folders, UAC / CyLR / tar.gz collections) with the same flow as Windows:
  detect the root, scan, plan, run, browse CSVs, build the super timeline, hash everything into the run manifest.
- One-click setup (EZ Forensics's core purpose): native C# parsers need nothing installed; anything that needs Linux tools uses the
  private EZ Forensics WSL distro (Docker optional), never the analyst's own distro or host.
- Output that looks like EZ tool output: one CSV per artifact, UTC `DateTimeUtc` first, stable column names, predictable paths.
- Evidence safety: read-only, never follow a link out of the evidence, never read the analyst's own `/etc`.
- Everything in `EzForensics.Core` (net8.0, no Windows APIs) so the Avalonia desktop app and the CLI reuse it unchanged.

Non-goals (for now): Linux memory analysis (Linux memory images are detected and flagged; Volatility runs on Windows, see the README), live response on a running Linux host (EZ Forensics consumes the
output of UAC & co.), full file-system carving.

## 2. Platform detection

### 2.1 `OsFamilyDetector.Detect(root)`

Pure file checks, in this order (macOS before Linux, because macOS also has `etc/passwd`):

| Family | Decisive evidence | Extra fields |
|---|---|---|
| Windows | `Windows/System32/config` folder | hives present are listed |
| macOS | `System/Library/CoreServices/SystemVersion.plist` (`ProductVersion`, `ProductName` by string extraction; binary plists noted), else `System/Library/CoreServices` + `private/var` | host from `private/etc/hostname` |
| Linux | `etc/os-release`, then `usr/lib/os-release` (`ID`, `NAME`/`PRETTY_NAME`, `VERSION_ID`), then `etc/lsb-release`, `etc/redhat-release` (and `centos-`, `fedora-`, `system-release`), `etc/debian_version`; without any release file: `etc/passwd` **and** `var/log` | host from `etc/hostname` |

`OsDetection.Evidence` lists what the decision rests on (shown to the analyst and written to the manifest).
`FindSystemRoots(source, maxDepth = 3)` walks a source breadth-first and stops descending at the first root, so a
collection folder with several hosts yields one root per host. Links and junctions are not followed.

### 2.2 `EvidencePath` (path semantics shared by every parser)

- Segments match exactly first, then case-insensitively: works on NTFS copies, case-sensitive ext4 mounts and
  case-sensitive NTFS folders (`fsutil file setCaseSensitiveInfo`).
- Symbolic links resolve **inside the root** (chroot semantics): `etc/os-release -> ../usr/lib/os-release` and the absolute
  `-> /usr/lib/os-release` both land in the evidence; `..` never climbs above the root. On a Linux host with a mounted
  image this is what stops EZ Forensics from reading the analyst's own files. Dangling links and links out of the evidence
  (`.bash_history -> /dev/null`, a classic anti-forensics trick) come back as `EvidenceFile(FullPath: null, LinkTarget: ...)`
  so parsers can report them.
- Hidden / system entries are never skipped; `**` does not follow links (no loops).

### 2.3 Source types

| Source | What EZ Forensics sees | Notes |
|---|---|---|
| Mounted image, on Linux | the mount point is the root | Mount `ro,noload` (ext4: no journal replay). LVM / LUKS are the analyst's job. |
| Mounted image, on Windows | Windows cannot read ext4/XFS/btrfs. `wsl --mount --bare` + mount inside the EZ Forensics distro needs admin; Arsenal Image Mounter / FTK Imager expose the disk but not the file system. | Prefer plaso (dfVFS reads ext/XFS images directly) or extraction. See open questions. |
| Extracted folder | a folder with `etc`, `var`, `home` ... | Metadata loss on Windows: see section 7. |
| **UAC** (Unix-like Artifacts Collector) | `uac-<host>-<os>-<stamp>.tar.gz` holding `[root]/` (files), `live_response/` (command output: `ps`, `netstat`, `lsmod`, `last` ...), `bodyfile/bodyfile.txt`, `hash_executables/` | `FindSystemRoots` finds `[root]`; live_response and the bodyfile get their own parsers (P2 / P1). |
| **CyLR** | zip keeping absolute paths (`etc/...`, `var/log/...`) | the extraction folder is the root. |
| Velociraptor offline collector | zip with `uploads/auto/<absolute path>` (`file` / `ntfs` accessors) | root = `uploads/auto`; URL-encoded names (`%2F`) must be decoded. |
| LinuxCatScale and similar | `catscale_<host>_<stamp>.tar.gz`: mostly command output + selected files | treated like live_response. |
| KAPE against a mounted Linux volume | KAPE is Windows-only; a target run over a mounted volume gives a folder tree | treated as an extracted folder. |
| Plain `.tar` / `.tar.gz` / `.tgz` triage archive | see below | |

**Archives.** Read them with `System.Formats.Tar` (+ `GZipStream`) rather than an external extractor: tar keeps mtime,
mode, uid/gid and symlinks that a Windows extraction drops, and names that Windows cannot hold (`:`, `*`, `?`, trailing dots,
`aux`, case-only duplicates `Makefile`/`makefile`). Plan: extract once into a case-private `Evidence\<host>\root` folder,
record every entry (path, size, mode, uid, gid, mtime, link target, SHA-256) in `Evidence\<host>\TarIndex.csv`, and map
problem names (`%3A`-style escaping, recorded in the index). Parsers then read the folder; the index keeps the metadata.

## 3. Parser abstraction

```csharp
public interface IArtifactParser { string Id; OsFamily Platform; string Category; string OutputName /* <Artifact>[_<Qualifier>].csv */;
  IEnumerable<string> InputGlobs /* relative to the system root */; Task<ArtifactParseResult> ParseAsync(ArtifactParseContext, CancellationToken); }
public sealed class ArtifactParseContext { string SystemRoot; IReadOnlyList<EvidenceFile> Inputs; string OutputDir /* <case>\<Host>\<Category> */;
  string HostName; OsDetection? Os; IProgress<ArtifactParseProgress>? Progress; string OutputPath(string name); }
public sealed class ArtifactParseResult { List<string> OutputFiles; long Rows; List<string> Warnings; }
public sealed class ArtifactParserRegistry { static CreateDefault(); Register(p); Find(id); ForPlatform(os);
  Discover(root, os) -> ArtifactParserMatch(Parser, Inputs); Discover(root) -> (OsDetection, matches); static RunAsync(match, root, caseDir, os, host, progress, ct); }
```

- `Register` enforces the conventions: lower-case id (`linux-passwd`), known platform, PascalCase category, output name
  matching `^[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)*\.csv$`, relative globs without `..`, unique id, unique
  `(platform, category, output)`.
- Parsers are stateless and thread safe; they read only their inputs (plus other evidence files through `EvidencePath`,
  e.g. `etc/login.defs` for `UID_MIN`) and write only into `OutputDir`.
- `ArtifactCsvWriter`: UTF-8 without BOM, CRLF, RFC 4180 quoting (also for leading/trailing white space), temp file +
  rename, never overwrites (`Users (2).csv`, same rule as `OutputNaming.UniquePath`).
- Warnings are hints for the analyst (UID 0 account other than root, password hash in `passwd`, history linked to
  `/dev/null`), never failures. A parser throws only for real failures (I/O on the output, cancellation).
- Additional outputs of one parser (details files) use the same naming: `Users.csv` + `Users_Shadow.csv`.

## 4. Output layout and naming

`<case>\<Host>\<Category>\<Artifact>[_<Qualifier>].csv` with `Host = ArtifactOutputLayout.SafeHostFolder(...)` (from
`etc/hostname`, else the collection name, else `Unknown`; Windows device names and invalid characters escaped).

Categories reuse the Windows names where the meaning matches: `Accounts`, `EventLogs`, `ProgramExecution`,
`Persistence`, `Network`, `FileSystem`, `SystemInfo`, `Software`, `WebServer`, `Containers`, `Timeline`.

Column conventions for every time-based CSV:

- `DateTimeUtc` first, `yyyy-MM-dd HH:mm:ss.fffffff` (`TimelineTimestamp.OutputFormat`), empty when unknown;
- `TimestampRaw` (the text as found) whenever the time was converted or inferred, plus `TimeInference` when a year or
  time zone was assumed (section 6);
- `SourceFile` (relative to the system root, `/`-separated) and `SourceLine` / `SourceOffset` for provenance;
- booleans as `Yes` / `No` (empty = unknown), like the Windows persistence CSVs.

## 5. Target artifacts

Approach: **N** = native C# in Core, **W** = Linux tool in the private EZ Forensics WSL distro (Docker optional), **P** = plaso.
Priority: P0 done, P1 first release, P2 next, P3 later.

| Artifact | Inputs (relative to root; rotated `.1` / `.gz` included) | Key fields | Output | How | Pri |
|---|---|---|---|---|---|
| Users | `etc/passwd`, `etc/login.defs` | UserName, Uid, Gid, Gecos, Home, Shell, IsSystemAccount, HasLoginShell | `Accounts\Users.csv` | N | **P0** |
| Shadow | `etc/shadow` | UserName, HashType (`$1$` MD5, `$5$`, `$6$`, `$y$` yescrypt, `$2b$`), Locked, NoPassword, LastChangeUtc, Min/Max/Warn/Inactive, ExpiresUtc | `Accounts\Users_Shadow.csv` | N | P1 |
| Groups | `etc/group`, `etc/gshadow` | GroupName, Gid, Members, IsPrivileged (root, sudo, wheel, adm, docker, lxd, disk, shadow) | `Accounts\Groups.csv` | N | P1 |
| Sudoers | `etc/sudoers`, `etc/sudoers.d/*` | File, Line, Principal, Hosts, RunAs, Tags (NOPASSWD), Commands, Flags | `Accounts\Sudoers.csv` | N | P1 |
| Auth log | `var/log/auth.log*` (Debian), `var/log/secure*` (RHEL) | DateTimeUtc, Program, Pid, EventType (SshAccepted, SshFailed, InvalidUser, Sudo, Su, SessionOpened/Closed, UserAdd, PasswordChange), User, SourceIp, SourcePort, AuthMethod, Command, Message | `EventLogs\Auth.csv` | N | P1 |
| Syslog | `var/log/syslog*`, `var/log/messages*`, `var/log/kern.log*`, `var/log/daemon.log*` | DateTimeUtc, Host, Program, Pid, Facility/Severity (when RFC 5424), Message | `EventLogs\Syslog.csv` | N | P1 |
| systemd journal | `var/log/journal/<machine-id>/*.journal[~]`, `run/log/journal/**` | DateTimeUtc (`__REALTIME_TIMESTAMP`), Hostname, SyslogIdentifier, Unit, Pid, Uid, Comm, Exe, CmdLine, Priority, Message, BootId, Cursor | `EventLogs\Journal.csv` | W now, N later | P2 |
| auditd | `var/log/audit/audit.log*` | DateTimeUtc, Serial, Type, Syscall, Uid/Auid/Euid, Exe, Comm, Key, Success, Proctitle (hex decoded) | `EventLogs\Audit.csv` | N | P2 |
| Logins | `var/log/wtmp*`, `var/run/utmp`, `run/utmp` | DateTimeUtc, Type (USER, DEAD, BOOT, RUN_LVL, SHUTDOWN), User, Terminal, Host, Ip, Pid, Session | `EventLogs\Logins.csv` | N (384-byte glibc records) | P1 |
| Failed logins | `var/log/btmp*` | same | `EventLogs\Logins_Failed.csv` | N | P1 |
| Last login | `var/log/lastlog` (sparse, 292-byte records by UID); `var/lib/lastlog/lastlog2.db`, `var/lib/wtmpdb/wtmp.db` (SQLite, Y2038-safe successors) | Uid, UserName (from the evidence's passwd), DateTimeUtc, Terminal, Host | `EventLogs\LastLog.csv` | N | P1 (SQLite P2) |
| Shell history | `root/.bash_history`, `home/*/.bash_history`, `.zsh_history` (`: <epoch>:<dur>;cmd`), `.local/share/fish/fish_history` (`- cmd:` / `when:`), `.ash_history`, `.python_history`, `.mysql_history`, `.psql_history`, `.lesshst`, `.viminfo` | DateTimeUtc (only when recorded: bash `#<epoch>` lines need `HISTTIMEFORMAT`), User, Shell, Order, Command, Flags (download-and-run, base64 decode, reverse shell, history tampering), FileModifiedUtc | `ProgramExecution\ShellHistory.csv` | N | P1 |
| Cron / at | `etc/crontab`, `etc/cron.d/*`, `etc/cron.{hourly,daily,weekly,monthly}/*`, `etc/anacrontab`, `var/spool/cron/crontabs/*` (Debian), `var/spool/cron/*` (RHEL), `var/spool/at*/*` | Source, User, Schedule, Command, FileModifiedUtc, Flags | `Persistence\Cron.csv` | N | P1 |
| systemd units & timers | `etc/systemd/system/**`, `usr/lib/systemd/system/**`, `lib/systemd/system/**`, `etc/systemd/user/**`, `home/*/.config/systemd/user/**`; enablement from `*.wants/` links; drop-ins `*.d/*.conf` | Unit, Kind (service, timer, socket, path), Scope (system/user), Origin (vendor `usr/lib` vs admin `etc`), Enabled, ExecStart/ExecStartPre, User, WantedBy, OnCalendar/OnBootSec, FileModifiedUtc, Flags (exec from `/tmp`, `/dev/shm`, home) | `Persistence\SystemdUnits.csv` | N | P1 |
| SysV init, rc.local, shell start-up | `etc/init.d/*`, `etc/rc*.d/*`, `etc/rc.local`, `etc/rc.d/rc.local`, `etc/profile`, `etc/profile.d/*`, `etc/bash.bashrc`, `home/*/.bashrc` `.profile` `.bash_profile` `.zshrc`, `etc/update-motd.d/*`, `etc/xdg/autostart/*` | File, Kind, Line, Command, FileModifiedUtc, Flags | `Persistence\InitScripts.csv` | N | P2 |
| Preload | `etc/ld.so.preload`, `etc/ld.so.conf`, `etc/ld.so.conf.d/*` | File, Entry, Exists in evidence | `Persistence\Preload.csv` | N | P1 (tiny, high value) |
| Kernel modules | `etc/modules`, `etc/modules-load.d/*`, `etc/modprobe.d/*`, `lib/modules/*/**/*.ko*` not listed in `modules.dep`; live `lsmod` from live_response | Module, Source, Options, Path, Flags | `Persistence\KernelModules.csv` | N | P2 |
| SSH keys | `root/.ssh/authorized_keys*`, `home/*/.ssh/authorized_keys*` | User, KeyType, FingerprintSha256, Comment, Options (`command=`, `from=`), FileModifiedUtc | `Persistence\SshAuthorizedKeys.csv` | N | P1 |
| SSH known hosts | `home/*/.ssh/known_hosts`, `root/.ssh/known_hosts`, `etc/ssh/ssh_known_hosts`, `.ssh/config` | User, Host (hashed `\|1\|` entries flagged), KeyType, Fingerprint | `Network\SshKnownHosts.csv` | N | P1 |
| sshd config | `etc/ssh/sshd_config`, `etc/ssh/sshd_config.d/*` | File, Line, Directive, Value, Flags (PermitRootLogin yes, PasswordAuthentication yes, changed AuthorizedKeysFile, extra Port) | `SystemInfo\SshdConfig.csv` | N | P1 |
| Package events | `var/log/dpkg.log*`, `var/log/apt/history.log*`, `var/log/yum.log*`, `var/log/dnf.log*`, `var/log/dnf.rpm.log*` | DateTimeUtc, Manager, Action, Package, Version, Arch, CommandLine / RequestedBy | `Software\PackageEvents.csv` | N | P2 |
| Installed packages | `var/lib/dpkg/status`; `var/lib/rpm` (SQLite on RHEL 9 / Fedora 33+, Berkeley DB / NDB older) | Package, Version, Arch, Status, Manager | `Software\Packages.csv` | N (dpkg, rpm SQLite), W (`rpm --dbpath` for BDB) | P2 |
| Web server | `var/log/apache2/*access*`, `var/log/httpd/*access*`, `var/log/nginx/*access*` (+ error logs) | DateTimeUtc, VHost, ClientIp, User, Method, Uri, Protocol, Status, Bytes, Referer, UserAgent | `WebServer\Access.csv`, `WebServer\Errors.csv` | N (Common / Combined; custom `LogFormat` from config later) | P2 |
| Containers | `var/lib/docker/containers/*/config.v2.json`, `hostconfig.json`, `*-json.log`; `var/lib/docker/image/*/repositories.json`; Podman `var/lib/containers/storage/overlay-containers/containers.json`, `home/*/.local/share/containers/**` | Id, Name, Image, CreatedUtc, StartedUtc, FinishedUtc, Cmd, Entrypoint, Privileged, Mounts, Ports, RestartPolicy; log lines | `Containers\Containers.csv`, `Containers\Logs.csv` | N (JSON) | P3 |
| Temp folders | `tmp/**`, `var/tmp/**`, `dev/shm/**` (tmpfs: usually only in live collections) | Path, Size, ModifiedUtc, Owner (tar/bodyfile), IsElf, IsHidden, IsExecutable, Sha256 (size cap) | `FileSystem\TempFiles.csv` | N | P1 |
| Bodyfile | UAC `bodyfile/bodyfile.txt` (mactime format) | DateTimeUtc rows per M/A/C/B, Path, Inode, Mode, Uid, Gid, Size | `FileSystem\Bodyfile.csv` | N | P1 |
| File system timeline | the image or folder | plaso `dynamic` CSV | `Timeline\SuperTimeline_plaso_<stamp>.csv` | P (`--parsers linux`) | P2 |
| Live response | UAC `live_response/**`, CatScale output | per command (ps, netstat/ss, lsmod, mounts) | `SystemInfo\LiveResponse_<Command>.csv` | N | P3 |
| Computer details | `etc/hostname`, `etc/machine-id`, os-release, `etc/timezone` / `etc/localtime`, `etc/hosts`, network config (netplan, NetworkManager, ifcfg) | Category, Field, Value, Source (same as Windows) | `SystemInfo\ComputerDetails.csv` | N (an `IComputerDetailsProvider`-like Linux collector) | P1 |

### Why native vs WSL vs plaso

- **Native C#** for line-oriented text (syslog, auth, cron, configs, histories, package logs, web logs) and small
  fixed-layout binaries (utmp/wtmp/btmp, lastlog): simple formats, no install, fast, cross-platform, unit-testable with
  fixtures, and we control timestamp handling (section 6), which generic tools get wrong for syslog.
- **WSL tools** where the format is complex and the reference implementation is the best reader: the systemd journal
  (compression xz/lz4/zstd, compact mode since systemd 252, sealing, corrupted/online files `*.journal~`). Plan:
  `journalctl --directory=<dir> --output=json --utc --no-pager --all` (`--file` per file to keep provenance) inside the
  private distro, with the evidence mounted read-only via `/mnt/<drive>` (UNC not readable, see README Zeek section),
  converted to CSV by Core. The distro's systemd must be at least as new as the evidence's (old journalctl cannot read
  compact journals): pin the distro to a current release and record `journalctl --version` in the manifest. On a Linux
  host the system `journalctl` is used. Also the fallback for BDB rpmdb.
- **plaso** for the whole-image file-system timeline (dfVFS reads ext2/3/4 including crtime, XFS, LVM), and as a second
  opinion for journal / syslog / utmp (it already has parsers). Not the primary per-artifact CSV source: its CSV is a
  timeline, not an artifact table, and it needs a heavy install.
- A **native journal parser** (P3) removes the WSL dependency for the most important Linux log; format is documented
  (systemd `JOURNAL_FILE_FORMAT.md`), but compression and compact-mode support make it a sizeable task.

## 6. Timestamp normalisation

All output is UTC. Rules per source:

- **RFC 3164 syslog** (`Mar  3 14:02:11 host prog[pid]: msg`, also auth.log, secure, kern.log, cron log, older yum.log):
  no year, no time zone.
  - *Year*: walk each file **backwards** from its last line, anchored to the file's mtime year (the last line was written
    shortly before the mtime); when the month jumps forward going backwards (Jan -> Dec), decrement the year. Each rotated
    file (`.1`, `.2.gz`) is anchored separately to its own mtime. If the mtime is unreliable (copied without preserving
    times: mtime later than the newest plausible date, or equal to the collection time), fall back to the collection
    date / analyst-supplied year and flag `TimeInference=YearAssumed`.
  - *Time zone*: the evidence's zone, in order: `etc/timezone` (Debian), `etc/localtime` link target
    (`/usr/share/zoneinfo/Europe/Berlin`), the POSIX TZ string at the end of the `etc/localtime` TZif v2+ file,
    `etc/sysconfig/clock` `ZONE=` (old RHEL). Convert with the evidence's own TZif rules (parsed in Core) rather than the
    analyst's tz database, because those are the rules the system applied. Unknown zone -> treat as UTC and flag
    `TimeInference=ZoneAssumedUtc`. Ambiguous (DST fall-back) hours are flagged `Ambiguous`.
  - Analyst override in the UI / CLI (`--tz`, `--year`) for both.
- **RFC 5424 / rsyslog high-precision** (`2024-03-03T14:02:11.123456+01:00`): exact, offset applied.
- **journal** `__REALTIME_TIMESTAMP` (µs since epoch, UTC): exact; keep `__MONOTONIC_TIMESTAMP` + `_BOOT_ID` columns.
- **utmp / wtmp / btmp / lastlog**: `int32` seconds (+ µs) since epoch, UTC. Records after 2038 wrap (Y2038): flag.
- **auditd** `msg=audit(1700000000.123:456)`: epoch UTC.
- **bash history** `#1700000000` lines; zsh `: 1700000000:0;`; fish `when: 1700000000`: epoch UTC. Lines without a time keep
  `DateTimeUtc` empty and an `Order` column.
- **dpkg.log, apt history.log** (`2024-03-03 14:02:11`): local time with year, no zone -> evidence zone.
  **dnf.log**: ISO with offset (`2024-03-03T14:02:11+0000` / `Z`). **Apache / nginx**: `[03/Mar/2024:14:02:11 +0100]`, exact.
- **Files** (mtime / atime / ctime / crtime from tar headers, bodyfile, or the extracted file): UTC; ctime and crtime
  exist only in the image, tar PAX headers or a bodyfile, never in a Windows extraction.

## 7. Forensic reliability advisories (Linux specific)

Shown on the Ingest page and recorded in the manifest, the same way as the Windows admin / partial-collection notes:

- **atime is weak evidence**: `relatime` (default since 2.6.30) updates atime only once a day or when older than
  mtime/ctime; `noatime` never. Do not read "not accessed" from atime.
- **Copied collections lose times**: `cp` without `-p`, zip, and extraction on Windows keep at most mtime; ctime is reset,
  crtime does not exist on ext3 and is only in the inode on ext4. File-time columns from a copy say so
  (`TimeSource=CopiedFile`), and the year inference (section 6) does not trust them blindly.
- **Ownership and modes** are lost in a Windows extraction: take them from the tar index or the bodyfile.
- **Log rotation**: logrotate keeps 4 weeks by default (`rotate 4`, weekly), compresses (`.gz`, `delaycompress`), and
  `copytruncate` can lose lines; journald caps size (`SystemMaxUse`) and, with `Storage=auto` and no
  `var/log/journal`, keeps the journal only in `run/log/journal` (tmpfs: gone from a disk image). Report the covered time
  range per log so gaps are visible.
- **Log tampering is easy**: wtmp/btmp/lastlog are editable (`utmpdump`), text logs editable by root. Cross-check auth.log
  vs wtmp vs journal; report gaps and out-of-order records.
- **Shell history is incomplete by design**: written at shell exit, overwritten without `histappend`, skipped with
  `HISTCONTROL=ignorespace`, disabled with `unset HISTFILE` / `HISTFILE=/dev/null` / a link to `/dev/null`, no
  timestamps without `HISTTIMEFORMAT`.
- **tmpfs**: `/tmp` (often), `/dev/shm` and `/run` are RAM-backed and absent from disk images; only live collections have them.
- **Time zone and clock**: syslog times are local and yearless; the zone may have changed during the period; no NTP ->
  drift. Record the assumptions per row (section 6).
- **UIDs, not names**: files and logs record UIDs; map them through the evidence's `passwd`, never the analyst's.
- **Containers**: container file systems live in overlay layers (`var/lib/docker/overlay2/<id>/diff`); host-level
  artifacts do not cover what happened inside a container.

## 8. Super timeline

- **EZ CSV merge**: add `linux-*` source types to `Resources/timeline/mappings.json`, recognised by file name and path
  (`(^|\\)EventLogs\\Auth\.csv$`, required header `DateTimeUtc`), with descriptions and MACB letters like the Windows
  ones: Auth/Syslog/Journal/Audit/Logins `....` (event) or `.A..` for logon / session start, ShellHistory `.A..`
  (executed), Cron / SystemdUnits / InitScripts `M...` (file modified), Bodyfile M/A/C/B per row, PackageEvents `....`
  with message `Action Package Version`, TempFiles M/A/C/B. Accounts CSVs without times are `ignore`.
- Because outputs move to `<case>\<Host>\...`, the merge should take a `Host` from the path's first folder. That needs a
  new column (or a `Host` prefix in `Source`) in `SuperTimelineBuilder.OutputHeaders`, which is a contract change: open
  question.
- **plaso** for Linux: `--parsers linux` preset (plus `--parsers` filter when only logs are wanted); dfVFS reads ext/XFS
  images directly, so the image is passed as the source, not an extraction.

## 9. Reuse for the Avalonia app and the CLI

- `Core/Platforms` uses no Windows APIs: `System.IO`, `System.Formats.Tar`, regex. Path logic is in `EvidencePath`, so the
  same code is right on case-sensitive Linux file systems and on NTFS copies.
- Ingest integration (P1): `SourceLayoutDetector` keeps the Windows roots; a Linux step calls
  `OsFamilyDetector.FindSystemRoots` and `ArtifactParserRegistry.Discover`, and the planner adds one job per
  `ArtifactParserMatch`. Jobs run like the persistence collector: a headless built-in command
  (`ezforensics parse --parser <id> --root <root> --out <dir> [--host <name>]`), so the job queue, logs, cancel and the run
  manifest (argv, SHA-256 of outputs, `Details`: parser id, OS detection, warnings count) work unchanged.
- **CLI** (planned, P3; not in the repo yet) (`EzForensics.Cli`, net8.0): `ezforensics triage --source <dir|archive> --case <dir> [--platform linux] [--parsers a,b]
  [--tz <zone>] [--year <yyyy>]` = detect -> discover -> run -> optional timeline, plus `ezforensics parse` for one parser.
- **Avalonia app** (planned, P3): the same view models over Core; only Windows-specific services (WSL, Authenticode, `RegLoadKey`)
  are hidden behind the existing OS checks. On a Linux host, `journalctl` / `rpm` run natively instead of through WSL
  (a `ILinuxToolRunner` with WSL / Docker / native implementations, built on `Core/Runtimes`).

## 10. Roadmap

| Phase | Content |
|---|---|
| P0 (done) | `OsFamily`, `OsFamilyDetector`, `EvidencePath`, parser abstraction + registry + CSV writer, `LinuxPasswdParser`, tests, this document |
| P1 | Ingest integration (Linux roots, plan rows, built-in job + manifest), Computer details for Linux, shadow / group / sudoers, auth, syslog, wtmp / btmp / lastlog, shell history, cron, systemd, preload, SSH, temp folders, UAC bodyfile, timeline mappings, timestamp engine (year inference + evidence TZif), advisories |
| P2 | Journal via WSL `journalctl`, auditd, init / start-up scripts, kernel modules, packages (dpkg / rpm SQLite), web server logs, tar.gz reader + `TarIndex.csv`, wtmpdb / lastlog2, plaso `linux` preset in the Super timeline card |
| P3 | Native journal parser, containers, live_response / CatScale parsers, Avalonia desktop app, CLI, macOS parsers on the same abstraction |

## 11. Open questions

1. **New layout vs Windows outputs**: Windows ingest writes `<case>\<Category>\...` today. Move Windows to
   `<case>\<Host>\<Category>` too (consistent, but changes Results, timeline path patterns and existing cases), or keep
   Windows as is and use the host level only for Linux / multi-host cases?
2. **Super timeline Host column**: add `Host` to `SuperTimelineBuilder.OutputHeaders` (contract change) or encode it in
   `Source`?
3. **Mounting Linux images on Windows**: support `wsl --mount` (admin, physical disks / VHDX only) in the private distro,
   or require extraction / plaso for images? Raw/E01 need a loop device inside WSL (`ewfmount` for E01).
4. **Password hashes**: should `Users_Shadow.csv` contain the hashes (useful for cracking, sensitive) or only the hash
   type and state? Proposal: type and state only, hashes behind an explicit option.
5. **Provenance columns for Users.csv**: the agreed columns have no `SourceFile` / `SourceLine` or raw password field
   indicator; add them (as for the other parsers), or keep `Users.csv` minimal?
6. **Year inference fallback**: when mtimes are not trustworthy, what default year: the collection date, the newest
   timestamp in other logs, or always ask?
7. **Category names**: `EventLogs\Logins.csv` vs a separate `Logins` category; `WebServer` vs `Applications`.
8. **Formula injection**: the CSVs keep values verbatim (evidence). Opening them in Excel can execute `=`/`+`/`@` cells;
   Timeline Explorer and the Results grid do not. Leave as is and warn on export, or offer an escaped export?
9. **EZ tools on a Linux host**: the net9 EZ tools are .NET console apps; which of them run on Linux (for Windows evidence
   ingested from the Avalonia app) needs testing per tool.
10. **WSL distro systemd version**: the private distro must read journals from newer systems; pin a rolling-ish release,
    or bundle a newer `journalctl` (licensing: LGPL-2.1+)?
11. **Velociraptor / UAC layouts**: verify the current folder names (`uploads/auto`, `[root]`) against recent releases
    before writing the root finder rules; add fixtures from real collections.
