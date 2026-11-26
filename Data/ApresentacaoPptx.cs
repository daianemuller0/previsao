using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// APRESENTAÇÃO EXECUTIVA — o desenho dos slides (modelo neutro) e a escrita
// do .pptx a partir do modelo PowerPoint corporativo (Modelos/apresentacao_executiva.pptx,
// embutido no programa: não fica numa pasta pública).
//
// O mesmo desenho alimenta a pré-visualização (HTML/SVG) e o arquivo final, então
// o que se vê na tela é o que vai para o PowerPoint. Tudo é gerado na memória:
// nenhum arquivo temporário é gravado no servidor.
//
// Unidades: EMU (1 pt = 12 700 EMU; slide 16:9 = 12 192 000 × 6 858 000).
// Tamanhos de fonte em centésimos de ponto (1000 = 10 pt), como no OpenXML.
// ---------------------------------------------------------------------------

public enum AxTipoSlide { Capa, Conteudo, Encerramento }

/// <summary>Um trecho de texto com formatação.</summary>
public sealed record AxRun(string Texto, int Tam = 1100, bool Negrito = false, string Cor = AxCores.Texto, bool Italico = false);

/// <summary>Um parágrafo (alinhamento l/ctr/r; espaço depois em centésimos de pt).</summary>
public sealed record AxPar(IReadOnlyList<AxRun> Runs, string Alinh = "l", int EspDepois = 200)
{
    public AxPar(AxRun r, string alinh = "l", int espDepois = 200) : this(new[] { r }, alinh, espDepois) { }
    public string TextoPuro => string.Concat(Runs.Select(r => r.Texto));
}

public abstract record AxElem(long X, long Y, long W, long H);

/// <summary>Caixa de texto ou forma (retângulo / retângulo arredondado), com ou sem texto.</summary>
public sealed record AxForma(long X, long Y, long W, long H) : AxElem(X, Y, W, H)
{
    public string Geo { get; init; } = "rect";
    public int Arredondado { get; init; } = 9000;
    public string? Fundo { get; init; }
    public string? Borda { get; init; }
    public bool Tracejada { get; init; }
    public IReadOnlyList<AxPar> Pars { get; init; } = Array.Empty<AxPar>();
    public string Ancora { get; init; } = "t";
    public long MargemH { get; init; } = 18288;
    public long MargemV { get; init; }
    /// <summary>true = é o título do slide (placeholder: aparece no modo de tópicos e na acessibilidade).</summary>
    public bool Titulo { get; init; }
    public string Nome { get; init; } = "";
}

public sealed record AxLinha(long X, long Y, long W, string Cor, long Espessura = 19050) : AxElem(X, Y, W, 0);

public sealed record AxGrafico(long X, long Y, long W, long H, AxChart Chart) : AxElem(X, Y, W, H);

public sealed record AxCelula(string Texto, bool Negrito = false, string Cor = AxCores.Escuro, string? Fundo = null, string Alinh = "l", int? Tam = null);

public sealed record AxTabela(long X, long Y, long W, long H) : AxElem(X, Y, W, H)
{
    /// <summary>Larguras relativas das colunas (somam qualquer valor; são repartidas em W).</summary>
    public double[] Pesos { get; init; } = Array.Empty<double>();
    public List<AxCelula[]> Linhas { get; init; } = new();
    public int Tam { get; init; } = 900;
    public bool Cabecalho { get; init; } = true;
    public string Nome { get; init; } = "Table";
}

public enum AxTipoGrafico { Colunas, ColunasEmpilhadas, Barras, BarrasEmpilhadas, Linhas }

public sealed record AxSerie(string Nome, double?[] Valores, string Cor)
{
    /// <summary>Num gráfico de colunas: esta série vira linha no eixo secundário (combo).</summary>
    public bool Linha { get; init; }
    public bool Tracejada { get; init; }
    public bool Rotulos { get; init; } = true;
    /// <summary>Formato próprio dos rótulos (ex.: % na linha de conversão).</summary>
    public string? Formato { get; init; }
    /// <summary>Cores por ponto (null = a da série), ex.: ano em curso mais claro.</summary>
    public string?[]? CoresPontos { get; init; }
    public long Espessura { get; init; } = 22225;
}

public sealed class AxChart
{
    public AxTipoGrafico Tipo { get; init; }
    public string[] Categorias { get; init; } = Array.Empty<string>();
    public List<AxSerie> Series { get; init; } = new();
    /// <summary>Formato numérico dos rótulos e do eixo (código Excel).</summary>
    public string Formato { get; init; } = "#,##0.0";
    public bool Legenda { get; init; } = true;
    public int TamFonte { get; init; } = 900;
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Eixo de valores visível (com grade leve). Gráficos com rótulos em todas as barras dispensam.</summary>
    public bool EixoValores { get; init; }
    public int Espaco { get; init; } = 60;
    /// <summary>Texto alternativo (acessibilidade).</summary>
    public string Descricao { get; init; } = "";
    public string Idioma { get; init; } = "en-US";
}

