# EZ Forensics: first-run guide

## Install

1. Download `EzForensics-Setup.exe` from [Releases](https://github.com/jlab1201/EZforensics/releases).
2. Run it. It installs for your user only. No admin rights needed.
3. EZ Forensics starts and opens the setup wizard.

You need Windows 10 (1809 or later) or Windows 11, 64-bit, and internet access during setup.

## The setup wizard

1. **Welcome.** Nothing is installed until you click Install.
2. **System check.** Shows what your computer already has (disk space, admin rights, internet and so on). Nothing here needs fixing. If it says the internet is not reachable, fix your connection or proxy and click *Check again*.
3. **Choose what to install.**
   - **Minimal**: Windows artifacts (Eric Zimmerman's tools), the super timeline (plaso) and file analysis (Explore tools). No admin rights needed. About 1 GB.
   - **Everything** (preselected): Minimal plus Network (Zeek, RITA) and Memory (Volatility 3). The local AI item is ticked only if your PC can run it.
   - Or tick your own. Click the arrow on a bundle to see its tools and download sizes.
   - Optional: an offline Windows symbol pack for Volatility (large, off by default).
4. **Summary.** Steps, download size, disk space and a time estimate. Tick the licence box to enable *Install*. *View licences* lists every tool.
5. **Install.** Shows progress and a log. If a step fails, read the log, fix the cause (usually network or proxy) and click *Retry*. Finished steps are kept. *Cancel* undoes what this setup installed.
6. **Done.** Shows what was installed. *Restart now* appears only if Windows needs a restart.

Setup may ask for admin rights once and a restart, only if you pick Network (Zeek, RITA). After the restart, setup reopens and finishes by itself. If you decline the admin prompt, everything else still installs.

You can open the wizard again with the *Open setup* button on Ingest (shown when a tool is missing). **Tools** (and **Options**) are at the bottom of the left menu. It installs and updates tools one by one.

## What the bundles install

| Bundle | What you get |
|---|---|
| Windows artifacts | Eric Zimmerman's command-line parsers, the latest event-log maps and batch files. Persistence and Computer details are built in. |
| Super timeline | log2timeline / plaso. |
| Network | Zeek (pcap to logs) and RITA (beacon detection). |
| Memory | Volatility 3. Optional offline Windows symbol pack. |
| File analysis (Explore) | Detect It Easy, ExifTool and CyberChef. No admin rights. |
| AI assistant (Spectre) | Local AI: Ollama and a model that suits your PC. |

Setup downloads each tool from its publisher. Nothing is bundled with the app.

## AI assistant (optional)

- Open **Options > AI** to set up Spectre.
- Local Ollama is the default and keeps your data on the PC.
- Anthropic, OpenAI and OpenRouter need your own API key. The data you share with Spectre goes to that provider.
- The AI page's semantic search needs the embedding model (build step in the README). Without it, the page uses text search.

## Updating

- **The app.** Go to *Options > Installation > Updates > Check for updates*, then *Install and restart*. This works for a copy installed with `EzForensics-Setup.exe`. You can also just run a newer `EzForensics-Setup.exe`.
- **The tools.** Use the **Tools** page. It shows each tool's installed and latest version, with Install / Update buttons.

## Where your data is kept

| What | Where |
|---|---|
| The app | `%LOCALAPPDATA%\EzForensics` |
| Settings, tools, logs | `%LOCALAPPDATA%\EzForensicsData` |
| Cases (default) | `Documents\EZ Forensics Cases` |

Change the case folder and the tools folder in **Options**. Updating or uninstalling the app never touches your data folder or your cases.

Logs: `%LOCALAPPDATA%\EzForensicsData\logs` (setup logs in the `setup` folder, app log in `app.log`).

## Proxy

EZ Forensics uses your Windows proxy settings. To change that, edit `settings.json` in the data folder: set `"proxyMode"` to `"system"` (default), `"none"` or `"manual"`. For `Manual`, also set `"proxyUrl"` (for example `http://proxy:8080`).

## Uninstall

1. If you installed Zeek or RITA, remove them first with *Uninstall* on the RITA and Zeek cards in **Tools**.
2. Open *Windows Settings > Apps > Installed apps*, pick EZ Forensics and uninstall. This removes the app and its shortcuts.
3. To remove everything, also delete `%LOCALAPPDATA%\EzForensicsData`. Your cases stay in `Documents\EZ Forensics Cases` until you delete them.

## Troubleshooting

- **A setup step failed.** Read the log on the Install page, fix the cause, click *Retry*.
- **Downloads fail.** Check your internet connection and proxy, then click *Check again* on the System check.
- **Low disk space.** Free some space, or move the tools folder in **Options**.
- **A tool does not start (.NET 9).** A few tools need .NET 9. Run setup again with the .NET 9 item ticked. No admin rights needed.
- **Network install fails.** Virtualization may be off in your BIOS/UEFI, or blocked by policy. Turn it on, then click *Retry*. Inside a virtual machine, enable nested virtualization.
- **Volatility says symbols are missing.** Go online and run again, or install the symbol pack in setup.
- **Volatility finds `hiberfil.sys` but does not analyse it.** Expected: hibernation files are not analysed in this release.
- **Jobs that need admin were skipped.** Raw `$MFT`, `$J` and live-drive reads need admin. On **Ingest**, click *Restart as Administrator*. Your page is restored.
