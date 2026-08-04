namespace HowdenSalesForecast.Data;

// Popula os dados demonstrativos na primeira execução (só se a pasta estiver vazia).
// Cada oportunidade é gravada como um Parquet, igual a qualquer edição posterior.
public static class DbInitializer
{
    public static void Initialize(ParquetStore store, Catalog catalog)
    {
        // Dados demonstrativos DESATIVADOS: a base real é importada pela guia
        // Oportunidades. Se quiser reativar o seed demo, descomente abaixo.
        // if (store.IsEmpty("opportunities"))
        //     new OpportunityRepository(store).SaveMany(DemoSeed.Opportunities(catalog));
    }
}
