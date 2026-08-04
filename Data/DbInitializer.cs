namespace HowdenSalesForecast.Data;

// Popula os dados demonstrativos na primeira execução (só se a pasta estiver vazia).
// Cada oportunidade é gravada como um Parquet, igual a qualquer edição posterior.
public static class DbInitializer
{
    public static void Initialize(ParquetStore store, Catalog catalog)
    {
        if (store.IsEmpty("opportunities"))
        {
            // Gravação em lote: um único Parquet (evita 40 escritas na rede).
            new OpportunityRepository(store).SaveMany(DemoSeed.Opportunities(catalog));
        }
    }
}
