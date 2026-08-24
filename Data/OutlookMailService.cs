using System.Runtime.Versioning;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Abre rascunhos de e-mail no Outlook do usuário (automação COM), do mesmo jeito
// que o macro VBA fazia: cria o e-mail, preenche destinatário/assunto/corpo,
// ANEXA o arquivo (PDF) e mostra o rascunho para revisão (.Display). Sem SMTP.
//
// Só funciona quando o programa roda na máquina Windows do usuário, com o
// Outlook (desktop clássico) instalado — que é o modo de uso previsto.
// ---------------------------------------------------------------------------
public class OutlookMailService
{
    public bool Available => OperatingSystem.IsWindows();

    public sealed record DraftSpec(string To, string? Cc, string Subject, string Body, string? AttachmentPath);
    public sealed record Result(int Created, int Attached, List<string> Errors);

    // Cria os rascunhos (um por item), cada um exibido para revisão. Reaproveita
    // uma única instância do Outlook. Roda numa thread STA (exigência do COM).
    public Result CreateDrafts(IReadOnlyList<DraftSpec> drafts)
    {
        var errors = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            errors.Add("Disponível apenas no Windows com o Outlook instalado.");
            return new Result(0, 0, errors);
        }
        return RunWindows(drafts, errors);
    }

    [SupportedOSPlatform("windows")]
    private static Result RunWindows(IReadOnlyList<DraftSpec> drafts, List<string> errors)
    {
        int created = 0, attached = 0;
        RunSta(() => CreateDraftsWin(drafts, ref created, ref attached, errors));
        return new Result(created, attached, errors);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateDraftsWin(IReadOnlyList<DraftSpec> drafts, ref int created, ref int attached, List<string> errors)
    {
        var progId = Type.GetTypeFromProgID("Outlook.Application");
        if (progId is null) { errors.Add("Outlook não encontrado nesta máquina."); return; }

        dynamic? outlook;
        try { outlook = Activator.CreateInstance(progId); }
        catch (Exception ex) { errors.Add("Não foi possível abrir o Outlook: " + ex.Message); return; }
        if (outlook is null) { errors.Add("Não foi possível abrir o Outlook."); return; }

        foreach (var d in drafts)
        {
            try
            {
                dynamic mail = outlook.CreateItem(0);   // 0 = olMailItem
                mail.To = d.To ?? "";
                if (!string.IsNullOrWhiteSpace(d.Cc)) mail.CC = d.Cc;
                mail.Subject = d.Subject ?? "";
                mail.Display(false);                    // abre o rascunho (não bloqueia)
                var html = (d.Body ?? "").Replace("\r\n", "\n").Replace("\n", "<br>");
                mail.HTMLBody = html + "<br><br>" + mail.HTMLBody;
                if (!string.IsNullOrWhiteSpace(d.AttachmentPath) && File.Exists(d.AttachmentPath))
                {
                    mail.Attachments.Add(d.AttachmentPath);
                    attached++;
                }
                created++;
            }
            catch (Exception ex) { errors.Add((d.To ?? "?") + ": " + ex.Message); }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RunSta(Action a)
    {
        Exception? err = null;
        var th = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        th.IsBackground = true;
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        if (err is not null) throw err;
    }
}
