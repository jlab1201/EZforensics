# Demo case (synthetic)

Everything in this folder is made up. It was written by hand for the
"Load demo case" button; it comes from no real computer, person or network.

- Host `DEMO-WS01`, user `demo.user`. IP addresses are from the documentation
  ranges (203.0.113.0/24), the URL uses `example.com`.
- The encoded PowerShell command decodes to `Write-Output demo`.
- No file here is or contains malware.

Story (14 March 2026, UTC): an RDP sign-in as demo.user, a macro invoice opened
in Word, Word starts PowerShell with `-enc`, PowerShell runs a DLL from Temp
with rundll32, rundll32 starts a hollowed svchost.exe, and a new auto-start
service is installed. Then the injected svchost.exe reads lsass.exe memory, the new
service calls out over HTTPS, and the Security log is cleared.

Layout (as Ingest writes it):

- `case.json` - case layout marker
- `DEMO-WS01\Memory\Memory_*.csv` - Volatility pslist, psscan, pstree, cmdline, malfind, hollowprocesses
- `DEMO-WS01\EventLogs\EventLogs.csv` - EvtxECmd (4624, 4688, 7045, 4656, 5156, 1102 and a few others)
- `DEMO-WS01\FileSystem\MFT.csv` - MFTECmd
- `DEMO-WS01\ProgramExecution\Prefetch.csv` - PECmd
- `.ezforensics\analysis.json` - analyst findings and key events for the Review page

The process tree data (`DEMO-WS01\.ezprocesses`, `DEMO-WS01\Processes`) is built
when the demo is loaded.
