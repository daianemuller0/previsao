using System.Globalization;
using ClosedXML.Excel;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Planilha Excel das oportunidades: UMA definição para a guia Oportunidades e
// para a Visão Executiva (recorte e indicadas). Sai completa: todas as colunas
// da tabela, na ordem padrão (Customer e PlantName lado a lado), e depois os
// demais campos da oportunidade que não cabem na tela.
// ---------------------------------------------------------------------------
public static class OpportunityExcel
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static byte[] Gerar(IEnumerable<Opportunity> lista, Catalog cat, string aba, DateTime hoje)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(string.IsNullOrWhiteSpace(aba) ? "Oportunidades" : aba);

        // (rótulo, valor) — a ordem aqui é a ordem das colunas na planilha.
        var colunas = new (string Rotulo, Func<Opportunity, XLCellValue> Valor)[]
        {
            // ---- as colunas da tabela, na ordem padrão da tela ----
            ("Setor",                 o => o.SetorEfetivo),
            ("Quarter",               o => o.ExpectedDateValue is { } d ? $"Q{Fmt.QuarterOf(d.Month)} {d.Year}" : ""),
            ("Date",                  o => o.ExpectedDateValue is { } d ? d.ToString("dd/MM/yyyy") : ""),
            ("País",                  o => cat.CountryName(o.CountryId)),
            ("Market Variável",       o => cat.SubMarketName(o.SubMarketId)),
            ("Market",                o => cat.MarketName(o.MarketId)),
            ("Product",               o => cat.ProductName(o.ProductId)),
            ("Tipo de Equipamento",   o => cat.EquipmentName(o.EquipmentTypeId)),
            ("Key Account",           o => OpportunityImporter.CanonicalVendedor(cat.KamName(o.KamId))),
            ("Customer",              o => cat.CustomerName(o.CustomerId)),
            ("PlantName",             o => o.EndUserSite),
            ("Proposta",              o => o.ProposalNumber),
            ("Net Value (R$)",        o => o.AmountBrl),
            ("PM %",                  o => o.GmPercentValue),
            ("% de Ganho",            o => o.WinProbabilityValue),
            ("% de Sair no Mês",      o => o.CloseProbabilityValue),
            ("Chance Conversão (R$)", o => ForecastCalc.ConversionBrl(o)),
            ("NB/AFM",                o => o.CategoriaNbAfm),
            ("Serviço previsto",      o => o.ServicoPrevisto),
            ("Market onestream",      o => o.MarketOnestream),
            ("Unidade de Venda",      o => cat.BuCode(o.PvBusinessUnitId)),
            ("BU Intercompany",       o => cat.BuCode(o.IntercompanyBu)),
            ("Observação",            o => o.Notes),
            ("PV",                    o => o.PvNumber),
            ("RAMP",                  o => o.Ramp),
            ("VALOR USD",             o => o.AmountUsd),
            ("Taxa",                  o => o.ExchangeRateValue),
            ("Coluna1",               o => o.Coluna1),
            ("OTP",                   o => o.Otp),
            ("TOP 10",                o => o.Top10),
            ("KYC",                   o => o.Kyc),
            ("BU de origem",          o => o.BuOrigem),
            ("CRM de origem",         o => o.CrmOrigem),
            ("Moeda de origem",       o => o.MoedaOrigem),
            ("Valor de origem",       o => string.IsNullOrWhiteSpace(o.ValorOrigem) ? (XLCellValue)Blank.Value : o.ValorOrigemValue),
            ("Refname",               o => o.Refname),
            ("Refno",                 o => o.Refno),
            ("Productcompany",        o => o.Productcompany),
            ("Articleno",             o => o.Articleno),
            ("Contractno",            o => o.Contractno),
            ("Serialno",              o => o.Serialno),
            ("Applicationtype",       o => o.Applicationtype),
            ("Designation",           o => o.Designation),
            ("Clientrefno",           o => o.Clientrefno),
            // ---- o restante da oportunidade ----
            ("Oportunidade",          o => o.Name),
            ("Indicada na previsão",  o => o.IndicadaValue ? "Sim" : "Não"),
            ("Perdida",               o => o.PerdidaValue ? "Sim" : "Não"),
            ("Categoria previsão",    o => ForecastCategories.Label(o.Category)),
            ("Risco",                 o => RiskLevels.Label(ForecastCalc.Level(o, hoje))),
            ("Stage",                 o => o.Stage),
            ("Moeda",                 o => o.CurrencyCode),
            ("Valor original",        o => o.AmountOriginalValue),
            ("Amount (CRM)",          o => o.AmountRaw),
            ("Previsão do gestor %",  o => o.ManagerProbabilityValue is { } m ? (XLCellValue)m : Blank.Value),
            ("Commercial Segment",    o => o.CommercialSegment),
            ("Process",               o => o.Process),
            ("Brand",                 o => o.Brand),
            ("Chance",                o => o.Chance),
            ("Customer Ref#",         o => o.CustomerRef),
            ("Is Inter Company",      o => o.IsInterCompany),
            ("Description",           o => o.Description),
            ("Status Description",    o => o.StatusDescription),
            ("Justificativa",         o => o.Justification),
            ("Próxima ação",          o => o.NextAction),
            ("Data da próxima ação",  o => Fmt.Date(o.NextActionDate)),
            ("Riscos",                o => o.Risks),
            ("Postergações",          o => o.PostponeCountValue),
            ("Criada em",             o => Fmt.Date(o.CreatedAt)),
            ("Atualizada em",         o => Fmt.Date(o.UpdatedAt)),
            ("Atualizada por",        o => o.UpdatedBy),
            ("Id",                    o => o.Id),
        };

        for (var i = 0; i < colunas.Length; i++) ws.Cell(1, i + 1).Value = colunas[i].Rotulo;
        ws.Row(1).Style.Font.Bold = true;

        var r = 2;
        foreach (var o in lista)
        {
            for (var i = 0; i < colunas.Length; i++)
            {
                try { ws.Cell(r, i + 1).Value = colunas[i].Valor(o); }
                catch { ws.Cell(r, i + 1).Value = ""; }
            }
            r++;
        }

        ws.SheetView.FreezeRows(1);
        ws.RangeUsed()?.SetAutoFilter();
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
