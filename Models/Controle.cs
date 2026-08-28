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
    public string Poc { get; set; } = "";            // "Sim" / "Não"
    public string Tipo { get; set; } = "";           // Revenda / Manufaturado
    public string Classificacao { get; set; } = "";  // como ler o valor (ver Classificacoes)
    public string Status { get; set; } = "";         // situação da oferta (lista editável)
    public string Moeda { get; set; } = "BRL";       // moeda de origem da oferta
    public string ValorOriginal { get; set; } = "";  // valor na moeda de origem
    public string Observacao { get; set; } = "";     // observação livre
    public string Atencao { get; set; } = "";        // nota de ATENÇÃO p/ o Controle
    public string OppId { get; set; } = "";          // oportunidade de origem (p/ desfazer a venda indicada)
    public string UpdatedBy { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public double ValorLiquidoV =>
        double.TryParse(ValorLiquido, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public double ValorOriginalV =>
        double.TryParse(ValorOriginal, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Semana (número) do registro; 0 quando não informada.</summary>
    public int SemanaV => int.TryParse(Semana, out var v) ? v : 0;

    /// <summary>Tem nota de atenção pendente para o Controle?</summary>
    public bool TemAtencao => !string.IsNullOrWhiteSpace(Atencao);

    // ---- tipo da oferta ----------------------------------------------------
    public static readonly string[] Tipos = { "Revenda", "Manufaturado" };

    // ---- como o valor deve ser lido ---------------------------------------
    // A classificação não muda o número: muda a confiança que se pode ter nele.
    // Por isso ela vira COR na tabela, e não mais uma coluna de texto para
    // alguém ter de cruzar com o valor ao lado.
    public const string ClassEstimado = "Estimado";
    public const string ClassConsolidado = "Consolidado";
    public const string ClassReportado = "Reportado pela Bianca";
    public static readonly string[] Classificacoes = { ClassEstimado, ClassConsolidado, ClassReportado };

    /// <summary>Classe CSS do valor conforme a classificação: estimado sai em
    /// vermelho e negrito, consolidado em preto, reportado com fundo rosa.</summary>
    public string ClasseValor => Classificacao switch
    {
        ClassEstimado => "val-estimado",
        ClassConsolidado => "val-consolidado",
        ClassReportado => "val-reportado",
        _ => "",
    };
}

// Taxa de conversão de moeda usada na importação do Aftermarket.
// Rate = quantos R$ por 1 unidade da moeda (ex.: USD 5,3726 = R$ por 1 dólar).
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

// ---------------------------------------------------------------------------
// Cotação de UM DIA de UMA moeda (R$ por 1 unidade).
//
// O fechamento do mês não é um campo à parte: é a última cotação daquele mês.
// Guardar o diário e derivar o fechamento evita a pergunta "quem digitou este
// número e de quando ele é?" — a resposta está na própria série.
//
// Id determinístico ("cb-USD-2026-08-27"): a consolidação por id do Parquet
// vira upsert, então buscar o mesmo dia duas vezes não duplica nada.
// ---------------------------------------------------------------------------
public class CambioDia
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";       // USD, EUR, GBP …
    public string Data { get; set; } = "";        // ISO yyyy-MM-dd
    public string Rate { get; set; } = "0";       // R$ por 1 unidade
    public string Fonte { get; set; } = "";       // "Banco Central" / "Manual"
    public string UpdatedBy { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public double RateV =>
        double.TryParse(Rate, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public DateTime? DataValue =>
        DateTime.TryParse(Data, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    public bool HasRate => RateV > 0;
    public bool Manual => string.Equals(Fonte, "Manual", StringComparison.OrdinalIgnoreCase);

    public static string IdDe(string code, DateTime dia) =>
        $"cb-{(code ?? "").Trim().ToUpperInvariant()}-{dia:yyyy-MM-dd}";
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
