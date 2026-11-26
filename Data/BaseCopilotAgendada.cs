using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Mantém a base do agente do Copilot em dia, no computador que tem a pasta
// configurada (Copilot:Pasta). A cada 15 minutos, das 06:00 às 22:00:
//   1. relê as listagens NB e AFM da pasta de rede SÓ se chegou arquivo novo
//      (a equipe salva as listagens às 08:00 e às 13:00; o horário exato varia);
//   2. relê a base do sistema (o vendedor pode ter indicado oportunidades em
//      outro computador);
//   3. regrava o Excel do Copilot só se o conteúdo mudou.
// Sem pasta configurada, não faz nada (os outros computadores seguem como antes).
// ---------------------------------------------------------------------------
public sealed class BaseCopilotAgendada : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(15);
    private readonly BaseCopilotService _copilot;
    private readonly DataSyncService _sync;
    private readonly OpportunityRepository _repo;
    private readonly ILogger<BaseCopilotAgendada> _log;

    public BaseCopilotAgendada(BaseCopilotService copilot, DataSyncService sync, OpportunityRepository repo, ILogger<BaseCopilotAgendada> log)
    {
        _copilot = copilot; _sync = sync; _repo = repo; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_copilot.Pasta == "") return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Intervalo, stoppingToken); }
            catch (TaskCanceledException) { break; }
            if (DateTime.Now.Hour is < 6 or >= 22) continue;
            try
            {
                _sync.SyncAll();        // pula a origem cuja planilha não mudou
                _repo.Refresh();        // indicações feitas em outros computadores
                if (_copilot.GravarSeMudou()) _log.LogInformation("{Msg}", _copilot.UltimaMensagem);
            }
            catch (Exception ex)
            {
                // rede fora do ar: tenta de novo na próxima volta
                _log.LogWarning(ex, "Falha ao atualizar a base do Copilot.");
            }
        }
    }
}
