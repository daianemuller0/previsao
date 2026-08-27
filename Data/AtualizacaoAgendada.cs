using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Atualiza a base nos horários combinados (08:00 e 13:00), para quem estiver
// com o programa aberto. Entre um horário e outro nada acontece: a importação
// só relê as planilhas se o CRM tiver exportado arquivo novo.
//
// Quem abre o programa depois do horário não perde nada — a sincronização da
// subida faz o mesmo trabalho. Isto aqui é para quem deixa aberto o dia todo.
// ---------------------------------------------------------------------------
public sealed class AtualizacaoAgendada : BackgroundService
{
    private static readonly int[] Horarios = { 8, 13 };

    private readonly DataSyncService _sync;
    private readonly OpportunityRepository _repo;
    private readonly ILogger<AtualizacaoAgendada> _log;

    public AtualizacaoAgendada(DataSyncService sync, OpportunityRepository repo, ILogger<AtualizacaoAgendada> log)
    {
        _sync = sync; _repo = repo; _log = log;
    }

    /// <summary>Próximo 08:00 ou 13:00 depois de "agora" (amanhã, se já passaram).</summary>
    internal static DateTime Proximo(DateTime agora)
    {
        foreach (var h in Horarios)
        {
            var hoje = agora.Date.AddHours(h);
            if (hoje > agora) return hoje;
        }
        return agora.Date.AddDays(1).AddHours(Horarios[0]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var alvo = Proximo(DateTime.Now);
            try { await Task.Delay(alvo - DateTime.Now, stoppingToken); }
            catch (TaskCanceledException) { break; }

            try
            {
                _sync.SyncAll();
                _repo.Refresh();
                _log.LogInformation("Atualização automática das {Hora:HH:mm}: {Nb} · {Afm}",
                    alvo, _sync.LastNb.Message, _sync.LastAfm.Message);
            }
            catch (Exception ex)
            {
                // Rede fora do ar na hora marcada não pode derrubar o serviço:
                // na próxima janela ele tenta de novo.
                _log.LogWarning(ex, "Falha na atualização automática das {Hora:HH:mm}.", alvo);
            }
        }
    }
}
