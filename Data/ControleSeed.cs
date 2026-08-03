namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Valores iniciais do Controle Orçamentário (planilha REPORT HSA), em R$.
// Os números da planilha estavam em milhares; aqui já entram × 1.000.
// Categorias: NB, AFM, SV, LTSA (realizado por categoria), INTER (intercompany)
// e CONS (orçamento consolidado por mês). Forecast começa vazio (a informar).
// ---------------------------------------------------------------------------
public static class ControleSeed
{
    public const string Consolidado = "CONS";
    public static readonly string[] Categorias = { "NB", "AFM", "SV", "LTSA", "INTER" };

    // Rótulos amigáveis das categorias.
    public static string Label(string c) => c switch
    {
        "NB" => "NB",
        "AFM" => "AFM",
        "SV" => "SV",
        "LTSA" => "LTSA",
        "INTER" => "Intercompany",
        "CONS" => "Consolidado",
        _ => c,
    };

    // Realizado por categoria (milhares) — Jan..Dez.
    private static readonly Dictionary<string, double[]> RealizadoMil = new()
    {
        ["NB"]    = new double[] { 2619, 5464, 5267, 7566, 5140, 37354, 0, 0, 0, 0, 0, 0 },
        ["AFM"]   = new double[] { 27646, 22600, 15883, 34058, 34615, 5193, 0, 0, 0, 0, 0, 0 },
        ["SV"]    = new double[] { 1102, 2712, 762, 1576, 1925, 1462, 0, 0, 0, 0, 0, 0 },
        ["LTSA"]  = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        ["INTER"] = new double[] { 372, 246, 1302, 17, 682, 23, 0, 0, 0, 0, 0, 0 },
    };

    // Orçamento consolidado (milhares) — Jan..Dez.
    private static readonly double[] OrcamentoMil =
        { 19909, 26143, 36008, 35047, 45613, 34055, 44602, 41376, 64145, 58015, 57418, 87670 };

    // Gera as linhas iniciais para um ano (valores em R$).
    public static IEnumerable<(string Category, int Month, double Budget, double Realizado, double Forecast)> Entries()
    {
        for (var m = 1; m <= 12; m++)
        {
            foreach (var cat in Categorias)
                yield return (cat, m, 0, RealizadoMil[cat][m - 1] * 1000, 0);
            yield return (Consolidado, m, OrcamentoMil[m - 1] * 1000, 0, 0);
        }
    }
}
