namespace HowdenSalesForecast.Models;

// Item de uma lista de preenchimento (dado-mestre editável).
// Persistido como texto (VARCHAR) no Parquet, no mesmo padrão das oportunidades.
public class ListItem
{
    public string Id { get; set; } = "";
    public string ListKey { get; set; } = "";   // ex.: "pais", "market", "product"
    public string Value { get; set; } = "";      // valor exibido/armazenado
    public string Sort { get; set; } = "0";       // ordem dentro da lista

    public int SortValue => int.TryParse(Sort, out var v) ? v : 0;
}
