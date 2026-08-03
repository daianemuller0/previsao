using System.Data;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// Repositório da entidade central (opportunities) sobre o ParquetStore.
// Mesmo padrão do projeto de Licenças (colunas VARCHAR, consolidação na leitura),
// com CACHE em memória: a base é lida uma vez e reutilizada entre as páginas.
// Registrado como singleton — o cache é compartilhado por toda a sessão do app.
//   • All()      → devolve cópias do cache (carrega na 1ª vez).
//   • Refresh()  → relê os Parquet da rede (botão "Atualizar").
//   • Save/Delete→ grava no Parquet e atualiza o cache na hora (write-through).
public class OpportunityRepository
{
    private const string Entity = "opportunities";
    private const string Cols =
        "id, name, proposal_number, pv_number, country_id, market_id, submarket_id, product_id, " +
        "equipment_type_id, kam_id, customer_id, plant_id, commercial_category, intercompany_bu, pv_bu_id, " +
        "currency_code, amount_original, exchange_rate, gm_percent, forecast_category, pipeline_stage_id, " +
        "expected_date, win_probability, close_probability, manager_probability, justification, next_action, " +
        "next_action_date, risks, notes, postpone_count, created_at, updated_at, updated_by, " +
        "value_changed_at, date_changed_at, servico_previsto, market_onestream";

    private readonly ParquetStore _store;
    private readonly object _lock = new();
    private List<Opportunity>? _cache;

    public OpportunityRepository(ParquetStore store) => _store = store;

    /// <summary>Momento da última leitura completa da base (para exibir na UI).</summary>
    public DateTime LoadedAt { get; private set; }

