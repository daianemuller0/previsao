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
// Só que perder a conexão NÃO é a mesma coisa que sair. Um F5, o login (que
// recarrega a página), uma oscilação da VPN, o computador dormindo ou o
// navegador suspendendo uma aba derrubam a conexão do mesmo jeito — e o Blazor
// não reconecta na hora, tenta de novo algumas vezes. Tratar os dois casos
// igual obrigava a escolher entre encerrar debaixo de quem está usando ou
// deixar o processo vivo depois que a pessoa fechou o navegador.
//
// Então são DOIS prazos, e quem os distingue é o próprio navegador: ao fechar a
// aba de verdade ele dispara "pagehide", e o app.js manda um aviso ao servidor.
//
//   • com aviso de saída  → FecharAoSairSegundos (20s): a pessoa fechou mesmo
//   • sem aviso nenhum    → FecharSemConexaoMinutos (4h): a aba provavelmente
//                            continua aberta e a pessoa volta — almoço, reunião,
//                            máquina bloqueada. Voltar e achar o programa morto
//                            é pior do que um processo parado algumas horas.
//
// ZERO em FecharAoSairSegundos desliga de vez o encerramento automático, para
// quem prefere fechar pelo Gerenciador de Tarefas.
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

    /// <summary>Prazo depois do aviso de saída do navegador. Curto, mas com
    /// folga para o recarregamento do login voltar antes numa rede lenta.</summary>
    public const int PadraoSegundos = 20;

    /// <summary>Prazo quando a conexão cai SEM aviso: aí o mais provável é que a
    /// aba continue aberta e a pessoa volte depois do almoço ou de uma reunião.
    /// Derrubar o programa nesse meio-tempo seria tirá-lo de quem ainda usa.</summary>
    public const int PadraoSemConexaoMinutos = 240;

    private static bool _avisouSaida;             // o navegador disse que fechou

    private readonly IHostApplicationLifetime _vida;
    private readonly TimeSpan _carenciaSaida;
    private readonly TimeSpan _carenciaSemAviso;
    private readonly bool _desativado;

    public DesligaAoFechar(IHostApplicationLifetime vida, IConfiguration cfg)
    {
        _vida = vida;
        // Curto o bastante para o programa sumir logo depois de fechar a aba, e
        // longo o bastante para uma recarga de página voltar antes do prazo.
        var s = cfg.GetValue("FecharAoSairSegundos", PadraoSegundos);
        _desativado = s <= 0;
        _carenciaSaida = TimeSpan.FromSeconds(Math.Clamp(s, 5, 600));
        _carenciaSemAviso = TimeSpan.FromMinutes(Math.Clamp(
            cfg.GetValue("FecharSemConexaoMinutos", PadraoSemConexaoMinutos), 1, 720));
    }

    /// <summary>Alguma aba já se conectou nesta execução?</summary>
    public static bool JaAbriu { get { lock (_trava) return _jaAbriu; } }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct)
    {
        lock (_trava)
        {
            _conectados++;
            _jaAbriu = true;
            _avisouSaida = false;                 // aviso antigo não vale mais
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

            Agendar(_vida, _avisouSaida ? _carenciaSaida : _carenciaSemAviso);
        }
        return Task.CompletedTask;
    }

    /// <summary>O navegador avisou que a aba está sendo fechada (app.js manda um
    /// beacon no "pagehide"). Se a conexão já tinha caído e o programa estava
    /// esperando os 10 minutos, agora dá para encurtar: foi saída mesmo.</summary>
    public static void AvisarSaida(IHostApplicationLifetime vida, TimeSpan carencia)
    {
        lock (_trava)
        {
            _avisouSaida = true;
            if (_conectados == 0 && _jaAbriu && _agendado is not null) Agendar(vida, carencia);
        }
    }

    // Sempre chamado com _trava tomada.
    private static void Agendar(IHostApplicationLifetime vida, TimeSpan carencia)
    {
        _agendado?.Cancel();
        var cts = new CancellationTokenSource();
        _agendado = cts;
        _ = EncerrarDepois(vida, cts.Token, carencia);
    }

    private static async Task EncerrarDepois(IHostApplicationLifetime vida, CancellationToken ct, TimeSpan carencia)
    {
        try { await Task.Delay(carencia, ct); }
        catch (TaskCanceledException) { return; }   // alguém reconectou

        lock (_trava) { if (_conectados > 0) return; }
        vida.StopApplication();
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
