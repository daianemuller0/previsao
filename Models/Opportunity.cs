using System.Globalization;

namespace HowdenSalesForecast.Models;

// ---------------------------------------------------------------------------
// Oportunidade comercial — entidade central do forecast.
// Segue o padrão Parquet do projeto: campos persistidos como texto (VARCHAR);
// os acessores tipados fazem a conversão. Referências a dados-mestre por Id.
// ---------------------------------------------------------------------------
public class Opportunity
{
    // Identificação
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";            // Nome da oportunidade
    public string ProposalNumber { get; set; } = "";  // Proposta
    public string PvNumber { get; set; } = "";        // PV

    // Classificação (foreign keys para o catálogo)
    public string CountryId { get; set; } = "";
    public string MarketId { get; set; } = "";
    public string SubMarketId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string EquipmentTypeId { get; set; } = "";
    public string KamId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string PlantId { get; set; } = "";
    public string CommercialCategory { get; set; } = "NB"; // NB/RT/AFM/SV
    public string IntercompanyBu { get; set; } = "";        // BU Intercompany
    public string PvBusinessUnitId { get; set; } = "";      // BU do PV / Unidade de Venda
    public string ServicoPrevisto { get; set; } = "";       // Serviço previsto (SIM/NÃO)
    public string MarketOnestream { get; set; } = "";       // Market onestream
    public string Ramp { get; set; } = "";                  // RAMP
    public string Coluna1 { get; set; } = "";               // Coluna1 (livre)
    public string Otp { get; set; } = "";                   // OTP (marcador Sim/Não · controle externo)
    public string Top10 { get; set; } = "";                 // TOP 10 (marcador Sim/Não · controle externo)

    // Valores financeiros
    public string CurrencyCode { get; set; } = "BRL";
    public string AmountOriginal { get; set; } = "0"; // valor na moeda de origem
    public string ExchangeRate { get; set; } = "0";   // cotação BRL por USD (ex.: 5,40)
    public string GmPercent { get; set; } = "0";      // margem bruta %

    // Forecast
    public string ForecastCategory { get; set; } = "Pipeline";
    public string PipelineStageId { get; set; } = "";
    public string ExpectedDate { get; set; } = "";        // ISO yyyy-MM-dd
    public string WinProbability { get; set; } = "0";     // % de ganho
    public string CloseInPeriodProbability { get; set; } = "0"; // % de sair no mês
    public string ManagerProbability { get; set; } = "";  // previsão do gestor (%)
    public string Justification { get; set; } = "";

    // Operação
    public string NextAction { get; set; } = "";
    public string NextActionDate { get; set; } = "";
    public string Risks { get; set; } = "";
    public string Notes { get; set; } = "";               // Observações
    public string PostponeCount { get; set; } = "0";      // nº de postergações

    // Auditoria
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public string ValueChangedAt { get; set; } = "";
    public string DateChangedAt { get; set; } = "";

    // ---- acessores tipados -------------------------------------------------

    private static double Num(string s)
    {
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v);
        return v;
    }

    public double AmountOriginalValue => Num(AmountOriginal);
    public double ExchangeRateValue => Num(ExchangeRate);
    public double GmPercentValue => Num(GmPercent);
    public double WinProbabilityValue => Num(WinProbability);
    public double CloseProbabilityValue => Num(CloseInPeriodProbability);
    public double? ManagerProbabilityValue =>
        string.IsNullOrWhiteSpace(ManagerProbability) ? null : Num(ManagerProbability);
    public int PostponeCountValue => (int)Num(PostponeCount);

    public ForecastCategory Category => ForecastCategories.Parse(ForecastCategory);

    private bool IsBrl => CurrencyCode.Equals("BRL", StringComparison.OrdinalIgnoreCase);
    private bool IsUsd => CurrencyCode.Equals("USD", StringComparison.OrdinalIgnoreCase);

    // Valor em BRL. Cotação = BRL por USD (Valor USD = Valor BRL ÷ taxa).
    public double AmountBrl => IsBrl
        ? AmountOriginalValue
        : AmountOriginalValue * (ExchangeRateValue > 0 ? ExchangeRateValue : 0);

    // Valor em USD — moeda de consolidação executiva.
    public double AmountUsd => IsUsd
        ? AmountOriginalValue
        : (ExchangeRateValue > 0 ? AmountOriginalValue / ExchangeRateValue : 0);

    public DateTime? ExpectedDateValue =>
        DateTime.TryParse(ExpectedDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public DateTime? UpdatedAtValue =>
        DateTime.TryParse(UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
