using System.Data;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Repositório das listas de preenchimento (dados-mestre editáveis), sobre o
// ParquetStore — mesmo padrão das oportunidades (VARCHAR, consolidação por id,
// cache em memória, write-through). Registrado como singleton.
//   • Items(key) → valores da lista, na ordem.
//   • Add / Update / Delete → manutenção pela aba Listas.
//   • SeedIfEmpty() → carrega os valores iniciais (ListSeed) na 1ª execução.
// ---------------------------------------------------------------------------
public class ListRepository
{
    private const string Entity = "lists";
    private const string Cols = "id, list_key, value, sort_order";

    private readonly ParquetStore _store;
    private readonly object _lock = new();
    private List<ListItem>? _cache;

    public ListRepository(ParquetStore store) => _store = store;

    private static string S(IDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    private List<ListItem> LoadFromStore() =>
        _store.ReadLatest(Entity, Cols, r => new ListItem
        {
            Id = S(r, 0),
            ListKey = S(r, 1),
            Value = S(r, 2),
            Sort = string.IsNullOrWhiteSpace(S(r, 3)) ? "0" : S(r, 3),
        });

    private List<ListItem> EnsureCache()
    {
        if (_cache is null) _cache = LoadFromStore();
        return _cache;
    }

    /// <summary>Todos os itens (cópias).</summary>
    public List<ListItem> All()
    {
        lock (_lock) return EnsureCache().Select(Clone).ToList();
    }

    /// <summary>Valores de uma lista, ordenados.</summary>
    public List<string> Items(string key)
    {
        lock (_lock)
            return EnsureCache().Where(i => i.ListKey == key)
                .OrderBy(i => i.SortValue).ThenBy(i => i.Value)
                .Select(i => i.Value).ToList();
    }

    /// <summary>Itens completos de uma lista, ordenados (para a manutenção).</summary>
    public List<ListItem> ItemsFull(string key)
    {
        lock (_lock)
            return EnsureCache().Where(i => i.ListKey == key)
                .OrderBy(i => i.SortValue).ThenBy(i => i.Value)
                .Select(Clone).ToList();
    }

    public void Add(string key, string value)
    {
        value = (value ?? "").Trim();
        if (value == "") return;
        int nextSort;
        lock (_lock)
        {
            var cache = EnsureCache();
            nextSort = cache.Where(i => i.ListKey == key).Select(i => i.SortValue).DefaultIfEmpty(0).Max() + 1;
        }
        var item = new ListItem { Id = "li-" + Guid.NewGuid().ToString("N"), ListKey = key, Value = value, Sort = nextSort.ToString() };
        _store.WriteRow(Entity, Row(item));
        lock (_lock) { EnsureCache().Add(Clone(item)); }
    }

    public void Update(string id, string value)
    {
        value = (value ?? "").Trim();
        ListItem? item;
        lock (_lock) item = EnsureCache().FirstOrDefault(i => i.Id == id);
        if (item is null || value == "") return;
        var updated = new ListItem { Id = item.Id, ListKey = item.ListKey, Value = value, Sort = item.Sort };
        _store.WriteRow(Entity, Row(updated));
        lock (_lock)
        {
            var cache = EnsureCache();
            cache.RemoveAll(i => i.Id == id);
            cache.Add(Clone(updated));
        }
    }

    public void Delete(string id)
    {
        _store.WriteRow(Entity, new KeyValuePair<string, object?>[] { new("id", id) }, deleted: true);
        lock (_lock) { EnsureCache().RemoveAll(i => i.Id == id); }
    }

    /// <summary>Carrega os valores iniciais (ListSeed) se a base ainda estiver vazia.</summary>
    public void SeedIfEmpty()
    {
        if (!_store.IsEmpty(Entity)) return;
        var rows = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        foreach (var def in ListSeed.All)
        {
            var sort = 0;
            foreach (var v in def.Seed)
                rows.Add(Row(new ListItem { Id = "li-" + Guid.NewGuid().ToString("N"), ListKey = def.Key, Value = v, Sort = (++sort).ToString() }));
        }
        if (rows.Count > 0) _store.WriteBatch(Entity, rows);
        lock (_lock) { _cache = null; }
    }

    private static KeyValuePair<string, object?>[] Row(ListItem i) =>
        new KeyValuePair<string, object?>[]
        {
            new("id", i.Id),
            new("list_key", i.ListKey),
            new("value", i.Value),
            new("sort_order", i.Sort),
        };

    private static ListItem Clone(ListItem i) => new()
    {
        Id = i.Id, ListKey = i.ListKey, Value = i.Value, Sort = i.Sort,
    };
}
