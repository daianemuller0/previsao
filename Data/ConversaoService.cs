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
// AFM e NB 20-22 (cabeçalho na linha 1): B nº da proposta · C país · D planta
//   do cliente · F mercado · I produto · K vendedor · P valor US$ · T status ·
//   V BU · AD data de envio. As demais colunas não são usadas (nem BU
//   intercompany: todas ficam "Não é Intercompany").
// NB (linhas 1–14 são resumo; cabeçalho na 15; equivale ao AFM com outras
//   letras): N nº da proposta · D país · E planta do cliente · G mercado ·
//   J produto · L vendedor · P valor JÁ EM R$ · R status · V BU ("HSA: Howden
//   South America" → HSA) · AB data de envio.
//   Linhas com "Budget Quote" na coluna AD NÃO entram (são contadas e informadas).
// NB histórico ("NB 20-22", opcional): de 2022 até a migração para a plataforma
//   nova (01/01/2024). Vem do sistema antigo, no MESMO formato do AFM (colunas,
//   status, valor em US$), mas as propostas são de NB. Só entram as enviadas antes
//   de 01/01/2024: daí em diante vale a planilha da plataforma nova.
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

    /// <summary>A planilha de onde veio ("AFM", "NB" ou "NB_Historico"); vazio = a da própria origem.</summary>
    public string Fonte { get; init; } = "";

    /// <summary>Revisão da proposta (AFM e NB 20-22: coluna AA), quando houver: a maior é a última.</summary>
    public int? Revisao { get; init; }

    /// <summary>
    /// Identificador para contar cada proposta uma vez: o número da proposta dentro da mesma planilha
    /// (números de sistemas diferentes não se misturam) ou, sem número, a linha da planilha.
    /// </summary>
    public string Codigo
    {
        get
        {
            var f = Fonte.Length > 0 ? Fonte : Origem;
            return Chave.Trim().Length > 0 ? f + ":" + Chave.Trim().ToUpperInvariant() : $"{f}#{Linha}";
        }
    }
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
    /// <summary>Linhas desconsideradas por regra (ex.: "Budget Quote" no NB, período coberto por outra planilha).</summary>
    public int Desconsideradas { get; set; }
    /// <summary>Data de envio mais antiga de cada origem (AFM/NB): mostra se os anos são comparáveis.</summary>
    public Dictionary<string, DateTime> Inicio { get; } = new();
}

public sealed class ConversaoService
{
    public const string NaoInformado = "Não informado";
    /// <summary>BU Intercompany vazia (AF e AE em branco): a proposta não é intercompany.</summary>
    public const string NaoIntercompany = "Não é Intercompany";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string Afm = "AFM", Nb = "NB";
    public static readonly string[] Origens = { Afm, Nb };

    // Status → classe (comparados já normalizados: sem diferença de caixa, acento, espaço ou tipo de hífen).
    // A mesma lista vale para todas as planilhas. O que não estiver aqui é "Não classificada".
    private static readonly (HashSet<string> Ganhas, HashSet<string> Perdidas, HashSet<string> Abertas) StatusConversao =
        (Conjunto("Closed - Won", "Closed Won", "E1 Order Entry", "OTP"),
         Conjunto("Cancelled", "Closed - Lost", "Closed - Unknown", "Closed - No Bid", "Closed - Customer Lost", "Project Cancelled", "Closed Lost", "Closed Abandoned"),
         Conjunto("Open", "Arrived at Agreement on Need", "Letter of Intent", "Negotiation Phase/Review", "Outstanding Quote--No Formal Negotiation",
                  "Proposal Pending/Waiting", "Qualification"));
    private static HashSet<string> Conjunto(params string[] s) => new(s.Select(NormalizarStatus), StringComparer.Ordinal);

    /// <summary>
    /// Uma planilha de propostas: a origem das propostas (AFM/NB) e o FORMATO em que ela vem (colunas, linha
    /// do cabeçalho, status e moeda). A planilha antiga de NB tem o formato do AFM, mas as propostas são NB.
    /// </summary>
    private sealed record FontePlanilha(string Chave, string Origem, string Formato, bool Opcional, string ArquivoPadrao, string Rotulo);