public sealed class AxSlide
{
    public AxTipoSlide Tipo { get; init; } = AxTipoSlide.Conteudo;
    public string Id { get; init; } = "";
    public string Titulo { get; init; } = "";
    public List<AxElem> Elems { get; } = new();
    public string Notas { get; set; } = "";
}

public sealed class AxDeck
{
    public List<AxSlide> Slides { get; } = new();
    public string Titulo { get; init; } = "";
    public string Assunto { get; init; } = "";
    public string Autor { get; init; } = "";
    public string Idioma { get; init; } = "en-US";
    public DateTime Gerado { get; init; } = DateTime.Now;
}

public static class AxCores
{
    public const string Escuro = "003D33", Verde = "00B999", VerdeMedio = "00866D", Azul = "1469B5",
        Texto = "5A6870", Cinza = "7B8E89", Linha = "DBE9E3", Card = "F2F7F5", CardLinha = "F4F8F6",
        Barra = "E6F2EB", CardDir = "EDF6F2", CardDirBorda = "D3E8DF", Vermelho = "B5473A", Laranja = "C77C02",
        Neutro = "B6C2BE", Claro = "F7FAF9";
}

/// <summary>Medidas do slide e uma estimativa de quanto texto cabe numa caixa (para escolher a fonte sem estourar).</summary>
public static class AxMedida
{
    public const long Larg = 12192000, Alt = 6858000, Pt = 12700;

    /// <summary>Linhas estimadas de um texto numa largura, para um tamanho de fonte (média ~0,55 em por caractere).</summary>
    public static int Linhas(string txt, long w, int tam)
    {
        var cpl = Math.Max(4, (int)(w / (double)Pt / (tam / 100.0 * 0.55)));
        var n = 0;
        foreach (var par in txt.Split('\n'))
        {
            // quebra por palavra: simula a quebra real (palavra que não cabe vai para a linha de baixo)
            var linha = 0; var l = 1;
            foreach (var pal in par.Split(' '))
            {
                var t = pal.Length;
                if (linha == 0) linha = t;
                else if (linha + 1 + t <= cpl) linha += 1 + t;
                else { l++; linha = t; }
                while (linha > cpl) { l++; linha -= cpl; }
            }
            n += l;
        }
        return n;
    }

    /// <summary>O maior tamanho (de maxTam até minTam, passo 50) em que o texto cabe na caixa.</summary>
    public static int Caber(string txt, long w, long h, int maxTam, int minTam = 700, double entrelinha = 1.22)
    {
        for (var t = maxTam; t > minTam; t -= 50)
            if (Linhas(txt, w, t) * t / 100.0 * entrelinha * Pt <= h) return t;
        return minTam;
    }
}

