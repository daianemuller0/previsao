using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Busca a cotação de fechamento de cada dia no Banco Central (PTAX) e guarda a
// série. O FECHAMENTO DO MÊS sai daí: é a última cotação lançada no mês.
//
// Só busca os dias que faltam, e só até hoje — dia sem cotação (fim de semana,
// feriado, ou pregão que ainda não fechou) volta vazio do BC e simplesmente não
// vira registro. Como o id é (moeda, dia), rodar de novo não duplica nada.
//
// A rede da Howden pode bloquear a saída para a internet. Isso NÃO é tratado
// como erro grave: o serviço registra o aviso, segue vivo, e o Controle continua
// podendo digitar a cotação na mão — a tela mostra a origem de cada número.
// ---------------------------------------------------------------------------
public sealed class CambioDiarioService : BackgroundService
{
    // Endpoint público de cotações do Banco Central (PTAX, formato OData).
    private const string Url =
        "https://olinda.bcb.gov.br/olinda/servico/PTAX/versao/v1/odata/" +
        "CotacaoMoedaDia(moeda=@moeda,dataCotacao=@dataCotacao)" +
        "?@moeda='{0}'&@dataCotacao='{1}'&$top=1&$format=json" +
        "&$select=cotacaoCompra,cotacaoVenda,dataHoraCotacao";

    /// <summary>Quantos dias para trás procurar cotação faltando. Cobre feriado
    /// prolongado e o tempo em que ninguém abriu o programa.</summary>
    private const int DiasParaTras = 12;

    public const string FonteBc = "Banco Central";

    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(3);

    private readonly IHttpClientFactory _http;
    private readonly ControleRepository _repo;
    private readonly ILogger<CambioDiarioService> _log;

    public CambioDiarioService(IHttpClientFactory http, ControleRepository repo, ILogger<CambioDiarioService> log)
    {
        _http = http; _repo = repo; _log = log;
    }

    /// <summary>Resultado de uma passada, para a tela poder dizer o que houve.</summary>
    public sealed record Resultado(int Novas, int Moedas, string Mensagem);

    /// <summary>Última passada — exibida na guia Controle.</summary>
    public Resultado? Ultima { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await AtualizarAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "Falha ao buscar as cotações do Banco Central."); }

            try { await Task.Delay(Intervalo, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Busca no BC as cotações que faltam. Chamado pelo serviço e pelo
    /// botão "Atualizar cotações" na guia Controle.</summary>
    public async Task<Resultado> AtualizarAsync(CancellationToken ct = default)
    {
        var moedas = _repo.CurrencyRates()
            .Select(m => m.Code)
            .Where(c => !string.IsNullOrWhiteSpace(c) && !string.Equals(c, "BRL", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (moedas.Count == 0)
            return Guardar(new Resultado(0, 0, "Nenhuma moeda estrangeira cadastrada — nada a buscar."));

        var novas = new List<CambioDia>();
        var falhou = 0;
        using var cliente = _http.CreateClient("bcb");

        foreach (var code in moedas)
        {
            var jaTem = _repo.DiasComCotacao(code);
            for (var i = 0; i <= DiasParaTras; i++)
            {
                if (ct.IsCancellationRequested) break;
                var dia = DateTime.Today.AddDays(-i);
                // Fim de semana não tem pregão: nem vale a ida à rede.
                if (dia.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                if (jaTem.Contains(dia)) continue;

                try
                {
                    var taxa = await BuscarAsync(cliente, code, dia, ct);
                    if (taxa is not { } v || v <= 0) continue;
                    novas.Add(new CambioDia
                    {
                        Code = code,
                        Data = dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Rate = v.ToString(CultureInfo.InvariantCulture),
                        Fonte = FonteBc,
                    });
                }
                catch (Exception ex)
                {
                    falhou++;
                    _log.LogDebug(ex, "Cotação {Code} de {Dia:yyyy-MM-dd} não veio.", code, dia);
                }
            }
        }

        if (novas.Count > 0) _repo.SaveCambios(novas, FonteBc);

        var msg = novas.Count > 0
            ? $"{novas.Count} cotação(ões) nova(s) do Banco Central."
            : falhou > 0
                ? "Não foi possível falar com o Banco Central. As cotações podem ser digitadas na tabela abaixo."
                : "Cotações já estavam em dia.";
        return Guardar(new Resultado(novas.Count, moedas.Count, msg));
    }

    private Resultado Guardar(Resultado r) { Ultima = r; return r; }

    // Cotação de VENDA do fechamento do dia (é a referência usual de fechamento).
    private static async Task<double?> BuscarAsync(HttpClient cliente, string code, DateTime dia, CancellationToken ct)
    {
        var url = string.Format(CultureInfo.InvariantCulture, Url,
            code.Trim().ToUpperInvariant(), dia.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture));

        using var resp = await cliente.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() == 0)
            return null;   // dia sem pregão: o BC responde lista vazia

        var item = value[0];
        if (item.TryGetProperty("cotacaoVenda", out var venda) && venda.TryGetDouble(out var v) && v > 0) return v;
        if (item.TryGetProperty("cotacaoCompra", out var compra) && compra.TryGetDouble(out var c) && c > 0) return c;
        return null;
    }
}
