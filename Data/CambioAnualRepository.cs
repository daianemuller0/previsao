namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Taxa USD→BRL POR ANO, usada pelo módulo Conversão. Guardada no ParquetStore
// (id = ano), então sobrevive a reinícios, atualizações e novas publicações.
// Não há valor padrão: sem taxa cadastrada para o ano, a tela sinaliza e NÃO
// converte (nada de cotação presumida nem de zero silencioso).
// ---------------------------------------------------------------------------
public sealed class CambioAnualRepository
{
    private const string Ent = "conversao_cambio";
    private const string Cols = "id, rate, updated_by, updated_at";

    private readonly ParquetStore _store;
    private readonly object _lock = new();
    private Dictionary<int, double>? _cache;

    public CambioAnualRepository(ParquetStore store) => _store = store;

    /// <summary>Sobe a cada gravação: quem já está renderizado percebe e recalcula.</summary>
    public int Version { get; private set; }

    private Dictionary<int, double> Cache()
    {
        if (_cache is null)
        {
            var d = new Dictionary<int, double>();
            try
            {
                foreach (var (id, rate) in _store.ReadLatest(Ent, Cols,
                    r => (Id: r.IsDBNull(0) ? "" : r.GetString(0), Rate: r.IsDBNull(1) ? "" : r.GetString(1))))
                    if (int.TryParse(id, out var ano)
                        && double.TryParse(rate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                        && v > 0)
                        d[ano] = v;
            }
            catch { /* primeira execução / pasta indisponível */ }
            _cache = d;
        }
        return _cache;
    }

    public Dictionary<int, double> Todas() { lock (_lock) return new Dictionary<int, double>(Cache()); }

    public double? Taxa(int ano) { lock (_lock) return Cache().TryGetValue(ano, out var v) ? v : null; }

    public void Salvar(int ano, double taxa, string usuario)
    {
        if (taxa <= 0) throw new ArgumentOutOfRangeException(nameof(taxa));
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[]
        {
            new("id", ano.ToString()),
            new("rate", taxa.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("updated_by", usuario ?? ""), new("updated_at", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")),
        });
        lock (_lock) { Cache()[ano] = taxa; Version++; }
    }

    public void Remover(int ano)
    {
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[] { new("id", ano.ToString()) }, deleted: true);
        lock (_lock) { Cache().Remove(ano); Version++; }
    }
}
