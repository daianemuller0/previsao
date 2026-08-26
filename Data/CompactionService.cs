using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HowdenSalesForecast.Data;

// Compacta periodicamente os Parquet de TODAS as entidades. Como a gravação é
// append-only, os arquivos se acumulam com o tempo (follow-up, logs, controle,
// usuários…); a compactação junta cada entidade num único arquivo, mantendo a
// leitura rápida no longo prazo. Roda uma vez ao subir e depois a cada 30 min.
public class CompactionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    // Espera antes da primeira passada: compactar é reescrever a entidade inteira
    // na rede. Fazer isso junto com a leitura da base e a importação das planilhas
    // disputa a mesma rede e atrasa justamente a abertura do programa.
    private static readonly TimeSpan Warmup = TimeSpan.FromMinutes(3);
    private const int Threshold = 25; // compacta quando ultrapassar este nº de arquivos

    private readonly ParquetStore _store;
    private readonly ILogger<CompactionService> _log;

    public CompactionService(ParquetStore store, ILogger<CompactionService> log)
    {
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Warmup, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var entity in _store.Entities())
            {
                try
                {
                    var count = _store.FileCount(entity);
                    if (count > Threshold)
                    {
                        _store.Compact(entity);
                        _log.LogInformation("Compactação de '{Entidade}': {Antes} arquivos → 1.", entity, count);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Falha ao compactar '{Entidade}' (tentará novamente).", entity);
                }
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
