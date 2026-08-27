namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Colunas da tabela de oportunidades: a definição única (chave, rótulo e
// alinhamento) usada pela guia Oportunidades e pela Visão Executiva. A mecânica
// de preferência por usuário (ordem + ocultas) mora em TableColumns, que serve
// a todas as tabelas do sistema.
// ---------------------------------------------------------------------------
public static class OpportunityColumns
{
    /// <summary>Chave desta tabela na preferência do usuário.</summary>
    public const string Tabela = "opps";

    // Ordem padrão da tabela.
    public static readonly TableColumns.ColDef[] All =
    {
        new TableColumns.ColDef("setor",       "Setor",              "center"),
        new TableColumns.ColDef("quarter",     "Quarter",            "nowrap"),
        new TableColumns.ColDef("date",        "Date",               "nowrap"),
        new TableColumns.ColDef("pais",        "País"),
        new TableColumns.ColDef("marketvar",   "Market Variável"),
        new TableColumns.ColDef("market",      "Market"),
        new TableColumns.ColDef("product",     "Product"),
        new TableColumns.ColDef("equip",       "Tipo de Equipamento"),
        new TableColumns.ColDef("kam",         "Key Account"),
        new TableColumns.ColDef("customer",    "Customer"),
        new TableColumns.ColDef("plantname",   "PlantName"),
        new TableColumns.ColDef("proposta",    "Proposta"),
        new TableColumns.ColDef("netvalue",    "Net Value",          "right"),
        new TableColumns.ColDef("pm",          "PM %",               "right"),
        new TableColumns.ColDef("ganho",       "% de Ganho",         "right"),
        new TableColumns.ColDef("sairmes",     "% de Sair no Mês",   "right"),
        new TableColumns.ColDef("conversao",   "Chance Conversão",   "right"),
        new TableColumns.ColDef("nbafm",       "NB/AFM",             "center"),
        new TableColumns.ColDef("servico",     "Serviço previsto"),
        new TableColumns.ColDef("onestream",   "Market onestream"),
        new TableColumns.ColDef("unidade",     "Unidade de Venda"),
        new TableColumns.ColDef("buinter",     "BU Intercompany"),
        new TableColumns.ColDef("obs",         "Observação"),
        new TableColumns.ColDef("pv",          "PV"),
        new TableColumns.ColDef("ramp",        "RAMP"),
        new TableColumns.ColDef("usd",         "VALOR USD",          "right"),
        new TableColumns.ColDef("taxa",        "Taxa",               "right"),
        new TableColumns.ColDef("coluna1",     "Coluna1"),
        new TableColumns.ColDef("otp",         "OTP",                "center"),
        new TableColumns.ColDef("top10",       "TOP 10",             "center"),
        new TableColumns.ColDef("kyc",         "KYC",                "center"),
    };

    // Colunas ocultas por padrão (o usuário pode reexibir).
    private static readonly HashSet<string> OcultasPadrao = new(StringComparer.OrdinalIgnoreCase) { "equip" };

    public static List<TableColumns.ColState> Default() => TableColumns.Default(All, OcultasPadrao);
    public static List<TableColumns.ColState> Parse(string? pref) => TableColumns.Parse(pref, All, OcultasPadrao);
}