    private const string NbHistorico = "NB_Historico";
    private static readonly FontePlanilha[] Fontes =
    {
        new(Afm, Afm, Afm, false, "", "AFM"),
        new(Nb, Nb, Nb, false, "", "NB"),
        new(NbHistorico, Nb, Afm, true, "NB 20-22", "NB histórico"),
    };

    /// <summary>Data a partir da qual a planilha deixa de valer (exclusiva): a antiga de NB vai até a migração.</summary>
    private static readonly Dictionary<string, DateTime> AtePadrao = new() { [NbHistorico] = new DateTime(2024, 1, 1) };
    /// <summary>Linhas que não entram, por planilha: coluna e textos (sem diferença de caixa/acento/espaço).</summary>
    private static readonly Dictionary<string, (string Coluna, string[] Valores)> ExclusaoPadrao = new() { [Nb] = ("AD", new[] { "Budget Quote" }) };

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
        var (g, p, a) = StatusConversao;
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
                var marca = string.Join(";", arquivos.Select(a => { var fi = new FileInfo(a.Arquivo); return $"{a.Fonte.Chave}|{fi.Name}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}"; }));
                if (!force && Ok && marca == _marca) return;

                var dados = new DadosConversao();
                if (aviso != "") dados.Avisos.Add(aviso);
                foreach (var (fonte, arquivo) in arquivos)
                {
                    var fi = new FileInfo(arquivo);
                    var bytes = LeituraSegura.Bytes(arquivo);
                    using var ms = new MemoryStream(bytes);
                    var grade = OpportunityImporter.LerGrade(fi.Name, ms, manterLinhasVazias: true,
                        aba: _cfg[$"Conversao:Fontes:{fonte.Chave}:Aba"] ?? _cfg["Conversao:Aba"], maiorAba: true);
                    Montar(dados, fonte, fi.Name, grade);
                }
                UnificarVendedores(dados);
                UltimaRevisao(dados);
                Conferir(dados, arquivos.Select(a => a.Fonte).ToList());

