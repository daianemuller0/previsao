using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Módulo Conversão · leitura da planilha de propostas (pasta da rede).
//
// A pasta tem UMA planilha (.xlsx/.xlsm/.xls/.csv; se houver mais, usa a mais recente e avisa). A
// leitura reaproveita a infraestrutura do projeto: LeituraSegura (copia para a
// memória sem travar quem salva) e OpportunityImporter.LerGrade.
//
// Nada é inventado: célula vazia continua vazia (valor sem número NÃO vira zero),
// data inválida e valor ambíguo são CONTADOS e mostrados, e nenhuma linha é
// descartada em silêncio. Só são ignoradas linhas totalmente em branco e linhas
// de total (e isso também é contado).
//
// Colunas pelo mapeamento combinado (letras do Excel, configuráveis em
// Conversao:Colunas): K vendedor · P valor US$ · T status · V BU · AD data de
// envio (AC) · AD aplicador · AE/AF BU intercompany (AF primeiro; vazia, AE).
// ---------------------------------------------------------------------------
public enum ClasseProposta { Ganha, Perdida, Aberta, NaoClassificada }

public sealed record PropostaConv(
    int Linha,                  // número da linha na planilha (auditoria)
    string Vendedor,            // coluna K
    string Aplicador,           // coluna AD
    double? ValorUsd,           // null = sem valor numérico válido
    string ValorTexto,          // como veio, para auditoria
    string StatusOriginal,
    ClasseProposta Classe,
    string Bu,                  // coluna V
    string BuIc,                // AF, senão AE, senão "Não é Intercompany"
    DateTime? Data,             // coluna AC
    string DataTexto,
    string Chave,               // identificador, se a planilha tiver coluna de número da proposta
    string[] Celulas);          // linha original completa

public sealed record ColunaOrigem(int Indice, string Letra, string Titulo);

public sealed class DadosConversao
{
    public List<PropostaConv> Propostas { get; } = new();
    public List<ColunaOrigem> Colunas { get; } = new();
    public List<string> Avisos { get; } = new();
    /// <summary>"K · título na planilha" de cada coluna usada, para conferência na tela.</summary>
    public List<string> Mapeamento { get; } = new();
    public int LinhasLidas { get; set; }
    public int SemData { get; set; }
    public int DatasInvalidas { get; set; }
    public int SemValor { get; set; }
    public int ValoresInvalidos { get; set; }
    public int Duplicadas { get; set; }
    public int TotaisIgnorados { get; set; }
}

public sealed class ConversaoService
{
    public const string NaoInformado = "Não informado";
    /// <summary>BU Intercompany vazia (AF e AE em branco): a proposta não é intercompany.</summary>
    public const string NaoIntercompany = "Não é Intercompany";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Status (já normalizados) → classe. O que não estiver aqui é "Não classificada".
    private static readonly HashSet<string> Perdidas = new(StringComparer.Ordinal)
        { "cancelled", "closed-customer lost", "closed-lost", "closed-no bid", "closed-unknown" };
    private static readonly HashSet<string> Ganhas = new(StringComparer.Ordinal)
        { "closed-won", "otp" };
    private static readonly HashSet<string> Abertas = new(StringComparer.Ordinal)
        { "open" };

    private readonly IConfiguration _cfg;
    private readonly object _lock = new();
    private DadosConversao _atual = new();
    private string _marca = "";

    public ConversaoService(IConfiguration cfg) => _cfg = cfg;

    /// <summary>Sobe a cada leitura nova: as telas percebem e recalculam.</summary>
    public int Version { get; private set; }
    public bool Ok { get; private set; }
    public string Mensagem { get; private set; } = "Ainda não carregado.";
    public DateTime? AtualizadoEm { get; private set; }

    public DadosConversao Atual { get { lock (_lock) return _atual; } }

    // ---- classificação ---------------------------------------------------------
    /// <summary>Texto do status sem diferença de caixa, acento, espaço ou tipo de hífen.</summary>
    public static string NormalizarStatus(string? s) =>
        Regex.Replace(OpportunityImporter.Normalizar(s ?? ""), @"\s*[-–—]\s*", "-");

