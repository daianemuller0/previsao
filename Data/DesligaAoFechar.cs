using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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
// A carência existe porque nem toda queda de conexão é uma saída: um F5, o
// redirecionamento depois do login, qualquer navegação com recarga, uma
// oscilação da VPN ou o navegador suspendendo uma aba em segundo plano derrubam
// a conexão e a refazem em seguida. E o Blazor não reconecta na hora: ele tenta
// de novo algumas vezes, com intervalos crescentes, o que passa fácil de meio
// minuto numa rede lenta. Carência curta demais encerra o programa DEBAIXO de
// quem está usando — por isso o padrão é generoso.
//
// Ajustável em "FecharAoSairSegundos" no appsettings; ZERO desliga de vez o
// encerramento automático, para quem prefere fechar pelo Gerenciador.
//
// Só vale no modo por-usuário; num servidor central, encerrar ao sair da última
// aba derrubaria o serviço para todo mundo.
// ---------------------------------------------------------------------------
public sealed class DesligaAoFechar : CircuitHandler
{
    private static readonly object _trava = new();
    private static int _conectados;
    private static bool _jaAbriu;                 // nunca encerra antes da 1ª aba
    private static CancellationTokenSource? _agendado;

    /// <summary>Padrão da carência. Cobre com folga a sequência de tentativas de
    /// reconexão do Blazor, que numa rede lenta passa de meio minuto.</summary>
    public const int PadraoSegundos = 60;

    private readonly IHostApplicationLifetime _vida;
    private readonly TimeSpan _carencia;
    private readonly bool _desativado;

    public DesligaAoFechar(IHostApplicationLifetime vida, IConfiguration cfg)
    {
        _vida = vida;
        // Curto o bastante para o programa sumir logo depois de fechar a aba, e
        // longo o bastante para uma recarga de página voltar antes do prazo.
        var s = cfg.GetValue("FecharAoSairSegundos", PadraoSegundos);
        _desativado = s <= 0;
        _carencia = TimeSpan.FromSeconds(Math.Clamp(s, 5, 600));
    }

    /// <summary>Alguma aba já se conectou nesta execução?</summary>
    public static bool JaAbriu { get { lock (_trava) return _jaAbriu; } }

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
            if (_desativado || _conectados > 0 || !_jaAbriu) return Task.CompletedTask;

            _agendado?.Cancel();
            var cts = new CancellationTokenSource();
            _agendado = cts;
            _ = EncerrarDepois(cts.Token, _carencia);
        }
        return Task.CompletedTask;
    }

    private async Task EncerrarDepois(CancellationToken ct, TimeSpan carencia)
    {
        try { await Task.Delay(carencia, ct); }
        catch (TaskCanceledException) { return; }   // alguém reconectou

        lock (_trava) { if (_conectados > 0) return; }
        _vida.StopApplication();
    }
}

// ---------------------------------------------------------------------------
// Rede de segurança: se o navegador não abrir (bloqueado, sem navegador padrão,
// erro na hora de iniciar), ninguém se conecta e o programa ficaria de pé para
// sempre, invisível. Passados alguns minutos sem nenhuma aba, ele desiste.
// ---------------------------------------------------------------------------
public sealed class EncerraSemNinguem : BackgroundService
{
    private static readonly TimeSpan Espera = TimeSpan.FromMinutes(5);

    private readonly IHostApplicationLifetime _vida;
    private readonly bool _desativado;

    public EncerraSemNinguem(IHostApplicationLifetime vida, IConfiguration cfg)
    {
        _vida = vida;
        _desativado = cfg.GetValue("FecharAoSairSegundos", DesligaAoFechar.PadraoSegundos) <= 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_desativado) return;

        try { await Task.Delay(Espera, stoppingToken); }
        catch (TaskCanceledException) { return; }

        // Só encerra se NINGUÉM chegou a abrir. Quem abriu e saiu é assunto da
        // carência ali em cima, que sabe se a pessoa está voltando.
        if (!DesligaAoFechar.JaAbriu) _vida.StopApplication();
    }
}
