using System.Data;
using System.Globalization;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Identidade visual: armazena os logos (capa/login e página interna) como
// data URIs (base64) no ParquetStore, no padrão chave-valor consolidado por id.
// Registrado como singleton.
// ---------------------------------------------------------------------------
public class BrandRepository
{
    public const string LoginLogo = "login_logo";   // logo da capa (tela de login)
    public const string AppLogo = "app_logo";       // logo da página interna (sidebar)

    private const string Ent = "brand";
    private const string Cols = "id, value, updated_at";

    private readonly ParquetStore _store;
    private readonly object _lock = new();
    private Dictionary<string, string>? _cache;

    public BrandRepository(ParquetStore store) => _store = store;

    private static string S(IDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    private Dictionary<string, string> Cache()
    {
        if (_cache is null)
        {
            _cache = new(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _store.ReadLatest(Ent, Cols, r => (Key: S(r, 0), Value: S(r, 1))))
                _cache[kv.Key] = kv.Value;
        }
        return _cache;
    }

    /// <summary>Data URI do logo, ou null se não configurado.</summary>
    public string? Get(string key)
    {
        lock (_lock)
        {
            var c = Cache();
            return c.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        }
    }

    /// <summary>Grava (ou limpa, com value vazio) o logo da chave.</summary>
    public void Set(string key, string? value)
    {
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[]
        {
            new("id", key),
            new("value", value ?? ""),
            new("updated_at", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)),
        });
        lock (_lock) { _cache = null; }
    }
}
