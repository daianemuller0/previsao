using System.Data;
using System.Globalization;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Repositório dos gráficos personalizados (estúdio da Visão Executiva) sobre o
// ParquetStore, no mesmo padrão das demais entidades: VARCHAR, consolidação por
// id, cache e write-through. Registrado como singleton.
// ---------------------------------------------------------------------------
public class ChartConfigRepository
{
    private const string Ent = "chart_specs";
    private const string Cols = "id, title, type, dimension, measure, top_n, sort_order, updated_at";

    private readonly ParquetStore _store;
    private readonly object _lock = new();
    private List<ChartSpec>? _cache;

    public ChartConfigRepository(ParquetStore store) => _store = store;

    private static string S(IDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private List<ChartSpec> Load() =>
        _store.ReadLatest(Ent, Cols, r => new ChartSpec
        {
            Id = S(r, 0), Title = S(r, 1), Type = S(r, 2), Dimension = S(r, 3),
            Measure = S(r, 4), TopN = string.IsNullOrWhiteSpace(S(r, 5)) ? "8" : S(r, 5),
            Sort = string.IsNullOrWhiteSpace(S(r, 6)) ? "0" : S(r, 6), UpdatedAt = S(r, 7),
        });

    private List<ChartSpec> Cache()
    {
        if (_cache is null) _cache = Load();
        return _cache;
    }

    public List<ChartSpec> All()
    {
        lock (_lock) return Cache().OrderBy(c => c.SortV).Select(c => c.Clone()).ToList();
    }

    private static KeyValuePair<string, object?>[] Row(ChartSpec c) => new KeyValuePair<string, object?>[]
    {
        new("id", c.Id), new("title", c.Title), new("type", c.Type), new("dimension", c.Dimension),
        new("measure", c.Measure), new("top_n", c.TopN), new("sort_order", c.Sort), new("updated_at", c.UpdatedAt),
    };

    public void Save(ChartSpec c)
    {
        if (string.IsNullOrWhiteSpace(c.Id)) c.Id = "chart-" + Guid.NewGuid().ToString("N");
        c.UpdatedAt = Iso(DateTime.Now);
        _store.WriteRow(Ent, Row(c));
        lock (_lock) { _cache = null; }
    }

    public void Delete(string id)
    {
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[] { new("id", id) }, deleted: true);
        lock (_lock) { _cache = null; }
    }

    public void SeedIfEmpty()
    {
        if (!_store.IsEmpty(Ent)) return;
        var seed = new[]
        {
            new ChartSpec { Title = "Valor por mercado",    Type = "bar",   Dimension = "market",   Measure = "valor",     TopN = "8", Sort = "0" },
            new ChartSpec { Title = "Ponderado por KAM",    Type = "hbar",  Dimension = "kam",      Measure = "ponderado", TopN = "8", Sort = "1" },
            new ChartSpec { Title = "Distribuição por BU",  Type = "donut", Dimension = "bu",       Measure = "valor",     TopN = "6", Sort = "2" },
            new ChartSpec { Title = "Valor por mês",        Type = "line",  Dimension = "month",    Measure = "valor",     TopN = "0", Sort = "3" },
        };
        var rows = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        foreach (var c in seed)
        {
            c.Id = "chart-" + Guid.NewGuid().ToString("N");
            c.UpdatedAt = Iso(DateTime.Now);
            rows.Add(Row(c));
        }
        _store.WriteBatch(Ent, rows);
        lock (_lock) { _cache = null; }
    }
}
