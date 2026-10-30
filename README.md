# EZ Forensics

A Windows app for DFIR triage. Point it at evidence, it picks and runs the right tools, and you read everything in one place.

## What you can do

**Set up**
- A setup wizard installs the tools for you: Eric Zimmerman's tools, plaso, Volatility 3, Zeek and RITA.
- Choose Minimal, Everything, or tick your own.

**Ingest evidence**
- Feed the **Ingest** page a KAPE collection, a mounted image, a folder, a live drive, a memory image or a pcap.
- Archives work too: zip, 7z, rar, tar, gz, vhd, vhdx. It asks for the password if there is one.
- It scans for artifacts and plans the jobs: event logs, registry, $MFT, $J, prefetch, LNK, jump lists, ShellBags, Amcache, ShimCache, SRUM, Recycle Bin, browser and other SQLite data.
- Untick what you don't want, edit the command line of any tool, then run.
- The **Jobs** page shows the queue, live logs, retry and cancel. Cancelled runs can be resumed.

**Analyze**
- Open any result CSV, even very large ones, in a fast grid.
- Filter by column, search all columns, build filters with AND / OR / NOT.
- Group by any column, sort, merge CSVs or open several as one view.
- Flag rows (Informational, Interesting, Suspicious, Malicious, Benign) and export flagged or filtered rows.
- Save filters and views and reuse them on the next case.
- A banner warns you when timestamps may be unreliable, for example on copied files.

**Investigate**
- **Process tree**: built from memory (Volatility) and event logs (4688, Sysmon), with Prefetch and Zeek data attached. Shows hidden, hollowed, ghosted and injected processes, odd parents and encoded commands. Click a process for its network, files, registry, modules, scripts and more.
- **Persistence**: Run keys, services, scheduled tasks, WMI and startup folders, read offline from the evidence.
- **Computer details**: name, OS, time zone, IP addresses, disks and users, read from the registry.
- **Timeline**: a super timeline (plaso) and a file-system MACB timeline from $MFT.
- **Memory**: Volatility 3 with a selectable plugin list (processes, command lines, network, malfind, services, DLLs and more).
- **Network**: Zeek turns pcaps into conn, DNS, HTTP and TLS logs. RITA flags beaconing, long connections and DNS tunneling.
- Every run writes a manifest with tool, version, command and hashes.

## Requirements

- Windows 10 (1809 or later) or Windows 11, 64-bit.
- About 1 GB of free disk for the Minimal set. Everything needs more, and cases need their own space.
- Internet during setup.

## Install

1. Download `EzForensics-Setup.exe` from [Releases](https://github.com/jlab1201/EZforensics/releases).
2. Run it. No admin rights needed.
3. Follow the setup wizard on first start. It may ask for admin rights once and a restart if you pick Zeek or RITA.

More in [docs/SETUP.md](docs/SETUP.md).

## Build from source

- Install the .NET 8 SDK.
- Build: `dotnet build EzForensics.sln`
- Test: `dotnet test tests/EzForensics.Core.Tests` and `dotnet test tests/EzForensics.App.Tests`
- Run: `dotnet run --project src/EzForensics.App`

## License

MIT, see [LICENSE](LICENSE). Third-party software is listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The tools are downloaded from their official sources, not bundled.

## Disclaimer

Use it only on evidence you are authorised to examine. No warranty. Verify important findings with a second tool.

Report security issues as described in [SECURITY.md](SECURITY.md).
