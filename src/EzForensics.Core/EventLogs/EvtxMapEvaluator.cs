using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace EzForensics.Core.EventLogs;

/// <summary>A field of an event record shown in the map editor: a leaf element or attribute with the XPath that selects it.</summary>
/// <param name="Label">Element / Data name (attributes as <c>@Name</c>).</param>
/// <param name="Depth">Nesting level under &lt;Event&gt; (0 = System / EventData / UserData).</param>
/// <param name="IsLeaf">False for container elements (no XPath to assign).</param>
public sealed record EvtxXmlField(string Label, string XPath, string Value, int Depth, bool IsLeaf)
{
    /// <summary>Variable name for the map (letters, digits, '_').</summary>
    public string SuggestedName
    {
        get
        {
            var n = Regex.Replace(Label.TrimStart('@'), "[^A-Za-z0-9_]", "");
            return n.Length == 0 ? "Value" : char.IsDigit(n[0]) ? "V" + n : n;
        }
    }
}

/// <summary>Evaluates EvtxECmd maps on event XML the way EvtxECmd does: XPath per value, optional Refine regex, lookups, <c>%Name%</c> substitution.</summary>
public static class EvtxMapEvaluator
{
    /// <summary>
    /// Event XML without namespaces (EvtxECmd's XPaths are written for <c>/Event/EventData/...</c>; the Windows event reader adds the
    /// events namespace). Returns the text unchanged when it is not XML.
    /// </summary>
    public static string Normalize(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var e in doc.Descendants().ToList())
            {
                e.Name = e.Name.LocalName;
                e.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
                foreach (var a in e.Attributes().Where(a => a.Name.Namespace != XNamespace.None).ToList())
                {
                    a.Remove();
                    e.SetAttributeValue(a.Name.LocalName, a.Value);
                }
                foreach (var a in e.Attributes()) a.Value = AsEvtxECmd(a.Value);
                if (!e.HasElements && e.Value.Length > 0) { var v = AsEvtxECmd(e.Value); if (v != e.Value) e.Value = v; }
            }
            // EvtxECmd writes unnamed <Data> items as one <Data>, the non-empty values joined by ", " (seen on System 6013),
            // and an array (repeated leaf elements, e.g. RestartManager 10002 <Files><File>) as one element, joined the same way.
            if (doc.Root?.Element("EventData") is { } ed)
            {
                var data = ed.Elements("Data").ToList();
                if (data.Count > 1 && data.All(d => d.Attribute("Name") == null)) Merge(data);
            }
            foreach (var part in new[] { doc.Root?.Element("EventData"), doc.Root?.Element("UserData") })
            {
                if (part == null) continue;
                foreach (var parent in part.DescendantsAndSelf().Where(p => p.HasElements).ToList())
                {
                    foreach (var run in parent.Elements().Where(c => !c.HasElements)
                                 .GroupBy(c => c.Name.LocalName + "|" + string.Join("|", c.Attributes().Select(a => a.Name.LocalName + "=" + a.Value)))
                                 .Where(g => g.Count() > 1).Select(g => g.ToList()).ToList())
                        Merge(run);
                }
            }
            return doc.Root!.ToString(SaveOptions.DisableFormatting);
        }
        catch (XmlException) { return xml; }
    }

    private static void Merge(List<XElement> items)
    {
        var first = items[0];
        var joined = new XElement(first.Name, first.Attributes(), string.Join(", ", items.Select(d => d.Value).Where(s => s.Length > 0)));
        first.AddBeforeSelf(joined);
        foreach (var d in items) d.Remove();
    }

    private static readonly Regex IsoUtc = new(@"^(\d{4}-\d\d-\d\d)T(\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?Z$", RegexOptions.Compiled);

    /// <summary>
    /// A typed value as EvtxECmd writes it in its XML (checked against EvtxECmd 2026 output): the Windows reader gives times as
    /// <c>2026-09-11T10:33:50.5000000Z</c> and booleans as <c>true</c>; EvtxECmd gives <c>2026-09-11 10:33:50.5000000</c> and <c>True</c>.
    /// </summary>
    public static string AsEvtxECmd(string v)
    {
        if (v == "true") return "True";
        if (v == "false") return "False";
        var m = IsoUtc.Match(v);
        return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}.{m.Groups[3].Value.PadRight(7, '0')}" : v;
    }

    /// <summary>The fields of an event (System, EventData, UserData), in document order, for the editor's field tree.</summary>
    public static IReadOnlyList<EvtxXmlField> Fields(string xml)
    {
        var list = new List<EvtxXmlField>();
        XElement root;
        try { root = XElement.Parse(Normalize(xml)); }
        catch (XmlException) { return list; }
        foreach (var top in root.Elements()) Walk(top, "/" + root.Name.LocalName, 0, list);
        return list;
    }

    private static void Walk(XElement e, string parentPath, int depth, List<EvtxXmlField> list)
    {
        var name = e.Name.LocalName;
        var path = parentPath + "/" + Step(e);
        var label = name == "Data" && e.Attribute("Name") is { } dn ? dn.Value : name;
        bool hasChildren = e.HasElements;
        list.Add(new EvtxXmlField(label, path, hasChildren ? "" : e.Value, depth, !hasChildren));
        foreach (var a in e.Attributes())
        {
            if (name == "Data" && a.Name.LocalName == "Name") continue;
            list.Add(new EvtxXmlField("@" + a.Name.LocalName, path + "/@" + a.Name.LocalName, a.Value, depth + 1, true));
        }
        foreach (var c in e.Elements()) Walk(c, path, depth + 1, list);
    }

    private static string Step(XElement e)
    {
        var name = e.Name.LocalName;
        if (name == "Data" && e.Attribute("Name") is { } n && !n.Value.Contains('"'))
            return $"Data[@Name=\"{n.Value}\"]";
        var same = e.Parent?.Elements(e.Name).ToList();
        return same is { Count: > 1 } ? $"{name}[{same.IndexOf(e) + 1}]" : name;
    }

    /// <summary>Null when <paramref name="xpath"/> compiles, else the parser's message.</summary>
    public static string? XPathError(string xpath)
    {
        try { XPathExpression.Compile(xpath); return null; }
        catch (XPathException ex) { return ex.Message; }
    }

    /// <summary>Value of one XPath: the first selected node's text, or the computed value of an expression. Null when nothing is selected.</summary>
    public static string? Select(XPathNavigator nav, string xpath)
    {
        object r;
        try { r = nav.Evaluate(xpath); }
        catch (XPathException) { return null; }
        switch (r)
        {
            case XPathNodeIterator it: return it.MoveNext() ? it.Current?.Value : null;
            case double d: return double.IsNaN(d) || double.IsInfinity(d) ? "" : d.ToString("R", CultureInfo.InvariantCulture);
            case bool b: return b ? "true" : "false";
            default: return r?.ToString();
        }
    }

    /// <summary>Column values of one event: column name to text, for every column the map fills.</summary>
    public static IReadOnlyDictionary<string, string> Evaluate(EvtxMap map, string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XPathNavigator nav;
        try { nav = new XPathDocument(new StringReader(Normalize(xml))).CreateNavigator(); }
        catch (XmlException) { return result; }
        foreach (var entry in map.Maps)
        {
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in entry.Values)
            {
                var val = Select(nav, v.Value) ?? "";
                if (!string.IsNullOrEmpty(v.Refine) && val.Length > 0)
                {
                    try
                    {
                        var hits = Regex.Matches(val, v.Refine, RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
                        if (hits.Count > 0) val = string.Join(" | ", hits);
                    }
                    catch (ArgumentException) { }
                }
                // EvtxECmd: no lookup on an empty value; an unknown code gives "Default (code)"
                var lookup = val.Length == 0 ? null : map.Lookups.FirstOrDefault(l => string.Equals(l.Name, v.Name, StringComparison.Ordinal));
                if (lookup != null)
                    val = lookup.Values.TryGetValue(val, out var t) ? t : lookup.Default is { Length: > 0 } d ? $"{d} ({val})" : val;
                vars[v.Name] = val;
            }
            var text = entry.PropertyValue;
            foreach (var kv in vars) text = text.Replace("%" + kv.Key + "%", kv.Value, StringComparison.Ordinal);
            result[entry.Property] = text;
        }
        return result;
    }

    /// <summary>EventRecordID of event XML (0 when missing).</summary>
    public static long RecordId(string xml)
    {
        try
        {
            var e = XElement.Parse(Normalize(xml)).Element("System")?.Element("EventRecordID");
            return long.TryParse(e?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
        catch (XmlException) { return 0; }
    }
}
