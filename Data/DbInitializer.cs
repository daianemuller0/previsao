namespace HowdenSalesForecast.Data;

// Popula os dados demonstrativos na primeira execução (só se a pasta estiver vazia).
// Cada oportunidade é gravada como um Parquet, igual a qualquer edição posterior.
public static class DbInitializer
{
    public static void Initialize(ParquetStore store, Catalog catalog)
    {
        if (store.IsEmpty("opportunities"))
        {
            var repo = new OpportunityRepository(store);
            foreach (var o in DemoSeed.Opportunities(catalog))
                repo.Save(o);
        }
    }
}
