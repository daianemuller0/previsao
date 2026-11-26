using System.Globalization;
using ClosedXML.Excel;
using HowdenSalesForecast.Models;
using Microsoft.Extensions.Configuration;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Base para o agente do Copilot: um Excel único com o pipeline em aberto do CRM
// (planilhas NB e AFM já unidas e limpas pelo sistema) e, em cada oportunidade,
// se o vendedor a indicou na previsão. É o que permite perguntar ao agente, por
// exemplo, "quanto o André tem em Closing e quanto indicou na previsão?".
//
// Abas:
//   Leia-me                  o que é cada coluna e a data da base (o agente lê isso)
//   Oportunidades            uma linha por oportunidade em aberto (tabela do Excel)
//   Vendedor x Etapa         em aberto × indicado na previsão, por vendedor, etapa e setor
//
// Regras: as mesmas das telas — cada número de proposta uma vez (Unicas), só as
// em aberto (fora fechadas ganhas/perdidas e as que viraram venda no Controle),
// vendedor com o nome unificado, valor em R$ (Amount convertido do CRM).
//
// Onde grava: a pasta de appsettings "Copilot:Pasta" (ex.: uma pasta do OneDrive/
// SharePoint sincronizada no computador). Nome fixo, sobrescrito a cada geração,
// para o agente apontar sempre para o mesmo arquivo. Sem pasta (ou pasta que não
// existe neste computador): não grava nada; o arquivo ainda sai pelo botão.
// ---------------------------------------------------------------------------
public sealed class BaseCopilotService
{
    public const string NomeArquivo = "Base_Previsao_NB_AFM.xlsx";
    private static readonly CultureInfo Br = new("pt-BR");
    private readonly IConfiguration _cfg;
    private readonly OpportunityRepository _repo;
    private readonly Catalog _cat;
    private readonly object _lock = new();

    public BaseCopilotService(IConfiguration cfg, OpportunityRepository repo, Catalog cat)
    {
        _cfg = cfg; _repo = repo; _cat = cat;
    }

    public string Pasta => (_cfg["Copilot:Pasta"] ?? "").Trim();
    public DateTime? UltimaGravacao { get; private set; }
    public string UltimaMensagem { get; private set; } = "";

    private sealed record Linha(Opportunity O, string Setor, string Vendedor, string Etapa, double Valor, bool Indicada);

    private List<Linha> Linhas() =>
        _repo.Unicas()
            .Where(o => ForecastCategories.IsOpen(o.Category) && !o.MovidaControleValue && !o.PerdidaValue)
            .Select(o => new Linha(o,
                o.CategoriaNbAfm is { Length: > 0 } s ? s : "—",
                OpportunityImporter.CanonicalVendedor(_cat.KamName(o.KamId)) is { Length: > 0 } v ? v : "(sem vendedor)",
                string.IsNullOrWhiteSpace(o.Stage) ? "(sem etapa)" : o.Stage.Trim(),
                Math.Round(o.AmountBrl, 2),
                o.IndicadaValue))
            .OrderBy(l => l.Vendedor, StringComparer.CurrentCultureIgnoreCase).ThenBy(l => l.Etapa).ThenByDescending(l => l.Valor)
            .ToList();

