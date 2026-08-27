using System.Data;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Preferência de colunas POR USUÁRIO E POR TABELA (ordem e colunas ocultas).
// Guardada no ParquetStore com id = "login|tabela", então cada pessoa monta
// cada tabela do jeito que preferir sem afetar as outras.
// Cache em memória: a leitura acontece uma vez por login.
// ---------------------------------------------------------------------------
public class ColumnPrefsRepository
{
    private const string Ent = "user_columns";
    private const string Cols = "id, cols";

    private readonly ParquetStore _store;
    private readonly object _lock = new();
    private Dictionary<string, string>? _cache;

    public ColumnPrefsRepository(ParquetStore store) => _store = store;

    /// <summary>Sobe a cada gravação: as tabelas já renderizadas percebem que a
    /// preferência mudou e se recarregam sem precisar recarregar a página.</summary>
    public int Version { get; private set; }

    private Dictionary<string, string> Cache()
    {
        if (_cache is null)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (id, cols) in _store.ReadLatest(Ent, Cols,
                    r => (Id: r.IsDBNull(0) ? "" : r.GetString(0), Cols: r.IsDBNull(1) ? "" : r.GetString(1))))
                    if (!string.IsNullOrWhiteSpace(id)) d[id] = cols;
            }
            catch { /* primeira execução / pasta indisponível */ }
            _cache = d;
        }
        return _cache;
    }

    private static string Chave(string login, string tabela) =>
        (login ?? "").Trim() + "|" + (tabela ?? "").Trim();

    /// <summary>Preferência do login para uma tabela (vazio = padrão).</summary>
    public string Get(string login, string tabela)
    {
        if (string.IsNullOrWhiteSpace(login)) return "";
        lock (_lock)
        {
            var c = Cache();
            if (c.TryGetValue(Chave(login, tabela), out var v)) return v;
            // Compatibilidade: antes de existirem várias tabelas, a preferência
            // da tabela de oportunidades era gravada só com o login.
            if (tabela == OpportunityColumns.Tabela && c.TryGetValue(login.Trim(), out var antigo)) return antigo;
            return "";
        }
    }

    public void Save(string login, string tabela, string cols)
    {
        if (string.IsNullOrWhiteSpace(login)) return;
        var key = Chave(login, tabela);
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[] { new("id", key), new("cols", cols ?? "") });
        lock (_lock) { Cache()[key] = cols ?? ""; Version++; }
    }

    /// <summary>Volta ao padrão (remove a preferência gravada).</summary>
    public void Reset(string login, string tabela)
    {
        if (string.IsNullOrWhiteSpace(login)) return;
        var key = Chave(login, tabela);
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[] { new("id", key) }, deleted: true);
        lock (_lock)
        {
            var c = Cache();
            c.Remove(key);
            if (tabela == OpportunityColumns.Tabela) c.Remove(login.Trim());
            Version++;
        }
    }
}
