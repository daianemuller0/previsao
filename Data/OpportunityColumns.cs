namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Colunas da tabela de oportunidades: definição única (chave, rótulo e
// alinhamento) usada pela guia Oportunidades e pela Visão Executiva, mais a
// leitura/gravação da preferência de cada usuário (ordem + colunas ocultas).
//
// Formato da preferência: "chave1,chave2,-chave3" — o "-" marca a coluna como
// oculta. Chaves desconhecidas são ignoradas e chaves novas (versões futuras)
// entram no fim, então a preferência nunca "quebra" ao adicionarmos colunas.
// ---------------------------------------------------------------------------
public static class OpportunityColumns
{
    public sealed record ColDef(string Key, string Label, string Align = "");

    // Ordem padrão da tabela.
    public static readonly ColDef[] All =
    {
        new("setor",       "Setor",              "center"),
        new("quarter",     "Quarter",            "nowrap"),
        new("date",        "Date",               "nowrap"),
        new("pais",        "País"),
        new("marketvar",   "Market Variável"),
        new("market",      "Market"),
        new("product",     "Product"),
        new("equip",       "Tipo de Equipamento"),
        new("kam",         "Key Account"),
        new("customer",    "Customer"),
        new("plantname",   "PlantName"),
        new("proposta",    "Proposta"),
        new("netvalue",    "Net Value",          "right"),
        new("pm",          "PM %",               "right"),
        new("ganho",       "% de Ganho",         "right"),
        new("sairmes",     "% de Sair no Mês",   "right"),
        new("conversao",   "Chance Conversão",   "right"),
        new("nbafm",       "NB/AFM",             "center"),
        new("servico",     "Serviço previsto"),
        new("onestream",   "Market onestream"),
        new("unidade",     "Unidade de Venda"),
        new("buinter",     "BU Intercompany"),
        new("obs",         "Observação"),
        new("pv",          "PV"),
        new("ramp",        "RAMP"),
        new("usd",         "VALOR USD",          "right"),
        new("taxa",        "Taxa",               "right"),
        new("coluna1",     "Coluna1"),
        new("otp",         "OTP",                "center"),
        new("top10",       "TOP 10",             "center"),
        new("kyc",         "KYC",                "center"),
    };

    // Colunas ocultas por padrão (o usuário pode reexibir).
    private static readonly HashSet<string> OcultasPadrao = new(StringComparer.OrdinalIgnoreCase) { "equip" };

    public sealed record ColState(ColDef Def, bool Visible);

    /// <summary>Preferência padrão: ordem de All, com as ocultas padrão desmarcadas.</summary>
    public static List<ColState> Default() =>
        All.Select(d => new ColState(d, !OcultasPadrao.Contains(d.Key))).ToList();

    /// <summary>Lê a preferência gravada; entradas desconhecidas somem e colunas
    /// novas entram no fim (visíveis), preservando o que o usuário já ajustou.</summary>
    public static List<ColState> Parse(string? pref)
    {
        if (string.IsNullOrWhiteSpace(pref)) return Default();

        var byKey = All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        var result = new List<ColState>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in pref.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var oculta = raw.StartsWith('-');
            var key = oculta ? raw[1..] : raw;
            if (!byKey.TryGetValue(key, out var def) || !vistos.Add(def.Key)) continue;
            result.Add(new ColState(def, !oculta));
        }

        // Colunas que ainda não estavam na preferência (ex.: recém-criadas).
        foreach (var d in All)
            if (!vistos.Contains(d.Key))
                result.Add(new ColState(d, !OcultasPadrao.Contains(d.Key)));

        return result;
    }

    /// <summary>Serializa a preferência para gravação.</summary>
    public static string Serialize(IEnumerable<ColState> cols) =>
        string.Join(",", cols.Select(c => (c.Visible ? "" : "-") + c.Def.Key));
}
