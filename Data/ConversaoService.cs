using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Módulo Conversão · leitura das planilhas de propostas (pasta da rede).
//
// A pasta tem DUAS planilhas exportadas do CRM, uma por origem: AFM
// ("group- AFM_Conversao") e NB ("group-NB_Conversao"). Cada origem tem o seu
// mapa de colunas, a sua linha de cabeçalho e as suas equivalências (status,
// nomes de vendedor, BU), em Conversao:Fontes. A leitura reaproveita a
// infraestrutura do projeto: LeituraSegura (copia para a memória sem travar
// quem salva) e OpportunityImporter.LerGrade.
//
// Nada é inventado: célula vazia continua vazia (valor sem número NÃO vira zero),
// data inválida e valor ambíguo são CONTADOS e mostrados, e nenhuma linha é
// descartada em silêncio. Só são ignoradas linhas totalmente em branco e linhas
// de total (e isso também é contado).
//
// AFM (cabeçalho na linha 1): K vendedor · P valor US$ · T status · V BU ·
//   AC data de envio · AF/AE BU intercompany (AF primeiro; vazia, AE) ·
//   F mercado · C país · I produto · D cliente.
// NB (linhas 1–14 são resumo; cabeçalho na 15): L vendedor · P valor JÁ EM R$ ·
//   R status · V BU ("HSA: Howden South America" → HSA) · AB data de envio ·
//   F mercado · D país · J produto · M cliente. NB não tem BU intercompany.
// ---------------------------------------------------------------------------
public enum ClasseProposta { Ganha, Perdida, Aberta, NaoClassificada }

