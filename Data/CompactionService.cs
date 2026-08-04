using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HowdenSalesForecast.Data;

// Compacta periodicamente os Parquet de 'opportunities'. Como a gravação é
// append-only, os arquivos se acumulam com o tempo; a compactação junta tudo
// num único arquivo, mantendo a leitura rápida no longo prazo.
public class CompactionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = _store.FileCount("opportunities");
                if (count > Threshold)
                {
                    _store.Compact("opportunities");
                    _log.LogInformation("Compactação de 'opportunities': {Antes} arquivos → 1.", count);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Falha ao compactar 'opportunities' (tentará novamente).");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
