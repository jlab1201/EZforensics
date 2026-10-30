using System.Globalization;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Processes;

/// <summary>Log an EvtxECmd row came from, as far as the process tree distinguishes them.</summary>
internal enum EvtxLog { Security, Sysmon, PowerShell, WindowsPowerShell, TaskScheduler, Wmi, Defender }

/// <summary>One EvtxECmd CSV row reduced to what the process parsers need.</summary>
internal sealed class EvtxRow
{
    public DateTime? Time;
    public string User = "", Exe = "", File = "", Payload = "";
    /// <summary>PayloadData1-6.</summary>
    public string[] Cells = { "", "", "", "", "", "" };
    /// <summary>The row's ProcessId column: the process that wrote the event (System/Execution), not necessarily the one it is about.</summary>
    public int? RowPid;

    private Dictionary<string, string>? _kv, _fields;
    /// <summary>"Key: value" pairs of the payload cells.</summary>
    public Dictionary<string, string> Kv => _kv ??= ProcessLogText.KeyValues(Cells);
    /// <summary>EventData fields of the Payload column.</summary>
    public Dictionary<string, string> Fields => _fields ??= ProcessLogText.PayloadFields(Payload);
}

/// <summary>
/// Turns EvtxECmd rows into <see cref="ProcessEventRecord"/>s. The payload layouts are the ones of the EZ Maps (evtx/Maps/*.map): every cell used here is
/// named after the PayloadData / UserName / ExecutableInfo target its map gives, and anything a map does not carry (Sysmon 3 ports, Sysmon 7 signature,
/// Security 4624 / 4663 ProcessId) comes from the raw Payload column.
/// </summary>
internal static class EvtxProcessEvents
{
    private const int MaxText = 2000;