public sealed record PropostaConv(
    string Origem,              // "AFM" ou "NB"
    int Linha,                  // número da linha na planilha (auditoria)
    string Vendedor,            // AFM K · NB L (nome do NB levado ao nome do AFM)
    double? ValorUsd,           // AFM: valor em US$ (null = sem valor numérico válido)
    double? ValorBrl,           // NB: o valor já vem em R$ (null no AFM, que é convertido pela taxa do ano)
    string ValorTexto,          // como veio, para auditoria
    string StatusOriginal,
    ClasseProposta Classe,
    string Bu,                  // coluna V (no NB, "HSA: Howden South America" → "HSA")
    string BuIc,                // AFM: AF, senão AE, senão "Não é Intercompany" · NB: sempre "Não é Intercompany"
    DateTime? Data,             // AFM AC · NB AB
    string DataTexto,
    string Mercado,
    string Pais,
    string Produto,
    string Cliente,
    string Chave,               // identificador, se a planilha tiver coluna de número da proposta
    string[] Celulas)           // linha original completa
{
    /// <summary>Tem valor numérico (em US$ no AFM ou já em R$ no NB).</summary>
    public bool TemValor => ValorUsd is not null || ValorBrl is not null;
}

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

    public const string Afm = "AFM", Nb = "NB";
    public static readonly string[] Origens = { Afm, Nb };

    // Status → classe, por origem (comparados já normalizados). O que não estiver aqui é "Não classificada".
    private static readonly Dictionary<string, (HashSet<string> Ganhas, HashSet<string> Perdidas, HashSet<string> Abertas)> StatusPorOrigem = new()
    {
        [Afm] = (Conjunto("Closed - Won", "OTP"),
                 Conjunto("Cancelled", "Closed - Customer Lost", "Closed - Lost", "Closed - No Bid", "Closed - Unknown"),
                 Conjunto("Open")),
        [Nb]  = (Conjunto("Closed Won", "E1 Order Entry"),
                 Conjunto("Closed Abandoned", "Closed Lost"),
                 Conjunto("Qualification", "Proposal Pending/Waiting", "Outstanding Quote--No Formal Negotiation",
                          "Negotiation Phase/Review", "Arrived at Agreement on Need", "T&C's stage", "Verbal Commitment")),
    };
    private static HashSet<string> Conjunto(params string[] s) => new(s.Select(NormalizarStatus), StringComparer.Ordinal);

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

    public static ClasseProposta Classificar(string? status, string origem = Afm)
    {
        var n = NormalizarStatus(status);
        var (g, p, a) = StatusPorOrigem.TryGetValue(origem, out var t) ? t : StatusPorOrigem[Afm];
        if (g.Contains(n)) return ClasseProposta.Ganha;
        if (p.Contains(n)) return ClasseProposta.Perdida;
        if (a.Contains(n)) return ClasseProposta.Aberta;
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
                var arquivos = Resolver(out var aviso);
                if (arquivos.Count == 0)
                {
                    Falhou(aviso != "" ? aviso : "Planilhas de conversão não encontradas.");
                    return;
                }
                var marca = string.Join(";", arquivos.Select(a => { var fi = new FileInfo(a.Arquivo); return $"{a.Origem}|{fi.Name}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}"; }));
                if (!force && Ok && marca == _marca) return;

                var dados = new DadosConversao();
                if (aviso != "") dados.Avisos.Add(aviso);
                foreach (var (origem, arquivo) in arquivos)
                {
                    var fi = new FileInfo(arquivo);
                    var bytes = LeituraSegura.Bytes(arquivo);
                    using var ms = new MemoryStream(bytes);
                    var grade = OpportunityImporter.LerGrade(fi.Name, ms, manterLinhasVazias: true,
                        aba: _cfg["Conversao:Aba"], maiorAba: true);
                    Montar(dados, origem, fi.Name, grade);
                }

                _atual = dados; _marca = marca;
                Ok = true; AtualizadoEm = DateTime.Now;
                Mensagem = $"{dados.Propostas.Count} propostas carregadas ({string.Join(" · ", arquivos.Select(a => $"{a.Origem} {dados.Propostas.Count(p => p.Origem == a.Origem)}"))}).";
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

    /// <summary>
    /// Acha a planilha de cada origem na pasta: pelo nome configurado (Conversao:Fontes:X:Arquivo,
    /// sem diferença de extensão, espaço, hífen ou maiúscula) e, se não houver, por um nome que
    /// contenha a sigla da origem ("..._AFM_...", "...-NB_..."). Falta de uma origem vira aviso; a
    /// outra é lida mesmo assim.
    /// </summary>
    private List<(string Origem, string Arquivo)> Resolver(out string aviso)
    {
        aviso = "";
        var res = new List<(string, string)>();
        var path = (_cfg["Conversao:Path"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path)) { aviso = "Caminho das planilhas de conversão não configurado (Conversao:Path)."; return res; }
        try
        {
            var pasta = Pasta(path);
            if (pasta is null) { aviso = $"Pasta das planilhas de conversão não encontrada ou sem acesso: {path}"; return res; }
            var arqs = pasta.EnumerateFiles()
                .Where(f => Exts.Contains(f.Extension.ToLowerInvariant()) && !f.Name.StartsWith("~$", StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            if (arqs.Count == 0)
            {
                aviso = $"Nenhuma planilha (.xlsx, .xlsm, .xls ou .csv) na pasta {pasta.FullName}.";
                return res;
            }
            var faltam = new List<string>();
            foreach (var origem in Origens)
            {
                var nome = Chave(_cfg[$"Conversao:Fontes:{origem}:Arquivo"] ?? "");
                var sigla = new Regex($@"(^|[^a-z]){origem.ToLowerInvariant()}([^a-z]|$)");
                var f = arqs.FirstOrDefault(a => nome != "" && Chave(Path.GetFileNameWithoutExtension(a.Name)) == nome)
                        ?? arqs.FirstOrDefault(a => sigla.IsMatch(SemAcento(Path.GetFileNameWithoutExtension(a.Name))));
                if (f is null) faltam.Add(origem);
                else res.Add((origem, f.FullName));
            }
            if (faltam.Count > 0)
                aviso = $"Planilha de {string.Join(" e ", faltam)} não encontrada na pasta {pasta.FullName}" + (res.Count > 0 ? $": exibindo só {string.Join(" e ", res.Select(r => r.Item1))}." : ".");
            return res;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            aviso = $"Sem acesso à pasta das planilhas de conversão ({path}).";
            return res;
        }
    }

    /// <summary>Nome comparável: sem acento, maiúscula, espaço, hífen ou sublinhado.</summary>
    private static string Chave(string s) => new(SemAcento(s).Where(char.IsLetterOrDigit).ToArray());

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
    // Letras padrão de cada origem (Conversao:Fontes:<origem>:Colunas sobrepõe).
    private static readonly Dictionary<string, Dictionary<string, string>> ColunasPadrao = new()
    {
        [Afm] = new() { ["Vendedor"] = "K", ["Valor"] = "P", ["Status"] = "T", ["Bu"] = "V", ["DataEnvio"] = "AC",
                        ["BuIcPrincipal"] = "AF", ["BuIcAlternativa"] = "AE",
                        ["Mercado"] = "F", ["Pais"] = "C", ["Produto"] = "I", ["Cliente"] = "D" },
        [Nb]  = new() { ["Vendedor"] = "L", ["Valor"] = "P", ["Status"] = "R", ["Bu"] = "V", ["DataEnvio"] = "AB",
                        ["Mercado"] = "F", ["Pais"] = "D", ["Produto"] = "J", ["Cliente"] = "M" },
    };
    private static readonly Dictionary<string, int> CabecalhoPadrao = new() { [Afm] = 1, [Nb] = 15 };

    /// <summary>Equivalências da origem (ex.: nome do vendedor no NB → nome no AFM), sem diferença de acento/caixa.</summary>
    private Dictionary<string, string> Equivalencias(string origem, string secao) =>
        _cfg.GetSection($"Conversao:Fontes:{origem}:{secao}").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .GroupBy(c => Chave(c.Key)).ToDictionary(g => g.Key, g => g.First().Value!.Trim());

    private void Montar(DadosConversao d, string origem, string arquivo, List<OpportunityImporter.Cel[]> grade)
    {
        int Col(string nome)
        {
            var letra = (_cfg[$"Conversao:Fontes:{origem}:Colunas:{nome}"] ?? ColunasPadrao[origem].GetValueOrDefault(nome, "")).Trim();
            return letra == "" ? -1 : AplicacaoService.Idx(letra);
        }
        var iVend = Col("Vendedor"); var iValor = Col("Valor"); var iStatus = Col("Status");
        var iBu = Col("Bu"); var iData = Col("DataEnvio");
        var iBuIcAlt = Col("BuIcAlternativa"); var iBuIcPrinc = Col("BuIcPrincipal");
        var iMerc = Col("Mercado"); var iPais = Col("Pais"); var iProd = Col("Produto"); var iCli = Col("Cliente");
        var valorEmBrl = _cfg.GetValue($"Conversao:Fontes:{origem}:ValorEmBrl", origem == Nb);
        var vendEq = Equivalencias(origem, "Vendedores");
        var buEq = Equivalencias(origem, "Bus");

        // Linha do cabeçalho: configurada (1 = primeira). 0 = a primeira linha "cheia".
        static int Cheias(OpportunityImporter.Cel[] r) => r.Count(c => !string.IsNullOrWhiteSpace(c.Text));
        var h = _cfg.GetValue($"Conversao:Fontes:{origem}:HeaderRow", CabecalhoPadrao[origem]) - 1;
        if (h < 0)
        {
            var janela = grade.Take(40).ToList();
            var max = janela.Count == 0 ? 0 : janela.Max(Cheias);
            h = 0;
            for (var i = 0; i < janela.Count; i++)
                if (Cheias(janela[i]) >= 3 && Cheias(janela[i]) * 2 >= max) { h = i; break; }
        }
        if (grade.Count <= h) { d.Avisos.Add($"{origem}: a planilha {arquivo} não tem linhas de dados."); return; }
        var cab = grade[h];

        string Cel(OpportunityImporter.Cel[] r, int i) => i >= 0 && i < r.Length ? (r[i].Text ?? "").Trim() : "";
        string Titulo(int i) => Cel(cab, i);

        d.Mapeamento.Add($"{origem}: arquivo {arquivo} · cabeçalho na linha {h + 1}{(valorEmBrl ? " · valor já em R$" : " · valor em US$")}");
        foreach (var (rot, i) in new[] { ("Vendedor", iVend), (valorEmBrl ? "Valor R$" : "Valor US$", iValor), ("Status", iStatus), ("BU", iBu),
                                         ("Data de envio", iData), ("BU Intercompany (principal)", iBuIcPrinc), ("BU Intercompany (alternativa)", iBuIcAlt),
                                         ("Mercado", iMerc), ("País", iPais), ("Produto", iProd), ("Cliente", iCli) })
        {
            if (i < 0) continue;
            d.Mapeamento.Add($"{origem} · {rot}: coluna {AplicacaoService.Letra(i)} · \"{(Titulo(i) == "" ? "sem título" : Titulo(i))}\"");
            if (Titulo(i) == "") d.Avisos.Add($"{origem}: a coluna {AplicacaoService.Letra(i)} ({rot}) está sem título no cabeçalho (linha {h + 1}): confira se o mapeamento ainda vale.");
        }

        // Coluna com o número da proposta (para checar duplicidade), se existir.
        var chaves = new[] { "quotenumber", "quote number", "proposta", "proposal", "opportunity number", "numero da proposta" };
        var iChave = -1;
        for (var i = 0; i < cab.Length && iChave < 0; i++)
            if (chaves.Contains(OpportunityImporter.Normalizar(Titulo(i)))) iChave = i;

        var vistasChave = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vistasLinha = new HashSet<string>(StringComparer.Ordinal);
        var largura = cab.Length;
        int lidas = 0, semData = 0, datasInv = 0, semValor = 0, valInv = 0, dup = 0, totais = 0;
        var vendSemEq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var n = h + 1; n < grade.Count; n++)
        {
            var row = grade[n];
            if (Cheias(row) == 0) continue;                       // linha em branco
            lidas++;

            var primeira = row.Select(c => OpportunityImporter.Normalizar(c.Text ?? "")).FirstOrDefault(x => x != "") ?? "";
            if (primeira is "total" or "totais" or "grand total" or "subtotal") { totais++; continue; }

            var (valor, invalido) = Valor(iValor >= 0 && iValor < row.Length ? row[iValor] : default);
            var valorTxt = Cel(row, iValor);
            if (valor is null && invalido) valInv++;
            else if (valor is null) semValor++;

            var dataTxt = Cel(row, iData);
            DateTime? data = dataTxt == "" ? null : AplicacaoService.Data(dataTxt);
            if (dataTxt == "") semData++;
            else if (data is null) datasInv++;

            var status = Cel(row, iStatus);
            var vend = Cel(row, iVend);
            if (vend != "" && vendEq.TryGetValue(Chave(vend), out var vAfm)) vend = vAfm;
            var bu = Cel(row, iBu);
            if (bu != "")
            {
                if (buEq.TryGetValue(Chave(bu), out var bAfm)) bu = bAfm;
                else if (bu.Contains(':')) bu = bu[..bu.IndexOf(':')].Trim();   // "HSA: Howden South America" → "HSA"
            }
            var buIc = Cel(row, iBuIcPrinc);                      // AF primeiro; vazia/nula/espaços → AE
            if (buIc == "") buIc = Cel(row, iBuIcAlt);
            if (buIc == "") buIc = NaoIntercompany;

            var chave = iChave >= 0 ? Cel(row, iChave) : "";
            var cels = new string[largura];
            for (var i = 0; i < largura; i++) cels[i] = Cel(row, i);

            var assinatura = string.Join("\u001F", cels);
            if ((chave != "" && !vistasChave.Add(chave)) || (chave == "" && !vistasLinha.Add(assinatura))) dup++;

            string Ou(string x) => x == "" ? NaoInformado : x;
            d.Propostas.Add(new PropostaConv(origem, n + 1, Ou(vend),
                valorEmBrl ? null : valor, valorEmBrl ? valor : null, valorTxt, status,
                Classificar(status, origem), Ou(bu), buIc, data, dataTxt,
                Ou(Cel(row, iMerc)), Ou(Cel(row, iPais)), Ou(Cel(row, iProd)), Ou(Cel(row, iCli)), chave, cels));
        }

        d.LinhasLidas += lidas; d.SemData += semData; d.DatasInvalidas += datasInv; d.SemValor += semValor;
        d.ValoresInvalidos += valInv; d.Duplicadas += dup; d.TotaisIgnorados += totais;
        var moeda = valorEmBrl ? "R$" : "US$";
        if (dup > 0)
            d.Avisos.Add($"{origem}: {dup} registro(s) parecem duplicados ({(iChave >= 0 ? "mesmo número de proposta" : "linhas idênticas")}). Todos foram mantidos; confira na Base de Propostas.");
        if (datasInv > 0) d.Avisos.Add($"{origem}: {datasInv} registro(s) com data de envio inválida: ficam sem ano{(valorEmBrl ? "" : " e sem conversão cambial")}.");
        if (semData > 0) d.Avisos.Add($"{origem}: {semData} registro(s) sem data de envio: ficam sem ano{(valorEmBrl ? "" : " e sem conversão cambial")}.");
        if (valInv > 0) d.Avisos.Add($"{origem}: {valInv} registro(s) com valor em {moeda} ilegível ou ambíguo: contam nas quantidades, mas não nos valores.");
        if (semValor > 0) d.Avisos.Add($"{origem}: {semValor} registro(s) sem valor em {moeda}: contam nas quantidades, mas não nos valores.");
        if (totais > 0) d.Avisos.Add($"{origem}: {totais} linha(s) de total da planilha foram ignoradas.");
    }

    /// <summary>Valor numérico da célula (US$ ou R$). Número nativo vale como veio; texto usa o último
    /// separador como decimal. Texto ambíguo ("1.234", "1,234") é recusado, não adivinhado.</summary>
    private static (double? Valor, bool Invalido) Valor(OpportunityImporter.Cel c)
    {
        if (c.Num is { } n) return (n, false);
        var t = (c.Text ?? "").Trim();
        if (t == "") return (null, false);

        var neg = t.StartsWith('(') && t.EndsWith(')');
        t = t.Trim('(', ')').Replace("R$", "", StringComparison.OrdinalIgnoreCase).Replace("BRL", "", StringComparison.OrdinalIgnoreCase).Replace("US$", "", StringComparison.OrdinalIgnoreCase).Replace("USD", "", StringComparison.OrdinalIgnoreCase)
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
