namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Ajustes que cada pessoa faz nos gráficos de barras (ordem das categorias e
// espessura da barra). Guardado no ParquetStore com id = "login|gráfico", então
// cada um monta a Visão Executiva do jeito que prefere sem afetar os outros.
// Cache em memória: a leitura acontece uma vez por sessão do app.
// ---------------------------------------------------------------------------
public class ChartPrefsRepository
{
    private const string Ent = "user_charts";
    private const string Cols = "id, ordem, barra";

    /// <summary>Espessura padrão da barra (px) e limites aceitos.</summary>
    public const int BarraPadrao = 15;
    public const int BarraMin = 6;
    public const int BarraMax = 34;

    public sealed record Pref(string Ordem, int Barra)
    {
        public static readonly Pref Padrao = new("", BarraPadrao);
    }

    private readonly ParquetStore _store;
    private readonly object _lock = new();
    private Dictionary<string, Pref>? _cache;

    public ChartPrefsRepository(ParquetStore store) => _store = store;

    /// <summary>Sobe a cada gravação: a página percebe que mudou e se redesenha.</summary>
    public int Version { get; private set; }

    private static string Chave(string login, string gráfico) =>
        (login ?? "").Trim() + "|" + (gráfico ?? "").Trim();

    private Dictionary<string, Pref> Cache()
    {
        if (_cache is null)
        {
            var d = new Dictionary<string, Pref>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (id, ordem, barra) in _store.ReadLatest(Ent, Cols, r => (
                    Id: r.IsDBNull(0) ? "" : r.GetString(0),
                    Ordem: r.IsDBNull(1) ? "" : r.GetString(1),
                    Barra: r.IsDBNull(2) ? "" : r.GetString(2))))
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    d[id] = new Pref(ordem, Limitar(barra));
                }
            }
            catch { /* primeira execução / pasta indisponível */ }
            _cache = d;
        }
        return _cache;
    }

    private static int Limitar(string? barra) =>
        int.TryParse(barra, out var v) ? Math.Clamp(v, BarraMin, BarraMax) : BarraPadrao;

    public Pref Get(string login, string gráfico)
    {
        if (string.IsNullOrWhiteSpace(login)) return Pref.Padrao;
        lock (_lock) return Cache().TryGetValue(Chave(login, gráfico), out var p) ? p : Pref.Padrao;
    }

    public void Save(string login, string gráfico, string ordem, int barra)
    {
        if (string.IsNullOrWhiteSpace(login)) return;
        var key = Chave(login, gráfico);
        var pref = new Pref(ordem ?? "", Math.Clamp(barra, BarraMin, BarraMax));
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[]
        {
            new("id", key), new("ordem", pref.Ordem), new("barra", pref.Barra.ToString()),
        });
        lock (_lock) { Cache()[key] = pref; Version++; }
    }

    /// <summary>Volta ao padrão (ordem automática por valor e barra padrão).</summary>
    public void Reset(string login, string gráfico)
    {
        if (string.IsNullOrWhiteSpace(login)) return;
        var key = Chave(login, gráfico);
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[] { new("id", key) }, deleted: true);
        lock (_lock) { Cache().Remove(key); Version++; }
    }

    // ---- ordem gravada ------------------------------------------------------
    // Guardada como os rótulos separados por "|" (rótulo é o que a pessoa vê e
    // arrasta). Rótulos que sumiram do recorte são ignorados e os que entraram
    // depois vão para o fim, então a ordem nunca "quebra" quando os dados mudam.

    public static string Serializar(IEnumerable<string> rótulos) =>
        string.Join("|", rótulos.Select(x => (x ?? "").Replace("|", "/")));

    public static List<T> Aplicar<T>(string ordem, List<T> itens, Func<T, string> rótulo)
    {
        if (string.IsNullOrWhiteSpace(ordem) || itens.Count == 0) return itens;

        var posição = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var partes = ordem.Split('|', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < partes.Length; i++) posição.TryAdd(partes[i], i);

        return itens
            .Select((x, i) => (Item: x, Fixa: posição.TryGetValue(rótulo(x), out var p) ? p : int.MaxValue, Orig: i))
            .OrderBy(x => x.Fixa).ThenBy(x => x.Orig)
            .Select(x => x.Item)
            .ToList();
    }
}
