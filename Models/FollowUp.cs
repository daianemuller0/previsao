using System.Globalization;

namespace HowdenSalesForecast.Models;

// ---------------------------------------------------------------------------
// Estado de follow-up de uma proposta em aberto (dado do app, não vem do CRM).
// Persistido como texto no Parquet, id = id da oportunidade.
// ---------------------------------------------------------------------------
public class FollowUp
{
    public string Id { get; set; } = "";           // = id da oportunidade
    public string Status { get; set; } = "Pendente"; // ver FollowUpStatus.All
    public string SentDate { get; set; } = "";       // yyyy-MM-dd — envio da proposta
    public string LastContact { get; set; } = "";    // yyyy-MM-dd — último contato
    public string NextFollowUp { get; set; } = "";   // yyyy-MM-dd — próximo follow-up
    public string CadenceDays { get; set; } = "14";  // cadência (dias) da rotina
    public string Notes { get; set; } = "";          // observação atual
    public string UpdatedBy { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public DateTime? SentDateValue => P(SentDate);

    private static DateTime? P(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public DateTime? LastContactValue => P(LastContact);
    public DateTime? NextFollowUpValue => P(NextFollowUp);
    public int CadenceDaysValue => int.TryParse(CadenceDays, out var v) && v > 0 ? v : 14;
    public bool HasRecord => !string.IsNullOrWhiteSpace(LastContact) || !string.IsNullOrWhiteSpace(NextFollowUp) || !string.IsNullOrWhiteSpace(Status);
}

// Registro de interação (histórico de follow-up).
public class FollowUpLog
{
    public string Id { get; set; } = "";
    public string OppId { get; set; } = "";
    public string Ts { get; set; } = "";       // ISO
    public string User { get; set; } = "";
    public string Channel { get; set; } = "";  // Ligação / E-mail / Reunião / WhatsApp / Outro
    public string Outcome { get; set; } = "";  // status resultante
    public string Note { get; set; } = "";

    public DateTime? TsValue =>
        DateTime.TryParse(Ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

public static class FollowUpStatus
{
    public const string Pendente = "Pendente";
    public const string EmContato = "Em contato";
    public const string Aguardando = "Aguardando cliente";
    public const string SemRetorno = "Sem retorno";
    public const string Ganho = "Ganho";
    public const string Perdido = "Perdido";

    public static readonly string[] All =
        { Pendente, EmContato, Aguardando, SemRetorno, Ganho, Perdido };

    // Status que ainda demandam acompanhamento (rotina ativa).
    public static bool IsActive(string s) => s != Ganho && s != Perdido;
}

public static class FollowUpChannel
{
    public static readonly string[] All =
        { "Ligação", "E-mail", "Reunião", "WhatsApp", "Visita", "Outro" };
}
