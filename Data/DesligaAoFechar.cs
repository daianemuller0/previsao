using Microsoft.AspNetCore.Components.Server.Circuits;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// O programa roda sem janela: quem o "fecha" é o navegador. Este handler conta
// as abas conectadas e encerra o processo quando a última some — senão ele
// ficaria rodando invisível, e cada abertura empilharia mais um.
//
// Conta CONEXÕES, não circuitos. Ao fechar a aba, o Blazor guarda o circuito
// por alguns minutos esperando uma reconexão, e só então o considera fechado —
// tempo demais para o processo ficar de pé sem ninguém. A queda da conexão, ao
// contrário, é imediata.
//
// A carência existe porque um F5 (ou uma oscilação de rede) derruba e refaz a
// conexão em segundos: nesse intervalo a aba volta e o encerramento é
// cancelado. Só vale no modo por-usuário; num servidor central, encerrar ao
// sair da última aba derrubaria o serviço para todo mundo.
// ---------------------------------------------------------------------------
public sealed class DesligaAoFechar : CircuitHandler
{
    private static readonly TimeSpan Carencia = TimeSpan.FromSeconds(45);

    private static readonly object _trava = new();
    private static int _conectados;
    private static bool _jaAbriu;                 // nunca encerra antes da 1ª aba
    private static CancellationTokenSource? _agendado;

    private readonly IHostApplicationLifetime _vida;

    public DesligaAoFechar(IHostApplicationLifetime vida) => _vida = vida;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct)
    {
        lock (_trava)
        {
            _conectados++;
            _jaAbriu = true;
            _agendado?.Cancel();                  // voltou: desiste de encerrar
            _agendado = null;
        }
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct)
    {
        lock (_trava)
        {
            _conectados = Math.Max(0, _conectados - 1);
            if (_conectados > 0 || !_jaAbriu) return Task.CompletedTask;

            _agendado?.Cancel();
            var cts = new CancellationTokenSource();
            _agendado = cts;
            _ = EncerrarDepois(cts.Token);
        }
        return Task.CompletedTask;
    }

    private async Task EncerrarDepois(CancellationToken ct)
    {
        try { await Task.Delay(Carencia, ct); }
        catch (TaskCanceledException) { return; }   // alguém reconectou

        lock (_trava) { if (_conectados > 0) return; }
        _vida.StopApplication();
    }
}
