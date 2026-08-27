using Microsoft.AspNetCore.Components.Server.Circuits;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// O programa roda sem janela: quem o "fecha" é o navegador. Este handler conta
// as abas abertas (cada aba do Blazor é um circuito) e, quando a última some,
// espera um pouco e encerra o processo — senão ele ficaria rodando invisível
// para sempre, e cada nova abertura empilharia mais um.
//
// A espera existe porque um F5 derruba e recria o circuito: dentro da janela de
// carência a aba volta e nada é encerrado. Só vale no modo por-usuário (o app
// abrindo em localhost); num servidor central, encerrar ao sair da última aba
// derrubaria o serviço para todo mundo.
// ---------------------------------------------------------------------------
public sealed class DesligaAoFechar : CircuitHandler
{
    private static readonly TimeSpan Carencia = TimeSpan.FromSeconds(30);

    private static int _abertos;
    private static bool _jaAbriu;      // nunca encerra antes de a 1ª aba conectar

    private readonly IHostApplicationLifetime _vida;

    public DesligaAoFechar(IHostApplicationLifetime vida) => _vida = vida;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        Interlocked.Increment(ref _abertos);
        Volatile.Write(ref _jaAbriu, true);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        var restantes = Interlocked.Decrement(ref _abertos);
        if (restantes > 0 || !Volatile.Read(ref _jaAbriu)) return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            await Task.Delay(Carencia);
            // Reabriu (ou era só um F5): segue no ar.
            if (Volatile.Read(ref _abertos) == 0) _vida.StopApplication();
        });
        return Task.CompletedTask;
    }
}
