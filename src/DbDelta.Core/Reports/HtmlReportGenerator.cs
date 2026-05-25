using System.Globalization;
using System.Net;
using System.Text;
using DbDelta.Core.Diff;

namespace DbDelta.Core.Reports;

/// <summary>
/// Renders a <see cref="ComparisonResult"/> to a self-contained HTML document
/// — embedded CSS only, no external assets. Collapsible sections per Kind,
/// rows colour-coded by <see cref="DifferenceStatus"/> using the DbDelta design
/// system palette (cobalt / crimson / emerald / grey).
/// </summary>
public sealed class HtmlReportGenerator
{
    private const string ColorDifferent = "#0064C8";
    private const string ColorOnlyTarget = "#B31220";
    private const string ColorOnlySource = "#007339";
    private const string ColorIdentical = "#9097A0";

    public string Generate(ComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        StringBuilder sb = new();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"it\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine("<title>DbDelta — Report di confronto</title>");
        AppendStyle(sb);
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        AppendHeader(sb, result);
        AppendKindSections(sb, result);

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void AppendStyle(StringBuilder sb)
    {
        sb.Append("<style>");
        sb.Append("""
            body{font-family:-apple-system,Segoe UI,Roboto,sans-serif;margin:0;background:#F4F5F7;color:#1B1F23;}
            header{padding:20px 28px;background:#1B1F23;color:#F4F5F7;}
            header h1{margin:0;font-size:20px;font-weight:600;letter-spacing:.2px;}
            header .totals{margin-top:10px;display:flex;gap:14px;flex-wrap:wrap;font-size:13px;}
            header .totals span{padding:4px 10px;border-radius:14px;background:rgba(255,255,255,.08);}
            header .totals .different{background:
            """);
        sb.Append(ColorDifferent);
        sb.Append(';');
        sb.Append("color:#fff;}");
        sb.Append("header .totals .only-target{background:");
        sb.Append(ColorOnlyTarget);
        sb.Append(';');
        sb.Append("color:#fff;}");
        sb.Append("header .totals .only-source{background:");
        sb.Append(ColorOnlySource);
        sb.Append(';');
        sb.Append("color:#fff;}");
        sb.Append("header .totals .identical{background:");
        sb.Append(ColorIdentical);
        sb.Append(';');
        sb.Append("color:#fff;}");
        sb.Append("""
            main{padding:20px 28px;}
            details{background:#fff;border:1px solid #D6DBE2;border-radius:8px;margin-bottom:12px;overflow:hidden;}
            details>summary{padding:12px 16px;font-weight:600;cursor:pointer;list-style:none;display:flex;align-items:center;gap:10px;}
            details>summary::-webkit-details-marker{display:none;}
            details>summary::before{content:'▶';font-size:10px;color:#5A6470;transition:transform .15s;}
            details[open]>summary::before{transform:rotate(90deg);}
            details>summary .kind-count{margin-left:auto;font-weight:400;color:#5A6470;font-size:13px;}
            table{width:100%;border-collapse:collapse;font-size:13px;}
            th,td{padding:8px 16px;text-align:left;border-top:1px solid #EEF0F3;}
            th{background:#F8F9FB;font-weight:500;color:#5A6470;text-transform:uppercase;letter-spacing:.4px;font-size:11px;}
            tr.status-different td:first-child{border-left:4px solid
            """);
        sb.Append(ColorDifferent);
        sb.Append(";}");
        sb.Append("tr.status-only-target td:first-child{border-left:4px solid ");
        sb.Append(ColorOnlyTarget);
        sb.Append(";}");
        sb.Append("tr.status-only-source td:first-child{border-left:4px solid ");
        sb.Append(ColorOnlySource);
        sb.Append(";}");
        sb.Append("tr.status-identical td:first-child{border-left:4px solid ");
        sb.Append(ColorIdentical);
        sb.Append(";color:#5A6470;}");
        sb.Append("td .badge{display:inline-block;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600;color:#fff;}");
        sb.Append(".badge.different{background:");
        sb.Append(ColorDifferent);
        sb.Append(";}");
        sb.Append(".badge.only-target{background:");
        sb.Append(ColorOnlyTarget);
        sb.Append(";}");
        sb.Append(".badge.only-source{background:");
        sb.Append(ColorOnlySource);
        sb.Append(";}");
        sb.Append(".badge.identical{background:");
        sb.Append(ColorIdentical);
        sb.Append(";}");
        sb.AppendLine("</style>");
    }

    private static void AppendHeader(StringBuilder sb, ComparisonResult result)
    {
        int different = 0, onlyA = 0, onlyB = 0, identical = 0;
        foreach (DifferencePair p in result.Differences)
        {
            switch (p.Status)
            {
                case DifferenceStatus.Different: different++; break;
                case DifferenceStatus.OnlyInA: onlyA++; break;
                case DifferenceStatus.OnlyInB: onlyB++; break;
                case DifferenceStatus.Identical: identical++; break;
                default: break;
            }
        }

        sb.AppendLine("<header>");
        sb.AppendLine("<h1>DbDelta — Report di confronto</h1>");
        sb.Append("<div class=\"totals\">");
        sb.Append("<span class=\"different\">Modificate: ").Append(different.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        sb.Append("<span class=\"only-target\">Solo destinazione: ").Append(onlyB.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        sb.Append("<span class=\"only-source\">Solo provenienza: ").Append(onlyA.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        sb.Append("<span class=\"identical\">Identiche: ").Append(identical.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        sb.AppendLine("</div>");
        sb.AppendLine("</header>");
    }

    private static void AppendKindSections(StringBuilder sb, ComparisonResult result)
    {
        sb.AppendLine("<main>");

        IEnumerable<IGrouping<string, DifferencePair>> byKind = result.Differences
            .GroupBy(d => d.Identity.Kind, StringComparer.Ordinal)
            .OrderBy(g => KindCatalog.SortOrder(g.Key))
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        foreach (IGrouping<string, DifferencePair> kindGroup in byKind)
        {
            string label = KindCatalog.DisplayLabel(kindGroup.Key);
            int count = kindGroup.Count();
            bool hasModifications = kindGroup.Any(p => p.Status != DifferenceStatus.Identical);

            sb.Append("<details");
            if (hasModifications)
            {
                sb.Append(" open");
            }
            sb.AppendLine(">");
            sb.Append("<summary>").Append(WebUtility.HtmlEncode(label))
              .Append(" <span class=\"kind-count\">(").Append(count.ToString(CultureInfo.InvariantCulture)).Append(")</span>")
              .AppendLine("</summary>");

            sb.AppendLine("<table>");
            sb.AppendLine("<thead><tr><th>Schema</th><th>Nome</th><th>Stato</th></tr></thead>");
            sb.AppendLine("<tbody>");

            IEnumerable<DifferencePair> ordered = kindGroup
                .OrderBy(p => KindCatalog.StatusOrder(p.Status))
                .ThenBy(p => p.Identity.SchemaName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Identity.ObjectName, StringComparer.OrdinalIgnoreCase);

            foreach (DifferencePair pair in ordered)
            {
                string statusClass = StatusCssClass(pair.Status);
                string statusLabel = StatusItalianLabel(pair.Status);

                sb.Append("<tr class=\"").Append(statusClass).AppendLine("\">");
                sb.Append("<td>").Append(WebUtility.HtmlEncode(pair.Identity.SchemaName)).AppendLine("</td>");
                sb.Append("<td>").Append(WebUtility.HtmlEncode(pair.Identity.ObjectName)).AppendLine("</td>");
                sb.Append("<td><span class=\"badge ").Append(BadgeCssClass(pair.Status)).Append("\">")
                  .Append(WebUtility.HtmlEncode(statusLabel)).AppendLine("</span></td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
            sb.AppendLine("</details>");
        }

        sb.AppendLine("</main>");
    }

    private static string StatusCssClass(DifferenceStatus status) => status switch
    {
        DifferenceStatus.Different => "status-different",
        DifferenceStatus.OnlyInB => "status-only-target",
        DifferenceStatus.OnlyInA => "status-only-source",
        DifferenceStatus.Identical => "status-identical",
        _ => "status-identical",
    };

    private static string BadgeCssClass(DifferenceStatus status) => status switch
    {
        DifferenceStatus.Different => "different",
        DifferenceStatus.OnlyInB => "only-target",
        DifferenceStatus.OnlyInA => "only-source",
        DifferenceStatus.Identical => "identical",
        _ => "identical",
    };

    private static string StatusItalianLabel(DifferenceStatus status) => status switch
    {
        DifferenceStatus.Different => "Modificata",
        DifferenceStatus.OnlyInB => "Solo destinazione",
        DifferenceStatus.OnlyInA => "Solo provenienza",
        DifferenceStatus.Identical => "Identica",
        _ => status.ToString(),
    };
}
