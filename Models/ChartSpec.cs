using System.Globalization;

namespace HowdenSalesForecast.Models;

// Configuração de um gráfico personalizado do "estúdio" da Visão Executiva
// (estilo Power BI: escolhe-se o tipo, a dimensão e a medida). Persistido como
// texto (VARCHAR) no Parquet, no padrão do projeto.
public class ChartSpec
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "bar";        // bar | hbar | line | donut | kpi | table
    public string Dimension { get; set; } = "market"; // market | kam | product | country | bu | category | customer | month | quarter
    public string Measure { get; set; } = "valor";    // valor | ponderado | margem | contagem
    public string TopN { get; set; } = "8";           // 0 = todos
    public string Sort { get; set; } = "0";           // ordem de exibição
    public string UpdatedAt { get; set; } = "";

    public int TopNV => int.TryParse(TopN, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 8;
    public int SortV => int.TryParse(Sort, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public ChartSpec Clone() => new()
    {
        Id = Id, Title = Title, Type = Type, Dimension = Dimension,
        Measure = Measure, TopN = TopN, Sort = Sort, UpdatedAt = UpdatedAt,
    };
}
