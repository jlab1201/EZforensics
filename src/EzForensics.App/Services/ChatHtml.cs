using System.Security.Cryptography;
using System.Text.Json;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;

namespace EzForensics.App.Services;

/// <summary>
/// Markdown to HTML and the transcript page for the Spectre chat. Replies and tool text contain evidence, so nothing in them may
/// ever run or navigate: raw HTML is escaped, links and images are shown as plain text, and the page carries a strict CSP.
/// </summary>
public static class ChatHtml
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()                 // <script>, <img onerror=...> and friends come out as escaped text
        .UsePipeTables()
        .UseEmphasisExtras()
        .Build();

    /// <summary>Links and images become their label; autolinks become their escaped text. No anchor, no src, no href is ever written.</summary>
    private sealed class PlainLinkRenderer : HtmlObjectRenderer<LinkInline>
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link) => renderer.WriteChildren(link);
    }

    private sealed class PlainAutolinkRenderer : HtmlObjectRenderer<AutolinkInline>
    {
        protected override void Write(HtmlRenderer renderer, AutolinkInline link) => renderer.WriteEscape(link.Url);
    }

    /// <summary>Markdown to a safe HTML fragment.</summary>
    public static string RenderMarkdown(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<LinkInlineRenderer>(new PlainLinkRenderer());
        renderer.ObjectRenderers.Replace<AutolinkInlineRenderer>(new PlainAutolinkRenderer());
        Markdown.Convert(markdown, renderer, Pipeline);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>Theme values the page needs (hex colours and font stacks).</summary>
    public sealed record Theme(bool Dark, string Window, string Surface, string SurfaceAlt, string Text, string TextSecondary,
        string Accent, string AccentText, string Border, string Selection, string Input, string Code, string FontUi, string FontMono);

    /// <summary>CSS variable name to value, for the page's <c>theme</c> message and the first paint.</summary>
    public static Dictionary<string, string> ThemeVars(Theme t) => new()
    {
        ["--bg"] = t.Window, ["--surface"] = t.Surface, ["--surfaceAlt"] = t.SurfaceAlt, ["--text"] = t.Text, ["--muted"] = t.TextSecondary,
        ["--accent"] = t.Accent, ["--accentText"] = t.AccentText, ["--border"] = t.Border, ["--selection"] = t.Selection,
        ["--input"] = t.Input, ["--code"] = t.Code, ["--fontUi"] = t.FontUi, ["--fontMono"] = t.FontMono,
    };

    /// <summary>A CSS value from the app's brushes and fonts: only characters that cannot end a declaration or open a rule.</summary>
    private static string CssSafe(string v) => new(v.Where(c => char.IsLetterOrDigit(c) || c is '#' or ' ' or ',' or '-' or '_' or '.').ToArray());

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    /// <summary>The whole transcript page. Messages arrive later as web messages (data, never script); the only script is the page's own.</summary>
    public static string BuildPage(Theme theme, string nonce)
    {
        var vars = string.Join(";", ThemeVars(theme).Select(kv => $"{kv.Key}:{CssSafe(kv.Value)}"));
        return PageTemplate.Replace("{{NONCE}}", nonce).Replace("{{VARS}}", vars).Replace("{{SCHEME}}", theme.Dark ? "dark" : "light");
    }

    // ------------------------------------------------------------------ messages to the page (PostWebMessageAsJson)

    public static string AddMessageJson(string id, string role, string? text = null, string? html = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["op"] = "add", ["id"] = id, ["role"] = role, ["text"] = text, ["html"] = html });

    public static string CardJson(string op, string id, string title, string detail, string reason, string state, string note, bool canUndo) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["op"] = op, ["id"] = id, ["role"] = "card", ["title"] = title, ["detail"] = detail, ["reason"] = reason,
            ["state"] = state, ["note"] = note, ["canUndo"] = canUndo,
        });

    /// <summary>The typing indicator (three animated dots and a small status line) on or off.</summary>
    public static string TypingJson(bool on, string? status) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["op"] = "typing", ["on"] = on, ["status"] = status ?? "" });

    public static string ClearJson() => "{\"op\":\"clear\"}";

    public static string ThemeJson(Theme t) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["op"] = "theme", ["dark"] = t.Dark, ["vars"] = ThemeVars(t).ToDictionary(kv => kv.Key, kv => CssSafe(kv.Value)),
    });

    /// <summary>Parses a message from the page. Only apply, dismiss, undo and applyAll (with a string id) and ready are accepted; anything else is null.</summary>
    public static (string Type, string Id)? ParseAction(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 512) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var type = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var id = doc.RootElement.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            return type is "apply" or "dismiss" or "undo" or "applyAll" or "ready" && (type == "ready" || !string.IsNullOrEmpty(id)) ? (type!, id ?? "") : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A static, script-free page with rendered Markdown (the Findings report preview). Same safety as the chat: raw HTML is escaped, links
    /// and images are plain text, and the CSP allows no script, no network and no images.
    /// </summary>
    public static string BuildDocument(string? markdown, Theme theme)
    {
        var vars = string.Join(";", ThemeVars(theme).Select(kv => $"{kv.Key}:{CssSafe(kv.Value)}"));
        return DocumentTemplate.Replace("{{VARS}}", vars).Replace("{{SCHEME}}", theme.Dark ? "dark" : "light").Replace("{{BODY}}", RenderMarkdown(markdown));
    }

    private const string DocumentTemplate = """
        <!doctype html>
        <html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
        <style>
        :root{ {{VARS}};color-scheme:{{SCHEME}} }
        html,body{margin:0;background:var(--surface);color:var(--text);font:13px/1.5 var(--fontUi)}
        body{padding:12px 18px;overflow-wrap:anywhere}
        h1,h2,h3{line-height:1.25;margin:1.1em 0 .4em}h1{font-size:20px}h2{font-size:16px;border-bottom:1px solid var(--border);padding-bottom:3px}h3{font-size:14px}
        table{border-collapse:collapse;margin:.6em 0;width:100%}th,td{border:1px solid var(--border);padding:3px 6px;text-align:left;vertical-align:top}th{background:var(--surfaceAlt)}
        code,pre{font-family:var(--fontMono);font-size:12px;background:var(--code)}pre{padding:8px;overflow-x:auto;border-radius:4px}code{padding:0 3px;border-radius:3px}
        blockquote{margin:.5em 0;padding:2px 10px;border-left:3px solid var(--accent);color:var(--muted)}
        ::-webkit-scrollbar{width:8px;height:8px}::-webkit-scrollbar-thumb{background:var(--border);border-radius:4px}
        </style></head><body>
        {{BODY}}
        </body></html>
        """;

    private const string PageTemplate = """
        <!doctype html>
        <html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{{NONCE}}'">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <style>
        :root{ {{VARS}};color-scheme:{{SCHEME}};--spFill:color-mix(in srgb,var(--accent) 12%,var(--surface));--spBorder:color-mix(in srgb,var(--accent) 38%,var(--surface)) }
        *{box-sizing:border-box}
        html,body{margin:0;background:var(--bg);color:var(--text);font:13px/1.45 var(--fontUi);overflow-x:hidden}
        body{padding:8px 10px}
        ::-webkit-scrollbar{width:8px;height:8px}
        ::-webkit-scrollbar-track,::-webkit-scrollbar-corner{background:transparent}
        ::-webkit-scrollbar-thumb{background:var(--border);border-radius:4px}
        ::-webkit-scrollbar-thumb:hover{background:var(--muted)}
        #log{display:flex;flex-direction:column;gap:10px;min-width:0}
        .msg{position:relative;max-width:90%;min-width:0;padding:6px 10px;border-radius:8px;background:var(--surfaceAlt);overflow-wrap:anywhere;word-break:break-word}
        .msg.you{align-self:flex-end;background:var(--selection);white-space:pre-wrap}
        .msg.typing{align-self:flex-start}
        .msg.spectre{background:var(--spFill);border:1px solid var(--spBorder);color:var(--text)}
        .row{align-self:flex-start;max-width:90%;min-width:0}.row>.msg{max-width:100%}
        .msg.typing::before{content:"";position:absolute;left:-6px;top:9px;border:6px solid transparent;border-left:0;border-right-color:var(--surfaceAlt)}
        .msg.spectre::before{content:"";position:absolute;left:-7px;top:8px;border:6px solid transparent;border-left:0;border-right-color:var(--spBorder)}
        .msg.spectre::after{content:"";position:absolute;left:-5px;top:9px;border:5px solid transparent;border-left:0;border-right-color:var(--spFill)}
        .msg.you::before{content:"";position:absolute;right:-6px;top:9px;border:6px solid transparent;border-right:0;border-left-color:var(--selection)}
        .msg.step{align-self:flex-start;background:transparent;color:var(--muted);font-size:12px;padding:0 2px 0 14px;max-width:100%;position:relative}
        .msg.step::before{content:"\203A";position:absolute;left:3px;top:0}
        .msg.status{align-self:flex-start;background:transparent;color:var(--muted);font-size:12px;padding:0 2px;max-width:100%}
        .msg.card{align-self:stretch;max-width:100%;border:1px solid var(--accent)}
        .who{font-size:11px;font-weight:600;color:var(--muted);margin:0 0 2px 4px}
        .dots{display:flex;gap:4px;padding:5px 0}
        .dots i{width:6px;height:6px;border-radius:50%;background:var(--muted);animation:blink 1.2s infinite ease-in-out both}
        .dots i:nth-child(2){animation-delay:.2s}.dots i:nth-child(3){animation-delay:.4s}
        @keyframes blink{0%,80%,100%{opacity:.25;transform:scale(.8)}40%{opacity:1;transform:scale(1)}}
        .tstatus{color:var(--muted);font-size:11px;margin-top:1px}
        .md>:first-child{margin-top:0}.md>:last-child{margin-bottom:0}
        .md p{margin:.4em 0}.md ul,.md ol{margin:.4em 0;padding-left:1.4em}
        .md h1,.md h2,.md h3,.md h4{font-size:13px;margin:.6em 0 .2em}
        .md code,.detail{font-family:var(--fontMono);font-size:12px}
        .md code{background:var(--code);padding:0 3px;border-radius:3px}
        .md pre{background:var(--code);border:1px solid var(--border);border-radius:5px;padding:6px 8px;margin:.5em 0;white-space:pre-wrap;overflow-wrap:anywhere}
        .md pre code{background:none;padding:0}
        .md table{border-collapse:collapse;margin:.4em 0;font-size:12px;display:block;overflow-x:auto;max-width:100%}.md th,.md td{border:1px solid var(--border);padding:2px 6px}
        .md blockquote{margin:.4em 0;padding-left:8px;border-left:3px solid var(--border);color:var(--muted)}
        .ctitle{font-weight:600}.detail{white-space:pre-wrap;margin-top:2px}.reason,.note{color:var(--muted);font-size:12px;margin-top:3px}
        .btns{display:flex;gap:6px;margin-top:6px}
        .applyall{align-self:stretch;display:flex;align-items:center;gap:8px}
        .applyall span{color:var(--muted);font-size:12px}
        button{font:inherit;color:var(--text);background:var(--surface);border:1px solid var(--border);border-radius:4px;padding:2px 10px;cursor:pointer}
        button.primary{background:var(--accent);border-color:var(--accent);color:var(--accentText)}
        button:hover{filter:brightness(1.12)}
        </style></head>
        <body><div id="log"></div>
        <script nonce="{{NONCE}}">
        (function(){
          var log=document.getElementById('log'), nodes={}, typing=null, stick=true, group=null;
          function el(t,c,txt){var e=document.createElement(t);if(c)e.className=c;if(txt!=null)e.textContent=txt;return e;}
          function post(o){try{window.chrome.webview.postMessage(o);}catch(e){}}
          function near(){return window.innerHeight+window.scrollY>=document.documentElement.scrollHeight-40;}
          function bottom(){window.scrollTo(0,document.documentElement.scrollHeight);}
          function follow(){if(stick)bottom();}
          function button(label,type,id,primary){var b=el('button',primary?'primary':'',label);b.addEventListener('click',function(){post({type:type,id:id});});return b;}
          function fillCard(n,m){
            n.textContent='';n.setAttribute('data-state',m.state||'');
            n.appendChild(el('div','ctitle',m.title));
            if(m.detail)n.appendChild(el('div','detail',m.detail));
            if(m.reason)n.appendChild(el('div','reason',m.reason));
            if(m.state==='pending'){var b=el('div','btns');b.appendChild(button('Apply','apply',m.id,true));b.appendChild(button('Dismiss','dismiss',m.id,false));n.appendChild(b);}
            else if(m.canUndo){var u=el('div','btns');u.appendChild(button('Undo','undo',m.id,false));n.appendChild(u);}
            if(m.note)n.appendChild(el('div','note',m.note));
          }
          function bar(g){
            if(!g)return;
            var pend=g.ids.filter(function(i){return nodes[i]&&nodes[i].getAttribute('data-state')==='pending';});
            if(pend.length<2){if(g.bar){g.bar.remove();g.bar=null;}return;}
            if(!g.bar)g.bar=el('div','applyall');
            g.bar.textContent='';
            g.bar.appendChild(button('Apply all ('+pend.length+')','applyAll',pend[0],true));
            g.bar.appendChild(el('span','','Applies the suggestions above, in order'));
            nodes[g.ids[g.ids.length-1]].after(g.bar);
          }
          function add(m){
            var n;
            if(m.role==='card'){
              n=el('div','msg card');fillCard(n,m);
              if(!group)group={ids:[],bar:null};
              group.ids.push(m.id);n._g=group;
            }
            else{
              if(m.role!=='step')group=null;   // a step line stays inside its reply's run of cards
              if(m.role==='status'){n=el('div','msg status',m.text);}
              else if(m.role==='step'){n=el('div','msg step',m.text);}
              else if(m.role==='you'){n=el('div','msg you',m.text);}
              else{n=el('div','row');n.appendChild(el('div','who','Spectre'));var b=el('div','msg spectre');var d=el('div','md');d.innerHTML=m.html||'';b.appendChild(d);n.appendChild(b);}
            }
            log.appendChild(n);nodes[m.id]=n;
            if(m.role==='card')bar(group);
            if(typing)log.appendChild(typing);
            stick=true;bottom();
          }
          function setTyping(m){
            if(!m.on){if(typing){typing.remove();typing=null;follow();}return;}
            if(!typing){
              typing=el('div','msg typing');
              var d=el('div','dots');d.appendChild(el('i'));d.appendChild(el('i'));d.appendChild(el('i'));
              typing.appendChild(d);typing.appendChild(el('div','tstatus',''));
              log.appendChild(typing);
            }
            typing.lastChild.textContent=m.status||'';
            typing.lastChild.style.display=m.status?'block':'none';
            follow();
          }
          function theme(m){
            var v=m.vars||{};
            for(var k in v){if(/^--[A-Za-z]+$/.test(k))document.documentElement.style.setProperty(k,v[k]);}
            if(typeof m.dark==='boolean')document.documentElement.style.colorScheme=m.dark?'dark':'light';
          }
          window.chrome.webview.addEventListener('message',function(e){
            var m=e.data;if(!m||typeof m!=='object')return;
            if(m.op==='add')add(m);
            else if(m.op==='card'&&nodes[m.id]){fillCard(nodes[m.id],m);bar(nodes[m.id]._g);follow();}
            else if(m.op==='typing')setTyping(m);
            else if(m.op==='clear'){log.textContent='';nodes={};typing=null;group=null;stick=true;}
            else if(m.op==='theme')theme(m);
          });
          window.addEventListener('scroll',function(){stick=near();});
          window.addEventListener('resize',follow);
          if(window.ResizeObserver)new ResizeObserver(follow).observe(log);
          document.addEventListener('contextmenu',function(e){e.preventDefault();});
          post({type:'ready'});
        })();
        </script></body></html>
        """;
}