                _atual = dados; _marca = marca;
                Ok = true; AtualizadoEm = DateTime.Now;
                var hist = dados.Propostas.Count(p => p.Fonte == NbHistorico);
                Mensagem = $"{dados.Propostas.Count} propostas carregadas ({string.Join(" · ", Origens.Select(o => $"{o} {dados.Propostas.Count(p => p.Origem == o)}"))}" +
                           (hist > 0 ? $", das quais {hist} de NB da planilha histórica" : "") + ").";
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
    /// Acha a planilha de cada fonte na pasta: pelo nome configurado (Conversao:Fontes:X:Arquivo, sem
    /// diferença de extensão, espaço, hífen ou maiúscula) e, nas fontes principais, se não houver, por um
    /// nome que contenha a sigla da origem ("..._AFM_...", "...-NB_..."), sem pegar arquivo que já é de outra
    /// fonte (a planilha "NB 20-22" também tem "NB" no nome). Falta de uma origem vira aviso; a outra é lida
    /// mesmo assim. A planilha histórica é opcional.
    /// </summary>
    private List<(FontePlanilha Fonte, string Arquivo)> Resolver(out string aviso)
    {
        aviso = "";
        var res = new List<(FontePlanilha, string)>();
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
            string NomeDe(FontePlanilha f) => Chave(_cfg[$"Conversao:Fontes:{f.Chave}:Arquivo"] ?? f.ArquivoPadrao);
            // 1) pelo nome exato de cada fonte
            var achados = new Dictionary<FontePlanilha, FileInfo>();
            foreach (var f in Fontes)
            {
                var nome = NomeDe(f);
                var a = nome == "" ? null : arqs.FirstOrDefault(x => Chave(Path.GetFileNameWithoutExtension(x.Name)) == nome && !achados.ContainsValue(x));
                if (a is not null) achados[f] = a;
            }
            // 2) fontes principais sem nome exato: pela sigla, entre os arquivos que não são de outra fonte
            var nomesConhecidos = Fontes.Select(NomeDe).Where(n => n != "").ToHashSet();
            foreach (var f in Fontes.Where(f => !f.Opcional && !achados.ContainsKey(f)))
            {
                var sigla = new Regex($@"(^|[^a-z]){f.Origem.ToLowerInvariant()}([^a-z]|$)");
                var a = arqs.FirstOrDefault(x => !achados.ContainsValue(x) && !nomesConhecidos.Contains(Chave(Path.GetFileNameWithoutExtension(x.Name)))
                                                 && sigla.IsMatch(SemAcento(Path.GetFileNameWithoutExtension(x.Name))));
                if (a is not null) achados[f] = a;
            }
            var faltam = Fontes.Where(f => !f.Opcional && !achados.ContainsKey(f)).Select(f => f.Origem).ToList();
            res.AddRange(Fontes.Where(achados.ContainsKey).Select(f => (f, achados[f].FullName)));
            if (faltam.Count > 0)
                aviso = $"Planilha de {string.Join(" e ", faltam)} não encontrada na pasta {pasta.FullName}" + (res.Count > 0 ? $": exibindo só {string.Join(" e ", res.Select(r => r.Item1.Origem).Distinct())}." : ".");
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
        [Afm] = new() { ["Proposta"] = "B", ["Revisao"] = "AA", ["Vendedor"] = "K", ["Valor"] = "P", ["Status"] = "T", ["Bu"] = "V", ["DataEnvio"] = "AC",
                        ["Mercado"] = "F", ["Pais"] = "C", ["Produto"] = "I", ["Cliente"] = "D" },
        [Nb]  = new() { ["Proposta"] = "N", ["Vendedor"] = "L", ["Valor"] = "P", ["Status"] = "R", ["Bu"] = "V", ["DataEnvio"] = "AB",
                        ["Mercado"] = "G", ["Pais"] = "D", ["Produto"] = "J", ["Cliente"] = "E" },
    };
    private static readonly Dictionary<string, int> CabecalhoPadrao = new() { [Afm] = 1, [Nb] = 15 };

    /// <summary>Equivalências da origem (ex.: nome do vendedor no NB → nome no AFM), sem diferença de acento/caixa.</summary>
    private Dictionary<string, string> Equivalencias(string fonte, string secao) =>
        _cfg.GetSection($"Conversao:Fontes:{fonte}:{secao}").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .GroupBy(c => Chave(c.Key)).ToDictionary(g => g.Key, g => g.First().Value!.Trim());

    /// <summary>Equivalências da planilha e, se ela for de outra fonte da mesma origem (NB histórico), também as da origem.</summary>
    private Dictionary<string, string> Equivalencias(FontePlanilha f, string secao)
    {
        var d = Equivalencias(f.Chave, secao);
        if (f.Chave != f.Origem)
            foreach (var (k, v) in Equivalencias(f.Origem, secao)) d.TryAdd(k, v);
        return d;
    }