    public static ClasseProposta Classificar(string? status)
    {
        var n = NormalizarStatus(status);
        if (Ganhas.Contains(n)) return ClasseProposta.Ganha;
        if (Perdidas.Contains(n)) return ClasseProposta.Perdida;
        if (Abertas.Contains(n)) return ClasseProposta.Aberta;
        return ClasseProposta.NaoClassificada;
    }

    // ---- leitura ----------------------------------------------------------------
    /// <summary>Lê a planilha se ela mudou (ou se <paramref name="force"/>). Nunca lança:
    /// falha vira mensagem, e o que já estava carregado continua valendo.</summary>
    public void Carregar(bool force = false)
    {
        lock (_lock)
        {
            try
            {
                var file = Resolver(out var aviso);
                if (file is null)
                {
                    Falhou(aviso != "" ? aviso : "Planilha de conversão não encontrada.");
                    return;
                }
                var fi = new FileInfo(file);
                var marca = $"{fi.Name}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
                if (!force && Ok && marca == _marca) return;

                var bytes = LeituraSegura.Bytes(file);
                using var ms = new MemoryStream(bytes);
                var grade = OpportunityImporter.LerGrade(fi.Name, ms, manterLinhasVazias: true,
                    aba: _cfg["Conversao:Aba"], maiorAba: true);
                var dados = Montar(grade);
                if (aviso != "") dados.Avisos.Insert(0, aviso);

                _atual = dados; _marca = marca;
                Ok = true; AtualizadoEm = DateTime.Now;
                Mensagem = $"{dados.Propostas.Count} propostas carregadas.";
                Version++;
            }
            catch (Exception ex)
            {
                Falhou("Não foi possível ler a planilha agora." + (ex is IOException ? " O arquivo pode estar sendo gravado; tente novamente em instantes." : ""));
            }
        }
    }

    private void Falhou(string msg)
    {
        Ok = false;
        Mensagem = _atual.Propostas.Count > 0 && AtualizadoEm is { } t
            ? $"{msg} Exibindo os dados de {t:dd/MM/yyyy HH:mm}."
            : msg;
        Version++;
    }

    /// <summary>Formatos aceitos (o CRM exporta .xls; também vale .xlsx/.xlsm/.csv).</summary>
    private static readonly string[] Exts = { ".xlsx", ".xlsm", ".xls", ".csv" };

