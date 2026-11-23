<#
  Renders docs/images/ai-graph.png: the AI page's Graph tab, drawn from SYNTHETIC notes (never from a real knowledge store).
  The real page (src/EzForensics.App/Assets/brain) is copied to a temp folder, fed a made-up graph in the same shape the app posts
  ({ nodes: [{key,kind,label,group,size,noteId}], links: [{source,target,kind,weight}] }) and screenshotted with headless Microsoft Edge.

    pwsh build/make-graph-shot.ps1            # or: powershell -File build\make-graph-shot.ps1
#>
param(
  [string]$Out = (Join-Path $PSScriptRoot '..\docs\images\ai-graph.png'),
  [string]$Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
  [string]$Select = '',
  [double]$Back = 3.0,
  [int]$Budget = 25000
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('ezf-graph-shot-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item (Join-Path $repo 'src\EzForensics.App\Assets\brain\*') $work -Recurse

# ---- concepts: kind:key | label
$conceptLines = @'
technique:T1078|T1078 Valid Accounts
technique:T1059.001|T1059.001 PowerShell
technique:T1218.011|T1218.011 Rundll32
technique:T1055.012|T1055.012 Process Hollowing
technique:T1055|T1055 Process Injection
technique:T1543.003|T1543.003 Windows Service
technique:T1003.001|T1003.001 LSASS Memory
technique:T1071.001|T1071.001 Web Protocols
technique:T1070.001|T1070.001 Clear Windows Event Logs
technique:T1021.001|T1021.001 Remote Desktop Protocol
technique:T1021.002|T1021.002 SMB/Windows Admin Shares
technique:T1053.005|T1053.005 Scheduled Task
technique:T1547.001|T1547.001 Registry Run Keys
technique:T1047|T1047 WMI
technique:T1550.002|T1550.002 Pass the Hash
technique:T1566.001|T1566.001 Spearphishing Attachment
technique:T1569.002|T1569.002 Service Execution
technique:T1036|T1036 Masquerading
technique:T1027|T1027 Obfuscated Files
artifact:prefetch|Prefetch
artifact:mft|$MFT
artifact:amcache|Amcache
artifact:shimcache|ShimCache
artifact:registry|Registry hives
artifact:lsass|lsass.exe
artifact:svchost|svchost.exe
artifact:evtx|Event logs
artifact:pcap|PCAP
artifact:usn|USN Journal
eventid:4624|Event 4624
eventid:4625|Event 4625
eventid:4688|Event 4688
eventid:7045|Event 7045
eventid:1102|Event 1102
eventid:4104|Event 4104
eventid:4698|Event 4698
eventid:5156|Event 5156
tool:volatility|Volatility
tool:zeek|Zeek
tool:rita|RITA
tool:evtxecmd|EvtxECmd
tool:pecmd|PECmd
tool:mftecmd|MFTECmd
tool:yara|YARA
tool:plaso|Plaso
tool:sysmon|Sysmon
'@ -split "`r?`n" | Where-Object { $_ }

# ---- notes: category | title | concept keys (kind:key, comma separated)
$noteLines = @'
Windows artifacts|Prefetch|artifact:prefetch,tool:pecmd,technique:T1059.001
Windows artifacts|ShimCache|artifact:shimcache,artifact:registry
Windows artifacts|Amcache|artifact:amcache,artifact:registry,technique:T1036
Windows artifacts|ShellBags|artifact:registry
Windows artifacts|UserAssist|artifact:registry
Windows artifacts|Jump Lists|artifact:registry
Windows artifacts|LNK files|artifact:mft
Windows artifacts|Recycle Bin $I files|artifact:mft
Windows artifacts|SRUM|artifact:registry
Windows artifacts|BAM and DAM|artifact:registry,technique:T1059.001
Windows artifacts|Zone.Identifier|artifact:mft,technique:T1566.001
Windows artifacts|NTFS $MFT|artifact:mft,tool:mftecmd
Windows artifacts|USN Journal|artifact:usn,artifact:mft,tool:mftecmd
Windows artifacts|Alternate data streams|artifact:mft
Windows artifacts|Volume Shadow Copies|artifact:registry
Windows artifacts|Registry hives overview|artifact:registry
Windows artifacts|Timestomping|artifact:mft,technique:T1036
Windows artifacts|Thumbcache|artifact:mft
Windows artifacts|Windows Search index|artifact:mft
Windows artifacts|MRU lists|artifact:registry
Event logs|Logon types in 4624|eventid:4624,technique:T1078,technique:T1021.001
Event logs|Failed logons 4625|eventid:4625,technique:T1078
Event logs|Process creation 4688|eventid:4688,tool:evtxecmd,technique:T1059.001
Event logs|Special privileges 4672|eventid:4624,technique:T1078
Event logs|Account created 4720|technique:T1078
Event logs|Scheduled task created 4698|eventid:4698,technique:T1053.005
Event logs|Service installed 7045|eventid:7045,technique:T1543.003,technique:T1569.002
Event logs|Log cleared 1102|eventid:1102,technique:T1070.001
Event logs|PowerShell script block 4104|eventid:4104,technique:T1059.001,technique:T1027
Event logs|PowerShell module logging 4103|technique:T1059.001
Event logs|Sysmon process creation|tool:sysmon,eventid:4688
Event logs|Sysmon network connection|tool:sysmon,eventid:5156,technique:T1071.001
Event logs|Sysmon process access|tool:sysmon,artifact:lsass,technique:T1003.001
Event logs|Sysmon image load|tool:sysmon,technique:T1218.011
Event logs|RDP session events|technique:T1021.001,eventid:4624
Event logs|Kerberos TGT requests|technique:T1550.002,eventid:4624
Event logs|WMI activity log|technique:T1047
Event logs|Defender detections|artifact:evtx
Event logs|AppLocker events|artifact:evtx
Event logs|EvtxECmd maps|tool:evtxecmd,artifact:evtx
Memory|pslist versus psscan|tool:volatility,artifact:svchost
Memory|malfind|tool:volatility,technique:T1055
Memory|Process hollowing|tool:volatility,technique:T1055.012,artifact:svchost,technique:T1055
Memory|DLL injection|technique:T1055,tool:volatility
Memory|Command lines in memory|tool:volatility,eventid:4688
Memory|Handles and mutexes|tool:volatility
Memory|netscan|tool:volatility,artifact:pcap
Memory|LSASS memory|artifact:lsass,technique:T1003.001
Memory|Credential dumping|technique:T1003.001,artifact:lsass,tool:sysmon
Memory|Memory acquisition|tool:volatility
Memory|VAD tree|tool:volatility,technique:T1055
Memory|Code injection indicators|technique:T1055,technique:T1055.012
Memory|Pagefile and hiberfil|tool:volatility
Memory|Parent-child process rules|artifact:svchost,technique:T1036,tool:volatility
Network|DNS log review|tool:zeek,technique:T1071.001
Network|Zeek conn.log|tool:zeek,artifact:pcap
Network|Zeek dns.log|tool:zeek
Network|Beaconing detection|tool:rita,technique:T1071.001
Network|RITA scoring|tool:rita,tool:zeek
Network|TLS and JA3|tool:zeek,technique:T1071.001
Network|PCAP triage|artifact:pcap,tool:zeek
Network|Proxy logs|technique:T1071.001
Network|Firewall 5156|eventid:5156,technique:T1071.001
Network|NetFlow basics|artifact:pcap
Network|Exfiltration over HTTPS|technique:T1071.001,tool:rita
Persistence|Run keys|technique:T1547.001,artifact:registry
Persistence|Scheduled tasks|technique:T1053.005,eventid:4698
Persistence|Auto-start services|technique:T1543.003,eventid:7045
Persistence|WMI event subscriptions|technique:T1047
Persistence|Startup folder|technique:T1547.001
Persistence|Winlogon helper|technique:T1547.001,artifact:registry
Persistence|IFEO debugger|artifact:registry,technique:T1547.001
Persistence|COM hijacking|artifact:registry
Persistence|AppInit DLLs|artifact:registry,technique:T1218.011
Lateral movement|RDP lateral movement|technique:T1021.001,eventid:4624
Lateral movement|PsExec|technique:T1569.002,technique:T1021.002,eventid:7045
Lateral movement|SMB admin shares|technique:T1021.002,eventid:4624
Lateral movement|WinRM|technique:T1021.002,technique:T1059.001
Lateral movement|Pass-the-hash|technique:T1550.002,eventid:4624,artifact:lsass
Lateral movement|Pass-the-ticket|technique:T1550.002
Lateral movement|Remote scheduled tasks|technique:T1053.005,eventid:4698
Lateral movement|DCOM execution|technique:T1047
Malware analysis|Static triage|tool:yara
Malware analysis|Strings and FLOSS|technique:T1027
Malware analysis|PE headers|technique:T1036
Malware analysis|Packers|technique:T1027
Malware analysis|YARA rules|tool:yara,technique:T1027
Malware analysis|Sandbox detonation|tool:plaso
Malware analysis|Rundll32 abuse|technique:T1218.011,eventid:4688
Malware analysis|Macro documents|technique:T1566.001,technique:T1059.001
Malware analysis|Encoded PowerShell|technique:T1059.001,technique:T1027,eventid:4104
Malware analysis|LOLBins|technique:T1218.011,technique:T1036
Malware analysis|Super timeline with Plaso|tool:plaso,artifact:mft,artifact:evtx
'@ -split "`r?`n" | Where-Object { $_ }

# explicit note-to-note [[wikilinks]] on top of the ones implied by shared concepts
$wiki = @(
  'Process hollowing>malfind', 'Process hollowing>pslist versus psscan', 'Process hollowing>Parent-child process rules', 'Process hollowing>Code injection indicators',
  'Prefetch>Amcache', 'Prefetch>ShimCache', 'Prefetch>Process creation 4688', 'Rundll32 abuse>Prefetch', 'Rundll32 abuse>Encoded PowerShell', 'Rundll32 abuse>Sysmon image load',
  'Encoded PowerShell>PowerShell script block 4104', 'Macro documents>Zone.Identifier', 'Macro documents>Encoded PowerShell',
  'Auto-start services>Service installed 7045', 'Credential dumping>Sysmon process access', 'Credential dumping>Pass-the-hash', 'LSASS memory>Credential dumping',
  'Beaconing detection>RITA scoring', 'Beaconing detection>Zeek conn.log', 'Beaconing detection>TLS and JA3', 'Exfiltration over HTTPS>Beaconing detection',
  'RDP lateral movement>RDP session events', 'RDP lateral movement>Logon types in 4624', 'PsExec>Service installed 7045', 'Log cleared 1102>Super timeline with Plaso',
  'Super timeline with Plaso>NTFS $MFT', 'Timestomping>NTFS $MFT', 'Timestomping>USN Journal', 'Scheduled tasks>Remote scheduled tasks', 'Run keys>Startup folder',
  'Command lines in memory>Process creation 4688', 'netscan>Firewall 5156', 'Firewall 5156>Sysmon network connection', 'YARA rules>Static triage', 'Packers>PE headers'
)

$nodes = New-Object System.Collections.Generic.List[object]
$links = New-Object System.Collections.Generic.List[object]
$mentions = @{}     # concept key -> doc count
$docByTitle = @{}
$docConcepts = @{}
$cats = [ordered]@{}
$id = 100
foreach ($line in $noteLines) {
  $p = $line -split '\|'
  $id++
  $key = "n:$id"
  $docByTitle[$p[1]] = $key
  $cs = $p[2] -split ',' | Select-Object -Unique
  $docConcepts[$key] = $cs
  $cats[$p[0]] = 1 + [int]$cats[$p[0]]
  $nodes.Add([ordered]@{ key = $key; kind = 'doc'; label = $p[1]; group = 'DFIR vault'; size = 1; noteId = $id })
  $links.Add([ordered]@{ source = $key; target = "cat:$($p[0])"; kind = 'category'; weight = 1 })
  foreach ($c in $cs) {
    $mentions[$c] = 1 + [int]$mentions[$c]
    $links.Add([ordered]@{ source = $key; target = "c:$c"; kind = 'mention'; weight = 1 })
  }
}
foreach ($line in $conceptLines) {
  $p = $line -split '\|'
  if (-not $mentions.ContainsKey($p[0])) { continue }
  $kind = ($p[0] -split ':')[0]
  $group = $null
  if ($kind -eq 'technique') { $group = 'Technique' }
  $nodes.Add([ordered]@{ key = "c:$($p[0])"; kind = $kind; label = $p[1]; group = $group; size = [math]::Log(1 + $mentions[$p[0]], 2); noteId = $null })
}
foreach ($c in $cats.Keys) { $nodes.Add([ordered]@{ key = "cat:$c"; kind = 'category'; label = $c; group = $null; size = $cats[$c]; noteId = $null }) }
foreach ($w in $wiki) {
  $a, $b = $w -split '>'
  if ($docByTitle[$a] -and $docByTitle[$b]) { $links.Add([ordered]@{ source = $docByTitle[$a]; target = $docByTitle[$b]; kind = 'wikilink'; weight = 1 }) }
  else { throw "unknown note in wikilink: $w" }
}
# concepts that share documents are drawn together (the app's 'cooccur' links, shared by at least two documents)
$pairs = @{}
foreach ($cs in $docConcepts.Values) {
  $s = @($cs | Sort-Object)
  for ($i = 0; $i -lt $s.Count; $i++) { for ($j = $i + 1; $j -lt $s.Count; $j++) { $k = $s[$i] + '|' + $s[$j]; $pairs[$k] = 1 + [int]$pairs[$k] } }
}
foreach ($k in $pairs.Keys) { if ($pairs[$k] -ge 2) { $a, $b = $k -split '\|'; $links.Add([ordered]@{ source = "c:$a"; target = "c:$b"; kind = 'cooccur'; weight = $pairs[$k] }) } }

$data = [ordered]@{ nodes = $nodes.ToArray(); links = $links.ToArray(); docsHidden = $false } | ConvertTo-Json -Depth 6 -Compress
$selectKey = $docByTitle[$Select]
if (-not $selectKey) { foreach ($line in $conceptLines) { $q = $line -split '\|'; if ($q[1] -eq $Select) { $selectKey = 'c:' + $q[0] } } }
if (-not $selectKey -and $cats.Contains($Select)) { $selectKey = 'cat:' + $Select }
if ($Select -and -not $selectKey) { throw "no such note to select: $Select" }
Write-Host ("synthetic graph: {0} notes, {1} nodes, {2} links" -f $noteLines.Count, $nodes.Count, $links.Count)

# the page: same index.html plus a script that hands over the data (the page's CSP only allows its own files) and selects a note
$shot = "window.__graph = $data;`n" + @"
(function wait() {
  if (!window.brain || !window.brain.graph()) return setTimeout(wait, 100);
  window.brain.setGraph(window.__graph, true);
})();
"@
[IO.File]::WriteAllText((Join-Path $work 'shot.js'), $shot, (New-Object Text.UTF8Encoding($false)))
$html = (Get-Content (Join-Path $work 'index.html') -Raw).Replace('<script src="boot.js"></script>', '<script src="boot.js"></script><script src="shot.js"></script>')
[IO.File]::WriteAllText((Join-Path $work 'index.html'), $html, (New-Object Text.UTF8Encoding($false)))

$outFull = [IO.Path]::GetFullPath($Out)
$profile = Join-Path $work 'edge-profile'
$url = ([Uri](Join-Path $work 'index.html')).AbsoluteUri
$port = 9300 + (Get-Random -Maximum 500)
if (Test-Path $outFull) { Remove-Item $outFull }

# headless Edge, driven over the DevTools protocol: the force layout needs real time (virtual time never advances the animation frames)
$proc = Start-Process $Edge -PassThru -ArgumentList @('--headless=new', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist',
  '--allow-file-access-from-files', "--user-data-dir=`"$profile`"", '--hide-scrollbars', '--no-first-run', "--remote-debugging-port=$port", '--window-size=1440,900', $url)
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$script:msgId = 0
function Send-Cdp([string]$Method, $Params = @{}) {
  $script:msgId++
  $id = $script:msgId
  $json = @{ id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 6 -Compress
  $bytes = [Text.Encoding]::UTF8.GetBytes($json)
  $ws.SendAsync([ArraySegment[byte]]$bytes, 'Text', $true, [Threading.CancellationToken]::None).Wait()
  $buf = New-Object byte[] 65536
  while ($true) {
    $sb = New-Object Text.StringBuilder
    do {
      $r = $ws.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None).Result
      [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
    } until ($r.EndOfMessage)
    $m = $sb.ToString() | ConvertFrom-Json
    if ($m.id -eq $id) { if ($m.error) { Write-Host ("cdp error: " + ($m.error | ConvertTo-Json -Compress)) }; return $m }
  }
}
try {
  $target = $null
  for ($i = 0; $i -lt 60 -and -not $target; $i++) {
    Start-Sleep -Milliseconds 500
    try { foreach ($x in (Invoke-RestMethod "http://127.0.0.1:$port/json")) { if ($x.type -eq 'page' -and $x.url -like '*ezf-graph-shot*') { $target = $x } } } catch { }
  }
  if (-not $target) { throw 'Edge did not open the page' }
  Write-Host ("page: " + @($target.url)[0])
  $ws.ConnectAsync([Uri][string](@($target.webSocketDebuggerUrl)[0]), [Threading.CancellationToken]::None).Wait()
  [void](Send-Cdp 'Emulation.setDeviceMetricsOverride' @{ width = 1440; height = 900; deviceScaleFactor = 1; mobile = $false })
  Start-Sleep -Seconds ([math]::Max(5, [int]($Budget / 1000)))        # the layout settles and the camera fits
  if ($selectKey) { [void](Send-Cdp 'Runtime.evaluate' @{ expression = "window.brain.focus('$selectKey')" }) }
  Start-Sleep -Seconds 3
  $pull = "(function(){var fg=window.brain.graph();var n=fg.graphData().nodes.find(function(x){return x.key==='$selectKey'});var p=fg.camera().position;var f=$Back;fg.cameraPosition({x:n.x+(p.x-n.x)*f,y:n.y+(p.y-n.y)*f,z:n.z+(p.z-n.z)*f},{x:n.x,y:n.y,z:n.z},1200)})()"
  if ($selectKey) { [void](Send-Cdp 'Runtime.evaluate' @{ expression = $pull }) }        # step back a little: the selected note and its neighbourhood in frame
  Start-Sleep -Seconds 4
  $probeR = Send-Cdp 'Runtime.evaluate' @{ expression = "JSON.stringify(window.brain.state())"; returnByValue = $true }
  $probe = $probeR | ConvertTo-Json -Depth 5 -Compress
  Write-Host "graph state: $probe"
  $png = Send-Cdp 'Page.captureScreenshot' @{ format = 'png' }
  [IO.File]::WriteAllBytes($outFull, [Convert]::FromBase64String($png.result.data))

}
finally {
  $ws.Dispose()
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Milliseconds 500
  Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path $outFull)) { throw 'no screenshot was written' }
Write-Host "wrote $outFull"
