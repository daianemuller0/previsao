using System.Data;
using System.Globalization;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Repositório do Controle Orçamentário sobre o ParquetStore (mesmo padrão das
// demais entidades: VARCHAR, consolidação por id, cache, write-through).
// Três entidades: valores (controle), auditoria (controle_hist) e observações
// de fechamento (controle_obs). Registrado como singleton.
// ---------------------------------------------------------------------------
public class ControleRepository
{
    private const string EntEntries = "controle";
    private const string EntHist = "controle_hist";
    private const string EntObs = "controle_obs";
    private const string EntStatus = "controle_status";

    private readonly ParquetStore _store;
    private readonly object _lock = new();
    private List<ControleEntry>? _entries;

    public ControleRepository(ParquetStore store) => _store = store;

    public DateTime LoadedAt { get; private set; }

    private static string S(IDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    // ---- valores -----------------------------------------------------------
    private List<ControleEntry> LoadEntries() =>
        _store.ReadLatest(EntEntries,
            "id, year, month, category, budget, realizado, forecast, note, updated_by, updated_at",
            r => new ControleEntry
            {
                Id = S(r, 0), Year = S(r, 1), Month = S(r, 2), Category = S(r, 3),
                Budget = string.IsNullOrWhiteSpace(S(r, 4)) ? "0" : S(r, 4),
                Realizado = string.IsNullOrWhiteSpace(S(r, 5)) ? "0" : S(r, 5),
                Forecast = S(r, 6), Note = S(r, 7), UpdatedBy = S(r, 8), UpdatedAt = S(r, 9),
            });

    private List<ControleEntry> Cache()
    {
        if (_entries is null) { _entries = LoadEntries(); LoadedAt = DateTime.Now; }
        return _entries;
    }

    public List<ControleEntry> Entries(int year)
    {
        lock (_lock) return Cache().Where(e => e.YearV == year).Select(Clone).ToList();
    }

    public ControleEntry? Get(int year, int month, string category)
    {
        lock (_lock) return Cache()
            .Where(e => e.YearV == year && e.MonthV == month && e.Category == category)
            .Select(Clone).FirstOrDefault();
    }

    public void Refresh()
    {
        lock (_lock) { _entries = LoadEntries(); LoadedAt = DateTime.Now; }
    }

    // Salva um valor e registra a auditoria (uma linha por campo alterado).
    public void Save(int year, int month, string category, double? budget, double? realizado,
        double? forecast, string note, string user, string justification)
    {
        ControleEntry cur;
        lock (_lock)
        {
            cur = Cache().FirstOrDefault(e => e.YearV == year && e.MonthV == month && e.Category == category)
                  ?? new ControleEntry { Id = "cx-" + Guid.NewGuid().ToString("N"), Year = year.ToString(), Month = month.ToString(), Category = category };
        }
        var updated = Clone(cur);
        var hist = new List<ControleHist>();
        void Track(string field, string oldV, double? newV, Action<string> set)
        {
            if (newV is null) return;
            var nv = newV.Value.ToString(CultureInfo.InvariantCulture);
            if (nv == oldV) return;
            set(nv);
            hist.Add(new ControleHist
            {
                Id = "ch-" + Guid.NewGuid().ToString("N"), Ts = Iso(DateTime.Now), User = user,
                Year = year.ToString(), Month = month.ToString(), Category = category, Field = field,
                OldValue = oldV, NewValue = nv, Justification = justification, Origin = "Manual",
            });
        }
        Track("Orçamento", cur.Budget, budget, v => updated.Budget = v);
        Track("Realizado", cur.Realizado, realizado, v => updated.Realizado = v);
        Track("Forecast", string.IsNullOrWhiteSpace(cur.Forecast) ? "" : cur.Forecast, forecast, v => updated.Forecast = v);
        updated.Note = note ?? "";
        updated.UpdatedBy = user;
        updated.UpdatedAt = Iso(DateTime.Now);

        _store.WriteRow(EntEntries, RowEntry(updated));
        lock (_lock)
        {
            var c = Cache();
            c.RemoveAll(e => e.Id == updated.Id || (e.YearV == year && e.MonthV == month && e.Category == category));
            c.Add(Clone(updated));
        }
        foreach (var h in hist) _store.WriteRow(EntHist, RowHist(h));
    }

    // ---- histórico ---------------------------------------------------------
    public List<ControleHist> History(int year)
    {
        var list = _store.ReadLatest(EntHist,
            "id, ts, user, year, month, category, field, old_value, new_value, justification, origin",
            r => new ControleHist
            {
                Id = S(r, 0), Ts = S(r, 1), User = S(r, 2), Year = S(r, 3), Month = S(r, 4),
                Category = S(r, 5), Field = S(r, 6), OldValue = S(r, 7), NewValue = S(r, 8),
                Justification = S(r, 9), Origin = string.IsNullOrWhiteSpace(S(r, 10)) ? "Manual" : S(r, 10),
            });
        return list.Where(h => h.Year == year.ToString()).OrderByDescending(h => h.Ts).ToList();
    }

    // ---- observações -------------------------------------------------------
    public List<ControleObs> Observations(int year)
    {
        var list = _store.ReadLatest(EntObs,
            "id, ts, year, month, category, author, priority, text",
            r => new ControleObs
            {
                Id = S(r, 0), Ts = S(r, 1), Year = S(r, 2), Month = S(r, 3), Category = S(r, 4),
                Author = S(r, 5), Priority = string.IsNullOrWhiteSpace(S(r, 6)) ? "Média" : S(r, 6), Text = S(r, 7),
            });
        return list.Where(o => o.Year == year.ToString()).OrderByDescending(o => o.Ts).ToList();
    }

    public void AddObservation(int year, int month, string category, string author, string priority, string text)
    {
        var o = new ControleObs
        {
            Id = "co-" + Guid.NewGuid().ToString("N"), Ts = Iso(DateTime.Now), Year = year.ToString(),
            Month = month.ToString(), Category = category, Author = author, Priority = priority, Text = text,
        };
        _store.WriteRow(EntObs, new KeyValuePair<string, object?>[]
        {
            new("id", o.Id), new("ts", o.Ts), new("year", o.Year), new("month", o.Month),
            new("category", o.Category), new("author", o.Author), new("priority", o.Priority), new("text", o.Text),
        });
    }

    public void DeleteObservation(string id) =>
        _store.WriteRow(EntObs, new KeyValuePair<string, object?>[] { new("id", id) }, deleted: true);

    // ---- status de fechamento por mês --------------------------------------
    // Estados: Aberto · Em preenchimento · Em revisão · Fechado.
    public Dictionary<int, string> MonthStatus(int year)
    {
        var list = _store.ReadLatest(EntStatus, "id, year, month, status",
            r => (Year: S(r, 1), Month: S(r, 2), Status: S(r, 3)));
        var d = new Dictionary<int, string>();
        foreach (var s in list.Where(x => x.Year == year.ToString()))
            if (int.TryParse(s.Month, out var m)) d[m] = string.IsNullOrWhiteSpace(s.Status) ? "Aberto" : s.Status;
        return d;
    }

    public void SetMonthStatus(int year, int month, string status, string user, string justification)
    {
        var old = MonthStatus(year).GetValueOrDefault(month, "Aberto");
        _store.WriteRow(EntStatus, new KeyValuePair<string, object?>[]
        {
            new("id", $"cs-{year}-{month}"), new("year", year.ToString()), new("month", month.ToString()), new("status", status),
        });
        _store.WriteRow(EntHist, RowHist(new ControleHist
        {
            Id = "ch-" + Guid.NewGuid().ToString("N"), Ts = Iso(DateTime.Now), User = user,
            Year = year.ToString(), Month = month.ToString(), Category = "—", Field = "Status",
            OldValue = old, NewValue = status, Justification = justification,
            Origin = status == "Fechado" ? "Fechamento" : old == "Fechado" ? "Reabertura" : "Manual",
        }));
    }

    // ---- seed --------------------------------------------------------------
    public void SeedIfEmpty()
    {
        if (!_store.IsEmpty(EntEntries)) return;
        var year = DateTime.Today.Year;
        var rows = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        foreach (var (cat, month, budget, realizado, forecast) in ControleSeed.Entries())
        {
            rows.Add(RowEntry(new ControleEntry
            {
                Id = "cx-" + Guid.NewGuid().ToString("N"),
                Year = year.ToString(), Month = month.ToString(), Category = cat,
                Budget = budget.ToString(CultureInfo.InvariantCulture),
                Realizado = realizado.ToString(CultureInfo.InvariantCulture),
                Forecast = forecast > 0 ? forecast.ToString(CultureInfo.InvariantCulture) : "",
                UpdatedBy = "Carga inicial", UpdatedAt = Iso(DateTime.Now),
            }));
        }
        if (rows.Count > 0) _store.WriteBatch(EntEntries, rows);
        lock (_lock) { _entries = null; }
    }

    // ---- helpers -----------------------------------------------------------
    private static KeyValuePair<string, object?>[] RowEntry(ControleEntry e) => new KeyValuePair<string, object?>[]
    {
        new("id", e.Id), new("year", e.Year), new("month", e.Month), new("category", e.Category),
        new("budget", e.Budget), new("realizado", e.Realizado), new("forecast", e.Forecast),
        new("note", e.Note), new("updated_by", e.UpdatedBy), new("updated_at", e.UpdatedAt),
    };

    private static KeyValuePair<string, object?>[] RowHist(ControleHist h) => new KeyValuePair<string, object?>[]
    {
        new("id", h.Id), new("ts", h.Ts), new("user", h.User), new("year", h.Year), new("month", h.Month),
        new("category", h.Category), new("field", h.Field), new("old_value", h.OldValue),
        new("new_value", h.NewValue), new("justification", h.Justification), new("origin", h.Origin),
    };

    private static ControleEntry Clone(ControleEntry e) => new()
    {
        Id = e.Id, Year = e.Year, Month = e.Month, Category = e.Category, Budget = e.Budget,
        Realizado = e.Realizado, Forecast = e.Forecast, Note = e.Note, UpdatedBy = e.UpdatedBy, UpdatedAt = e.UpdatedAt,
    };
}
