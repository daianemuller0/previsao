using System.Globalization;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Rotina de follow-up: 4 marcos de contato com o cliente e modelos de e-mail em
// 3 idiomas (Português · English · Español). Os marcos são calculados a partir
// da data de envio da proposta e da data prevista de fechamento.
//   1º  pós-envio       → 3 dias úteis após o envio
//   2º  15 dias antes   → fechamento − 15 dias
//   3º  1 semana antes  → fechamento − 7 dias
//   4º  fechamento      → dia do fechamento
// ---------------------------------------------------------------------------
public static class FollowUpRoutine
{
    public static readonly string[] Langs = { "Português", "English", "Español" };

    public sealed record Milestone(int Num, string Label, DateTime Date);

    // Contexto para preencher o modelo de e-mail.
    public sealed record EmailCtx(string Cliente, string Proposta, string Valor, string Fechamento, string Vendedor);

    public static DateTime AddBusinessDays(DateTime d, int n)
    {
        var r = d;
        while (n > 0)
        {
            r = r.AddDays(1);
            if (r.DayOfWeek != DayOfWeek.Saturday && r.DayOfWeek != DayOfWeek.Sunday) n--;
        }
        return r;
    }

    // Marcos válidos (ordenados por data) para uma proposta.
    public static List<Milestone> Milestones(DateTime? sent, DateTime? close)
    {
        var list = new List<Milestone>();
        if (sent is { } s) list.Add(new Milestone(1, "1º pós-envio", AddBusinessDays(s, 3)));
        if (close is { } c)
        {
            list.Add(new Milestone(2, "2º · 15 dias antes", c.AddDays(-15)));
            list.Add(new Milestone(3, "3º · 1 semana antes", c.AddDays(-7)));
            list.Add(new Milestone(4, "4º · fechamento", c));
        }
        return list.OrderBy(m => m.Date).ToList();
    }

    // Marco atual pendente (o primeiro depois do último contato). Null = rotina concluída.
    public static Milestone? Current(List<Milestone> ms, DateTime? lastContact)
    {
        foreach (var m in ms)
            if (lastContact is null || m.Date.Date > lastContact.Value.Date) return m;
        return null;
    }

    // ---- Modelos de e-mail: [marco 1..4][idioma pt/en/es] = (assunto, corpo) ----
    // Placeholders: {cliente} {proposta} {valor} {fechamento} {vendedor}
    private static readonly (string Subj, string Body)[][] Tpl =
    {
        // Marco 1 — pós-envio
        new[]
        {
            ("Proposta {proposta} — {cliente}",
             "Prezados,\n\nConfirmando o envio da nossa proposta {proposta} (valor {valor}). Ficamos à disposição para esclarecer dúvidas e avançar com os próximos passos.\n\nAtenciosamente,\n{vendedor} — Howden"),
            ("Proposal {proposta} — {cliente}",
             "Dear team,\n\nJust confirming we have sent our proposal {proposta} (value {valor}). We remain available to clarify any questions and move forward with next steps.\n\nBest regards,\n{vendedor} — Howden"),
            ("Propuesta {proposta} — {cliente}",
             "Estimados,\n\nConfirmamos el envío de nuestra propuesta {proposta} (valor {valor}). Quedamos a disposición para aclarar dudas y avanzar con los próximos pasos.\n\nSaludos cordiales,\n{vendedor} — Howden"),
        },
        // Marco 2 — 15 dias antes
        new[]
        {
            ("Acompanhamento — Proposta {proposta}",
             "Prezados,\n\nEstamos nos aproximando da data prevista de decisão ({fechamento}). Podemos agendar uma breve conversa para alinhar os detalhes da proposta {proposta} (valor {valor})?\n\nAtenciosamente,\n{vendedor} — Howden"),
            ("Follow-up — Proposal {proposta}",
             "Dear team,\n\nWe are approaching the expected decision date ({fechamento}). Could we schedule a short call to align the details of proposal {proposta} (value {valor})?\n\nBest regards,\n{vendedor} — Howden"),
            ("Seguimiento — Propuesta {proposta}",
             "Estimados,\n\nNos acercamos a la fecha prevista de decisión ({fechamento}). ¿Podemos coordinar una breve reunión para alinear los detalles de la propuesta {proposta} (valor {valor})?\n\nSaludos,\n{vendedor} — Howden"),
        },
        // Marco 3 — 1 semana antes
        new[]
        {
            ("Proposta {proposta} — próxima semana",
             "Prezados,\n\nFaltando cerca de uma semana para {fechamento}, gostaríamos de confirmar o andamento da proposta {proposta} e apoiar no que for necessário para a aprovação.\n\nAtenciosamente,\n{vendedor} — Howden"),
            ("Proposal {proposta} — next week",
             "Dear team,\n\nWith about a week to go until {fechamento}, we would like to confirm the status of proposal {proposta} and support whatever is needed for approval.\n\nBest regards,\n{vendedor} — Howden"),
            ("Propuesta {proposta} — próxima semana",
             "Estimados,\n\nA cerca de una semana de {fechamento}, quisiéramos confirmar el avance de la propuesta {proposta} y apoyar en lo necesario para la aprobación.\n\nSaludos,\n{vendedor} — Howden"),
        },
        // Marco 4 — fechamento
        new[]
        {
            ("Decisão — Proposta {proposta}",
             "Prezados,\n\nHoje é a data prevista de fechamento ({fechamento}). Seguimos à disposição para concluir a proposta {proposta} (valor {valor}). Há algo em que possamos ajudar para avançar?\n\nAtenciosamente,\n{vendedor} — Howden"),
            ("Decision — Proposal {proposta}",
             "Dear team,\n\nToday is the expected closing date ({fechamento}). We remain available to finalize proposal {proposta} (value {valor}). Is there anything we can do to help move forward?\n\nBest regards,\n{vendedor} — Howden"),
            ("Decisión — Propuesta {proposta}",
             "Estimados,\n\nHoy es la fecha prevista de cierre ({fechamento}). Quedamos a disposición para concluir la propuesta {proposta} (valor {valor}). ¿Podemos ayudar en algo para avanzar?\n\nSaludos,\n{vendedor} — Howden"),
        },
    };

    // Assunto + corpo do e-mail para um marco (1..4) e idioma (0=pt,1=en,2=es).
    public static string Email(int milestone, int lang, EmailCtx c)
    {
        var mi = Math.Clamp(milestone, 1, 4) - 1;
        var li = Math.Clamp(lang, 0, 2);
        var (subj, body) = Tpl[mi][li];
        string F(string s) => s
            .Replace("{cliente}", c.Cliente)
            .Replace("{proposta}", c.Proposta)
            .Replace("{valor}", c.Valor)
            .Replace("{fechamento}", c.Fechamento)
            .Replace("{vendedor}", c.Vendedor);
        var assunto = lang == 1 ? "Subject" : lang == 2 ? "Asunto" : "Assunto";
        return $"{assunto}: {F(subj)}\n\n{F(body)}";
    }
}
