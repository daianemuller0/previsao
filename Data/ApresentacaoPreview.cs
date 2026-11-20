using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Pré-visualização da Apresentação Executiva: desenha, em HTML + SVG, o MESMO
// desenho de slides que vai para o .pptx (AxDeck). Posições em % do slide e
// fontes em unidades do contêiner (cqw), então a miniatura escala sem quebrar.
// Também serve de base para o PDF (impressão do navegador, um slide por página).
// ---------------------------------------------------------------------------
public static class AxHtml
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string H(string s) => WebUtility.HtmlEncode(s);
    private static string P(double v) => v.ToString("0.###", Inv);
    private static string Px(long emu, long total) => P(emu * 100.0 / total) + "%";
    /// <summary>EMU → cqw (largura do slide = 100cqw).</summary>
    private static string Cq(double emu) => P(emu * 100.0 / AxMedida.Larg) + "cqw";
    /// <summary>Tamanho de fonte (centésimos de pt) → cqw (o slide tem 960 pt de largura).</summary>
    private static string Fs(int tam) => P(tam / 100.0 / 960.0 * 100) + "cqw";

    public static string Slide(AxSlide s, string? fundo, CultureInfo ci)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"ax-slide\"").Append(fundo is null ? "" : $" style=\"background-image:url('{fundo}')\"").Append('>');
        foreach (var e in s.Elems)
        {
            var pos = $"left:{Px(e.X, AxMedida.Larg)};top:{Px(e.Y, AxMedida.Alt)};width:{Px(e.W, AxMedida.Larg)};";
            switch (e)
            {
                case AxForma f:
                {
                    var st = new StringBuilder(pos).Append($"height:{Px(f.H, AxMedida.Alt)};");
                    if (f.Fundo is not null) st.Append($"background:#{f.Fundo};");
                    if (f.Borda is not null) st.Append($"border:1px {(f.Tracejada ? "dashed" : "solid")} #{f.Borda};");
                    if (f.Geo == "roundRect") st.Append($"border-radius:{Cq(Math.Min(f.W, f.H) * f.Arredondado / 100000.0)};");
                    st.Append($"padding:{Cq(f.MargemV)} {Cq(f.MargemH)};justify-content:{(f.Ancora == "ctr" ? "center" : f.Ancora == "b" ? "flex-end" : "flex-start")};");
                    sb.Append($"<div class=\"ax-el ax-forma\" style=\"{st}\">");
                    foreach (var p in f.Pars)
                    {
                        var tam = p.Runs.Count > 0 ? p.Runs[0].Tam : 1000;
                        sb.Append($"<p style=\"text-align:{(p.Alinh == "ctr" ? "center" : p.Alinh == "r" ? "right" : "left")};font-size:{Fs(tam)};margin-bottom:{Fs(p.EspDepois)}\">");
                        foreach (var r in p.Runs)
                            sb.Append($"<span style=\"color:#{r.Cor};font-size:{Fs(r.Tam)};{(r.Negrito ? "font-weight:700;" : "")}{(r.Italico ? "font-style:italic;" : "")}\">{H(r.Texto)}</span>");
                        sb.Append("</p>");
                    }
                    sb.Append("</div>");
                    break;
                }
                case AxLinha l:
                    sb.Append($"<div class=\"ax-el\" style=\"{pos}height:0;border-top:{Cq(l.Espessura)} solid #{l.Cor}\"></div>");
                    break;
                case AxGrafico g:
                    sb.Append($"<div class=\"ax-el\" style=\"{pos}height:{Px(g.H, AxMedida.Alt)}\" role=\"img\" aria-label=\"{H(g.Chart.Descricao)}\">{Svg(g.Chart, g.W / (double)AxMedida.Pt, g.H / (double)AxMedida.Pt, ci)}</div>");
                    break;
                case AxTabela t:
                {
                    var soma = t.Pesos.Sum();
                    sb.Append($"<table class=\"ax-el ax-tab\" style=\"{pos}height:{Px(t.H, AxMedida.Alt)}\"><colgroup>");
                    foreach (var w in t.Pesos) sb.Append($"<col style=\"width:{P(w / soma * 100)}%\">");
                    sb.Append("</colgroup>");
                    for (var i = 0; i < t.Linhas.Count; i++)
                    {
                        sb.Append("<tr>");
                        foreach (var c in t.Linhas[i])
                        {
                            var cab = t.Cabecalho && i == 0;
                            var fundoC = cab ? AxCores.Escuro : c.Fundo;
                            sb.Append($"<td style=\"font-size:{Fs(c.Tam ?? t.Tam)};color:#{(cab ? "FFFFFF" : c.Cor)};text-align:{(c.Alinh == "ctr" ? "center" : c.Alinh == "r" ? "right" : "left")};{(fundoC is null ? "" : $"background:#{fundoC};")}{(c.Negrito || cab ? "font-weight:700;" : "")}\">{H(c.Texto)}</td>");
                        }
                        sb.Append("</tr>");
                    }
                    sb.Append("</table>");
                    break;
                }
            }
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // ---------------- formato numérico (o mesmo código Excel dos rótulos do PowerPoint) ----------------

    private static readonly Regex Numero = new(@"[#0,]*0(\.0+)?", RegexOptions.Compiled);

    /// <summary>Aplica um código de formato Excel simples ("R$ "#,##0.0"M" · 0.0"%" · #,##0) com a cultura do idioma.</summary>
    public static string Formatar(double v, string fmt, CultureInfo ci)
    {
        var m = Numero.Match(fmt);
        if (!m.Success) return v.ToString("0.#", ci);
        var dec = m.Groups[1].Success ? m.Groups[1].Value.Length - 1 : 0;
        var milhar = m.Value.Contains(',');
        var num = Math.Abs(v).ToString((milhar ? "#,##0" : "0") + (dec > 0 ? "." + new string('0', dec) : ""), ci);
        string Lit(string s) => s.Replace("\"", "").Replace("\\", "");
        return (v < 0 && Math.Round(Math.Abs(v), dec) > 0 ? "-" : "") + Lit(fmt[..m.Index]) + num + Lit(fmt[(m.Index + m.Length)..]);
    }

    // ---------------- gráficos em SVG ----------------

    private static string T(double x, double y, string txt, double fs, string cor, string anchor = "middle", bool negrito = false) =>
        $"<text x=\"{P(x)}\" y=\"{P(y)}\" font-size=\"{P(fs)}\" fill=\"#{cor}\" text-anchor=\"{anchor}\"{(negrito ? " font-weight=\"700\"" : "")}>{H(txt)}</text>";

    private static string Cortar(string s, double largura, double fs)
    {
        var max = Math.Max(3, (int)(largura / (fs * 0.55)));
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    public static string Svg(AxChart c, double w, double h, CultureInfo ci)
    {
        var fs = c.TamFonte / 100.0;
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {P(w)} {P(h)}\" width=\"100%\" height=\"100%\" preserveAspectRatio=\"none\" font-family=\"Aptos, 'Segoe UI', Arial, sans-serif\">");
        var topo = 4.0;
        if (c.Legenda && c.Series.Count > 0)
        {
            // legenda centralizada no topo, como no PowerPoint
            var itens = c.Series.Select(s => (s, larg: 12 + s.Nome.Length * fs * 0.55 + 10)).ToList();
            var total = itens.Sum(i => i.larg);
            var x = Math.Max(2, (w - total) / 2);
            foreach (var (s, larg) in itens)
            {
                if (s.Linha || c.Tipo == AxTipoGrafico.Linhas)
                    sb.Append($"<line x1=\"{P(x)}\" y1=\"{P(topo + fs / 2)}\" x2=\"{P(x + 9)}\" y2=\"{P(topo + fs / 2)}\" stroke=\"#{s.Cor}\" stroke-width=\"2\"{(s.Tracejada ? " stroke-dasharray=\"3 2\"" : "")}/>");
                else sb.Append($"<rect x=\"{P(x)}\" y=\"{P(topo + 1)}\" width=\"8\" height=\"{P(fs - 1)}\" fill=\"#{s.Cor}\"/>");
                sb.Append(T(x + 12, topo + fs * 0.85, s.Nome, fs, AxCores.Texto, "start"));
                x += larg;
            }
            topo += fs + 8;
        }
        var horizontal = c.Tipo is AxTipoGrafico.Barras or AxTipoGrafico.BarrasEmpilhadas;
        if (horizontal) sb.Append(Horizontal(c, w, h, topo, fs, ci));
        else sb.Append(Vertical(c, w, h, topo, fs, ci));
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Escala "redonda" para o eixo (passos de 1, 2, 2,5 ou 5 × 10ⁿ), como o PowerPoint faz.</summary>
    private static (double Min, double Max, double Passo) Bonito(double mn, double mx, int n = 5)
    {
        var amp = mx - mn; if (amp <= 0) amp = 1;
        var bruto = amp / n; var mag = Math.Pow(10, Math.Floor(Math.Log10(bruto)));
        var f = bruto / mag;
        var passo = (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * mag;
        return (Math.Floor(mn / passo) * passo, Math.Ceiling(mx / passo) * passo, passo);
    }

    private static (double Min, double Max) Faixa(IEnumerable<double> vals, double folga)
    {
        var l = vals.ToList();
        var mx = l.DefaultIfEmpty(0).Max(); var mn = l.DefaultIfEmpty(0).Min();
        mn = Math.Min(0, mn); mx = Math.Max(0, mx);
        if (mx - mn < 1e-9) mx = mn + 1;
        var amp = mx - mn;
        return (mn < 0 ? mn - amp * 0.08 : mn, mx + amp * folga);
    }

    private static string Vertical(AxChart c, double w, double h, double topo, double fs, CultureInfo ci)
    {
        var sb = new StringBuilder();
        var n = Math.Max(1, c.Categorias.Length);
        var esq = c.EixoValores ? 34.0 : 6; var dir = 6.0; var baixo = fs + 8;
        var pw = w - esq - dir; var ph = h - topo - baixo - 6;
        var y0 = topo + 6;
        var barras = c.Tipo == AxTipoGrafico.Linhas ? new List<AxSerie>() : c.Series.Where(s => !s.Linha).ToList();
        var linhas = c.Tipo == AxTipoGrafico.Linhas ? c.Series : c.Series.Where(s => s.Linha).ToList();
        var emp = c.Tipo == AxTipoGrafico.ColunasEmpilhadas;
        var banda = pw / n;

        // escala principal
        IEnumerable<double> valsPri;
        if (barras.Count > 0 && emp)
            valsPri = Enumerable.Range(0, n).SelectMany(i => new[] { barras.Sum(s => Math.Max(0, s.Valores.ElementAtOrDefault(i) ?? 0)), barras.Sum(s => Math.Min(0, s.Valores.ElementAtOrDefault(i) ?? 0)) });
        else valsPri = (barras.Count > 0 ? barras : linhas).SelectMany(s => s.Valores).Where(v => v is not null).Select(v => v!.Value);
        var (mn, mx) = (c.Min ?? double.NaN, c.Max ?? double.NaN);
        var fx = Faixa(valsPri, 0.16);
        if (double.IsNaN(mn)) mn = fx.Min; if (double.IsNaN(mx)) mx = fx.Max;
        var combo = AxPptx.FaixasCombo(c);
        if (c.Max is null && combo.MaxPri is double mp) mx = Math.Max(mp, mn + 1e-9);
        var passo = (mx - mn) / 4;
        if (c.EixoValores) (mn, mx, passo) = Bonito(mn, mx);
        double Y(double v) => y0 + ph - (v - mn) / (mx - mn) * ph;

        if (c.EixoValores)
            for (var v = mn; v <= mx + passo * 0.01; v += passo)
            {
                var y = Y(v);
                sb.Append($"<line x1=\"{P(esq)}\" y1=\"{P(y)}\" x2=\"{P(w - dir)}\" y2=\"{P(y)}\" stroke=\"#E3ECE9\" stroke-width=\"0.6\"/>");
                sb.Append(T(esq - 3, y + fs * 0.3, Formatar(v, c.Formato, ci), fs - 0.5, AxCores.Cinza, "end"));
            }
        // linha do zero / eixo das categorias
        sb.Append($"<line x1=\"{P(esq)}\" y1=\"{P(Y(0))}\" x2=\"{P(w - dir)}\" y2=\"{P(Y(0))}\" stroke=\"#B6CDC6\" stroke-width=\"0.8\"/>");
        for (var i = 0; i < n; i++)
            sb.Append(T(esq + banda * (i + 0.5), h - 4, Cortar(c.Categorias.ElementAtOrDefault(i) ?? "", banda, fs), fs, AxCores.Texto));

        if (barras.Count > 0)
        {
            var interno = banda * 100.0 / (100 + c.Espaco);
            var bw = emp ? interno : interno / barras.Count;
            for (var i = 0; i < n; i++)
            {
                double acPos = 0, acNeg = 0;
                for (var j = 0; j < barras.Count; j++)
                {
                    var s = barras[j];
                    if (s.Valores.ElementAtOrDefault(i) is not double v) continue;
                    var cor = s.CoresPontos?.ElementAtOrDefault(i) ?? s.Cor;
                    double x = esq + banda * i + (banda - interno) / 2 + (emp ? 0 : j * bw);
                    double ya, yb;
                    if (emp) { if (v >= 0) { ya = Y(acPos + v); yb = Y(acPos); acPos += v; } else { ya = Y(acNeg); yb = Y(acNeg + v); acNeg += v; } }
                    else { ya = Y(Math.Max(0, v)); yb = Y(Math.Min(0, v)); }
                    sb.Append($"<rect x=\"{P(x)}\" y=\"{P(ya)}\" width=\"{P(Math.Max(0.5, bw - (emp ? 0 : 0.8)))}\" height=\"{P(Math.Max(0.4, yb - ya))}\" fill=\"#{cor}\"/>");
                    if (!s.Rotulos) continue;
                    var txt = Formatar(v, s.Formato ?? c.Formato, ci);
                    if (emp)
                    {
                        if (yb - ya >= fs * 1.05 && txt.Length * fs * 0.5 <= bw + 6) sb.Append(T(x + bw / 2, (ya + yb) / 2 + fs * 0.35, txt, fs, "FFFFFF", "middle", true));
                    }
                    else sb.Append(T(x + bw / 2, v >= 0 ? ya - 2.5 : yb + fs, txt, fs, AxCores.Escuro, "middle", true));
                }
            }
        }

        if (linhas.Count > 0)
        {
            var sec = barras.Count > 0;
            double mn2 = mn, mx2 = mx;
            if (sec) { mn2 = combo.MinSec ?? 0; mx2 = combo.MaxSec ?? 1; }
            double Y2(double v) => y0 + ph - (v - mn2) / (mx2 - mn2) * ph;
            foreach (var s in linhas)
            {
                var pts = new List<(double X, double Y, double V)>();
                for (var i = 0; i < n; i++)
                    if (s.Valores.ElementAtOrDefault(i) is double v) pts.Add((esq + banda * (i + 0.5), sec ? Y2(v) : Y(v), v));
                // segmentos só entre meses seguidos (mês sem dado = buraco, como no PowerPoint)
                for (var k = 1; k < pts.Count; k++)
                    if (Math.Abs(pts[k].X - pts[k - 1].X - banda) < banda * 0.01)
                        sb.Append($"<line x1=\"{P(pts[k - 1].X)}\" y1=\"{P(pts[k - 1].Y)}\" x2=\"{P(pts[k].X)}\" y2=\"{P(pts[k].Y)}\" stroke=\"#{s.Cor}\" stroke-width=\"{P(s.Espessura / 12700.0)}\" stroke-linecap=\"round\"{(s.Tracejada ? " stroke-dasharray=\"4 3\"" : "")}/>");
                foreach (var (x, y, v) in pts)
                {
                    sb.Append($"<circle cx=\"{P(x)}\" cy=\"{P(y)}\" r=\"2.2\" fill=\"#{s.Cor}\" stroke=\"#fff\" stroke-width=\"0.6\"/>");
                    if (s.Rotulos) sb.Append(T(x, y - 4, Formatar(v, s.Formato ?? c.Formato, ci), fs, s.Cor, "middle", true));
                }
            }
        }
        return sb.ToString();
    }

    private static string Horizontal(AxChart c, double w, double h, double topo, double fs, CultureInfo ci)
    {
        var sb = new StringBuilder();
        var n = Math.Max(1, c.Categorias.Length);
        var maxRot = c.Categorias.Select(x => x.Length).DefaultIfEmpty(4).Max();
        var esq = Math.Min(w * 0.36, maxRot * fs * 0.55 + 8);
        var dir = 6.0;
        var ph = h - topo - 4; var y0 = topo + 2;
        var banda = ph / n;
        var emp = c.Tipo == AxTipoGrafico.BarrasEmpilhadas;
        var barras = c.Series;
        IEnumerable<double> vals = emp
            ? Enumerable.Range(0, n).Select(i => barras.Sum(s => Math.Max(0, s.Valores.ElementAtOrDefault(i) ?? 0)))
            : barras.SelectMany(s => s.Valores).Where(v => v is not null).Select(v => v!.Value);
        var (mn, mx) = Faixa(vals, emp ? 0.02 : 0.22);
        var pw = w - esq - dir;
        double X(double v) => esq + (v - mn) / (mx - mn) * pw;
        sb.Append($"<line x1=\"{P(X(0))}\" y1=\"{P(y0)}\" x2=\"{P(X(0))}\" y2=\"{P(y0 + ph)}\" stroke=\"#B6CDC6\" stroke-width=\"0.8\"/>");
        var interno = banda * 100.0 / (100 + c.Espaco);
        var bh = emp ? interno : interno / barras.Count;
        for (var i = 0; i < n; i++)
        {
            sb.Append(T(esq - 4, y0 + banda * (i + 0.5) + fs * 0.35, Cortar(c.Categorias[i], esq - 6, fs), fs, AxCores.Texto, "end"));
            double ac = 0;
            for (var j = 0; j < barras.Count; j++)
            {
                var s = barras[j];
                if (s.Valores.ElementAtOrDefault(i) is not double v) continue;
                var y = y0 + banda * i + (banda - interno) / 2 + (emp ? 0 : j * bh);
                double xa, xb;
                if (emp) { xa = X(ac); xb = X(ac + Math.Max(0, v)); ac += Math.Max(0, v); }
                else { xa = X(Math.Min(0, v)); xb = X(Math.Max(0, v)); }
                var cor = s.CoresPontos?.ElementAtOrDefault(i) ?? s.Cor;
                sb.Append($"<rect x=\"{P(xa)}\" y=\"{P(y)}\" width=\"{P(Math.Max(0.4, xb - xa))}\" height=\"{P(Math.Max(0.5, bh - (emp ? 0 : 0.8)))}\" fill=\"#{cor}\"/>");
                if (!s.Rotulos) continue;
                var txt = Formatar(v, s.Formato ?? c.Formato, ci);
                if (emp) { if (xb - xa >= txt.Length * fs * 0.52 + 2) sb.Append(T((xa + xb) / 2, y + bh / 2 + fs * 0.35, txt, fs, "FFFFFF", "middle", true)); }
                else sb.Append(T(xb + 3, y + bh / 2 + fs * 0.35, txt, fs, AxCores.Escuro, "start", true));
            }
        }
        return sb.ToString();
    }
}
