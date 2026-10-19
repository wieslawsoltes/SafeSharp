using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SafeSharp.Core;

namespace SafeSharp.Reporting;

/// <summary>Writes a self-contained, source-safe HTML inspector containing the computed analysis.</summary>
public sealed class HtmlReportWriter : IAnalysisReportWriter
{
    public void Write(AnalysisReport report, TextWriter writer, CancellationToken cancellationToken = default)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        cancellationToken.ThrowIfCancellationRequested();
        writer.Write("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        writer.Write("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'\"><title>");
        writer.Write(ReportText.Html(report.Title)); writer.Write("</title><style>"); writer.Write(Styles);
        writer.Write("</style></head><body><header><div class=\"brand\"><span class=\"brand-mark\">S</span><div><strong>SafeSharp</strong><span>OWNERSHIP INSPECTOR</span></div></div><span class=\"version\">v");
        writer.Write(ReportText.Html(report.ToolVersion)); writer.Write(" · report "); writer.Write(AnalysisReport.SchemaVersion);
        writer.Write("</span></header><main><div class=\"page-heading\"><p class=\"eyebrow\">C# · ROSLYN · BORROW ANALYSIS</p><h1>");
        writer.Write(ReportText.Html(report.Title));
        writer.Write("</h1><p>Inspect control flow, ownership transitions, loan liveness, and source-grounded evidence.</p></div><div class=\"stats\">");
        Stat(writer, "Methods", report.Results.Length);
        Stat(writer, "Basic blocks", report.Results.Sum(result => result.Graph.Blocks.Length));
        Stat(writer, "Diagnostics", report.Diagnostics.Length);
        Stat(writer, "Errors", report.Diagnostics.Count(diagnostic => diagnostic.Severity == ReportSeverity.Error));
        writer.Write("</div>");
        if (!report.AnalysisComplete)
            writer.Write("<div class=\"notice warning\" role=\"alert\"><strong>Analysis incomplete.</strong> At least one analysis did not finish within its configured limits. An absence of further diagnostics is not a successful verification.</div>");
        else
            writer.Write("<div class=\"notice\">The configured analyses completed. Findings apply to the modeled C# subset and declared contracts. Abstract join states and provenance do not constitute a whole-program memory-safety proof.</div>");
        writer.Write("<section class=\"panel\"><div class=\"panel-heading\"><h2>Diagnostics</h2><span class=\"badge\">configured severity</span></div>");
        WriteDiagnostics(writer, report.Diagnostics, cancellationToken);
        writer.Write("</section><section class=\"panel inspector\"><div class=\"panel-heading\"><h2>Analysis explorer</h2><span class=\"badge\">actual engine snapshots</span></div><div class=\"toolbar\"><label for=\"method-picker\">Method</label><select id=\"method-picker\"><option value=\"all\">All methods</option>");
        for (var method = 0; method < report.Results.Length; method++)
        {
            writer.Write("<option value=\""); writer.Write(ReportText.Number(method)); writer.Write("\">");
            writer.Write(ReportText.Html(report.Results[method].Graph.Name)); writer.Write("</option>");
        }
        writer.Write("</select><label for=\"method-filter\">Search</label><input id=\"method-filter\" type=\"search\" placeholder=\"Filter method names\"><button type=\"button\" id=\"zoom-out\" aria-label=\"Zoom graph out\">−</button><button type=\"button\" id=\"zoom-reset\">100%</button><button type=\"button\" id=\"zoom-in\" aria-label=\"Zoom graph in\">+</button></div><noscript><p class=\"notice\">All graphs and state tables remain available without JavaScript.</p></noscript>");
        if (report.Results.IsEmpty)
            writer.Write("<p class=\"empty\">No method graphs were supplied. Diagnostics above may describe type-level or compilation-level analysis.</p>");
        for (var method = 0; method < report.Results.Length; method++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteMethod(writer, report.Results[method], method, cancellationToken);
        }
        writer.Write("<p id=\"no-methods\" class=\"empty\" hidden>No methods match the filter.</p></section><footer>SafeSharp · Deterministic report · UTF-16 source coordinates · No network dependencies. CFG edges are the supplied frontend graph; exceptional behavior is limited to that frontend's model.</footer></main><script>");
        writer.Write(Script); writer.Write("</script></body></html>\n");
    }

