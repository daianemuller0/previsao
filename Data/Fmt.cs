using System.Globalization;

namespace HowdenSalesForecast.Data;

// Formatação corporativa consistente (moeda, percentual, datas, período).
public static class Fmt
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    public static DateTime Today => DateTime.Today;

    public static string Usd(double v) => "US$ " + v.ToString("#,##0", Br);
    public static string Brl(double v) => "R$ " + v.ToString("#,##0", Br);

    // Compacto para cards executivos: US$ 1,2M / US$ 940k.
    public static string UsdShort(double v)
    {
        var a = Math.Abs(v);
        if (a >= 1_000_000) return "US$ " + (v / 1_000_000).ToString("0.0", Br) + "M";
        if (a >= 1_000) return "US$ " + Math.Round(v / 1_000).ToString("#,##0", Br) + "k";
        return "US$ " + v.ToString("#,##0", Br);
    }

    public static string Pct(double v, int dec = 0) => v.ToString("0." + new string('0', dec), Br) + "%";

    public static string Signed(double v)
    {
        var s = v >= 0 ? "+" : "";
        return s + v.ToString("0.0", Br) + "%";
    }

    public static string Date(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM/yyyy") : iso;
    }

    public static string MonthName(int m) => m is >= 1 and <= 12
        ? Br.DateTimeFormat.GetAbbreviatedMonthName(m).ToUpperInvariant().TrimEnd('.')
        : "—";

    public static string Quarter(DateTime d) => $"Q{(d.Month - 1) / 3 + 1} {d.Year}";

    public static int QuarterOf(int month) => (month - 1) / 3 + 1;
}