    private void Montar(DadosConversao d, FontePlanilha fonte, string arquivo, List<OpportunityImporter.Cel[]> grade)
    {
        var origem = fonte.Origem;
        var cfg = $"Conversao:Fontes:{fonte.Chave}";
        var rotulo = fonte.Chave == fonte.Origem ? origem : $"{origem} ({fonte.Rotulo})";
        int Col(string nome)
        {
            var letra = (_cfg[$"{cfg}:Colunas:{nome}"] ?? ColunasPadrao[fonte.Formato].GetValueOrDefault(nome, "")).Trim();
            return letra == "" ? -1 : AplicacaoService.Idx(letra);
        }
        var iVend = Col("Vendedor"); var iValor = Col("Valor"); var iStatus = Col("Status");
        var iBu = Col("Bu"); var iData = Col("DataEnvio");
        var iBuIcAlt = Col("BuIcAlternativa"); var iBuIcPrinc = Col("BuIcPrincipal");
        var iMerc = Col("Mercado"); var iPais = Col("Pais"); var iProd = Col("Produto"); var iCli = Col("Cliente");
        var iProposta = Col("Proposta"); var iRevisao = Col("Revisao");
        var valorEmBrl = _cfg.GetValue($"{cfg}:ValorEmBrl", fonte.Formato == Nb);
        var vendEq = Equivalencias(fonte, "Vendedores");
        var buEq = Equivalencias(fonte, "Bus");

        // linhas que não entram (ex.: NB, coluna AD = "Budget Quote")
        var exPadrao = ExclusaoPadrao.TryGetValue(fonte.Chave, out var ep) ? ep : ("", Array.Empty<string>());
        var exLetra = (_cfg[$"{cfg}:Excluir:Coluna"] ?? exPadrao.Item1).Trim();
        var exValores = _cfg.GetSection($"{cfg}:Excluir:Valores").GetChildren().Select(c => c.Value ?? "").Where(v => v.Trim() != "").ToArray();
        if (exValores.Length == 0 && _cfg[$"{cfg}:Excluir:Coluna"] is null) exValores = exPadrao.Item2;
        var iExcl = exLetra == "" || exValores.Length == 0 ? -1 : AplicacaoService.Idx(exLetra);
        var exNorm = exValores.Select(v => OpportunityImporter.Normalizar(v)).ToHashSet();
        // período coberto por outra planilha: a antiga de NB só vale antes da migração
        DateTime? ate = DateTime.TryParse(_cfg[$"{cfg}:Ate"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ac) ? ac
                      : AtePadrao.TryGetValue(fonte.Chave, out var ap) ? ap : null;

        // Linha do cabeçalho: configurada (1 = primeira). 0 = a primeira linha "cheia".
        static int Cheias(OpportunityImporter.Cel[] r) => r.Count(c => !string.IsNullOrWhiteSpace(c.Text));
        var h = _cfg.GetValue($"{cfg}:HeaderRow", CabecalhoPadrao[fonte.Formato]) - 1;
        if (h < 0)
        {
            var janela = grade.Take(40).ToList();
            var max = janela.Count == 0 ? 0 : janela.Max(Cheias);
            h = 0;
            for (var i = 0; i < janela.Count; i++)
                if (Cheias(janela[i]) >= 3 && Cheias(janela[i]) * 2 >= max) { h = i; break; }
        }
        if (grade.Count <= h) { d.Avisos.Add($"{rotulo}: a planilha {arquivo} não tem linhas de dados."); return; }
        var cab = grade[h];

        // Data de envio: acha a coluna pelo TÍTULO ("Actual" no AFM e no NB 20-22, "Quotation Sent Date" no NB);
        // a letra configurada só vale se nenhum título conhecido estiver no cabeçalho. Assim uma coluna a mais
        // ou a menos na exportação não faz a data cair em outra coluna (ex.: nomes de engenheiros).
        var titulosData = new[] { "actual", "quotation sent date", "quote sent date", "data de envio", "sent date" };
        for (var i = 0; i < cab.Length; i++)
            if (titulosData.Contains(OpportunityImporter.Normalizar((cab[i].Text ?? "").Trim()))) { iData = i; break; }

        string Cel(OpportunityImporter.Cel[] r, int i) => i >= 0 && i < r.Length ? (r[i].Text ?? "").Trim() : "";
        string Titulo(int i) => Cel(cab, i);

        d.Mapeamento.Add($"{rotulo}: arquivo {arquivo} · cabeçalho na linha {h + 1}{(valorEmBrl ? " · valor já em R$" : " · valor em US$")}" +
                         (fonte.Formato != fonte.Origem ? $" · formato {fonte.Formato} (colunas e status do {fonte.Formato}), propostas contadas como {origem}" : "") +
                         (ate is DateTime at ? $" · só propostas enviadas antes de {at:dd/MM/yyyy}" : ""));
        foreach (var (rot, i) in new[] { ("Nº da proposta", iProposta), ("Revisão", iRevisao), ("Vendedor", iVend), (valorEmBrl ? "Valor R$" : "Valor US$", iValor), ("Status", iStatus), ("BU", iBu),
                                         ("Data de envio", iData), ("BU Intercompany (principal)", iBuIcPrinc), ("BU Intercompany (alternativa)", iBuIcAlt),
                                         ("Mercado", iMerc), ("País", iPais), ("Produto", iProd), ("Cliente", iCli),
                                         ($"Desconsiderar \"{string.Join("\" / \"", exValores)}\"", iExcl) })
        {
            if (i < 0) continue;
            d.Mapeamento.Add($"{rotulo} · {rot}: coluna {AplicacaoService.Letra(i)} · \"{(Titulo(i) == "" ? "sem título" : Titulo(i))}\"");
            if (Titulo(i) == "") d.Avisos.Add($"{rotulo}: a coluna {AplicacaoService.Letra(i)} ({rot}) está sem título no cabeçalho (linha {h + 1}): confira se o mapeamento ainda vale.");
        }