    /// <summary>O Excel da base (as três abas).</summary>
    public byte[] Gerar()
    {
        var linhas = Linhas();
        var agora = DateTime.Now;
        using var wb = new XLWorkbook();

        // ---- Leia-me ----
        var lm = wb.Worksheets.Add("Leia-me");
        var textos = new[]
        {
            ("Base", "Pipeline em aberto do CRM (planilhas New Business e Aftermarket) com a indicação de cada oportunidade na previsão de vendas do sistema Howden Sales Forecast."),
            ("Gerada em", agora.ToString("dd/MM/yyyy HH:mm", Br)),
            ("Oportunidades", linhas.Count.ToString("N0", Br) + " em aberto"),
            ("Em aberto", "Oportunidades que não estão fechadas (ganhas ou perdidas), não foram marcadas como perdidas pelo vendedor e não viraram venda indicada (Controle)."),
            ("Valor (R$)", "Amount convertido do CRM, em reais."),
            ("Indicada na previsão", "Sim = o vendedor indicou a oportunidade na previsão de vendas do sistema. Valor indicado (R$) = o valor dessas oportunidades; nas não indicadas é 0."),
            ("Vendedor", "Nome unificado (o mesmo vendedor escrito de formas diferentes nas planilhas aparece com um nome só)."),
            ("Etapa", "Etapa do funil (Stage). No Aftermarket, a etapa do CRM é convertida para a etapa equivalente do funil do New Business."),
            ("Número da proposta", "Cada número conta uma vez (o sistema ignora as repetições)."),
            ("Aba Vendedor x Etapa", "Para cada vendedor, etapa e setor: quantidade e valor em aberto, quantidade e valor indicados na previsão, e o valor em aberto NÃO indicado (diferença)."),
            ("Como perguntar", "Ex.: \"Quanto o André tem em Closing e quanto indicou na previsão?\" → some Valor (R$) e Valor indicado (R$) da aba Oportunidades filtrando Vendedor e Etapa, ou leia a aba Vendedor x Etapa."),
        };
        lm.Cell(1, 1).SetValue("Campo"); lm.Cell(1, 2).SetValue("Descrição");
        for (var i = 0; i < textos.Length; i++) { lm.Cell(i + 2, 1).SetValue(textos[i].Item1); lm.Cell(i + 2, 2).SetValue(textos[i].Item2); }
        lm.Row(1).Style.Font.Bold = true;
        lm.Column(1).Width = 24; lm.Column(2).Width = 120; lm.Column(2).Style.Alignment.WrapText = true;

        // ---- Oportunidades ----
        var ws = wb.Worksheets.Add("Oportunidades");
        var cab = new[]
        {
            "Setor", "Número da proposta", "Oportunidade", "Cliente", "Vendedor", "Etapa", "Valor (R$)",
            "Indicada na previsão", "Valor indicado (R$)", "Data prevista de fechamento", "Mês previsto",
            "% de ganho", "% de sair no mês", "Mercado", "País", "Produto", "Business Unit", "Atualizada em",
        };
        for (var c = 0; c < cab.Length; c++) ws.Cell(1, c + 1).SetValue(cab[c]);
        var r = 2;
        foreach (var l in linhas)
        {
            var o = l.O;
            var d = o.ExpectedDateValue;
            ws.Cell(r, 1).SetValue(l.Setor);
            ws.Cell(r, 2).SetValue(o.ProposalNumber);
            ws.Cell(r, 3).SetValue(o.Name);
            ws.Cell(r, 4).SetValue(_cat.CustomerName(o.CustomerId));
            ws.Cell(r, 5).SetValue(l.Vendedor);
            ws.Cell(r, 6).SetValue(l.Etapa);
            ws.Cell(r, 7).SetValue(l.Valor);
            ws.Cell(r, 8).SetValue(l.Indicada ? "Sim" : "Não");
            ws.Cell(r, 9).SetValue(l.Indicada ? l.Valor : 0d);
            if (d is DateTime dd) { ws.Cell(r, 10).SetValue(dd); ws.Cell(r, 11).SetValue(dd.ToString("yyyy-MM")); }
            ws.Cell(r, 12).SetValue(o.WinProbabilityValue / 100);
            ws.Cell(r, 13).SetValue(o.CloseProbabilityValue / 100);
            ws.Cell(r, 14).SetValue(_cat.MarketName(o.MarketId));
            ws.Cell(r, 15).SetValue(_cat.CountryName(o.CountryId));
            ws.Cell(r, 16).SetValue(_cat.ProductName(o.ProductId));
            ws.Cell(r, 17).SetValue(_cat.BuName(o.PvBusinessUnitId));
            if (o.UpdatedAtValue is DateTime up) ws.Cell(r, 18).SetValue(up);
            r++;
        }
        if (linhas.Count > 0)
        {
            ws.Range(1, 1, r - 1, cab.Length).CreateTable("Oportunidades");
            ws.Range(2, 7, r - 1, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(2, 9, r - 1, 9).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(2, 10, r - 1, 10).Style.NumberFormat.Format = "dd/mm/yyyy";
            ws.Range(2, 12, r - 1, 13).Style.NumberFormat.Format = "0%";
            ws.Range(2, 18, r - 1, 18).Style.NumberFormat.Format = "dd/mm/yyyy hh:mm";
        }
        ws.Columns().AdjustToContents(1, Math.Min(r, 300), 8, 60);
        ws.SheetView.FreezeRows(1);

        // ---- Vendedor x Etapa ----
        var rs = wb.Worksheets.Add("Vendedor x Etapa");
        var cab2 = new[]
        {
            "Vendedor", "Etapa", "Setor", "Oportunidades em aberto", "Valor em aberto (R$)",
            "Oportunidades indicadas", "Valor indicado (R$)", "Valor em aberto não indicado (R$)", "% do valor indicado",
        };
        for (var c = 0; c < cab2.Length; c++) rs.Cell(1, c + 1).SetValue(cab2[c]);
        var grupos = linhas.GroupBy(l => (l.Vendedor, l.Etapa, l.Setor))
            .OrderBy(g => g.Key.Vendedor, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Key.Etapa).ThenBy(g => g.Key.Setor);
        var r2 = 2;
        foreach (var g in grupos)
        {
            double aberto = g.Sum(x => x.Valor), indicado = g.Where(x => x.Indicada).Sum(x => x.Valor);
            rs.Cell(r2, 1).SetValue(g.Key.Vendedor);
            rs.Cell(r2, 2).SetValue(g.Key.Etapa);
            rs.Cell(r2, 3).SetValue(g.Key.Setor);
            rs.Cell(r2, 4).SetValue(g.Count());
            rs.Cell(r2, 5).SetValue(aberto);
            rs.Cell(r2, 6).SetValue(g.Count(x => x.Indicada));
            rs.Cell(r2, 7).SetValue(indicado);
            rs.Cell(r2, 8).SetValue(aberto - indicado);
            if (aberto > 0) rs.Cell(r2, 9).SetValue(indicado / aberto);
            r2++;
        }
        if (r2 > 2)
        {
            rs.Range(1, 1, r2 - 1, cab2.Length).CreateTable("VendedorEtapa");
            rs.Range(2, 5, r2 - 1, 5).Style.NumberFormat.Format = "#,##0.00";
            rs.Range(2, 7, r2 - 1, 8).Style.NumberFormat.Format = "#,##0.00";
            rs.Range(2, 9, r2 - 1, 9).Style.NumberFormat.Format = "0%";
        }
        rs.Columns().AdjustToContents(1, Math.Min(r2, 300), 8, 50);
        rs.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Grava a base na pasta configurada (sobrescreve). Sem pasta, ou pasta que não existe neste
    /// computador, não faz nada. Nunca lança: o resultado fica em UltimaMensagem.
    /// </summary>
    public bool Gravar()
    {
        lock (_lock)
        {
            var pasta = Pasta;
            if (pasta == "") { UltimaMensagem = "Pasta do Copilot não configurada (appsettings: Copilot:Pasta)."; return false; }
            if (!Directory.Exists(pasta)) { UltimaMensagem = $"A pasta {pasta} não existe neste computador."; return false; }
            try
            {
                var bytes = Gerar();
                var destino = Path.Combine(pasta, NomeArquivo);
                var tmp = destino + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, destino, overwrite: true);   // troca de uma vez: o OneDrive nunca sobe um arquivo pela metade
                UltimaGravacao = DateTime.Now;
                UltimaMensagem = $"Base do Copilot gravada em {destino} às {UltimaGravacao:HH:mm}.";
                return true;
            }
            catch (Exception ex)
            {
                UltimaMensagem = "Não foi possível gravar a base do Copilot: " + ex.Message;
                return false;
            }
        }
    }
}