    private static string S(IDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    // Leitura crua do ParquetStore (consolidada por id, sem apagados).
    private List<Opportunity> LoadFromStore() =>
        _store.ReadLatest(Entity, Cols, r => new Opportunity
        {
            Id = S(r, 0),
            Name = S(r, 1),
            ProposalNumber = S(r, 2),
            PvNumber = S(r, 3),
            CountryId = S(r, 4),
            MarketId = S(r, 5),
            SubMarketId = S(r, 6),
            ProductId = S(r, 7),
            EquipmentTypeId = S(r, 8),
            KamId = S(r, 9),
            CustomerId = S(r, 10),
            PlantId = S(r, 11),
            CommercialCategory = S(r, 12),
            IntercompanyBu = S(r, 13),
            PvBusinessUnitId = S(r, 14),
            CurrencyCode = string.IsNullOrWhiteSpace(S(r, 15)) ? "BRL" : S(r, 15),
            AmountOriginal = string.IsNullOrWhiteSpace(S(r, 16)) ? "0" : S(r, 16),
            ExchangeRate = string.IsNullOrWhiteSpace(S(r, 17)) ? "0" : S(r, 17),
            GmPercent = string.IsNullOrWhiteSpace(S(r, 18)) ? "0" : S(r, 18),
            ForecastCategory = string.IsNullOrWhiteSpace(S(r, 19)) ? "Pipeline" : S(r, 19),
            PipelineStageId = S(r, 20),
            ExpectedDate = S(r, 21),
            WinProbability = string.IsNullOrWhiteSpace(S(r, 22)) ? "0" : S(r, 22),
            CloseInPeriodProbability = string.IsNullOrWhiteSpace(S(r, 23)) ? "0" : S(r, 23),
            ManagerProbability = S(r, 24),
            Justification = S(r, 25),
            NextAction = S(r, 26),
            NextActionDate = S(r, 27),
            Risks = S(r, 28),
            Notes = S(r, 29),
            PostponeCount = string.IsNullOrWhiteSpace(S(r, 30)) ? "0" : S(r, 30),
            CreatedAt = S(r, 31),
            UpdatedAt = S(r, 32),
            UpdatedBy = S(r, 33),
            ValueChangedAt = S(r, 34),
            DateChangedAt = S(r, 35),
            ServicoPrevisto = S(r, 36),
            MarketOnestream = S(r, 37),
        }, orderBy: "expected_date");

    // Garante o cache carregado (dentro do lock).
    private List<Opportunity> EnsureCache()
    {
        if (_cache is null)
        {
            _cache = LoadFromStore();
            LoadedAt = DateTime.Now;
        }
        return _cache;
    }

    /// <summary>Devolve cópias do cache (não afeta o cache se o chamador editar).</summary>
    public List<Opportunity> All()
    {
        lock (_lock) return EnsureCache().Select(Clone).ToList();
    }

    /// <summary>Relê a base dos Parquet (descarta o cache atual).</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _cache = LoadFromStore();
            LoadedAt = DateTime.Now;
        }
    }

    public Opportunity? Get(string id)
    {
        lock (_lock) return EnsureCache().Where(o => o.Id == id).Select(Clone).FirstOrDefault();
    }

    // Grava várias oportunidades num único arquivo Parquet (usado no seed).
    public void SaveMany(IEnumerable<Opportunity> opps)
    {
        var rows = opps.Select(o => (IReadOnlyList<KeyValuePair<string, object?>>)Row(o)).ToList();
        if (rows.Count == 0) return;
        _store.WriteBatch(Entity, rows);
        lock (_lock) { _cache = null; } // recarrega na próxima leitura
    }

    public void Save(Opportunity o)
    {
        _store.WriteRow(Entity, Row(o));

        // Write-through: reflete no cache imediatamente (sem reler a rede).
        lock (_lock)
        {
            var cache = EnsureCache();
            cache.RemoveAll(x => x.Id == o.Id);
            cache.Add(Clone(o));
        }
    }

    private static KeyValuePair<string, object?>[] Row(Opportunity o) =>
        new KeyValuePair<string, object?>[]
        {
            new("id", o.Id),
            new("name", o.Name),
            new("proposal_number", o.ProposalNumber),
            new("pv_number", o.PvNumber),
            new("country_id", o.CountryId),
            new("market_id", o.MarketId),
            new("submarket_id", o.SubMarketId),
            new("product_id", o.ProductId),
            new("equipment_type_id", o.EquipmentTypeId),
            new("kam_id", o.KamId),
            new("customer_id", o.CustomerId),
            new("plant_id", o.PlantId),
            new("commercial_category", o.CommercialCategory),
            new("intercompany_bu", o.IntercompanyBu),
            new("pv_bu_id", o.PvBusinessUnitId),
            new("currency_code", o.CurrencyCode),
            new("amount_original", o.AmountOriginal),
            new("exchange_rate", o.ExchangeRate),
            new("gm_percent", o.GmPercent),
            new("forecast_category", o.ForecastCategory),
            new("pipeline_stage_id", o.PipelineStageId),
            new("expected_date", o.ExpectedDate),
            new("win_probability", o.WinProbability),
            new("close_probability", o.CloseInPeriodProbability),
            new("manager_probability", o.ManagerProbability),
            new("justification", o.Justification),
            new("next_action", o.NextAction),
            new("next_action_date", o.NextActionDate),
            new("risks", o.Risks),
            new("notes", o.Notes),
            new("postpone_count", o.PostponeCount),
            new("created_at", o.CreatedAt),
            new("updated_at", o.UpdatedAt),
            new("updated_by", o.UpdatedBy),
            new("value_changed_at", o.ValueChangedAt),
            new("date_changed_at", o.DateChangedAt),
            new("servico_previsto", o.ServicoPrevisto),
            new("market_onestream", o.MarketOnestream),
        };

    public void Delete(string id)
    {
        _store.WriteRow(Entity, new KeyValuePair<string, object?>[] { new("id", id) }, deleted: true);
        lock (_lock) { EnsureCache().RemoveAll(x => x.Id == id); }
    }

    /// <summary>Apaga TODA a base de oportunidades (remove os Parquet e zera o cache).</summary>
    public void DeleteAll()
    {
        _store.Clear(Entity);
        lock (_lock) { _cache = new List<Opportunity>(); LoadedAt = DateTime.Now; }
    }

    private static Opportunity Clone(Opportunity o) => new()
    {
        Id = o.Id, Name = o.Name, ProposalNumber = o.ProposalNumber, PvNumber = o.PvNumber,
        CountryId = o.CountryId, MarketId = o.MarketId, SubMarketId = o.SubMarketId, ProductId = o.ProductId,
        EquipmentTypeId = o.EquipmentTypeId, KamId = o.KamId, CustomerId = o.CustomerId, PlantId = o.PlantId,
        CommercialCategory = o.CommercialCategory, IntercompanyBu = o.IntercompanyBu, PvBusinessUnitId = o.PvBusinessUnitId,
        CurrencyCode = o.CurrencyCode, AmountOriginal = o.AmountOriginal, ExchangeRate = o.ExchangeRate, GmPercent = o.GmPercent,
        ForecastCategory = o.ForecastCategory, PipelineStageId = o.PipelineStageId, ExpectedDate = o.ExpectedDate,
        WinProbability = o.WinProbability, CloseInPeriodProbability = o.CloseInPeriodProbability,
        ManagerProbability = o.ManagerProbability, Justification = o.Justification, NextAction = o.NextAction,
        NextActionDate = o.NextActionDate, Risks = o.Risks, Notes = o.Notes, PostponeCount = o.PostponeCount,
        CreatedAt = o.CreatedAt, UpdatedAt = o.UpdatedAt, UpdatedBy = o.UpdatedBy,
        ValueChangedAt = o.ValueChangedAt, DateChangedAt = o.DateChangedAt,
        ServicoPrevisto = o.ServicoPrevisto, MarketOnestream = o.MarketOnestream,
    };
}