// ===========================================================================
// ESCRITA DO .PPTX
// ===========================================================================
public static class AxPptx
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsC = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Fonte = "Aptos";

    /// <summary>Partes do modelo que são refeitas (slides de exemplo, índice, propriedades).</summary>
    private static bool Refeita(string n) =>
        n.StartsWith("ppt/slides/") || n.StartsWith("ppt/notesSlides/") || n.StartsWith("ppt/charts/") || n.StartsWith("ppt/embeddings/") ||
        n is "[Content_Types].xml" or "ppt/presentation.xml" or "ppt/_rels/presentation.xml.rels" or "docProps/core.xml" or "docProps/app.xml" or "docProps/thumbnail.jpeg";

    public static byte[] Gerar(AxDeck deck, byte[] modelo)
    {
        using var entrada = new ZipArchive(new MemoryStream(modelo), ZipArchiveMode.Read);
        string Ler(string n) { using var r = new StreamReader(entrada.GetEntry(n)!.Open()); return r.ReadToEnd(); }

        var ms = new MemoryStream();
        using (var saida = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Gravar(string n, string conteudo)
            {
                using var w = new StreamWriter(saida.CreateEntry(n, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(conteudo);
            }
            void GravarBytes(string n, byte[] b) { using var s = saida.CreateEntry(n, CompressionLevel.Optimal).Open(); s.Write(b); }

            foreach (var e in entrada.Entries.Where(e => !Refeita(e.FullName)))
            {
                using var origem = e.Open();
                using var destino = saida.CreateEntry(e.FullName, CompressionLevel.Optimal).Open();
                origem.CopyTo(destino);
            }

            var tipos = new StringBuilder();
            var nGraf = 0;
            for (var i = 0; i < deck.Slides.Count; i++)
            {
                var s = deck.Slides[i];
                var n = i + 1;
                var rels = new List<(string Id, string Tipo, string Alvo)>
                {
                    ("rId1", "slideLayout", s.Tipo == AxTipoSlide.Conteudo ? "../slideLayouts/slideLayout1.xml" : "../slideLayouts/slideLayout7.xml"),
                    ("rId2", "image", s.Tipo switch { AxTipoSlide.Capa => "../media/image1.png", AxTipoSlide.Encerramento => "../media/image3.png", _ => "../media/image2.png" }),
                    ("rId3", "notesSlide", $"../notesSlides/notesSlide{n}.xml"),
                };
                var graficos = new Dictionary<AxGrafico, string>();
                foreach (var g in s.Elems.OfType<AxGrafico>())
                {
                    nGraf++;
                    var rid = "rId" + (rels.Count + 1);
                    rels.Add((rid, "chart", $"../charts/chart{nGraf}.xml"));
                    graficos[g] = rid;
                    Gravar($"ppt/charts/chart{nGraf}.xml", ChartXml(g.Chart, deck.Idioma));
                    Gravar($"ppt/charts/_rels/chart{nGraf}.xml.rels",
                        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{RelBase}package" Target="../embeddings/Microsoft_Excel_Worksheet{nGraf}.xlsx"/></Relationships>""");
                    GravarBytes($"ppt/embeddings/Microsoft_Excel_Worksheet{nGraf}.xlsx", Planilha(g.Chart));
                    tipos.Append($"""<Override PartName="/ppt/charts/chart{nGraf}.xml" ContentType="application/vnd.openxmlformats-officedocument.drawingml.chart+xml"/>""");
                }
                Gravar($"ppt/slides/slide{n}.xml", SlideXml(s, graficos, deck.Idioma));
                Gravar($"ppt/slides/_rels/slide{n}.xml.rels", Rels(rels));
                Gravar($"ppt/notesSlides/notesSlide{n}.xml", NotasXml(s.Notas, deck.Idioma));
                Gravar($"ppt/notesSlides/_rels/notesSlide{n}.xml.rels", Rels(new() { ("rId1", "notesMaster", "../notesMasters/notesMaster1.xml"), ("rId2", "slide", $"../slides/slide{n}.xml") }));
                tipos.Append($"""<Override PartName="/ppt/slides/slide{n}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>""");
                tipos.Append($"""<Override PartName="/ppt/notesSlides/notesSlide{n}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml"/>""");
            }

            // índice de slides da apresentação
            var pres = Ler("ppt/presentation.xml");
            var ids = string.Concat(deck.Slides.Select((_, i) => $"""<p:sldId id="{256 + i}" r:id="rId{1001 + i}"/>"""));
            pres = Regex.Replace(pres, "<p:sldIdLst>.*?</p:sldIdLst>", "<p:sldIdLst>" + ids + "</p:sldIdLst>", RegexOptions.Singleline);
            Gravar("ppt/presentation.xml", pres);
            var prels = Regex.Replace(Ler("ppt/_rels/presentation.xml.rels"), "<Relationship [^>]*?/slide\" Target=\"slides/[^\"]*\"/>", "");
            prels = prels.Replace("</Relationships>", string.Concat(deck.Slides.Select((_, i) =>
                $"""<Relationship Id="rId{1001 + i}" Type="{RelBase}slide" Target="slides/slide{i + 1}.xml"/>""")) + "</Relationships>");
            Gravar("ppt/_rels/presentation.xml.rels", prels);

            var ct = Regex.Replace(Ler("[Content_Types].xml"), "<Override PartName=\"/ppt/(slides|notesSlides|charts)/[^\"]*\"[^>]*/>", "");
            if (!ct.Contains("Extension=\"xlsx\""))
            {
                var k = ct.IndexOf("<Default ", StringComparison.Ordinal);
                ct = ct.Insert(k, """<Default Extension="xlsx" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"/>""");
            }
            ct = ct.Replace("</Types>", tipos + "</Types>");
            Gravar("[Content_Types].xml", ct);

            var agora = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);
            Gravar("docProps/core.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:dcmitype="http://purl.org/dc/dcmitype/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>{X(deck.Titulo)}</dc:title><dc:subject>{X(deck.Assunto)}</dc:subject><dc:creator>{X(deck.Autor)}</dc:creator><cp:keywords>Confidential</cp:keywords><dc:description>Confidential. Aggregated data only. Generated by Howden Sales Forecast.</dc:description><cp:lastModifiedBy>{X(deck.Autor)}</cp:lastModifiedBy><cp:revision>1</cp:revision><dcterms:created xsi:type="dcterms:W3CDTF">{agora}</dcterms:created><dcterms:modified xsi:type="dcterms:W3CDTF">{agora}</dcterms:modified><cp:category>Confidential</cp:category></cp:coreProperties>""");
            Gravar("docProps/app.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"><Application>Microsoft Office PowerPoint</Application><PresentationFormat>Widescreen</PresentationFormat><Slides>{deck.Slides.Count}</Slides><Notes>{deck.Slides.Count}</Notes><HiddenSlides>0</HiddenSlides><Company>Howden South America</Company><LinksUpToDate>false</LinksUpToDate><SharedDoc>false</SharedDoc><HyperlinksChanged>false</HyperlinksChanged><AppVersion>16.0000</AppVersion></Properties>""");
        }
        return ms.ToArray();
    }

    // ---------------- partes ----------------

    private static string X(string? s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";
    private static string L(long v) => v.ToString(Inv);
    private static string D(double v) => v.ToString("0.######", Inv);

    private static string Rels(List<(string Id, string Tipo, string Alvo)> l) =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
        string.Concat(l.Select(r => $"""<Relationship Id="{r.Id}" Type="{RelBase}{r.Tipo}" Target="{r.Alvo}"/>""")) + "</Relationships>";

    private static string RunXml(AxRun r, string lang) =>
        $"""<a:r><a:rPr lang="{lang}" sz="{r.Tam}" b="{(r.Negrito ? 1 : 0)}" i="{(r.Italico ? 1 : 0)}" dirty="0"><a:solidFill><a:srgbClr val="{r.Cor}"/></a:solidFill><a:latin typeface="{Fonte}"/><a:cs typeface="{Fonte}"/></a:rPr><a:t>{X(r.Texto)}</a:t></a:r>""";

    private static string ParXml(AxPar p, string lang)
    {
        var tam = p.Runs.Count > 0 ? p.Runs[0].Tam : 1000;
        return $"""<a:p><a:pPr algn="{p.Alinh}"><a:spcBef><a:spcPts val="0"/></a:spcBef><a:spcAft><a:spcPts val="{p.EspDepois}"/></a:spcAft><a:buNone/></a:pPr>""" +
               string.Concat(p.Runs.Where(r => r.Texto.Length > 0).Select(r => RunXml(r, lang))) + $"""<a:endParaRPr lang="{lang}" sz="{tam}" dirty="0"/></a:p>""";
    }

    private static string TxBody(IReadOnlyList<AxPar> pars, string ancora, long mh, long mv, string lang) =>
        $"""<p:txBody><a:bodyPr wrap="square" lIns="{L(mh)}" tIns="{L(mv)}" rIns="{L(mh)}" bIns="{L(mv)}" rtlCol="0" anchor="{ancora}"><a:noAutofit/></a:bodyPr><a:lstStyle/>""" +
        (pars.Count == 0 ? $"""<a:p><a:endParaRPr lang="{lang}" dirty="0"/></a:p>""" : string.Concat(pars.Select(p => ParXml(p, lang)))) + "</p:txBody>";

    private static string Xfrm(AxElem e) =>
        $"""<a:xfrm><a:off x="{L(e.X)}" y="{L(e.Y)}"/><a:ext cx="{L(e.W)}" cy="{L(e.H)}"/></a:xfrm>""";

    private static string SlideXml(AxSlide s, Dictionary<AxGrafico, string> graficos, string lang)
    {
        var sb = new StringBuilder();
        sb.Append($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld xmlns:a="{NsA}" xmlns:p="{NsP}" xmlns:r="{NsR}"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>""");
        // fundo do modelo (imagem de página inteira, como nos slides de referência)
        sb.Append($"""<p:pic><p:nvPicPr><p:cNvPr id="2" name="Template background" descr="Template background"/><p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed="rId2"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{AxMedida.Larg}" cy="{AxMedida.Alt}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic>""");
        var id = 3;
        foreach (var e in s.Elems)
        {
            switch (e)
            {
                case AxForma f:
                {
                    var nome = X(f.Nome.Length > 0 ? f.Nome : (f.Titulo ? "Title" : "Shape " + id));
                    var nv = f.Titulo
                        ? $"""<p:nvSpPr><p:cNvPr id="{id}" name="{nome}"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="ctrTitle"/></p:nvPr></p:nvSpPr>"""
                        : $"""<p:nvSpPr><p:cNvPr id="{id}" name="{nome}"/><p:cNvSpPr{(f.Fundo is null && f.Borda is null ? " txBox=\"1\"" : "")}/><p:nvPr/></p:nvSpPr>""";
                    var geo = f.Geo == "roundRect"
                        ? $"""<a:prstGeom prst="roundRect"><a:avLst><a:gd name="adj" fmla="val {f.Arredondado}"/></a:avLst></a:prstGeom>"""
                        : """<a:prstGeom prst="rect"><a:avLst/></a:prstGeom>""";
                    var fundo = f.Fundo is null ? "<a:noFill/>" : $"""<a:solidFill><a:srgbClr val="{f.Fundo}"/></a:solidFill>""";
                    var borda = f.Borda is null ? "<a:ln><a:noFill/></a:ln>"
                        : $"""<a:ln w="9525"><a:solidFill><a:srgbClr val="{f.Borda}"/></a:solidFill>{(f.Tracejada ? "<a:prstDash val=\"dash\"/>" : "")}</a:ln>""";
                    sb.Append("<p:sp>").Append(nv).Append("<p:spPr>").Append(Xfrm(f)).Append(geo).Append(fundo).Append(borda).Append("</p:spPr>")
                      .Append(TxBody(f.Pars, f.Ancora, f.MargemH, f.MargemV, lang)).Append("</p:sp>");
                    break;
                }
                case AxLinha l:
                    sb.Append($"""<p:cxnSp><p:nvCxnSpPr><p:cNvPr id="{id}" name="Line {id}"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr><p:spPr><a:xfrm><a:off x="{L(l.X)}" y="{L(l.Y)}"/><a:ext cx="{L(l.W)}" cy="0"/></a:xfrm><a:prstGeom prst="line"><a:avLst/></a:prstGeom><a:ln w="{L(l.Espessura)}"><a:solidFill><a:srgbClr val="{l.Cor}"/></a:solidFill></a:ln></p:spPr></p:cxnSp>""");
                    break;
                case AxGrafico g:
                    sb.Append($"""<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id="{id}" name="Chart {id}" descr="{X(g.Chart.Descricao)}"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x="{L(g.X)}" y="{L(g.Y)}"/><a:ext cx="{L(g.W)}" cy="{L(g.H)}"/></p:xfrm><a:graphic><a:graphicData uri="{NsC}"><c:chart xmlns:c="{NsC}" r:id="{graficos[g]}"/></a:graphicData></a:graphic></p:graphicFrame>""");
                    break;
                case AxTabela t:
                    sb.Append(TabelaXml(t, id, lang));
                    break;
            }
            id++;
        }
        sb.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        return sb.ToString();
    }

    private static string TabelaXml(AxTabela t, int id, string lang)
    {
        var soma = t.Pesos.Sum();
        var larg = t.Pesos.Select(p => (long)(t.W * p / soma)).ToArray();
        var hLinha = t.Linhas.Count > 0 ? t.H / t.Linhas.Count : t.H;
        var sb = new StringBuilder();
        sb.Append($"""<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id="{id}" name="{X(t.Nome)}"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp="1"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x="{L(t.X)}" y="{L(t.Y)}"/><a:ext cx="{L(larg.Sum())}" cy="{L(t.H)}"/></p:xfrm><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/table"><a:tbl><a:tblPr firstRow="{(t.Cabecalho ? 1 : 0)}" bandRow="0"/><a:tblGrid>""");
        foreach (var w in larg) sb.Append($"""<a:gridCol w="{L(w)}"/>""");
        sb.Append("</a:tblGrid>");
        for (var i = 0; i < t.Linhas.Count; i++)
        {
            sb.Append($"""<a:tr h="{L(hLinha)}">""");
            foreach (var c in t.Linhas[i])
            {
                var tam = c.Tam ?? t.Tam;
                var cab = t.Cabecalho && i == 0;
                var cor = cab ? "FFFFFF" : c.Cor;
                var fundo = cab ? AxCores.Escuro : c.Fundo;
                var run = c.Texto.Length == 0 ? "" : RunXml(new AxRun(c.Texto, tam, c.Negrito || cab, cor), lang);
                sb.Append($"""<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:pPr algn="{c.Alinh}"/>{run}<a:endParaRPr lang="{lang}" sz="{tam}" dirty="0"/></a:p></a:txBody>""");
                sb.Append("""<a:tcPr marL="54864" marR="54864" marT="18288" marB="18288" anchor="ctr">""");
                sb.Append("""<a:lnL w="6350"><a:noFill/></a:lnL><a:lnR w="6350"><a:noFill/></a:lnR><a:lnT w="6350"><a:noFill/></a:lnT>""");
                sb.Append($"""<a:lnB w="6350"><a:solidFill><a:srgbClr val="{AxCores.Linha}"/></a:solidFill></a:lnB>""");
                sb.Append(fundo is null ? "<a:noFill/>" : $"""<a:solidFill><a:srgbClr val="{fundo}"/></a:solidFill>""");
                sb.Append("</a:tcPr></a:tc>");
            }
            sb.Append("</a:tr>");
        }
        sb.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
        return sb.ToString();
    }

    private static string NotasXml(string notas, string lang)
    {
        var pars = string.Concat((notas.Length == 0 ? new[] { "" } : notas.Split('\n')).Select(l =>
            l.Length == 0 ? $"""<a:p><a:endParaRPr lang="{lang}" dirty="0"/></a:p>""" : $"""<a:p><a:r><a:rPr lang="{lang}" dirty="0"/><a:t>{X(l)}</a:t></a:r></a:p>"""));
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:notes xmlns:a="{NsA}" xmlns:p="{NsP}" xmlns:r="{NsR}"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr><p:sp><p:nvSpPr><p:cNvPr id="2" name="Slide Image Placeholder 1"/><p:cNvSpPr><a:spLocks noGrp="1" noRot="1" noChangeAspect="1"/></p:cNvSpPr><p:nvPr><p:ph type="sldImg" idx="2"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp><p:sp><p:nvSpPr><p:cNvPr id="3" name="Notes Placeholder 2"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="body" idx="3" sz="quarter"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{pars}</p:txBody></p:sp></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>""";
    }

    // ---------------- gráficos nativos (editáveis no PowerPoint) ----------------

    private static string Col(int i) => i < 26 ? ((char)('A' + i)).ToString() : "A" + (char)('A' + i - 26);

    /// <summary>A planilha embutida do gráfico ("Editar dados" no PowerPoint): categorias na coluna A, uma série por coluna.</summary>
    private static byte[] Planilha(AxChart c)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        for (var j = 0; j < c.Series.Count; j++) ws.Cell(1, j + 2).Value = c.Series[j].Nome;
        for (var i = 0; i < c.Categorias.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = c.Categorias[i];
            for (var j = 0; j < c.Series.Count; j++)
                if (i < c.Series[j].Valores.Length && c.Series[j].Valores[i] is double v) ws.Cell(i + 2, j + 2).Value = v;
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static string TxPr(int tam, bool negrito, string cor, string lang) =>
        $"""<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz="{tam}" b="{(negrito ? 1 : 0)}"><a:solidFill><a:srgbClr val="{cor}"/></a:solidFill><a:latin typeface="{Fonte}"/><a:cs typeface="{Fonte}"/></a:defRPr></a:pPr><a:endParaRPr lang="{lang}"/></a:p></c:txPr>""";

    private static string Rotulos(string formato, string pos, int tam, string cor, string lang) =>
        $"""<c:dLbls><c:numFmt formatCode="{X(formato)}" sourceLinked="0"/><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>{TxPr(tam, true, cor, lang)}<c:dLblPos val="{pos}"/><c:showLegendKey val="0"/><c:showVal val="1"/><c:showCatName val="0"/><c:showSerName val="0"/><c:showPercent val="0"/><c:showBubbleSize val="0"/></c:dLbls>""";

    private static string SerieXml(AxChart c, AxSerie s, int idx, bool linha, string lang)
    {
        var n = c.Categorias.Length;
        var col = Col(idx + 1);
        var sb = new StringBuilder();
        sb.Append($"""<c:ser><c:idx val="{idx}"/><c:order val="{idx}"/><c:tx><c:strRef><c:f>Sheet1!${col}$1</c:f><c:strCache><c:ptCount val="1"/><c:pt idx="0"><c:v>{X(s.Nome)}</c:v></c:pt></c:strCache></c:strRef></c:tx>""");
        if (linha)
        {
            sb.Append($"""<c:spPr><a:ln w="{L(s.Espessura)}" cap="rnd"><a:solidFill><a:srgbClr val="{s.Cor}"/></a:solidFill>{(s.Tracejada ? "<a:prstDash val=\"dash\"/>" : "")}<a:round/></a:ln></c:spPr>""");
            sb.Append($"""<c:marker><c:symbol val="circle"/><c:size val="5"/><c:spPr><a:solidFill><a:srgbClr val="{s.Cor}"/></a:solidFill><a:ln w="9525"><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill></a:ln></c:spPr></c:marker>""");
            if (s.Rotulos) sb.Append(Rotulos(s.Formato ?? c.Formato, "t", c.TamFonte, s.Cor, lang));
        }
        else
        {
            sb.Append($"""<c:spPr><a:solidFill><a:srgbClr val="{s.Cor}"/></a:solidFill><a:ln><a:noFill/></a:ln></c:spPr><c:invertIfNegative val="0"/>""");
            if (s.CoresPontos is not null)
                for (var i = 0; i < s.CoresPontos.Length && i < n; i++)
                    if (s.CoresPontos[i] is string cp)
                        sb.Append($"""<c:dPt><c:idx val="{i}"/><c:invertIfNegative val="0"/><c:bubble3D val="0"/><c:spPr><a:solidFill><a:srgbClr val="{cp}"/></a:solidFill><a:ln><a:noFill/></a:ln></c:spPr></c:dPt>""");
            var empilhado = c.Tipo is AxTipoGrafico.ColunasEmpilhadas or AxTipoGrafico.BarrasEmpilhadas;
            if (s.Rotulos) sb.Append(Rotulos(s.Formato ?? c.Formato, empilhado ? "ctr" : "outEnd", c.TamFonte, empilhado ? "FFFFFF" : AxCores.Escuro, lang));
        }
        sb.Append($"""<c:cat><c:strRef><c:f>Sheet1!$A$2:$A${n + 1}</c:f><c:strCache><c:ptCount val="{n}"/>""");
        for (var i = 0; i < n; i++) sb.Append($"""<c:pt idx="{i}"><c:v>{X(c.Categorias[i])}</c:v></c:pt>""");
        sb.Append($"""</c:strCache></c:strRef></c:cat><c:val><c:numRef><c:f>Sheet1!${col}$2:${col}${n + 1}</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val="{n}"/>""");
        for (var i = 0; i < n && i < s.Valores.Length; i++)
            if (s.Valores[i] is double v && double.IsFinite(v)) sb.Append($"""<c:pt idx="{i}"><c:v>{D(v)}</c:v></c:pt>""");
        sb.Append("</c:numCache></c:numRef></c:val>");
        if (linha) sb.Append("""<c:smooth val="0"/>""");
        sb.Append("</c:ser>");
        return sb.ToString();
    }

    private static string Eixos(AxChart c, int idCat, int idVal, bool secundario, bool horizontal, double? min, double? max, string lang)
    {
        var posCat = horizontal ? "l" : "b";
        var posVal = secundario ? "r" : horizontal ? "b" : "l";
        var escala = "<c:scaling><c:orientation val=\"minMax\"/>" +
                     (max is double mx ? $"""<c:max val="{D(mx)}"/>""" : "") + (min is double mn ? $"""<c:min val="{D(mn)}"/>""" : "") + "</c:scaling>";
        var escalaCat = "<c:scaling><c:orientation val=\"" + (horizontal ? "maxMin" : "minMax") + "\"/></c:scaling>";
        var valVisivel = c.EixoValores && !secundario;
        var cat = $"""<c:catAx><c:axId val="{idCat}"/>{escalaCat}<c:delete val="{(secundario ? 1 : 0)}"/><c:axPos val="{posCat}"/><c:numFmt formatCode="General" sourceLinked="0"/><c:majorTickMark val="none"/><c:minorTickMark val="none"/><c:tickLblPos val="low"/><c:spPr><a:ln w="9525"><a:solidFill><a:srgbClr val="B6CDC6"/></a:solidFill></a:ln></c:spPr>{TxPr(c.TamFonte, false, AxCores.Texto, lang)}<c:crossAx val="{idVal}"/><c:crosses val="autoZero"/><c:auto val="1"/><c:lblAlgn val="ctr"/><c:lblOffset val="100"/><c:noMultiLvlLbl val="0"/></c:catAx>""";
        var grade = valVisivel ? """<c:majorGridlines><c:spPr><a:ln w="6350"><a:solidFill><a:srgbClr val="E3ECE9"/></a:solidFill></a:ln></c:spPr></c:majorGridlines>""" : "";
        var fmt = secundario ? (c.Series.FirstOrDefault(s => s.Linha)?.Formato ?? c.Formato) : c.Formato;
        // o eixo secundário (da linha) fica "existente mas invisível": apagado, alguns leitores (LibreOffice) passam a linha para a escala das colunas
        var val = $"""<c:valAx><c:axId val="{idVal}"/>{escala}<c:delete val="{(valVisivel || secundario ? 0 : 1)}"/><c:axPos val="{posVal}"/>{grade}<c:numFmt formatCode="{X(fmt)}" sourceLinked="0"/><c:majorTickMark val="none"/><c:minorTickMark val="none"/><c:tickLblPos val="{(secundario ? "none" : "nextTo")}"/><c:spPr><a:ln><a:noFill/></a:ln></c:spPr>{TxPr(c.TamFonte - 50, false, AxCores.Cinza, lang)}<c:crossAx val="{idCat}"/><c:crosses val="{(secundario ? "max" : "autoZero")}"/><c:crossBetween val="between"/></c:valAx>""";
        return cat + val;
    }

    /// <summary>
    /// Combo (colunas + linha em eixo próprio): as colunas ficam na faixa de baixo (até ~55% da altura) e a linha
    /// na de cima (topo em ~93%), as duas a partir do zero — os rótulos não se sobrepõem e nada é exagerado.
    /// </summary>
    public static (double? MaxPri, double? MinSec, double? MaxSec) FaixasCombo(AxChart c)
    {
        var barras = c.Series.Where(s => c.Tipo != AxTipoGrafico.Linhas && !s.Linha).ToList();
        var linhas = c.Series.Where(s => c.Tipo == AxTipoGrafico.Linhas || s.Linha).ToList();
        if (barras.Count == 0 || linhas.Count == 0) return (null, null, null);
        var n = c.Categorias.Length;
        var emp = c.Tipo is AxTipoGrafico.ColunasEmpilhadas or AxTipoGrafico.BarrasEmpilhadas;
        var maxB = emp ? Enumerable.Range(0, n).Select(i => barras.Sum(s => Math.Max(0, s.Valores.ElementAtOrDefault(i) ?? 0))).DefaultIfEmpty(0).Max()
                       : barras.SelectMany(s => s.Valores).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var vl = linhas.SelectMany(s => s.Valores).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).ToList();
        return (maxB > 0 ? maxB / 0.55 : null, Math.Min(0, vl.Min()), vl.Max() > 0 ? vl.Max() / 0.93 : 1);
    }

    public static string ChartXml(AxChart c, string lang)
    {
        var sb = new StringBuilder();
        sb.Append($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><c:chartSpace xmlns:c="{NsC}" xmlns:a="{NsA}" xmlns:r="{NsR}"><c:date1904 val="0"/><c:lang val="{lang}"/><c:roundedCorners val="0"/><c:chart><c:autoTitleDeleted val="1"/><c:plotArea><c:layout/>""");
        var barras = c.Series.Select((s, i) => (s, i)).Where(x => c.Tipo != AxTipoGrafico.Linhas && !x.s.Linha).ToList();
        var linhas = c.Series.Select((s, i) => (s, i)).Where(x => c.Tipo == AxTipoGrafico.Linhas || x.s.Linha).ToList();
        var horizontal = c.Tipo is AxTipoGrafico.Barras or AxTipoGrafico.BarrasEmpilhadas;
        if (barras.Count > 0)
        {
            var emp = c.Tipo is AxTipoGrafico.ColunasEmpilhadas or AxTipoGrafico.BarrasEmpilhadas;
            sb.Append($"""<c:barChart><c:barDir val="{(horizontal ? "bar" : "col")}"/><c:grouping val="{(emp ? "stacked" : "clustered")}"/><c:varyColors val="0"/>""");
            foreach (var (s, i) in barras) sb.Append(SerieXml(c, s, i, false, lang));
            sb.Append($"""<c:gapWidth val="{c.Espaco}"/>{(emp ? "<c:overlap val=\"100\"/>" : "<c:overlap val=\"-8\"/>")}<c:axId val="5001"/><c:axId val="5002"/></c:barChart>""");
        }
        var linhaSecundaria = barras.Count > 0 && linhas.Count > 0;
        if (linhas.Count > 0)
        {
            sb.Append("""<c:lineChart><c:grouping val="standard"/><c:varyColors val="0"/>""");
            foreach (var (s, i) in linhas) sb.Append(SerieXml(c, s, i, true, lang));
            sb.Append($"""<c:marker val="1"/><c:axId val="{(linhaSecundaria ? 5003 : 5001)}"/><c:axId val="{(linhaSecundaria ? 5004 : 5002)}"/></c:lineChart>""");
        }
        var (maxPri, minSec, maxSec) = FaixasCombo(c);
        sb.Append(Eixos(c, 5001, 5002, false, horizontal, c.Min, c.Max ?? maxPri, lang));
        if (linhaSecundaria) sb.Append(Eixos(c, 5003, 5004, true, false, minSec, maxSec, lang));
        sb.Append("<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr></c:plotArea>");
        if (c.Legenda) sb.Append($"""<c:legend><c:legendPos val="t"/><c:overlay val="0"/>{TxPr(c.TamFonte, false, AxCores.Texto, lang)}</c:legend>""");
        sb.Append($"""<c:plotVisOnly val="1"/><c:dispBlanksAs val="gap"/></c:chart><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>{TxPr(c.TamFonte, false, AxCores.Texto, lang)}<c:externalData r:id="rId1"><c:autoUpdate val="0"/></c:externalData></c:chartSpace>""");
        return sb.ToString();
    }
}
