# Changelog

## Unreleased

### Added

- EZ Forensics no longer installs Ollama; install it from ollama.com and use Tools → AI → Use local AI to download a model.
- Review shows one "Spectre: not set up" chip. Run Spectre is hidden until Spectre is ready.

### Changed

- Left menu: Ingest, Analyze, Explore and Review on top; AI (was Learn), Tools and Options at the bottom, one click each. The Settings gear is gone.
- Story: the header reads "Slide N of M" and the card reads "Key event N of M".
- Analyze opens new CSVs with the time column first and record-id columns hidden (EvtxECmd, MFTECmd, PECmd and generic CSVs). Saved views win.
- The group-by bar is hidden until you group or use Actions > Show group bar.
- Artifact notes sit on one line.
- Process details explain each marker once. Copy buttons show on hover.
- Spectre engine replies are capped, with a visible truncation marker.

### Fixed

- The app always quits after you close it; if something holds it open, it exits after 10 seconds and logs it.
- One shared timestamp parser for the whole app.
- A dialog appears when flags or the Analyze view cannot be saved at exit. Load and save errors are now logged.

## 1.4.0

Versions 1.1 to 1.3 were internal builds and were never published. This release covers everything since 1.0.0.

### Added

- **Review** page: findings and key events. Flag a row, promote it to a finding, then to a key event. Overview, Story timeline (zoom and pan), Details and a report you can export.
- **Spectre**, the AI assistant. Local Ollama by default. Anthropic, OpenAI and OpenRouter are optional and need your own key. It can scan, search, open pages and write findings through built-in engine tools.
- **Learn** page: a knowledge base for Spectre. Add files, folders, an Obsidian vault or .ezkb packs. Review them on the **To review** tab, then accept or reject. A 3D graph shows the accepted notes. Documents are taken in with AI labels and a Distill step.
- **Explore** page: browse case folders and files, with overview, hex, strings, metadata and PE tabs. Files are only read, never run. Includes an offline CyberChef and advanced search.
- Setup wizard: "File analysis (Explore)" bundle (Detect It Easy, ExifTool, CyberChef), no admin rights. Tools page card to install, update or remove them.
- Process tree: navigator, "Flag all" for indicators, PPID click jumps to the parent.
- Filter builder: type-ahead fields and a Flag condition. Row-number gutter in the table.
- EvtxECmd custom map editor and event selection.
- Tools page: coloured, collapsible sections and an editable Ingest command per rule.
- Options: AI settings, hot keys, hints and a Settings flyout.
- **Load demo case** on Ingest and Review: a small made-up case to try the process tree, Review and slides without evidence or AI.
- Review: **Present** button for the slide walk-through, and report export as a single HTML file.
- Spectre: a "Set up local AI" card when no model is ready, and starter prompts in an empty chat.

### Changed

- Ingest and Jobs share one page with one Jobs list. Ingest can be stopped and resumed.
- Setup runs in the background and resumes after a restart.
- Timestamps are labelled UTC in the grid and exports.
- Windows fit smaller screens, no text below 11 px, clearer empty states, Learn opens on Intake until there are notes.
- A technique with several tactics shows the earliest in the kill chain (T1078 is Initial Access).
- Downloads: Eric Zimmerman tools only from their official https hosts, with each zip's SHA-256 logged. The .NET install script must carry Microsoft's signature. plaso is pinned to one version. Unsafe archive paths stop the extract.

### Fixed

- Timestamp parsing: Plaso dates are read as month/day only where the tool writes them that way.
- Several layout overlaps in Options, Analyze and Findings, in light and dark themes.
- Cancelled jobs resume without repeating finished steps.
- Opening the app twice now focuses the running window.

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
