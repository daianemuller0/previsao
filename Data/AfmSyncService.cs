using Microsoft.Extensions.Configuration;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Sincroniza a base de AFTERMARKET a partir da planilha exportada do CRM na
// rede. Lê o arquivo mais recente da pasta configurada (Afm:Path), converte a
// moeda (taxas da aba Controle) e o GM, e ESPELHA as oportunidades AFM no
// repositório: faz upsert das linhas da planilha e dá baixa (tombstone) nas que
// sumiram. Roda na subida do app e sob demanda (botão em Configurações).
// Registrado como singleton.
// ---------------------------------------------------------------------------
public sealed class AfmSyncService
{
    private const string AfmPrefix = "afm-";
    private static readonly string[] Exts = { ".xlsx", ".xlsm", ".xls", ".csv" };

    private readonly IConfiguration _cfg;
    private readonly Catalog _cat;
    private readonly OpportunityRepository _repo;
    private readonly ControleRepository _controle;
    private readonly object _lock = new();

    public AfmSyncService(IConfiguration cfg, Catalog cat, OpportunityRepository repo, ControleRepository controle)
    {
        _cfg = cfg; _cat = cat; _repo = repo; _controle = controle;
    }

    public bool Enabled => _cfg.GetValue("Afm:Enabled", true);
    public string ConfiguredPath => _cfg["Afm:Path"] ?? "";

    public AfmSyncStatus Last { get; private set; } = new();

    // Lê a planilha da rede e espelha as oportunidades AFM. Nunca lança: qualquer
    // problema vira uma mensagem no status (a planilha da rede pode estar
    // indisponível/lenta e o app precisa seguir no ar).
    public AfmSyncStatus Sync()
    {
        lock (_lock)
        {
            var status = new AfmSyncStatus { StartedAt = DateTime.Now, Path = ConfiguredPath };
            try
            {
                if (!Enabled)
                {
                    status.Ok = false;
                    status.Message = "Sincronização automática do Aftermarket desativada (Afm:Enabled = false).";
                    return Finish(status);
                }

                var file = ResolveFile(ConfiguredPath);
                if (file is null)
                {
                    status.Ok = false;
                    status.Message = string.IsNullOrWhiteSpace(ConfiguredPath)
                        ? "Caminho da planilha AFM não configurado (Afm:Path)."
                        : "Nenhuma planilha (.xlsx/.csv) encontrada no caminho configurado.";
                    return Finish(status);
                }
                status.File = file;

                // Copia o arquivo para memória permitindo leitura mesmo com o CRM
                // gravando (FileShare.ReadWrite).
                byte[] bytes;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var buf = new MemoryStream())
                {
                    fs.CopyTo(buf);
                    bytes = buf.ToArray();
                }

                // Snapshot das taxas (uma leitura só) para o resolver por linha.
                var rates = _controle.CurrencyRates()
                    .Where(m => m.HasRate)
                    .ToDictionary(m => m.Code, m => m.RateV, StringComparer.OrdinalIgnoreCase);
                double? Resolve(string code) => rates.TryGetValue(code, out var v) ? v : (double?)null;

                var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var importer = new OpportunityImporter(_cat);
                OpportunityImporter.Result result;
                using (var ms = new MemoryStream(bytes))
                    result = importer.Parse(Path.GetFileName(file), ms, OpportunityImporter.Source.Afm,
                        Resolve, code => currencies.Add(code));

                // Garante uma linha de taxa para cada moeda estrangeira encontrada.
                foreach (var c in currencies) _controle.EnsureCurrency(c);

                // Espelha só quando houve dados lidos — evita apagar a base por uma
                // leitura vazia/transitória.
                if (result.Rows.Count > 0)
                {
                    var newIds = result.Rows.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
                    var existing = _repo.All()
                        .Where(o => o.Id.StartsWith(AfmPrefix, StringComparison.Ordinal))
                        .Select(o => o.Id).ToList();

                    _repo.SaveMany(result.Rows);
                    foreach (var id in existing)
                        if (!newIds.Contains(id)) _repo.Delete(id);
                    _repo.Refresh();
                }

                status.Rows = result.Rows.Count;
                status.Warnings = result.Warnings.Count;
                status.Currencies = currencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                status.Ok = true;
                status.Message = result.Rows.Count > 0
                    ? $"{result.Rows.Count} oportunidade(s) de Aftermarket sincronizada(s)."
                    : "Planilha lida, mas nenhuma oportunidade reconhecida — confira o cabeçalho das colunas.";
                return Finish(status);
            }
            catch (Exception ex)
            {
                status.Ok = false;
                status.Message = "Falha ao sincronizar a planilha AFM: " + ex.Message;
                return Finish(status);
            }
        }
    }

    private AfmSyncStatus Finish(AfmSyncStatus s)
    {
        s.FinishedAt = DateTime.Now;
        Last = s;
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

// Resultado da última sincronização (exibido em Configurações).
public sealed class AfmSyncStatus
{
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