    private string? Resolver(out string aviso)
    {
        aviso = "";
        var path = (_cfg["Conversao:Path"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path)) { aviso = "Caminho da planilha de conversão não configurado (Conversao:Path)."; return null; }
        try
        {
            if (File.Exists(path)) return path;
            var pasta = Pasta(path);
            if (pasta is null) { aviso = $"Pasta da planilha de conversão não encontrada ou sem acesso: {path}"; return null; }
            var arqs = pasta.EnumerateFiles()
                .Where(f => Exts.Contains(f.Extension.ToLowerInvariant()) && !f.Name.StartsWith("~$", StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            if (arqs.Count == 0)
            {
                aviso = $"Nenhuma planilha (.xlsx, .xlsm, .xls ou .csv) na pasta {pasta.FullName}.";
                return null;
            }
            if (arqs.Count > 1)
                aviso = $"A pasta tem {arqs.Count} planilhas; foi usada a mais recente ({arqs[0].Name}).";
            return arqs[0].FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            aviso = $"Sem acesso à pasta da planilha de conversão ({path}).";
            return null;
        }
    }

    /// <summary>
    /// A pasta configurada. Se o nome com acento ("Conversão") não bater exatamente — acento
    /// gravado em outra forma Unicode, ou digitado sem acento —, procura na pasta-mãe um nome
    /// equivalente ignorando acentos e maiúsculas.
    /// </summary>
    private static DirectoryInfo? Pasta(string path)
    {
        if (Directory.Exists(path)) return new DirectoryInfo(path);
        var di = new DirectoryInfo(path.TrimEnd('\\', '/'));
        var mae = di.Parent;
        if (mae is null || !mae.Exists) return null;
        var alvo = SemAcento(di.Name);
        return mae.EnumerateDirectories().FirstOrDefault(d => SemAcento(d.Name) == alvo);
    }

    private static string SemAcento(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s.Normalize(System.Text.NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    // ---- montagem ---------------------------------------------------------------
    private DadosConversao Montar(List<OpportunityImporter.Cel[]> grade)
    {
        var d = new DadosConversao();

        int Col(string nome, string padrao)
        {
            var letra = (_cfg[$"Conversao:Colunas:{nome}"] ?? padrao).Trim();
            return AplicacaoService.Idx(letra);
        }
        var iVend = Col("Vendedor", "K"); var iValor = Col("ValorUsd", "P"); var iStatus = Col("Status", "T");
        var iBu = Col("Bu", "V"); var iData = Col("DataEnvio", "AC"); var iAplic = Col("Aplicador", "AD");
        var iBuIcAlt = Col("BuIcAlternativa", "AE"); var iBuIcPrinc = Col("BuIcPrincipal", "AF");

        // Linha do cabeçalho: configurada (1 = primeira) ou a primeira linha "cheia".
        static int Cheias(OpportunityImporter.Cel[] r) => r.Count(c => !string.IsNullOrWhiteSpace(c.Text));
        var h = _cfg.GetValue("Conversao:HeaderRow", 0) - 1;
        if (h < 0)
        {
            var janela = grade.Take(40).ToList();
            var max = janela.Count == 0 ? 0 : janela.Max(Cheias);
            h = 0;
            for (var i = 0; i < janela.Count; i++)
                if (Cheias(janela[i]) >= 3 && Cheias(janela[i]) * 2 >= max) { h = i; break; }
        }
        if (grade.Count <= h) { d.Avisos.Add("A planilha não tem linhas de dados."); return d; }
        var cab = grade[h];

        string Cel(OpportunityImporter.Cel[] r, int i) => i >= 0 && i < r.Length ? (r[i].Text ?? "").Trim() : "";
        string Titulo(int i) => Cel(cab, i);

        for (var i = 0; i < cab.Length; i++)
            if (Titulo(i) != "") d.Colunas.Add(new ColunaOrigem(i, AplicacaoService.Letra(i), Titulo(i)));
        foreach (var (rot, i) in new[] { ("Vendedor", iVend), ("Aplicador", iAplic), ("Valor US$", iValor), ("Status", iStatus), ("BU", iBu),
                                         ("Data de envio", iData), ("BU Intercompany (alternativa)", iBuIcAlt), ("BU Intercompany (principal)", iBuIcPrinc) })
        {
            d.Mapeamento.Add($"{rot}: coluna {AplicacaoService.Letra(i)} · \"{(Titulo(i) == "" ? "sem título" : Titulo(i))}\"");
            if (Titulo(i) == "") d.Avisos.Add($"A coluna {AplicacaoService.Letra(i)} ({rot}) está sem título no cabeçalho: confira se o mapeamento ainda vale.");
        }

        // Coluna com o número da proposta (para checar duplicidade), se existir.
        var chaves = new[] { "quotenumber", "quote number", "proposta", "proposal", "opportunity number", "numero da proposta" };
        var iChave = d.Colunas.FirstOrDefault(c => chaves.Contains(OpportunityImporter.Normalizar(c.Titulo)))?.Indice ?? -1;

        var vistasChave = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vistasLinha = new HashSet<string>(StringComparer.Ordinal);
        var largura = cab.Length;

        for (var n = h + 1; n < grade.Count; n++)
        {
            var row = grade[n];
            if (Cheias(row) == 0) continue;                       // linha em branco
            d.LinhasLidas++;

            var primeira = row.Select(c => OpportunityImporter.Normalizar(c.Text ?? "")).FirstOrDefault(x => x != "") ?? "";
            if (primeira is "total" or "totais" or "grand total" or "subtotal") { d.TotaisIgnorados++; continue; }

            var (valor, invalido) = Valor(iValor >= 0 && iValor < row.Length ? row[iValor] : default);
            var valorTxt = Cel(row, iValor);
            if (valor is null && invalido) d.ValoresInvalidos++;
            else if (valor is null) d.SemValor++;

            var dataTxt = Cel(row, iData);
            DateTime? data = dataTxt == "" ? null : AplicacaoService.Data(dataTxt);
            if (dataTxt == "") d.SemData++;
            else if (data is null) d.DatasInvalidas++;

            var status = Cel(row, iStatus);
            var vend = Cel(row, iVend); var aplic = Cel(row, iAplic);
            var bu = Cel(row, iBu);
            var buIc = Cel(row, iBuIcPrinc);                      // AF primeiro; vazia/nula/espaços → AE
            if (buIc == "") buIc = Cel(row, iBuIcAlt);
            if (buIc == "") buIc = NaoIntercompany;

            var chave = iChave >= 0 ? Cel(row, iChave) : "";
            var cels = new string[largura];
            for (var i = 0; i < largura; i++) cels[i] = Cel(row, i);

            var assinatura = string.Join("\u001F", cels);
            if ((chave != "" && !vistasChave.Add(chave)) || (chave == "" && !vistasLinha.Add(assinatura))) d.Duplicadas++;

            d.Propostas.Add(new PropostaConv(n + 1, vend == "" ? NaoInformado : vend, aplic == "" ? NaoInformado : aplic, valor, valorTxt, status,
                Classificar(status), bu == "" ? NaoInformado : bu, buIc, data, dataTxt, chave, cels));
        }

        if (d.Duplicadas > 0)
            d.Avisos.Add($"{d.Duplicadas} registro(s) parecem duplicados ({(iChave >= 0 ? "mesmo número de proposta" : "linhas idênticas")}). Todos foram mantidos; confira na Base de Propostas.");
        if (d.DatasInvalidas > 0) d.Avisos.Add($"{d.DatasInvalidas} registro(s) com data de envio inválida: ficam sem ano e sem conversão cambial.");
        if (d.SemData > 0) d.Avisos.Add($"{d.SemData} registro(s) sem data de envio: ficam sem ano e sem conversão cambial.");
        if (d.ValoresInvalidos > 0) d.Avisos.Add($"{d.ValoresInvalidos} registro(s) com valor em US$ ilegível ou ambíguo: contam nas quantidades, mas não nos valores.");
        if (d.SemValor > 0) d.Avisos.Add($"{d.SemValor} registro(s) sem valor em US$: contam nas quantidades, mas não nos valores.");
        if (d.TotaisIgnorados > 0) d.Avisos.Add($"{d.TotaisIgnorados} linha(s) de total da planilha foram ignoradas.");
        return d;
    }

    /// <summary>Valor numérico da célula. Número nativo vale como veio; texto usa o último
    /// separador como decimal. Texto ambíguo ("1.234", "1,234") é recusado, não adivinhado.</summary>
    private static (double? Valor, bool Invalido) Valor(OpportunityImporter.Cel c)
    {
        if (c.Num is { } n) return (n, false);
        var t = (c.Text ?? "").Trim();
        if (t == "") return (null, false);

        var neg = t.StartsWith('(') && t.EndsWith(')');
        t = t.Trim('(', ')').Replace("US$", "", StringComparison.OrdinalIgnoreCase).Replace("USD", "", StringComparison.OrdinalIgnoreCase)
             .Replace("$", "").Replace(" ", "").Replace(" ", "");
        if (t.StartsWith('-')) { neg = true; t = t[1..]; }
        if (t == "" || !t.All(ch => char.IsDigit(ch) || ch is ',' or '.')) return (null, true);

        int virgulas = t.Count(ch => ch == ','), pontos = t.Count(ch => ch == '.');
        int lv = t.LastIndexOf(','), lp = t.LastIndexOf('.');
        if (virgulas > 0 && pontos > 0)
            t = lv > lp ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        else if (virgulas > 0 || pontos > 0)
        {
            var sep = virgulas > 0 ? ',' : '.';
            var qtd = virgulas > 0 ? virgulas : pontos;
            if (qtd > 1) t = t.Replace(sep.ToString(), "");                       // "1.234.567" → milhares
            else if (t.Length - t.IndexOf(sep) - 1 == 3) return (null, true);     // "1.234": milhar ou decimal?
            else t = t.Replace(sep, '.');
        }
        return double.TryParse(t, NumberStyles.Float, Inv, out var v) ? (neg ? -v : v, false) : (null, true);
    }
}
