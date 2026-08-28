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

    // Categorias que compõem "Vendas NB+AFM (Realizado)" no REPORT HSA.
    private static readonly string[] ReportCats = { "NB", "AFM", "SV", "LTSA" };

    /// <summary>Categoria das VENDAS REALIZADAS — o que de fato se concretizou,
    /// lançado mês a mês na guia Controle. Anda ao lado das vendas reportadas
    /// (VNBAFM), que são o que o CRM reporta; as duas convivem porque nem tudo
    /// que entra reportado vira venda no mesmo mês.</summary>
    public const string RealizadaCat = "VREAL";

    // Séries mensais do REPORT HSA (índice 0 = janeiro … 11 = dezembro) para uso
    // em dashboards externos (ex.: Visão Executiva). Reproduz exatamente as linhas
    // da planilha: Reportado = soma de NB+AFM+SV+LTSA; Meta = orçamento consolidado.
    public (double[] Reportado, double[] Realizada, double[] Meta, double[] MetaHsa) ReportSeries(int year)
    {
        var entries = Entries(year);
        double Real(string cat, int m) => entries.FirstOrDefault(e => e.MonthV == m && e.Category == cat)?.RealizadoV ?? 0;
        double Budget(string cat, int m) => entries.FirstOrDefault(e => e.MonthV == m && e.Category == cat)?.BudgetV ?? 0;
        // Reportado NB+AFM: valor digitado direto (VNBAFM) quando existir; senão soma a composição.
        bool HasVendas(int m) => entries.Any(e => e.MonthV == m && e.Category == "VNBAFM");
        var reportado = new double[12];
        var realizada = new double[12];
        var meta = new double[12];
        var metaHsa = new double[12];
        for (var m = 1; m <= 12; m++)
        {
            reportado[m - 1] = HasVendas(m) ? Real("VNBAFM", m) : ReportCats.Sum(c => Real(c, m));
            realizada[m - 1] = Real(RealizadaCat, m);
            meta[m - 1] = Budget("CONS", m);
            metaHsa[m - 1] = Budget("MHSA", m);
        }
        return (reportado, realizada, meta, metaHsa);
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

    // ---- registros de ofertas/pedidos (tabela livre) -----------------------
    private const string EntReg = "controle_registros";
    private const string RegCols = "id, oferta, pedido, cliente, mercado, kam, valor_liquido, semana, mes, poc, " +
        "moeda, valor_original, observacao, atencao, opp_id, updated_by, updated_at, " +
        "tipo, classificacao, status";

    public List<ControleRegistro> Registros() =>
        _store.ReadLatest(EntReg, RegCols, r => new ControleRegistro
        {
            Id = S(r, 0), Oferta = S(r, 1), Pedido = S(r, 2), Cliente = S(r, 3), Mercado = S(r, 4),
            Kam = S(r, 5), ValorLiquido = S(r, 6), Semana = S(r, 7), Mes = S(r, 8), Poc = S(r, 9),
            Moeda = string.IsNullOrWhiteSpace(S(r, 10)) ? "BRL" : S(r, 10),
            ValorOriginal = S(r, 11), Observacao = S(r, 12), Atencao = S(r, 13), OppId = S(r, 14),
            UpdatedBy = S(r, 15), UpdatedAt = S(r, 16),
            Tipo = S(r, 17), Classificacao = S(r, 18), Status = S(r, 19),
        });

    private static KeyValuePair<string, object?>[] RowReg(ControleRegistro g) => new KeyValuePair<string, object?>[]
    {
        new("id", g.Id), new("oferta", g.Oferta), new("pedido", g.Pedido), new("cliente", g.Cliente),
        new("mercado", g.Mercado), new("kam", g.Kam), new("valor_liquido", g.ValorLiquido),
        new("semana", g.Semana), new("mes", g.Mes), new("poc", g.Poc),
        new("moeda", g.Moeda), new("valor_original", g.ValorOriginal),
        new("observacao", g.Observacao), new("atencao", g.Atencao), new("opp_id", g.OppId),
        new("updated_by", g.UpdatedBy), new("updated_at", g.UpdatedAt),
        new("tipo", g.Tipo), new("classificacao", g.Classificacao), new("status", g.Status),
    };

    public void SaveRegistro(ControleRegistro g, string user)
    {
        if (string.IsNullOrWhiteSpace(g.Id)) g.Id = "cr-" + Guid.NewGuid().ToString("N");
        g.UpdatedBy = user; g.UpdatedAt = Iso(DateTime.Now);
        _store.WriteRow(EntReg, RowReg(g));
    }

    public void SaveRegistros(IEnumerable<ControleRegistro> list, string user)
    {
        var rows = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        foreach (var g in list)
        {
            if (string.IsNullOrWhiteSpace(g.Id)) g.Id = "cr-" + Guid.NewGuid().ToString("N");
            g.UpdatedBy = user; g.UpdatedAt = Iso(DateTime.Now);
            rows.Add(RowReg(g));
        }
        if (rows.Count > 0) _store.WriteBatch(EntReg, rows);
    }

    public void DeleteRegistro(string id) =>
        _store.WriteRow(EntReg, new KeyValuePair<string, object?>[] { new("id", id) }, deleted: true);

    // ---- taxas de conversão de moeda (importação AFM) ----------------------
    // R$ por 1 unidade da moeda. Consolidação por id ("cm-<CODE>") = upsert.
    private const string EntMoedas = "controle_moedas";

    public List<CurrencyRate> CurrencyRates() =>
        _store.ReadLatest(EntMoedas, "id, code, rate, updated_by, updated_at",
            r => new CurrencyRate
            {
                Code = S(r, 1),
                Rate = string.IsNullOrWhiteSpace(S(r, 2)) ? "0" : S(r, 2),
                UpdatedBy = S(r, 3), UpdatedAt = S(r, 4),
            })
        .Where(m => !string.IsNullOrWhiteSpace(m.Code))
        .OrderBy(m => m.Code, StringComparer.OrdinalIgnoreCase).ToList();

    // Taxa vigente da moeda (null quando não cadastrada ou zero).
    public double? RateFor(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var hit = CurrencyRates().FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
        return hit is { HasRate: true } ? hit.RateV : (double?)null;
    }

    // Garante uma linha (taxa 0) para a moeda aparecer no editor de Configurações.
    public void EnsureCurrency(string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code is "" or "BRL") return;
        if (CurrencyRates().Any(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase))) return;
        _store.WriteRow(EntMoedas, new KeyValuePair<string, object?>[]
        {
            new("id", "cm-" + code), new("code", code), new("rate", "0"),
            new("updated_by", "Detecção automática (AFM)"), new("updated_at", Iso(DateTime.Now)),
        });
    }

    public void SaveCurrencyRate(string code, double rate, string user)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code is "" or "BRL") return;
        _store.WriteRow(EntMoedas, new KeyValuePair<string, object?>[]
        {
            new("id", "cm-" + code), new("code", code),
            new("rate", rate.ToString(CultureInfo.InvariantCulture)),
            new("updated_by", user), new("updated_at", Iso(DateTime.Now)),
        });
    }

    // ---- câmbio diário -----------------------------------------------------
    // Série de cotações por dia. O FECHAMENTO DO MÊS é derivado dela — a última
    // cotação do mês —, e não um campo separado que alguém teria de manter em
    // dia à parte. Cache em memória porque a tabela é lida em toda linha de
    // Ofertas e pedidos para converter o valor.
    private const string EntCambio = "controle_cambio";
    private List<CambioDia>? _cambios;

    private List<CambioDia> CacheCambio()
    {
        lock (_lock)
        {
            _cambios ??= _store.ReadLatest(EntCambio, "id, code, data, rate, fonte, updated_by, updated_at",
                r => new CambioDia
                {
                    Id = S(r, 0), Code = S(r, 1), Data = S(r, 2),
                    Rate = string.IsNullOrWhiteSpace(S(r, 3)) ? "0" : S(r, 3),
                    Fonte = S(r, 4), UpdatedBy = S(r, 5), UpdatedAt = S(r, 6),
                })
                .Where(c => !string.IsNullOrWhiteSpace(c.Code) && c.DataValue is not null)
                .ToList();
            return _cambios;
        }
    }

    public List<CambioDia> Cambios()
    {
        lock (_lock) return CacheCambio().Select(CloneCambio).ToList();
    }

    public void RefreshCambio() { lock (_lock) _cambios = null; }

    private static CambioDia CloneCambio(CambioDia c) => new()
    {
        Id = c.Id, Code = c.Code, Data = c.Data, Rate = c.Rate,
        Fonte = c.Fonte, UpdatedBy = c.UpdatedBy, UpdatedAt = c.UpdatedAt,
    };

    /// <summary>Grava as cotações e invalida o cache. Id determinístico por
    /// (moeda, dia): regravar o mesmo dia atualiza, não duplica.</summary>
    public void SaveCambios(IEnumerable<CambioDia> lista, string user)
    {
        var rows = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        foreach (var c in lista)
        {
            if (c.DataValue is not { } d || string.IsNullOrWhiteSpace(c.Code)) continue;
            c.Id = CambioDia.IdDe(c.Code, d);
            c.Code = c.Code.Trim().ToUpperInvariant();
            c.UpdatedBy = user;
            c.UpdatedAt = Iso(DateTime.Now);
            rows.Add(new KeyValuePair<string, object?>[]
            {
                new("id", c.Id), new("code", c.Code), new("data", c.Data), new("rate", c.Rate),
                new("fonte", c.Fonte), new("updated_by", c.UpdatedBy), new("updated_at", c.UpdatedAt),
            });
        }
        if (rows.Count == 0) return;
        _store.WriteBatch(EntCambio, rows);
        RefreshCambio();
    }

    public void SaveCambio(CambioDia c, string user) => SaveCambios(new[] { c }, user);

    /// <summary>Dias que já têm cotação daquela moeda (para não rebuscar).</summary>
    public HashSet<DateTime> DiasComCotacao(string code)
    {
        var set = new HashSet<DateTime>();
        foreach (var c in CacheCambio())
            if (string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase) && c.HasRate && c.DataValue is { } d)
                set.Add(d.Date);
        return set;
    }

    /// <summary>Fechamento do mês: a última cotação lançada dentro dele.
    /// Null quando o mês ainda não tem nenhuma.</summary>
    public CambioDia? FechamentoMes(string code, int year, int month)
    {
        if (string.IsNullOrWhiteSpace(code) || string.Equals(code, "BRL", StringComparison.OrdinalIgnoreCase))
            return null;
        return CacheCambio()
            .Where(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase) && c.HasRate
                        && c.DataValue is { } d && d.Year == year && d.Month == month)
            .OrderByDescending(c => c.DataValue!.Value)
            .Select(CloneCambio)
            .FirstOrDefault();
    }

    /// <summary>Cotação mais recente até hoje (o "fechamento do dia" à vista).</summary>
    public CambioDia? UltimaCotacao(string code)
    {
        var hoje = DateTime.Today;
        return CacheCambio()
            .Where(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase) && c.HasRate
                        && c.DataValue is { } d && d.Date <= hoje)
            .OrderByDescending(c => c.DataValue!.Value)
            .Select(CloneCambio)
            .FirstOrDefault();
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
        SeedRegistrosIfEmpty();
    }

    // Ofertas/pedidos de exemplo — carga inicial da tabela do Controle.
    public void SeedRegistrosIfEmpty()
    {
        if (!_store.IsEmpty(EntReg)) return;
        var y = DateTime.Today.Year;
        var seed = new[]
        {
            new ControleRegistro { Oferta = $"OF-{y}-018", Pedido = "PV-2607", Cliente = "Votorantim Cimentos", Mercado = "Cimento",          Kam = "Ricardo Mendes",    ValorLiquido = "15745000", Semana = "32", Mes = "Agosto",    Poc = "75%" },
            new ControleRegistro { Oferta = $"OF-{y}-021", Pedido = "PV-2620", Cliente = "Vale",                Mercado = "Mineração",        Kam = "Ana Beatriz Rocha", ValorLiquido = "8320000",  Semana = "33", Mes = "Agosto",    Poc = "60%" },
            new ControleRegistro { Oferta = $"OF-{y}-009", Pedido = "PV-2615", Cliente = "Suzano",              Mercado = "Papel e Celulose", Kam = "Thiago Nogueira",   ValorLiquido = "5935000",  Semana = "34", Mes = "Setembro",  Poc = "45%" },
            new ControleRegistro { Oferta = $"OF-{y}-031", Pedido = "",        Cliente = "Gerdau",              Mercado = "Siderurgia",       Kam = "Camila Prado",      ValorLiquido = "4500000",  Semana = "31", Mes = "Julho",     Poc = "90%" },
        };
        SaveRegistros(seed, "Carga inicial");
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