        // Coluna com o número da proposta (cada número conta uma vez): a configurada; sem ela, achada pelo título.
        var chaves = new[] { "quotenumber", "quote number", "proposta", "proposal", "opportunity number", "numero da proposta" };
        var iChave = iProposta;
        for (var i = 0; i < cab.Length && iChave < 0; i++)
            if (chaves.Contains(OpportunityImporter.Normalizar(Titulo(i)))) iChave = i;

        var vistasChave = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vistasLinha = new HashSet<string>(StringComparer.Ordinal);
        var largura = cab.Length;
        int lidas = 0, semData = 0, datasInv = 0, semValor = 0, valInv = 0, dup = 0, totais = 0, excluidas = 0, foraDoPeriodo = 0;
        var vendSemEq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var n = h + 1; n < grade.Count; n++)
        {
            var row = grade[n];
            if (Cheias(row) == 0) continue;                       // linha em branco
            lidas++;

            var primeira = row.Select(c => OpportunityImporter.Normalizar(c.Text ?? "")).FirstOrDefault(x => x != "") ?? "";
            if (primeira is "total" or "totais" or "grand total" or "subtotal") { totais++; continue; }
            if (iExcl >= 0 && exNorm.Contains(OpportunityImporter.Normalizar(Cel(row, iExcl)))) { excluidas++; continue; }

            var (valor, invalido) = Valor(iValor >= 0 && iValor < row.Length ? row[iValor] : default);
            var valorTxt = Cel(row, iValor);
            if (valor is null && invalido) valInv++;
            else if (valor is null) semValor++;

            var dataTxt = Cel(row, iData);
            DateTime? data = dataTxt == "" ? null : AplicacaoService.Data(dataTxt);
            if (ate is DateTime limite && data is DateTime dt && dt.Date >= limite) { foraDoPeriodo++; continue; }
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
                Classificar(status, fonte.Formato), Ou(bu), buIc, data, dataTxt,
                Ou(Cel(row, iMerc)), Ou(Cel(row, iPais)), Ou(Cel(row, iProd)), Ou(Cel(row, iCli)), chave, cels) { Fonte = fonte.Chave, Revisao = NumeroRevisao(Cel(row, iRevisao)) });
        }

        d.LinhasLidas += lidas; d.SemData += semData; d.DatasInvalidas += datasInv; d.SemValor += semValor;
        d.ValoresInvalidos += valInv; d.Duplicadas += dup; d.TotaisIgnorados += totais; d.Desconsideradas += excluidas + foraDoPeriodo;
        var moeda = valorEmBrl ? "R$" : "US$";
        if (iExcl >= 0)
            d.Mapeamento.Add($"{rotulo}: {excluidas} linha(s) com \"{string.Join("\" / \"", exValores)}\" na coluna {AplicacaoService.Letra(iExcl)} foram desconsideradas (não entram em nenhuma conta).");
        if (ate is DateTime lim)
            d.Mapeamento.Add($"{rotulo}: {foraDoPeriodo} linha(s) enviadas a partir de {lim:dd/MM/yyyy} foram desconsideradas: esse período vem da planilha da plataforma nova.");
        if (dup > 0)
            d.Avisos.Add(iChave >= 0
                ? $"{rotulo}: {dup} linha(s) repetem um número de proposta (revisões): fica só a última revisão de cada proposta."
                : $"{rotulo}: {dup} registro(s) parecem duplicados (linhas idênticas). Todos foram mantidos; confira na Base de Propostas.");
        if (datasInv > 0) d.Avisos.Add($"{rotulo}: {datasInv} registro(s) com data de envio inválida: ficam sem ano{(valorEmBrl ? "" : " e sem conversão cambial")}.");
        if (semData > 0) d.Avisos.Add($"{rotulo}: {semData} registro(s) sem data de envio: ficam sem ano{(valorEmBrl ? "" : " e sem conversão cambial")}.");
        if (valInv > 0) d.Avisos.Add($"{rotulo}: {valInv} registro(s) com valor em {moeda} ilegível ou ambíguo: contam nas quantidades, mas não nos valores.");
        if (semValor > 0) d.Avisos.Add($"{rotulo}: {semValor} registro(s) sem valor em {moeda}: contam nas quantidades, mas não nos valores.");
        if (totais > 0) d.Avisos.Add($"{rotulo}: {totais} linha(s) de total da planilha foram ignoradas.");
    }

    private static int? NumeroRevisao(string t)
    {
        var dig = new string(t.Where(char.IsDigit).ToArray());
        return dig.Length is > 0 and <= 6 && int.TryParse(dig, out var n) ? n : null;
    }

    /// <summary>
    /// Cada número de proposta conta UMA vez em tudo (enviadas, valores e conversão): das linhas com o mesmo
    /// número na mesma planilha (revisões), vale a última — a de maior revisão; sem revisão, a de data de envio
    /// mais recente; empate, a que vem mais abaixo na planilha — com a data do PRIMEIRO envio. Linhas sem
    /// número continuam cada uma por si.
    /// </summary>
    private static void UltimaRevisao(DadosConversao d)
    {
        foreach (var fonte in d.Propostas.Select(p => p.Fonte).Distinct().ToList())
        {
            var linhas = d.Propostas.Where(p => p.Fonte == fonte).ToList();
            var comNumero = linhas.Where(p => p.Chave.Trim().Length > 0).ToList();
            var rot = fonte == NbHistorico ? "NB (NB histórico)" : fonte;
            if (comNumero.Count == 0) continue;
            // trava: se a coluna do número tem poucos valores diferentes, ela NÃO é o número da proposta
            // (ex.: moeda, tipo, setor) — juntar por ela apagaria propostas de verdade
            var distintos = comNumero.Select(p => p.Codigo).Distinct().Count();
            if (comNumero.Count >= 20 && distintos < comNumero.Count * 0.6)
            {
                var exemplos = comNumero.GroupBy(p => p.Chave.Trim()).OrderByDescending(g => g.Count()).Take(3).Select(g => $"\"{g.Key}\" ({g.Count()}×)");
                d.Avisos.Add($"{rot}: a coluna do número da proposta tem só {distintos} valores diferentes em {comNumero.Count} linhas ({string.Join(", ", exemplos)}): não parece o número da proposta, então as linhas NÃO foram juntadas como revisões. Confira a coluna em \"Colunas lidas da planilha\".");
                continue;
            }
            // a última revisão dá o status e o valor; a data é a do PRIMEIRO envio (a proposta continua no mês em
            // que foi enviada, mesmo que tenha sido revisada meses ou anos depois — no NB, a mesma oportunidade
            // aparece várias vezes ao longo do tempo)
            var troca = new Dictionary<PropostaConv, PropostaConv>(ReferenceEqualityComparer.Instance);
            foreach (var g in comNumero.GroupBy(p => p.Codigo))
            {
                var ultima = g.OrderByDescending(p => p.Revisao ?? -1).ThenByDescending(p => p.Data ?? DateTime.MinValue).ThenByDescending(p => p.Linha).First();
                var primeira = g.Where(p => p.Data is not null).OrderBy(p => p.Data).FirstOrDefault();
                troca[ultima] = primeira is null || primeira.Data == ultima.Data ? ultima : ultima with { Data = primeira.Data, DataTexto = primeira.DataTexto };
            }
            var fora = d.Propostas.RemoveAll(p => p.Fonte == fonte && p.Chave.Trim().Length > 0 && !troca.ContainsKey(p));
            for (var i = 0; i < d.Propostas.Count; i++)
                if (troca.TryGetValue(d.Propostas[i], out var nova)) d.Propostas[i] = nova;
            d.Desconsideradas += fora;
            d.Mapeamento.Add($"{rot} · revisões: {fora} linha(s) de revisões anteriores desconsideradas — cada número de proposta conta uma vez, no mês do primeiro envio, com o status e o valor da última revisão.");
        }
        // resumo por planilha: quantas propostas ficaram e quantas por ano (para conferir com o Excel)
        foreach (var g in d.Propostas.GroupBy(p => p.Fonte))
        {
            var rot = g.Key == NbHistorico ? "NB (NB histórico)" : g.Key;
            var anos = g.Where(p => p.Data is not null).GroupBy(p => p.Data!.Value.Year).OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Count()}");
            d.Mapeamento.Add($"{rot} · propostas que entram nas contas: {g.Count()} ({string.Join(" · ", anos)}{(g.Any(p => p.Data is null) ? $" · sem data: {g.Count(p => p.Data is null)}" : "")}).");
        }
    }

    /// <summary>Equivalências de vendedor que valem para todas as planilhas (Conversao:Vendedores sobrepõe/acrescenta).</summary>
    private static readonly Dictionary<string, string> VendedoresPadrao = new() { ["Edson Geraldini"] = "Edson Luis Geraldini" };

    /// <summary>
    /// Junta o mesmo vendedor escrito de jeitos diferentes nas planilhas:
    /// 1) equivalências fixas (ex.: "Edson Geraldini" → "Edson Luis Geraldini");
    /// 2) mesmos nomes em outra ordem ou sem acento ("Veiga Thiago" = "Thiago Veiga");
    /// 3) nome curto contido num nome completo ("Toledo Rafael" → "Rafael Ribeiro Toledo"), só quando
    ///    há UM nome completo possível — se houver dois, não junta (seriam pessoas diferentes) e avisa.
    /// O nome que fica é o mais completo (mais partes); empate, o que aparece mais vezes.
    /// </summary>
    private void UnificarVendedores(DadosConversao d)
    {
        var fixas = VendedoresPadrao.ToDictionary(kv => Chave(kv.Key), kv => kv.Value);
        foreach (var c in _cfg.GetSection("Conversao:Vendedores").GetChildren().Where(c => !string.IsNullOrWhiteSpace(c.Value)))
            fixas[Chave(c.Key)] = c.Value!.Trim();
        string Fixo(string v) => fixas.TryGetValue(Chave(v), out var n) ? n : v;

        static string[] Partes(string v) => SemAcento(v).Split(new[] { ' ', '.', ',', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();

        var contagem = d.Propostas.Where(p => p.Vendedor != NaoInformado).GroupBy(p => Fixo(p.Vendedor)).ToDictionary(g => g.Key, g => g.Count());
        // grupos com as mesmas partes (ordem/acento diferentes)
        var grupos = contagem.Keys.GroupBy(n => string.Join(" ", Partes(n))).Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList());
        var conjuntos = grupos.Keys.ToDictionary(k => k, k => k.Split(' ').ToHashSet());
        // nome curto → o único nome completo que o contém
        var destino = grupos.Keys.ToDictionary(k => k, k => k);
        var ambiguos = new List<string>();
        foreach (var k in grupos.Keys.Where(k => conjuntos[k].Count >= 2))
        {
            var sup = grupos.Keys.Where(o => o != k && conjuntos[o].Count > conjuntos[k].Count && conjuntos[k].IsSubsetOf(conjuntos[o])).ToList();
            // entre os que contêm, fica só o "máximo" (os que não estão contidos em outro da lista)
            var maximos = sup.Where(o => !sup.Any(x => x != o && conjuntos[o].IsSubsetOf(conjuntos[x]))).ToList();
            if (maximos.Count == 1) destino[k] = maximos[0];
            else if (maximos.Count > 1) ambiguos.Add($"\"{grupos[k][0]}\" ({string.Join(" / ", maximos.Select(m => grupos[m].OrderByDescending(n => contagem[n]).First()))})");
        }
        string Raiz(string k) { var vistos = 0; while (destino[k] != k && vistos++ < 10) k = destino[k]; return k; }
        // nome que fica em cada grupo final: o mais completo; empate, o mais frequente
        var final = new Dictionary<string, string>();
        foreach (var raiz in grupos.Keys.GroupBy(Raiz))
        {
            var nomes = raiz.SelectMany(k => grupos[k]).ToList();
            var escolhido = nomes.OrderByDescending(n => Partes(n).Length).ThenByDescending(n => contagem[n]).ThenBy(n => n, StringComparer.Ordinal).First();
            foreach (var n in nomes) final[n] = escolhido;
        }
        var trocas = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 0; i < d.Propostas.Count; i++)
        {
            var p = d.Propostas[i];
            if (p.Vendedor == NaoInformado) continue;
            var f = Fixo(p.Vendedor);
            var novo = final.TryGetValue(f, out var x) ? x : f;
            if (novo != p.Vendedor) { d.Propostas[i] = p with { Vendedor = novo }; trocas.Add($"\"{p.Vendedor}\" → \"{novo}\""); }
        }
        if (trocas.Count > 0) d.Mapeamento.Add($"Vendedores unificados ({trocas.Count}): {string.Join("; ", trocas)}.");
        if (ambiguos.Count > 0) d.Avisos.Add($"Vendedor(es) com nome curto que pode ser de mais de uma pessoa, mantido(s) separado(s): {string.Join("; ", ambiguos)}. Para juntar, informe em Conversao:Vendedores.");
    }

    /// <summary>
    /// Depois de ler tudo: a data mais antiga de cada origem (para avisar quando os anos não são comparáveis)
    /// e a sobreposição entre a planilha antiga de NB e a da plataforma nova (que contaria a mesma proposta duas vezes).
    /// </summary>
    private void Conferir(DadosConversao d, List<FontePlanilha> lidas)
    {
        foreach (var g in d.Propostas.Where(p => p.Data is not null).GroupBy(p => p.Origem))
            d.Inicio[g.Key] = g.Min(p => p.Data!.Value);
        if (d.Inicio.Count == Origens.Length && d.Inicio.Values.Select(x => x.Year).Distinct().Count() > 1)
        {
            var ult = d.Inicio.MaxBy(kv => kv.Value);
            d.Avisos.Add($"As propostas começam em datas diferentes ({string.Join("; ", d.Inicio.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key} desde {kv.Value:MM/yyyy}"))}): " +
                         $"antes de {ult.Value.Year} só há propostas de {string.Join(" e ", d.Inicio.Where(kv => kv.Value.Year < ult.Value.Year).Select(kv => kv.Key))}, então os totais desses anos não se comparam aos seguintes.");
        }
        if (lidas.Any(f => f.Chave == NbHistorico))
        {
            var limite = DateTime.TryParse(_cfg[$"Conversao:Fontes:{NbHistorico}:Ate"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) ? a : AtePadrao[NbHistorico];
            var antes = d.Propostas.Count(p => p.Fonte == Nb && p.Data is DateTime dt && dt.Date < limite);
            if (antes > 0)
                d.Avisos.Add($"NB: {antes} proposta(s) da plataforma nova foram enviadas antes de {limite:dd/MM/yyyy}, período também coberto pela planilha NB histórica: confira se não estão nas duas.");
        }
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
