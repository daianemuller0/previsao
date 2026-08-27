using Microsoft.Extensions.Configuration;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Sincroniza as bases de oportunidades a partir das planilhas exportadas do CRM
// na rede — duas origens:
//   • New Business (Funil de Vendas HSA) → Nb:Path  · ids "imp-"
//   • Aftermarket  (novo CRM)            → Afm:Path  · ids "afm-" · converte moeda
// Lê o arquivo mais recente da pasta configurada, aplica o De-Para e ESPELHA as
// oportunidades daquela origem: upsert das linhas da planilha + baixa (tombstone)
// das que sumiram. Cada origem mexe só no seu namespace de id, então convivem
// sem interferência (e não tocam nas criadas à mão "opp-"). Roda na subida do
// app e sob demanda (botões em Configurações). Registrado como singleton.
// ---------------------------------------------------------------------------
public sealed class DataSyncService
{
    private static readonly string[] Exts = { ".xlsx", ".xlsm", ".xls", ".csv" };

    private readonly IConfiguration _cfg;
    private readonly Catalog _cat;
    private readonly OpportunityRepository _repo;
    private readonly ControleRepository _controle;
    private readonly ParquetStore _store;
    private readonly object _lock = new();

    public DataSyncService(IConfiguration cfg, Catalog cat, OpportunityRepository repo,
        ControleRepository controle, ParquetStore store)
    {
        _cfg = cfg; _cat = cat; _repo = repo; _controle = controle; _store = store;
    }

    public bool NbEnabled => _cfg.GetValue("Nb:Enabled", true);
    public string NbPath => _cfg["Nb:Path"] ?? "";
    public bool AfmEnabled => _cfg.GetValue("Afm:Enabled", true);
    public string AfmPath => _cfg["Afm:Path"] ?? "";

    public DataSyncStatus LastNb { get; private set; } = new() { Label = "New Business" };
    public DataSyncStatus LastAfm { get; private set; } = new() { Label = "Aftermarket" };

    // Configuração de cada origem.
    private sealed record Profile(string Label, bool Enabled, string Path,
        OpportunityImporter.Source Source, string Prefix, bool Currency);

    private Profile NbProfile => new("New Business", NbEnabled, NbPath, OpportunityImporter.Source.Nb, "imp-", false);
    private Profile AfmProfile => new("Aftermarket", AfmEnabled, AfmPath, OpportunityImporter.Source.Afm, "afm-", true);

    /// <summary>Sincroniza na subida do app: pula a origem cuja planilha não mudou.</summary>
    public void SyncAll() { LastNb = Run(NbProfile, force: false); LastAfm = Run(AfmProfile, force: false); }
    /// <summary>Sincronização manual (botão em Configurações): sempre reimporta.</summary>
    public DataSyncStatus SyncNb() { var s = Run(NbProfile, force: true); LastNb = s; return s; }
    public DataSyncStatus SyncAfm() { var s = Run(AfmProfile, force: true); LastAfm = s; return s; }

    // ---- marca da última importação (compartilhada entre as máquinas) --------
    // Guarda "arquivo|data de modificação|tamanho" por origem. Enquanto a planilha
    // exportada pelo CRM não mudar, ninguém precisa reimportar — só a primeira
    // pessoa a abrir depois de uma nova exportação paga o custo.
    private const string EntSync = "sync_state";

    // Entra na marca: quando o mapeamento das planilhas muda (uma coluna nova
    // passa a ser lida), a versão sobe e todo mundo reimporta na próxima abertura,
    // mesmo que a planilha da rede continue exatamente a mesma.
    private const string ImportVersion = "v3";

    private string? MarcaGravada(string prefix)
    {
        try
        {
            return _store.ReadLatest(EntSync, "id, stamp", r => new
            {
                Id = r.IsDBNull(0) ? "" : r.GetString(0),
                Stamp = r.IsDBNull(1) ? "" : r.GetString(1),
            }).FirstOrDefault(x => x.Id == prefix)?.Stamp;
        }
        catch { return null; }
    }

