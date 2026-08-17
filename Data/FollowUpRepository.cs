using System.Data;
using System.Globalization;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Repositório do follow-up sobre o ParquetStore (padrão do projeto: VARCHAR,
// consolidação por id). Duas entidades: estado atual (followups, id = id da
// oportunidade) e histórico de interações (followup_log). Singleton.
// ---------------------------------------------------------------------------
public class FollowUpRepository
{
    private const string Ent = "followups";
    private const string EntLog = "followup_log";
    private const string Cols = "id, status, last_contact, next_followup, cadence_days, notes, updated_by, updated_at, sent_date, contact_name, contact_email";
    private const string LogCols = "id, opp_id, ts, user, channel, outcome, note";

    private readonly ParquetStore _store;
    private readonly object _lock = new();
    private List<FollowUp>? _state;       // cache do estado (evita ler a rede a cada página)
    private List<FollowUpLog>? _logs;     // cache do histórico
    public FollowUpRepository(ParquetStore store) => _store = store;

    private static string S(IDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private List<FollowUp> LoadState() =>
        _store.ReadLatest(Ent, Cols, r => new FollowUp
        {
            Id = S(r, 0),
            Status = string.IsNullOrWhiteSpace(S(r, 1)) ? FollowUpStatus.Pendente : S(r, 1),
            LastContact = S(r, 2),
            NextFollowUp = S(r, 3),
            CadenceDays = string.IsNullOrWhiteSpace(S(r, 4)) ? "14" : S(r, 4),
            Notes = S(r, 5),
            UpdatedBy = S(r, 6),
            UpdatedAt = S(r, 7),
            SentDate = S(r, 8),
            ContactName = S(r, 9),
            ContactEmail = S(r, 10),
        });

    private List<FollowUp> StateCache()
    {
        if (_state is null) _state = LoadState();
        return _state;
    }

    private List<FollowUpLog> LogsCache()
    {
        if (_logs is null)
            _logs = _store.ReadLatest(EntLog, LogCols, r => new FollowUpLog
            {
                Id = S(r, 0), OppId = S(r, 1), Ts = S(r, 2), User = S(r, 3),
                Channel = S(r, 4), Outcome = S(r, 5), Note = S(r, 6),
            });
        return _logs;
    }

    // Estado de follow-up por id de oportunidade (do cache).
    public Dictionary<string, FollowUp> All()
    {
        lock (_lock)
        {
            var d = new Dictionary<string, FollowUp>(StringComparer.Ordinal);
            foreach (var f in StateCache()) if (!string.IsNullOrWhiteSpace(f.Id)) d[f.Id] = Clone(f);
            return d;
        }
    }

    public void Save(FollowUp f, string user)
    {
        if (string.IsNullOrWhiteSpace(f.Id)) return;
        f.UpdatedBy = user;
        f.UpdatedAt = Iso(DateTime.Now);
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[]
        {
            new("id", f.Id), new("status", f.Status), new("last_contact", f.LastContact),
            new("next_followup", f.NextFollowUp), new("cadence_days", f.CadenceDays),
            new("notes", f.Notes), new("updated_by", f.UpdatedBy), new("updated_at", f.UpdatedAt),
            new("sent_date", f.SentDate),
            new("contact_name", f.ContactName), new("contact_email", f.ContactEmail),
        });
        // Write-through: reflete no cache sem reler a rede.
        lock (_lock)
        {
            var c = StateCache();
            c.RemoveAll(x => x.Id == f.Id);
            c.Add(Clone(f));
        }
    }

    // Registra uma interação (histórico).
    public void AddLog(FollowUpLog l)
    {
        if (string.IsNullOrWhiteSpace(l.Id)) l.Id = "fl-" + Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(l.Ts)) l.Ts = Iso(DateTime.Now);
        _store.WriteRow(EntLog, new KeyValuePair<string, object?>[]
        {
            new("id", l.Id), new("opp_id", l.OppId), new("ts", l.Ts), new("user", l.User),
            new("channel", l.Channel), new("outcome", l.Outcome), new("note", l.Note),
        });
        lock (_lock) { if (_logs is not null) _logs.Add(l); }
    }

    public List<FollowUpLog> Logs(string oppId)
    {
        lock (_lock)
            return LogsCache().Where(l => l.OppId == oppId).OrderByDescending(l => l.Ts).ToList();
    }

    public int LogCount(string oppId)
    {
        lock (_lock) return LogsCache().Count(l => l.OppId == oppId);
    }

    private static FollowUp Clone(FollowUp f) => new()
    {
        Id = f.Id, Status = f.Status, SentDate = f.SentDate, LastContact = f.LastContact,
        NextFollowUp = f.NextFollowUp, CadenceDays = f.CadenceDays, Notes = f.Notes,
        ContactName = f.ContactName, ContactEmail = f.ContactEmail,
        UpdatedBy = f.UpdatedBy, UpdatedAt = f.UpdatedAt,
    };
}
