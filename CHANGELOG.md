# Changelog

## 1.0.0 – 2026-10-30

First public release.

- **Setup wizard**: installs Eric Zimmerman's tools, plaso, Volatility 3, Zeek and RITA. Minimal, Everything or your own picks. May ask for admin rights once and a restart for Zeek and RITA.
- **Ingest**: KAPE collections, mounted images, folders, live drives, memory images and pcaps. Archives (zip, 7z, rar, tar, gz, vhd, vhdx), with a password prompt when needed. Artifact scan, job plan, editable command lines.
- **Jobs**: queue, live logs, retry, cancel and resume. A manifest with hashes for every run.
- **Analyze**: fast grid for very large CSVs, column filters, AND / OR / NOT search, grouping, merge and combined views, flags, export, saved filters and views, hot keys, timestamp-reliability warnings.
- **Process tree**: from memory and event logs, with Prefetch and Zeek evidence. Flags suspicious parents, hollowing, ghosting, injection and encoded commands.
- **Persistence** and **Computer details**: built in, read offline from the evidence.
- **Timeline**: super timeline (plaso) and MACB timeline from $MFT.
- **Memory**: Volatility 3 with a selectable plugin list.
- **Network**: Zeek logs from pcaps, RITA beacon detection.
- **Tools** page to install and update tools. In-app update check through GitHub releases.
