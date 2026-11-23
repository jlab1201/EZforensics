# EvtxECmd default event selection

Data file: `src/EzForensics.Core/Resources/evtx/recommended-events.json`
Research date: 2026-10-16. Research only; no C# or XAML changed.

## How the list was chosen

1. **EZ Maps folder** (`evtx/Maps` in the EricZimmerman/evtx repo). Only events with a map can be decoded into named fields. Candidates were kept only if a map exists, or if the event is a well-known high-value source and is marked `unverified` in the data file.
2. **Microsoft Appendix L (Events to Monitor)** for criticality ranking. It is written for Active Directory, so DC-only events (Kerberos, DS changes, AD replication) were left default-off.
3. **JPCERT/CC "Detecting Lateral Movement through Tracking Event Logs"** and a community transcription of it, for the lateral movement group (logon types, RDP, WinRM, service and task creation). The PDF itself could not be parsed in this session, so its IDs are only partly confirmed.
4. **NSA/CISA "Spotting the Adversary with Windows Event Log Monitoring"**. The official PDF returned HTTP 403 and was not read. Only a secondary mirror was checked, which confirmed 1102 and 7045 as relevant. Treat the rest of its IDs as unconfirmed.
5. Sysmon IDs are included as optional sources, since Sysmon is not default on Windows.

Verified EZ maps (file exists in `evtx/Maps` and its targets were read): Security 4624, 4625, 4688, 4697, 4698, 4720, 4732, 1102; System 7045; PowerShell 4103, 4104; Sysmon 1; TaskScheduler 106; TerminalServices-LocalSessionManager 21; Defender 1116; WMI-Activity 5861. Verified in the 2026-10-16 pass: Security 4648, 4672, 4634, 4769, 4771, 4722, 4724, 4726, 4728, 4702, 4778, 4779, 5140, 5145, 4719; Sysmon 3, 8, 10, 11, 13; System 7036, 104; TaskScheduler 200, 140; TerminalServices-LocalSessionManager 24, 25; TerminalServices-RemoteConnectionManager 1149; WinRM 91; Defender 1117, 5001, 5007; WMI-Activity 5860.

No EZ map exists (HTTP 404 on the predicted filename) for Security 4756 and PowerShell 4105. They are marked `no-map`. No EZ map exists for them, so their fields come only from the raw Payload.

Map lookup for this pass: the GitHub tree view for `evtx/Maps` truncates at 97 files, so it cannot be used to list the rest. Each unverified event was checked by fetching its predicted raw file name (`Channel_Provider_EventID.map`) from `raw.githubusercontent.com`. Field targets were read through a summarising fetch, not from a raw copy, so re-check exact `PayloadData` slots against the map file before relying on them in a parser.

## Groups

| Group | Channel(s) | Events | Default on |
|---|---|---|---|
| Logon and authentication | Security | 7 | 4 (4624, 4625, 4648, 4672) |
| Account management | Security | 7 | 2 (4720, 4732) |
| Process execution | Security, Sysmon | 6 | 4 (4688, Sysmon 1, 3, 10) |
| Process activity (process tree details) | Sysmon, AppLocker, TaskScheduler, WMI-Activity, Windows PowerShell, Security | 28 | 23 |
| Services and persistence | System, Security, Sysmon | 4 | 2 (7045, 4697) |
| Scheduled tasks | TaskScheduler, Security | 5 | 3 (TS 106, TS 200, 4698) |
| PowerShell | PowerShell/Operational | 3 | 2 (4104, 4103) |
| RDP and remote access | LocalSessionManager, RemoteConnectionManager, WinRM, Security | 9 | 3 (LSM 21, RCM 1149, WinRM 91) |
| Log tampering | Security, System | 3 | 3 (1102, 104, 4719) |
| Defender | Windows Defender/Operational | 4 | 4 (1116, 1117, 5001, 5007) |
| WMI persistence | WMI-Activity/Operational | 2 | 1 (5861) |
| **Total** | | **78** | **51** |

Each event entry has: `channel`, `provider`, `eventId`, `title`, `fields` (Windows EventData names), `mapProperties` (EZ targets, where verified), `mapStatus`, and `default`.

## How EvtxECmd filtering works