    public static EvtxLog? Classify(string channel, string provider, int eid)
    {
        bool Has(string s) => channel.Contains(s, StringComparison.OrdinalIgnoreCase) || provider.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (channel.Length == 0 && provider.Length == 0)
        {
            if (eid is >= 4000 and < 6000) return EvtxLog.Security;
            return eid is 1 or 2 or 3 or 5 or 6 or 7 or 8 or 10 or 11 or 12 or 13 or 14 or 15 or 17 or 18 or 22 or 23 or 25 or 26 ? EvtxLog.Sysmon : null;
        }
        if (Has("Sysmon")) return EvtxLog.Sysmon;
        if (channel.Equals("Security", StringComparison.OrdinalIgnoreCase) || provider.Contains("Security-Auditing", StringComparison.OrdinalIgnoreCase)) return EvtxLog.Security;
        if (Has("TaskScheduler")) return EvtxLog.TaskScheduler;
        if (Has("WMI-Activity")) return EvtxLog.Wmi;
        if (Has("Windows Defender") || Has("Windows-Defender")) return EvtxLog.Defender;
        if (Has("Microsoft-Windows-PowerShell") || Has("PowerShellCore")) return EvtxLog.PowerShell;
        if (channel.Equals("Windows PowerShell", StringComparison.OrdinalIgnoreCase) || provider.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)) return EvtxLog.WindowsPowerShell;
        return null;
    }

    /// <summary>Events this reader handles, including the ones handled by the caller (process creation / exit).</summary>
    public static bool Wanted(EvtxLog log, int eid) => log switch
    {
        EvtxLog.Security => eid is 4688 or 4689 or 4624 or 4648 or 4656 or 4657 or 4663 or 5156 or 5158,
        EvtxLog.Sysmon => eid is 1 or 2 or 3 or 5 or 6 or 7 or 8 or 10 or 11 or 12 or 13 or 14 or 15 or 17 or 18 or 22 or 23 or 25 or 26,
        EvtxLog.PowerShell => eid is 4103 or 4104,
        EvtxLog.WindowsPowerShell => eid is 400 or 800,
        EvtxLog.TaskScheduler => eid is 129 or 200 or 201,
        EvtxLog.Wmi => eid is 5857 or 5860,
        EvtxLog.Defender => eid is 1116 or 1117,
        _ => false,
    };

    /// <summary>Events whose map does not carry everything needed: the raw Payload column is parsed for them.</summary>
    public static bool NeedsPayload(EvtxLog log, int eid) =>
        (log == EvtxLog.Sysmon && eid is 3 or 7) || (log == EvtxLog.Security && eid is 4624 or 4663);

    /// <summary>Parses everything except process creation and exit (4688 / 4689 / Sysmon 1 / 5, which the caller turns into nodes). Null when the row names no process and no path.</summary>
    public static ProcessEventRecord? Parse(EvtxLog log, int eid, EvtxRow row)
    {
        var r = log switch
        {
            EvtxLog.Sysmon => Sysmon(eid, row),
            EvtxLog.Security => Security(eid, row),
            EvtxLog.PowerShell => PowerShell(eid, row),
            EvtxLog.WindowsPowerShell => WinPowerShell(eid, row),
            EvtxLog.TaskScheduler => Task(eid, row),
            EvtxLog.Wmi => Wmi(eid, row),
            EvtxLog.Defender => Defender(eid, row),
            _ => null,
        };
        if (r == null) return null;
        if (r.Pid == null && r.Guid.Length == 0 && r.TargetPid == null && r.TargetGuid.Length == 0 && r.Paths.Count == 0) return null;
        return r;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------------------------

    private static string Cut(string s, int max = MaxText) => s.Length <= max ? s : s[..max] + "...";

    private static string Cell(EvtxRow row, int i) => ProcessLogText.StripLabel(row.Cells[i]);

    /// <summary>Value of <paramref name="key"/> from the "Key: value" cells, else the EventData field, else the label-stripped cell <paramref name="cell"/> (-1: none).</summary>
    private static string V(EvtxRow row, string key, int cell = -1)
    {
        if (row.Kv.TryGetValue(key, out var v) && v.Length > 0) return v;
        if (row.Payload.Length > 0 && row.Fields.TryGetValue(key, out var f) && f.Length > 0) return f;
        return cell >= 0 ? Cell(row, cell) : "";
    }

    private static readonly TimeFit StartFit = new(TimeRole.Start, -2, 15);

    /// <summary>A PathTime link: only a full path counts, a bare file name gives no link.</summary>
    private static List<ProcessLinkPath> OnePath(string path, TimeFit fit, string dir = "") =>
        ProcessLogText.PathKey(path).Length > 0 ? new List<ProcessLinkPath> { new(path, dir, fit) } : new List<ProcessLinkPath>();

    internal static string HashOf(EvtxRow row)
    {
        foreach (var c in row.Cells)
        {
            var h = ProcessLogText.BestHash(ProcessLogText.StripLabel(c));
            if (h.Length > 0) return h;
        }
        return "";
    }

    private static string ProtocolName(string p) => p switch { "6" => "TCP", "17" => "UDP", "1" => "ICMP", "58" => "ICMPv6", _ => p };

    /// <summary>"10.0.0.1:443" or "[::1]:443" to address and port (last colon).</summary>
    private static (string Addr, string Port) SplitEndpoint(string s)
    {
        s = s.Trim();
        int i = s.LastIndexOf(':');
        if (i <= 0) return (s, "");
        var addr = s[..i].Trim('[', ']');
        return (addr, s[(i + 1)..].Trim());
    }

    // ---- Sysmon -------------------------------------------------------------------------------------------------------------------------------------

    private static ProcessEventRecord? Sysmon(int eid, EvtxRow row)
    {
        string S(string key, int cell = -1) => V(row, key, cell);
        var pid = ProcessLogText.ParsePid(S("processid"));
        var guid = ProcessLogText.NormGuid(S("processguid"));
        var image = S("image");
        if (image.Length == 0 && eid is 3 or 23 or 25 or 26) image = ProcessLogText.ExePath(row.Exe);
        string User() => row.User.StartsWith("ParentUser:", StringComparison.OrdinalIgnoreCase) ? "" : row.User;

        ProcessEventRecord Make(ProcessEventGroup g, string title, string t1 = "", string t2 = "", string t3 = "", string t4 = "", string detail = "") => new()
        {
            Time = row.Time, EventId = eid, Log = "Sysmon", Group = g, Pid = pid, Guid = guid, Title = title, Image = image,
            T1 = t1, T2 = t2, T3 = t3, T4 = t4, User = User(), Detail = detail.Length > 0 ? detail : t1, SourceFile = row.File,
        };

        switch (eid)
        {
            case 3:
            {
                var host = S("destinationhostname", 4);
                var ip = S("destinationip", 5);
                var port = S("destinationport");
                var src = S("sourceip", 3);
                var proto = S("protocol");
                var dest = (host.Length > 0 && host != ip ? host + " " : "") + ip + (port.Length > 0 ? ":" + port : "");
                var initiated = S("initiated");
                var detail = dest.Trim() + (proto.Length > 0 ? " " + proto : "") + (initiated.Equals("false", StringComparison.OrdinalIgnoreCase) ? " (inbound)" : "");
                return new ProcessEventRecord
                {
                    Time = row.Time, EventId = eid, Log = "Sysmon", Group = ProcessEventGroup.Network, Pid = pid, Guid = guid, Title = "Network connection", Image = image,
                    T1 = host, T2 = ip, T3 = port, T4 = src, User = User(), Detail = detail, SourceFile = row.File,
                    Protocol = ProtocolName(proto).ToLowerInvariant(), SourcePort = S("sourceport"),
                };
            }
            case 22:
            {
                var q = S("queryname", 3);
                var res = S("queryresults", 5);
                return Make(ProcessEventGroup.Network, "DNS query", q, res, S("querystatus", 4), "", q + (res.Length > 0 ? " -> " + Cut(res, 300) : ""));
            }
            case 11:
            {
                var target = S("targetfilename", 3);
                return Make(ProcessEventGroup.Files, "File created", target);
            }
            case 23:
            case 26:
            {
                var target = S("targetfilename", 3);
                var archived = S("archived");
                return Make(ProcessEventGroup.Files, eid == 23 ? "File deleted" : "File delete detected", target, HashOf(row), archived.Length > 0 ? "Archived: " + archived : "", S("isexecutable"));
            }
            case 2:
            {
                var target = S("targetfilename", 3);
                var now = S("creationtimeutc", 4);
                var before = S("previouscreationtimeutc", 5);
                return Make(ProcessEventGroup.Files, "File time changed", target, now, before, "", target + " (" + before + " -> " + now + ")");
            }
            case 15:
            {
                var target = S("targetfilename", 3);
                return Make(ProcessEventGroup.Files, "File stream created", target, HashOf(row));
            }
            case 12:
            case 13:
            case 14:
            {
                var type = S("eventtype", 3);
                var key = S("targetobject", 4);
                var extra = eid == 13 ? S("details", 5) : eid == 14 ? S("newname", 5) : "";
                var title = eid == 13 ? "Registry value set" : eid == 14 ? "Registry renamed" : type.Contains("Delete", StringComparison.OrdinalIgnoreCase) ? "Registry key/value deleted" : "Registry key/value created";
                var detail = eid == 14 ? key + (extra.Length > 0 ? " -> " + extra : "") : key + (extra.Length > 0 ? " = " + extra : "");
                return Make(ProcessEventGroup.Registry, title, key, extra, type, "", detail);
            }
            case 7:
            {
                var loaded = row.Exe;
                var signed = S("signed");
                var sig = row.Fields.TryGetValue("signature", out var sg) ? sg : "";
                var status = row.Fields.TryGetValue("signaturestatus", out var ss) ? ss : "";
                var r = Make(ProcessEventGroup.Modules, "Image loaded", loaded, HashOf(row), signed.Equals("true", StringComparison.OrdinalIgnoreCase) ? "Signed" : signed.Equals("false", StringComparison.OrdinalIgnoreCase) ? "Unsigned" : "", sig,
                    loaded + (sig.Length > 0 ? " (" + sig + ")" : ""));
                r.Signed = signed.Equals("true", StringComparison.OrdinalIgnoreCase) ? true : signed.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : null;
                r.Signature = sig;
                r.SignatureStatus = status;
                return r;
            }
            case 6:
            {
                // Driver load: the map has no process cells. Drivers load in the kernel, so the event belongs to System (PID 4).
                var signed = S("signed", 3);
                var sig = S("signature", 4);
                var status = S("signaturestatus", 5);
                return new ProcessEventRecord
                {
                    Time = row.Time, EventId = 6, Log = "Sysmon", Group = ProcessEventGroup.Modules, Pid = 4, Title = "Driver loaded", Image = "System", T1 = row.Exe, T2 = HashOf(row),
                    T3 = signed.Equals("true", StringComparison.OrdinalIgnoreCase) ? "Signed" : signed.Equals("false", StringComparison.OrdinalIgnoreCase) ? "Unsigned" : "", T4 = sig,
                    Detail = row.Exe + (sig.Length > 0 ? " (" + sig + ")" : ""), SourceFile = row.File,
                    Signed = signed.Equals("true", StringComparison.OrdinalIgnoreCase) ? true : signed.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : null,
                    Signature = sig, SignatureStatus = status,
                };
            }
            case 17:
            case 18:
            {
                var pipe = S("pipename", 1);
                return Make(ProcessEventGroup.Threats, eid == 17 ? "Pipe created" : "Pipe connected", pipe);
            }
            case 25:
            {
                var type = S("type", 5);
                return Make(ProcessEventGroup.Threats, "Process tampering", type, "", "", "", "Process image tampering: " + type);
            }
            case 8:
            case 10:
                return Access(eid, row);
        }
        return null;
    }

    /// <summary>Sysmon 8 (remote thread) and 10 (process access): linked to the source ("accessed") and the target ("accessed by").</summary>
    private static ProcessEventRecord Access(int eid, EvtxRow row)
    {
        var sPid = ProcessLogText.ParsePid(V(row, "sourceprocessid"));
        var sGuid = ProcessLogText.NormGuid(V(row, "sourceprocessguid"));
        var sImage = V(row, "sourceimage", 3);
        var tPid = ProcessLogText.ParsePid(V(row, "targetprocessid"));
        var tGuid = ProcessLogText.NormGuid(V(row, "targetprocessguid"));
        var tImage = V(row, "targetimage", 5);
        string t2 = eid == 10 ? V(row, "grantedaccess", 1) : V(row, "startaddress", 0);
        var callTrace = row.Exe.StartsWith("CallTrace", StringComparison.OrdinalIgnoreCase) ? ProcessLogText.AfterColon(row.Exe) : row.Exe;
        string t4 = eid == 10 ? Cut(callTrace, 400) : V(row, "startfunction");
        return new ProcessEventRecord
        {
            Time = row.Time, EventId = eid, Log = "Sysmon", Group = ProcessEventGroup.Threats, Pid = sPid, Guid = sGuid, Image = sImage,
            TargetPid = tPid, TargetGuid = tGuid, TargetImage = tImage,
            Title = eid == 8 ? "Remote thread created" : "Process accessed", T1 = tImage, T2 = t2, T3 = tPid?.ToString(CultureInfo.InvariantCulture) ?? "", T4 = t4,
            User = row.User, Detail = (eid == 8 ? "remote thread in " : "accessed ") + tImage + (t2.Length > 0 ? (eid == 8 ? " at " : " (access ") + t2 + (eid == 8 ? "" : ")") : ""),
            SourceFile = row.File,
        };
    }

    // ---- Security -----------------------------------------------------------------------------------------------------------------------------------

    private static ProcessEventRecord? Security(int eid, EvtxRow row)
    {
        switch (eid)
        {
            case 4624:
            {
                var pid = ProcessLogText.ParsePid(V(row, "processid"));
                var image = row.Exe;
                var target = Cell(row, 0);
                var type = row.Cells[1].Replace("LogonType", "", StringComparison.OrdinalIgnoreCase).Trim(' ', ':');
                return new ProcessEventRecord
                {
                    Time = row.Time, EventId = eid, Log = "Security", Group = ProcessEventGroup.Identity, Pid = pid, Image = image, Title = "Logon",
                    T1 = target, T2 = type, T3 = Cell(row, 2), T4 = Cell(row, 3), User = row.User,
                    Detail = $"Logon type {type} as {target}" + (Cell(row, 4).Length > 0 ? " via " + Cell(row, 4) : ""), SourceFile = row.File,
                };
            }
            case 4648:
            {
                var pid = ProcessLogText.ParsePid(V(row, "pid", 2));
                var image = row.Exe;
                var target = Cell(row, 0);
                var server = V(row, "targetservername", 1);
                return new ProcessEventRecord
                {
                    Time = row.Time, EventId = eid, Log = "Security", Group = ProcessEventGroup.Identity, Pid = pid, Image = image, Title = "Logon with explicit credentials",
                    T1 = target, T2 = server, User = row.User, Detail = $"explicit credentials for {target}" + (server.Length > 0 ? " on " + server : ""), SourceFile = row.File,
                };
            }
            case 4656:
            {
                var (image, pid) = ProcessLogText.SplitTrailingPid(row.Exe);
                var type = V(row, "objecttype", 0);
                var name = V(row, "objectname", 1);
                var access = V(row, "accesslist", 4);
                return new ProcessEventRecord
                {
                    Time = row.Time, EventId = eid, Log = "Security", Group = ObjectGroup(type), Pid = pid, Image = image, Title = type.Equals("Key", StringComparison.OrdinalIgnoreCase) ? "Registry handle requested" : "Object handle requested",
                    T1 = name, T2 = access, T3 = type, User = row.User, Detail = name, SourceFile = row.File,
                };
            }
            case 4657:
            {
                var (image, pid) = ProcessLogText.SplitTrailingPid(row.Exe);
                var key = V(row, "registrykey", 2);
                var value = V(row, "valuename", 3);
                var op = V(row, "operationtype", 1);
                var newVal = ProcessLogText.AfterColon(row.Cells[5]);
                return new ProcessEventRecord
                {
                    Time = row.Time, EventId = eid, Log = "Security", Group = ProcessEventGroup.Registry, Pid = pid, Image = image, Title = "Registry value modified",
                    T1 = key, T2 = newVal, T3 = op, T4 = value, User = row.User, Detail = key + (value.Length > 0 ? "\\" + value : "") + (newVal.Length > 0 ? " = " + newVal : ""),
                    SourceFile = row.File,
                };
            }
            case 4663:
            {
                var pid = ProcessLogText.ParsePid(V(row, "processid"));
                var image = row.Exe;
                var type = V(row, "objecttype", 1);
                var name = V(row, "objectname", 2);
                var access = V(row, "accesslist", 3);
                return new ProcessEventRecord
                {
                    Time = row.Time, EventId = eid, Log = "Security", Group = ObjectGroup(type), Pid = pid, Image = image, Title = "Object accessed",
                    T1 = name, T2 = access, T3 = type, User = row.User, Detail = name, SourceFile = row.File,
                };
            }
            case 5156:
            {
                var pid = ProcessLogText.ParsePid(V(row, "pid", 3));
                var image = row.Exe;
                var (src, srcPort) = SplitEndpoint(V(row, "source", 0));
                var (dst, dstPort) = SplitEndpoint(V(row, "dest", 1));
                var proto = ProtocolName(V(row, "protocol", 2));
                var dir = V(row, "direction", 4);
                return new ProcessEventRecord
                {
                    Time = row.Time, EventId = eid, Log = "Security", Group = ProcessEventGroup.Network, Pid = pid, Image = image, Title = "Connection allowed (firewall)",
                    T1 = "", T2 = dst, T3 = dstPort, T4 = src, User = row.User, Detail = $"{dst}:{dstPort} {proto} {dir}".Trim(), SourceFile = row.File,
                    Protocol = proto.ToLowerInvariant(), SourcePort = srcPort,
                };
            }
            case 5158:
            {
                var pid = ProcessLogText.ParsePid(V(row, "pid", 2));
                var image = row.Exe;
                var (src, srcPort) = SplitEndpoint(V(row, "source", 0));
                var proto = ProtocolName(V(row, "protocol", 1));
                return new ProcessEventRecord
                {
                    Time = row.Time, EventId = eid, Log = "Security", Group = ProcessEventGroup.Network, Pid = pid, Image = image, Title = "Port bind (firewall)",
                    T1 = "", T2 = "", T3 = srcPort, T4 = src, User = row.User, Detail = $"bind {src}:{srcPort} {proto}".Trim(), SourceFile = row.File,
                };
            }
        }
        return null;
    }

    private static ProcessEventGroup ObjectGroup(string objectType) =>
        objectType.Equals("Key", StringComparison.OrdinalIgnoreCase) ? ProcessEventGroup.Registry : ProcessEventGroup.Files;

    // ---- the other logs -----------------------------------------------------------------------------------------------------------------------------

    private static ProcessEventRecord? PowerShell(int eid, EvtxRow row)
    {
        var pid = row.RowPid;
        if (eid == 4104)
        {
            var path = V(row, "path", 0);
            var text = Cut(ProcessLogText.AfterColon(row.Cells[1]).Length > 0 && row.Cells[1].StartsWith("ScriptBlockText", StringComparison.OrdinalIgnoreCase) ? ProcessLogText.AfterColon(row.Cells[1]) : row.Cells[1]);
            return new ProcessEventRecord
            {
                Time = row.Time, EventId = eid, Log = "PowerShell", Group = ProcessEventGroup.Scripts, Pid = pid, Title = "Script block", T1 = text, T2 = path,
                User = row.User, Detail = Cut(text, 500), SourceFile = row.File,
            };
        }
        var host = ProcessLogText.AfterSeparator(row.Cells[1]);
        var cmdName = ProcessLogText.AfterSeparator(row.Cells[0]);
        var payload = Cut(ProcessLogText.AfterSeparator(row.Cells[5]), 600);
        var image = ProcessLogText.ExePath(host);
        return new ProcessEventRecord
        {
            Time = row.Time, EventId = eid, Log = "PowerShell", Group = ProcessEventGroup.Scripts, Pid = pid, Image = image, Title = "Pipeline executed",
            T1 = cmdName, T2 = payload, T3 = host, User = row.User, Detail = cmdName + (payload.Length > 0 ? ": " + Cut(payload, 300) : ""), SourceFile = row.File,
        };
    }

    private static ProcessEventRecord? WinPowerShell(int eid, EvtxRow row)
    {
        var pid = row.RowPid;
        var host = row.Cells[0].Trim();
        var image = ProcessLogText.ExePath(host);
        return new ProcessEventRecord
        {
            Time = row.Time, EventId = eid, Log = "Windows PowerShell", Group = ProcessEventGroup.Scripts, Pid = pid, Image = image,
            Title = eid == 400 ? "PowerShell engine started" : "PowerShell pipeline executed", T1 = Cut(host, 800), T2 = Cell(row, 1), User = row.User, Detail = Cut(host, 500), SourceFile = row.File,
        };
    }

    private static ProcessEventRecord? Task(int eid, EvtxRow row)
    {
        var task = V(row, "task", 0);
        var image = ProcessLogText.ExePath(ProcessLogText.ExpandEnvironmentTokens(row.Exe));
        if (eid == 129)
        {
            var pid = ProcessLogText.ParsePid(V(row, "processid", 1));
            return new ProcessEventRecord
            {
                Time = row.Time, EventId = eid, Log = "Task Scheduler", Group = ProcessEventGroup.Persistence, Pid = pid, Image = image, Title = "Task launched a process",
                T1 = task, T2 = row.Exe, Detail = task + " -> " + row.Exe, SourceFile = row.File,
            };
        }
        return new ProcessEventRecord
        {
            Time = row.Time, EventId = eid, Log = "Task Scheduler", Group = ProcessEventGroup.Persistence, Image = image, Title = eid == 200 ? "Task action started" : "Task action completed",
            T1 = task, T2 = row.Exe, T3 = V(row, "instanceid", 1), Detail = task + " -> " + row.Exe, SourceFile = row.File, Paths = OnePath(image, StartFit),
        };
    }

    private static ProcessEventRecord? Wmi(int eid, EvtxRow row)
    {
        if (eid == 5857)
        {
            // The provider host (wmiprvse) PID is in the event data; the row's ProcessId is the WMI service that wrote it.
            var pid = ProcessLogText.ParsePid(V(row, "pid", 0));
            var provider = V(row, "path", 1);
            return new ProcessEventRecord
            {
                Time = row.Time, EventId = eid, Log = "WMI", Group = ProcessEventGroup.Persistence, Pid = pid, Title = "WMI provider started", T1 = provider,
                User = row.User, Detail = provider, SourceFile = row.File,
            };
        }
        var client = ProcessLogText.ParsePid(V(row, "pid", 1));
        var query = V(row, "query", 0);
        return new ProcessEventRecord
        {
            Time = row.Time, EventId = eid, Log = "WMI", Group = ProcessEventGroup.Persistence, Pid = client, Title = "Temporary WMI event subscription", T1 = query,
            T2 = V(row, "clientmachine", 2), User = V(row, "user", 3), Detail = query, SourceFile = row.File,
        };
    }

    private static readonly Regex DefenderPrefix = new(@"^(?:file|containerfile|process|regkey|runkey)[:_]+(?:_)?", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static ProcessEventRecord? Defender(int eid, EvtxRow row)
    {
        // The row's ProcessId is MsMpEng (the writer), not the process the detection is about: link by the real-time Process name and by the detected file.
        var process = ProcessLogText.AfterColon(row.Cells[3]);
        var detected = row.Exe.Trim();
        int arrow = detected.IndexOf("->", StringComparison.Ordinal);
        var fileText = arrow > 0 ? detected[..arrow].Trim() : detected;
        string file;
        try { file = DefenderPrefix.Replace(fileText, "").Trim(); }
        catch (RegexMatchTimeoutException) { file = fileText; }
        var threat = ProcessLogText.AfterColon(row.Cells[0]);
        var category = ProcessLogText.AfterColon(row.Cells[1]);
        var paths = new List<ProcessLinkPath>();
        if (ProcessLogText.PathKey(process).Length > 0) paths.Add(new ProcessLinkPath(process, "", TimeFit.During));
        if (ProcessLogText.PathKey(file).Length > 0) paths.Add(new ProcessLinkPath(file, ProcessEvent.FileDetected, TimeFit.During));
        return new ProcessEventRecord
        {
            Time = row.Time, EventId = eid, Log = "Defender", Group = ProcessEventGroup.Threats, Image = process.Length > 0 ? process : file,
            Title = eid == 1116 ? "Malware detected" : "Malware action taken", T1 = threat, T2 = file, T3 = category, User = ProcessLogText.AfterColon(row.User),
            Detail = threat + (file.Length > 0 ? " in " + file : ""), SourceFile = row.File, Paths = paths,
        };
    }
}
