using System.Globalization;

namespace HowdenSalesForecast.Models;

// Entrada do Controle Orçamentário: um valor por (ano, mês, categoria).
// Persistido como texto (VARCHAR) no Parquet, no padrão do projeto.
public class ControleEntry
{
    public string Id { get; set; } = "";
    public string Year { get; set; } = "";
    public string Month { get; set; } = "";
    public string Category { get; set; } = "";
    public string Budget { get; set; } = "0";       // orçamento (R$)
    public string Realizado { get; set; } = "0";     // realizado (R$)
    public string Forecast { get; set; } = "";       // projeção (R$) — vazio = não informado
    public string Note { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    private static double N(string s) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public int YearV => (int)N(Year);
    public int MonthV => (int)N(Month);
    public double BudgetV => N(Budget);
    public double RealizadoV => N(Realizado);
    public bool HasForecast => !string.IsNullOrWhiteSpace(Forecast);
    public double ForecastV => N(Forecast);
    public DateTime? UpdatedAtValue =>
        DateTime.TryParse(UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

// Registro de auditoria de alteração.
public class ControleHist
{
    public string Id { get; set; } = "";
    public string Ts { get; set; } = "";           // ISO
    public string User { get; set; } = "";
    public string Year { get; set; } = "";
    public string Month { get; set; } = "";
    public string Category { get; set; } = "";
    public string Field { get; set; } = "";        // Orçamento / Realizado / Forecast
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public string Justification { get; set; } = "";
    public string Origin { get; set; } = "Manual"; // Manual / Importação

    public DateTime? TsValue =>
        DateTime.TryParse(Ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

// Registro de oferta/pedido (tabela livre no Controle).
public class ControleRegistro
{
    public string Id { get; set; } = "";
    public string Oferta { get; set; } = "";
    public string Pedido { get; set; } = "";
    public string Cliente { get; set; } = "";
    public string Mercado { get; set; } = "";
    public string Kam { get; set; } = "";
    public string ValorLiquido { get; set; } = "";   // R$
    public string Semana { get; set; } = "";
    public string Mes { get; set; } = "";
    public string Poc { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public double ValorLiquidoV =>
        double.TryParse(ValorLiquido, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

// Taxa de conversão de moeda usada na importação do Aftermarket.
// Rate = quantos R$ por 1 unidade da moeda (ex.: USD 5,42 = R$ por 1 dólar).
// Cadastrada na aba Controle / Configurações. BRL não precisa de taxa.
public class CurrencyRate
{
    public string Code { get; set; } = "";       // USD, EUR, GBP …
    public string Rate { get; set; } = "0";       // R$ por 1 unidade
    public string UpdatedBy { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public double RateV =>
        double.TryParse(Rate, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public bool HasRate => RateV > 0;
    public DateTime? UpdatedAtValue =>
        DateTime.TryParse(UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

// Observação de fechamento.
public class ControleObs
{
    public string Id { get; set; } = "";
    public string Ts { get; set; } = "";
    public string Year { get; set; } = "";
    public string Month { get; set; } = "";
    public string Category { get; set; } = "";
    public string Author { get; set; } = "";
    public string Priority { get; set; } = "Média";   // Alta / Média / Baixa
    public string Text { get; set; } = "";

    public DateTime? TsValue =>
        DateTime.TryParse(Ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