- **`--inc`** is a comma-separated list of event IDs. Only those IDs are processed and everything else is ignored. The help text gives the format as `4624,4625,5410`. The placeholder shows a range (`5500-5600`), which suggests ranges are accepted, but this was not tested.
- **`--exc`** is the reverse: it drops the listed IDs and keeps the rest. **`--inc` overrides `--exc`** when both are given.
- **Filtering is by event ID only.** There is no `--channel` or `--provider` option. Channel is controlled by the input: `-f` takes one `.evtx` file, `-d` recursively processes a folder.
- `--sd` and `--ed` (UTC start/end) are independent of the ID filters. How they combine with `--inc` is not documented.
- **Maps** are looked up by Channel + Provider + EventId; the filename is only a convention. Files are named `Channel_Provider_EventID.map`. Maps load in alphabetical order. A user override is made by copying a map and prefixing its name with `1_`, which loads first and suppresses the default. Event IDs are not globally unique, so the Provider requirement stops an ID from matching an unrelated provider.
- `--maps` sets the maps folder (default: `Maps` next to the exe). `--sync` downloads updated maps and is off by default.

## Implications for EZ Forensics

- Channel selection must be done by choosing input files, not by an EvtxECmd flag. Run one invocation per channel file with that channel's ID list in `--inc`. Running a single `-d` pass with a combined list would pull in IDs that collide across channels.
- Because `--inc` is global, a combined ID list can also select events from channels that were not intended, when they share an ID.
- Use `--inc` for the default-on set and let users add or remove IDs from the JSON groups.

## Caveats

- Map status across the 78 events: 74 `verified`, 4 `no-map` (Security 4756, PowerShell 4105, AppLocker 8003 and 8006), 0 `unverified`.
- Verified EZ maps are the only source of named `mapProperties`. For `no-map` events, use the raw Payload.
- Sysmon, WinRM, TaskScheduler/Operational, Defender and WMI-Activity are not enabled on a default Windows install and only produce events when configured or installed.
- 4688 command-line capture requires the "Include command line in process creation events" policy. Without it, `CommandLine` will be empty.
- 4103/4104 require PowerShell module and script block logging to be enabled.
- Appendix L criticality is a guide written for AD, not a standalone detection rule. Some "High" items (for example 4618, 4649) are not in the default set.
- Raw event data is preserved even when no map matches, but this was not verified for the tool version currently bundled.
- GitHub issue #11 in the EvtxECmd repo reports that `--inc`/`--exc` can misread IDs when the event string contains qualifiers. Its current status was not confirmed.
- JPCERT and NSA IDs were not fully verified. The JSON `sources` list records every URL consulted, including the two partly read secondary sources.

## Sources

- https://github.com/EricZimmerman/evtx
- https://github.com/EricZimmerman/evtx/tree/master/evtx/Maps
- https://raw.githubusercontent.com/EricZimmerman/evtx/master/evtx/Maps/!!!!README.md
- https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/plan/appendix-l--events-to-monitor
- https://www.jpcert.or.jp/english/pub/sr/DetectingLateralMovementThroughTrackingEventLogs_version2.pdf
- https://hannahsuarez.github.io/2019/eventids-lateral/
- https://www.nsa.gov/ia/_files/app/spotting_the_adversary_with_windows_event_log_monitoring.pdf

## Custom maps (Edit fields…)

In Customize, **Edit fields…** on an event opens a map editor. It reads up to 20 records of that event from the evidence, shows their fields, and lets you pick which field goes into which EvtxECmd column (UserName, RemoteHost, ExecutableInfo, PayloadData1-6). The format uses `%Name%` for each value, as in the stock maps. The preview shows the result on the samples and marks empty columns.

**Save** runs the installed EvtxECmd on the sample's .evtx with only the new map (`--maps`, `--inc <id>`) and compares its CSV with the preview. You can save anyway or fix the map. Without EvtxECmd the check is skipped.

- Custom maps live in `%LocalAppData%\EzForensicsData\evtx-maps`, named `Channel_Provider_EventId.map`. Stock maps are never edited.
- Before each EvtxECmd run, and after `--sync`, they are copied into the tool's `Maps` folder as `0_EzForensics_<name>.map`.
- EvtxECmd loads maps in file name order and keeps the first one per EventId + Channel + Provider. The `0_` copy sorts first, so it wins. The stock map is then skipped with an "An item with the same key has already been added" line in the job log. Checked with EvtxECmd 2026 (net9).
- **Reset to stock** deletes the custom map and its copy.
- Known gap: EvtxECmd writes `>` as `&gt;` in some values (Time-Service 35/37); the preview shows `>`, so the check reports it.
