using System.Data;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Preferência de colunas POR USUÁRIO (ordem e colunas ocultas da tabela de
// oportunidades). Guardada no ParquetStore com id = login, então cada pessoa
// monta a tabela do jeito que preferir sem afetar as outras.
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

    /// <summary>Preferência do login (vazio = padrão).</summary>
    public string Get(string login)
    {
        var key = (login ?? "").Trim();
        if (key == "") return "";
        lock (_lock) return Cache().TryGetValue(key, out var v) ? v : "";
    }

    public void Save(string login, string cols)
    {
        var key = (login ?? "").Trim();
        if (key == "") return;
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[] { new("id", key), new("cols", cols ?? "") });
        lock (_lock) { Cache()[key] = cols ?? ""; Version++; }
    }

    /// <summary>Volta ao padrão (remove a preferência gravada).</summary>
    public void Reset(string login)
    {
        var key = (login ?? "").Trim();
        if (key == "") return;
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[] { new("id", key) }, deleted: true);
        lock (_lock) { Cache().Remove(key); Version++; }
    }
}