    private static void WriteDiagnostics(TextWriter writer, IEnumerable<ReportDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var any = false;
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            any = true;
            writer.Write("<article class=\"diagnostic\"><div class=\"diagnostic-top\"><span class=\"severity ");
            writer.Write(diagnostic.Severity.ToString().ToLowerInvariant()); writer.Write("\">");
            writer.Write(diagnostic.Severity.ToString().ToUpperInvariant()); writer.Write("</span><strong>");
            writer.Write(ReportText.Html(diagnostic.Id)); writer.Write("</strong>");
            if (diagnostic.Certainty.HasValue)
            {
                writer.Write("<span class=\"badge\">"); writer.Write(diagnostic.Certainty.Value.ToString()); writer.Write("</span>");
            }
            writer.Write("</div><p>"); writer.Write(ReportText.Html(diagnostic.Message)); writer.Write("</p><code class=\"source\">");
            writer.Write(ReportText.Html(ReportText.Site(diagnostic.Site))); writer.Write("</code>");
            if (!diagnostic.Properties.IsEmpty)
            {
                var witness = DependencyWitness.FromDiagnostic(diagnostic);
                writer.Write("<details class=\"evidence\"><summary>");
                writer.Write(witness is null ? "Diagnostic properties" : "Service dependency witness and diagnostic properties");
                writer.Write("</summary>");
                if (witness is not null) WriteDependencyWitness(writer, witness);
                TableStart(writer, "Compiler diagnostic metadata", "Property", "Value");
                foreach (var property in diagnostic.Properties.OrderBy(property => property.Key, StringComparer.Ordinal))
                    Row(writer, property.Key, property.Value ?? "(null)");
                TableEnd(writer);
                writer.Write("</details>");
            }
            if (!diagnostic.Evidence.IsEmpty || !diagnostic.RelatedLocations.IsEmpty)
            {
                writer.Write("<details class=\"evidence\"><summary>Ownership provenance and related locations</summary><p class=\"muted\">These are abstract-analysis explanations. Events from joined paths need not form a single executable trace.</p><ol>");
                foreach (var evidence in diagnostic.Evidence)
                {
                    writer.Write("<li><strong>"); writer.Write(ReportText.Html(evidence.Kind)); writer.Write("</strong> · ");
                    writer.Write(ReportText.Html(evidence.Message)); writer.Write("<code class=\"source\">");
                    writer.Write(ReportText.Html(ReportText.Site(evidence.Site))); writer.Write("</code></li>");
                }
                writer.Write("</ol>");
                foreach (var related in diagnostic.RelatedLocations)
                {
                    writer.Write("<code class=\"source\">"); writer.Write(ReportText.Html(ReportText.Site(related))); writer.Write("</code>");
                }
                writer.Write("</details>");
            }
            writer.Write("</article>");
        }
        if (!any) writer.Write("<p class=\"empty\">No diagnostics reported by the configured analyses.</p>");
    }

    private static void WriteDependencyWitness(TextWriter writer, DependencyWitness witness)
    {
        writer.Write("<p class=\"muted\">The service analyzer recorded this dependency path. Labels preserve the reported implementation, service identity, and registration lifetime.</p><ol class=\"dependency-witness\" aria-label=\"Reported service dependency path\">");
        foreach (var step in witness.Steps)
        {
            writer.Write("<li><div class=\"dependency-node\"><strong><code>"); writer.Write(ReportText.Html(step.Implementation));
            writer.Write("</code></strong>");
            if (step.Service is not null && step.Service != step.Implementation)
            {
                writer.Write("<span class=\"muted\">Service <code>"); writer.Write(ReportText.Html(step.Service)); writer.Write("</code></span>");
            }
            if (step.Lifetime is not null)
            {
                writer.Write("<span class=\"badge\">"); writer.Write(ReportText.Html(step.Lifetime)); writer.Write("</span>");
            }
            writer.Write("</div></li>");
        }
        writer.Write("</ol>");
    }

    private static void WriteMethod(TextWriter writer, AnalysisResult result, int method,
        CancellationToken cancellationToken)
    {
        writer.Write("<article class=\"method\" data-method=\""); writer.Write(ReportText.Number(method));
        writer.Write("\" data-name=\""); writer.Write(ReportText.Html(result.Graph.Name));
        writer.Write("\"><div class=\"method-heading\"><h3>"); writer.Write(ReportText.Html(result.Graph.Name));
        writer.Write("</h3><p><code>"); writer.Write(ReportText.Html(ReportText.Site(result.Graph.Site)));
        writer.Write("</code> · "); writer.Write(ReportText.Number(result.Iterations)); writer.Write(" iterations · ");
        writer.Write(ReportText.Number(result.Issues.Length)); writer.Write(" raw engine findings");
        if (result.BudgetExceeded) writer.Write(" · <strong class=\"incomplete\">BUDGET EXCEEDED</strong>");
        writer.Write("</p></div>");
        HtmlCfgRenderer.Write(writer, result, method, cancellationToken);
        writer.Write("<p class=\"graph-hint\">Select a block to inspect its instructions. Scroll to pan. Graph previews are abbreviated; complete instructions and captured states follow.</p>");
        var snapshots = result.Blocks.ToDictionary(block => block.Id);
        foreach (var block in result.Graph.Blocks.OrderBy(block => block.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write("<section class=\"block\" id=\""); writer.Write(ReportText.BlockId(method, block.Id));
            writer.Write("\"><h4>Block B"); writer.Write(ReportText.Number(block.Id)); writer.Write(" <span class=\"muted\">→ ");
            writer.Write(ReportText.Html(block.Successors.IsEmpty ? "exit" : string.Join(", ", block.Successors.OrderBy(id => id).Select(id => "B" + ReportText.Number(id)))));
            writer.Write("</span></h4>");
            if (snapshots.TryGetValue(block.Id, out var snapshot))
            {
                Liveness(writer, "Live in", snapshot.LiveIn);
                Liveness(writer, "Live out", snapshot.LiveOut);
                writer.Write("<details class=\"snapshot\"><summary>Block entry and exit states</summary><div class=\"states\"><div><h5>Entry</h5>");
                WriteState(writer, snapshot.Entry);
                writer.Write("</div><div><h5>Exit</h5>"); WriteState(writer, snapshot.Exit);
                writer.Write("</div></div></details>");
                foreach (var step in snapshot.Steps.OrderBy(step => step.Index))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write("<details class=\"instruction\"><summary><span class=\"instruction-index\">");
                    writer.Write(ReportText.Number(step.Index)); writer.Write("</span><code>");
                    writer.Write(ReportText.Html(ReportText.Instruction(step.Instruction))); writer.Write("</code></summary><div class=\"instruction-body\"><code class=\"source\">");
                    writer.Write(ReportText.Html(ReportText.Site(step.Instruction.Site))); writer.Write("</code>");
                    Liveness(writer, "Live before", step.LiveBefore);
                    Liveness(writer, "Live after", step.LiveAfter);
                    writer.Write("<div class=\"states\"><div><h5>Before</h5>"); WriteState(writer, step.Before);
                    writer.Write("</div><div><h5>After</h5>"); WriteState(writer, step.After);
                    writer.Write("</div></div></div></details>");
                }
            }
            else
            {
                writer.Write("<p class=\"muted\">No captured state: this block may be unreachable, or snapshot capture was disabled.</p><ol class=\"instructions\">");
                foreach (var instruction in block.Instructions)
                {
                    writer.Write("<li><code>"); writer.Write(ReportText.Html(ReportText.Instruction(instruction))); writer.Write("</code></li>");
                }
                writer.Write("</ol>");
            }
            writer.Write("</section>");
        }
        writer.Write("</article>");
    }

    private static void WriteState(TextWriter writer, StateSnapshot state)
    {
        if (!state.IsReachable)
            writer.Write("<p class=\"notice\">This state is infeasible under the modeled path constraints. Retained facts below are abstract debugging data.</p>");
        TableStart(writer, "Resources", "Resource", "Ownership", "Status", "Owner scopes");
        foreach (var resource in state.Resources.OrderBy(resource => resource.Id, StringComparer.Ordinal))
            Row(writer, resource.Id, resource.Ownership.ToString(), resource.Status.ToString(), string.Join(", ", resource.OwnerResourceIds.OrderBy(id => id, StringComparer.Ordinal)));
        TableEnd(writer);
        TableStart(writer, "Places and aliases", "Place", "Resource", "Availability", "Loan / projection");
        foreach (var place in state.Places.OrderBy(place => place.Place, StringComparer.Ordinal))
        {
            if (place.References.IsEmpty)
                Row(writer, place.Place, "∅", place.MayBeNull ? "Null" : "No resource", "—");
            foreach (var reference in place.References.OrderBy(reference => reference.ResourceId, StringComparer.Ordinal)
                         .ThenBy(reference => reference.Projection, StringComparer.Ordinal)
                         .ThenBy(reference => reference.LoanId, StringComparer.Ordinal))
                Row(writer, place.Place, reference.ResourceId, reference.Availability +
                    (place.MayBeNull ? "; may be null" : "") + (reference.MayBeAbsent ? "; may be absent" : ""),
                    (reference.LoanId ?? "—") + " / " + (reference.Projection.Length == 0 ? "(root)" : reference.Projection) +
                    (reference.CoversResource ? "; covers resource" : "; projection only") +
                    (reference.MovedProjections.IsEmpty ? "" : "; moved: " + string.Join(", ", reference.MovedProjections.OrderBy(value => value, StringComparer.Ordinal))));
        }
        TableEnd(writer);
        TableStart(writer, "Live loans", "Loan", "Resource / projection", "Holder", "Kind / phase / parent");
        foreach (var loan in state.Loans.OrderBy(loan => loan.Id, StringComparer.Ordinal))
            Row(writer, loan.Id, loan.ResourceId + loan.Projection, loan.Holder,
                loan.Kind + " / " + loan.Phase + (loan.MayBeAbsent ? " (possible)" : "") + " / " + (loan.ParentId ?? "—") +
                (loan.AllowsSuspension ? "; suspension allowed" : "") +
                (loan.ActivationSites.IsEmpty ? "" : "; activation: " + string.Join(", ", loan.ActivationSites.Select(ReportText.Site))));
        TableEnd(writer);
    }

    private static void Liveness(TextWriter writer, string title, IEnumerable<string> values)
    {
        writer.Write("<p class=\"liveness\"><strong>"); writer.Write(title); writer.Write("</strong><code>");
        var rendered = string.Join(", ", values.OrderBy(value => value, StringComparer.Ordinal));
        writer.Write(ReportText.Html(rendered.Length == 0 ? "∅" : rendered)); writer.Write("</code></p>");
    }

    private static void TableStart(TextWriter writer, string title, params string[] headings)
    {
        writer.Write("<div class=\"table-scroll\"><table><caption>"); writer.Write(title); writer.Write("</caption><thead><tr>");
        foreach (var heading in headings) { writer.Write("<th scope=\"col\">"); writer.Write(heading); writer.Write("</th>"); }
        writer.Write("</tr></thead><tbody>");
    }

    private static void Row(TextWriter writer, params string[] values)
    {
        writer.Write("<tr>");
        foreach (var value in values) { writer.Write("<td>"); writer.Write(ReportText.Html(value)); writer.Write("</td>"); }
        writer.Write("</tr>");
    }

    private static void TableEnd(TextWriter writer) => writer.Write("</tbody></table></div>");

    private static void Stat(TextWriter writer, string title, int value)
    {
        writer.Write("<div class=\"stat\"><span>"); writer.Write(title); writer.Write("</span><strong>");
        writer.Write(ReportText.Number(value)); writer.Write("</strong></div>");
    }

    private const string Styles = """
        :root{color-scheme:dark;--bg:#0a1220;--panel:#0f1b2c;--line:#26384e;--text:#e5eefb;--muted:#91a6bf;--accent:#79d8cd;--warning:#ffb578}
        .evidence ol.dependency-witness{padding:0;list-style:none;max-width:720px;margin:20px 0}.evidence .dependency-witness li{margin:0;padding:0}.dependency-witness li+li:before{content:"↓ depends on";display:block;color:var(--muted);font-size:10px;margin:5px 0 5px 20px}.dependency-node{border:1px solid #3d5e70;border-left:3px solid var(--accent);background:#102632;border-radius:8px;padding:12px 16px;display:flex;flex-wrap:wrap;align-items:center;gap:8px 14px}.dependency-node strong{flex-basis:100%;overflow-wrap:anywhere}.dependency-node>.muted{min-width:0;overflow-wrap:anywhere}.dependency-witness li:last-child .dependency-node{border-left-color:var(--warning)}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.6 system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}header{display:flex;align-items:center;justify-content:space-between;padding:20px max(24px,calc((100vw - 1440px)/2));border-bottom:1px solid var(--line);background:#0c1726}.brand{display:flex;align-items:center;gap:12px}.brand-mark{width:40px;height:40px;display:grid;place-items:center;font-weight:800;font-size:26px;background:var(--accent);color:#08252a;border-radius:10px}.brand strong{display:block;font-size:18px;line-height:1.3}.brand div>span{display:block;font-size:9px;letter-spacing:2px;color:var(--muted)}.version{color:var(--muted);font:12px ui-monospace,monospace}main{max-width:1488px;padding:32px 24px 60px;margin:auto}.page-heading{margin:0 0 26px}.eyebrow{font-size:10px;letter-spacing:2px;color:var(--accent)}h1{font-size:clamp(28px,4vw,40px);line-height:1.2;letter-spacing:-1px;margin:10px 0 14px}.page-heading>p:last-child{color:var(--muted);font-size:15px}.stats{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin-bottom:22px}.stat{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:18px 22px}.stat span{display:block;color:var(--muted);font-size:12px}.stat strong{display:block;font-size:30px;line-height:1.3;margin-top:5px}.notice{border:1px solid #31515b;border-left:3px solid var(--accent);background:#10262d;border-radius:8px;padding:14px 18px;margin:0 0 24px;font-size:13px;color:#bfdcdd}.notice.warning{border-color:#675034;background:#2c241c;color:#ffdbba}.panel{border:1px solid var(--line);border-radius:12px;overflow:hidden;background:var(--panel);margin-bottom:26px}.panel-heading{padding:16px 22px;border-bottom:1px solid var(--line);display:flex;align-items:center;justify-content:space-between;gap:12px}.panel-heading h2{font-size:16px;margin:0}.badge{font-size:10px;letter-spacing:.3px;padding:3px 8px;border:1px solid var(--line);border-radius:20px;color:var(--muted)}.diagnostic{padding:18px 22px;border-bottom:1px solid var(--line)}.diagnostic:last-child{border-bottom:0}.diagnostic-top{display:flex;align-items:center;gap:10px}.diagnostic p{margin:10px 0 6px}.severity{font-size:9px;font-weight:750;letter-spacing:1px;border-radius:4px;padding:3px 6px}.severity.warning{color:var(--warning);background:#4a3223}.severity.error{color:#ff9e9e;background:#44272c}.severity.note{color:#8eceff;background:#1b3b50}code{font:12px/1.6 ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere}.source{display:block;color:var(--muted);font-size:11px;white-space:pre-wrap}.evidence{margin-top:12px;padding-top:10px;border-top:1px dashed var(--line)}summary{cursor:pointer;list-style-position:outside;margin-left:16px}summary:hover{color:var(--accent)}.evidence summary{font-size:12px}.evidence ol{padding-left:24px}.evidence li{margin:10px 0}.muted{color:var(--muted);font-weight:400}.toolbar{display:flex;align-items:center;flex-wrap:wrap;gap:10px;padding:16px 22px;background:#111f31;border-bottom:1px solid var(--line)}.toolbar label{font-size:11px;color:var(--muted)}select,input,button{font:inherit;color:var(--text);border:1px solid #3b5068;background:#0e1929;border-radius:6px;min-height:36px;padding:6px 10px}select{flex:1;min-width:160px;max-width:650px}input{width:200px}button{cursor:pointer;min-width:36px}button:hover,button:focus-visible{border-color:var(--accent);color:var(--accent)}input:focus-visible,select:focus-visible{outline:2px solid var(--accent);outline-offset:1px}.method{border-bottom:1px solid var(--line);padding-bottom:24px}.method:last-of-type{border-bottom:0}.method-heading{padding:20px 22px 10px}.method-heading h3{margin:0;font-size:16px;overflow-wrap:anywhere}.method-heading p{font-size:11px;color:var(--muted);margin:8px 0}.incomplete{color:var(--warning)}.graph-scroll{overflow:auto;max-height:650px;background:radial-gradient(#273449 1px,transparent 1px);background-size:18px 18px;border-top:1px solid #1b2b40;border-bottom:1px solid #1b2b40}.cfg{display:block;max-width:none;transform-origin:top left}.cfg a:hover rect,.cfg a:focus-visible rect{stroke:var(--accent);stroke-width:3}.graph-hint{font-size:11px;color:var(--muted);margin:12px 22px 22px}.block{margin:0 22px 18px;border:1px solid var(--line);border-radius:8px;overflow:hidden;scroll-margin-top:24px}.block h4{font-size:13px;background:#152337;padding:12px 16px;margin:0;border-bottom:1px solid var(--line)}.block:target{border-color:var(--accent)}.liveness{display:flex;align-items:baseline;gap:12px;padding:0 16px;margin:10px 0;font-size:11px}.liveness strong{color:var(--muted);min-width:78px;font-weight:500}.liveness code{color:var(--accent);font-size:11px}.snapshot{padding:8px 16px;font-size:12px}.instruction{border-top:1px solid var(--line);padding:10px 14px;background:#0d1828}.instruction summary{display:list-item;overflow-wrap:anywhere}.instruction-index{color:var(--muted);display:inline-block;min-width:28px;font:11px ui-monospace,monospace}.instruction-body{padding:12px 0 0}.states{display:grid;grid-template-columns:1fr 1fr;gap:18px;padding:4px 0 12px;min-width:0}.states>div{min-width:0}.states h5{font-size:12px;margin:10px 0}.table-scroll{overflow:auto;margin:8px 0 14px}table{width:100%;border-collapse:collapse;font-size:10px;text-align:left}caption{text-align:left;color:var(--accent);font-size:10px;padding-bottom:5px}th{font-weight:500;color:var(--muted);background:#17263a}td,th{border:1px solid var(--line);padding:6px 8px;vertical-align:top;overflow-wrap:anywhere}td{font-family:ui-monospace,monospace}tbody:empty:after{content:"∅";display:table-cell;padding:6px 8px;color:var(--muted)}.block>.muted{margin:12px 16px;font-size:11px}.instructions{padding:0 32px}.empty{padding:18px 22px;margin:0;color:var(--muted)}footer{font-size:11px;color:var(--muted);text-align:center;padding:8px 12px}[hidden]{display:none!important}@media(max-width:800px){.states{grid-template-columns:1fr}.toolbar{gap:8px}.toolbar input{flex:1}.stats{grid-template-columns:repeat(2,1fr)}header{padding:16px 20px}main{padding:24px 12px}.panel-heading,.method-heading,.diagnostic{padding-left:16px;padding-right:16px}.block{margin-left:12px;margin-right:12px}.version{font-size:10px}}@media print{body{background:white;color:#152237}.toolbar,header,.graph-scroll{display:none}.method[hidden]{display:block!important}.panel,.block,.diagnostic{break-inside:avoid}.stats{display:flex}.stat{flex:1}main{max-width:none;padding:0}.notice{background:white;color:#152237}details{display:block}table,td,th{color:#152237}footer{color:#555}}
        """;

    // This script is constant. No source name, diagnostic message, or JSON is interpolated into it.
    private const string Script = """
        (()=>{"use strict";const picker=document.getElementById("method-picker"),filter=document.getElementById("method-filter"),methods=Array.from(document.querySelectorAll(".method")),empty=document.getElementById("no-methods");let scale=1;function update(){const query=filter.value.toLocaleLowerCase();let visible=0;for(const method of methods){const show=(picker.value==="all"||method.dataset.method===picker.value)&&method.dataset.name.toLocaleLowerCase().includes(query);method.hidden=!show;if(show)visible++;}empty.hidden=visible!==0;}function zoom(value){scale=Math.max(.25,Math.min(2,value));for(const svg of document.querySelectorAll("svg.cfg")){const box=svg.viewBox.baseVal;svg.setAttribute("width",String(box.width*scale));svg.setAttribute("height",String(box.height*scale));}document.getElementById("zoom-reset").textContent=Math.round(scale*100)+"%";}picker.addEventListener("change",update);filter.addEventListener("input",update);document.getElementById("zoom-in").addEventListener("click",()=>zoom(scale+.15));document.getElementById("zoom-out").addEventListener("click",()=>zoom(scale-.15));document.getElementById("zoom-reset").addEventListener("click",()=>zoom(1));if(methods.length>1)picker.value="0";update();})();
        """;
}