    private void GravarMarca(string prefix, string stamp)
    {
        try
        {
            _store.WriteRow(EntSync, new KeyValuePair<string, object?>[]
            {
                new("id", prefix), new("stamp", stamp),
            });
        }
        catch { /* não impede a sincronização */ }
    }

    private static string MarcaDoArquivo(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            return $"{ImportVersion}|{fi.Name}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        }
        catch { return ""; }
    }

    // Lê a planilha da rede e espelha as oportunidades da origem. Nunca lança:
    // qualquer problema vira mensagem no status (a rede pode estar lenta/indisponível
    // e o app precisa seguir no ar).
    private DataSyncStatus Run(Profile p, bool force)
    {
        lock (_lock)
        {
            var st = new DataSyncStatus { Label = p.Label, StartedAt = DateTime.Now, Path = p.Path };
            try
            {
                if (!p.Enabled)
                {
                    st.Ok = false;
                    st.Message = $"Sincronização automática de {p.Label} desativada.";
                    return Finish(st);
                }

                var file = ResolveFile(p.Path);
                if (file is null)
                {
                    st.Ok = false;
                    st.Message = string.IsNullOrWhiteSpace(p.Path)
                        ? "Caminho da planilha não configurado."
                        : "Nenhuma planilha (.xlsx/.xlsm/.xls/.csv) encontrada no caminho configurado.";
                    return Finish(st);
                }
                st.File = file;

                // Planilha inalterada desde a última importação: não há o que fazer.
                // (Só pula se a base realmente tiver os dados dessa origem — assim,
                //  se alguém apagar a base, a reimportação acontece mesmo assim.)
                var marca = MarcaDoArquivo(file);
                var temDados = _repo.All().Any(o => o.Id.StartsWith(p.Prefix, StringComparison.Ordinal));
                if (!force && temDados && marca != "" && marca == MarcaGravada(p.Prefix))
                {
                    st.Ok = true;
                    st.Message = $"{p.Label} já está atualizado (planilha sem alterações desde a última importação).";
                    return Finish(st);
                }

                // Copia para memória permitindo leitura mesmo com o CRM gravando.
                byte[] bytes;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var buf = new MemoryStream())
                {
                    fs.CopyTo(buf);
                    bytes = buf.ToArray();
                }

                // Conversão de moeda só no Aftermarket (snapshot das taxas: 1 leitura).
                var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Func<string, double?>? resolve = null;
                Action<string>? onCurrency = null;
                if (p.Currency)
                {
                    var rates = _controle.CurrencyRates()
                        .Where(m => m.HasRate)
                        .ToDictionary(m => m.Code, m => m.RateV, StringComparer.OrdinalIgnoreCase);
                    resolve = code => rates.TryGetValue(code, out var v) ? v : (double?)null;
                    onCurrency = code => currencies.Add(code);
                }

                var importer = new OpportunityImporter(_cat);
                OpportunityImporter.Result result;
                using (var ms = new MemoryStream(bytes))
                    result = importer.Parse(Path.GetFileName(file), ms, p.Source, resolve, onCurrency);

                if (p.Currency)
                    foreach (var c in currencies) _controle.EnsureCurrency(c);

                // Espelha só quando houve dados lidos — evita apagar a base por uma
                // leitura vazia/transitória.
                if (result.Rows.Count > 0)
                {
                    var newIds = result.Rows.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
                    var existing = _repo.All()
                        .Where(o => o.Id.StartsWith(p.Prefix, StringComparison.Ordinal))
                        .ToDictionary(o => o.Id, StringComparer.Ordinal);

                    // Funde a linha da planilha com o que já estava guardado, para
                    // que a re-sincronização NUNCA apague o trabalho de quem está
                    // usando o sistema.
                    foreach (var row in result.Rows)
                        if (existing.TryGetValue(row.Id, out var prev))
                            Preservar(row, prev);

                    _repo.SaveMany(result.Rows);
                    foreach (var id in existing.Keys)
                        if (!newIds.Contains(id)) _repo.Delete(id);
                    _repo.Refresh();
                }

                if (result.Rows.Count > 0 && marca != "") GravarMarca(p.Prefix, marca);

                st.Rows = result.Rows.Count;
                st.Warnings = result.Warnings.Count;
                st.Currencies = currencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                st.Ok = true;
                st.Message = result.Rows.Count > 0
                    ? $"{result.Rows.Count} oportunidade(s) de {p.Label} sincronizada(s)."
                    : "Planilha lida, mas nenhuma oportunidade reconhecida — confira o cabeçalho das colunas.";
                return Finish(st);
            }
            catch (Exception ex)
            {
                st.Ok = false;
                st.Message = $"Falha ao sincronizar {p.Label}: " + ex.Message;
                return Finish(st);
            }
        }
    }

    // ---- fusão planilha × app ----------------------------------------------
    // A planilha do CRM manda no que ela traz; o que a pessoa preenche no
    // sistema é dela. Sem isso, cada re-sincronização devolvia a linha ao estado
    // da planilha e apagava o preenchimento feito na tela.

    // Campos que a planilha NUNCA traz: pertencem ao app e vencem sempre.
    private static readonly HashSet<string> SoDoApp = new(StringComparer.Ordinal)
    {
        nameof(Opportunity.Indicada),         // indicada na previsão
        nameof(Opportunity.MovidaControle),   // venda indicada → Controle
        nameof(Opportunity.Kyc),
        nameof(Opportunity.Top10),
        nameof(Opportunity.PlantId),
        nameof(Opportunity.ForecastCategory), // escolha do usuário na tela
        nameof(Opportunity.PipelineStageId),  // idem (Etapa)
        nameof(Opportunity.ManagerProbability),
        nameof(Opportunity.Justification),
        nameof(Opportunity.NextAction),
        nameof(Opportunity.NextActionDate),
        nameof(Opportunity.Risks),
        nameof(Opportunity.ValueChangedAt),
        nameof(Opportunity.DateChangedAt),
        nameof(Opportunity.CreatedAt),        // data de criação original
    };

    // Tudo é persistido como texto; percorrer as propriedades de texto cobre a
    // entidade inteira e continua cobrindo os campos que forem criados depois.
    private static readonly System.Reflection.PropertyInfo[] Campos =
        typeof(Opportunity).GetProperties()
            .Where(x => x.PropertyType == typeof(string) && x.CanRead && x.CanWrite
                        && x.Name != nameof(Opportunity.Id))
            .ToArray();

    // "0" conta como ausência: os campos numéricos nascem em "0" e o importador
    // devolve "0" quando a coluna vem vazia — não é uma informação da planilha.
    private static bool SemInfo(string? v) => string.IsNullOrWhiteSpace(v) || v == "0";

    private static void Preservar(Opportunity nova, Opportunity antiga)
    {
        foreach (var campo in Campos)
        {
            var doApp = (string?)campo.GetValue(antiga);
            if (string.IsNullOrWhiteSpace(doApp)) continue;
            if (SoDoApp.Contains(campo.Name) || SemInfo((string?)campo.GetValue(nova)))
                campo.SetValue(nova, doApp);
        }
    }

    private static DataSyncStatus Finish(DataSyncStatus s)
    {
        s.FinishedAt = DateTime.Now;
        return s;
    }

    // Aceita um arquivo direto ou uma pasta (usa o mais recente por data).
    private static string? ResolveFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path)) return null;
        return new DirectoryInfo(path).EnumerateFiles()
            .Where(f => Exts.Contains(f.Extension.ToLowerInvariant()) && !f.Name.StartsWith("~$"))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }
}

// Resultado da última sincronização de uma origem (exibido em Configurações).
public sealed class DataSyncStatus
{
    public string Label { get; set; } = "";
    public bool Ok { get; set; }
    public string Message { get; set; } = "Ainda não sincronizado nesta sessão.";
    public string Path { get; set; } = "";
    public string File { get; set; } = "";
    public int Rows { get; set; }
    public int Warnings { get; set; }
    public List<string> Currencies { get; set; } = new();
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public bool HasRun => FinishedAt is not null;
}
