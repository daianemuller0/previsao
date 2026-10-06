using System.Globalization;
using System.Security.Claims;
using System.Text;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Recorte de acesso do usuário logado: papel, se enxerga tudo e a lista de
// vendedores permitidos. Lido dos claims do cookie. As páginas usam para
// filtrar os dados (Oportunidades / Visão Executiva / Follow-up) e para
// bloquear abas fora do papel.
// ---------------------------------------------------------------------------
public sealed class AccessScope
{
    public string Login { get; init; } = "";
    public string Nome { get; init; } = "";
    public string Role { get; init; } = AccessRoles.Vendedor;
    public bool All { get; init; }                       // vê a base inteira
    public string Setor { get; init; } = "";             // "AFM" = só Aftermarket; vazio = todos os setores
    private readonly HashSet<string> _names = new();     // vendedores permitidos (normalizados)

    public AccessScope() { }
    private AccessScope(IEnumerable<string> names) { foreach (var n in names) _names.Add(Norm(n)); }

    public const string VendedoresClaim = "vendedores";

    // Vê a carteira de um vendedor? (compara pelo nome canônico, normalizado)
    public bool CanSeeVendedor(string canonicalVendedor)
    {
        if (All) return true;
        var target = Norm(canonicalVendedor);
        if (target.Length == 0) return false;
        if (_names.Contains(target)) return true;
        // Entradas de um só nome (ex.: "Douglas") casam pelo primeiro nome.
        var first = target.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        foreach (var allowed in _names)
            if (!allowed.Contains(' ') && allowed == first) return true;
        return false;
    }

    public bool CanTab(string tabKey) => AccessRoles.CanTab(Role, tabKey);

    // Vê este setor? (coluna Setor da oportunidade / origem da planilha)
    public bool CanSeeSetor(string? setor) =>
        Setor == "" || string.Equals((setor ?? "").Trim(), Setor, StringComparison.OrdinalIgnoreCase);

    // Oportunidade dentro do recorte: carteira do vendedor E setor.
    public bool CanSee(Opportunity o, string canonicalVendedor) =>
        CanSeeVendedor(canonicalVendedor) && CanSeeSetor(o.SetorEfetivo);

    public IReadOnlyCollection<string> Names => _names;

    // Resolve o recorte a partir do principal (claims do cookie).
    public static AccessScope Resolve(ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true })
            return new AccessScope { Role = AccessRoles.Vendedor };

        var role = AccessRoles.Normalize(user.FindFirst(ClaimTypes.Role)?.Value);
        var names = (user.FindFirst(VendedoresClaim)?.Value ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new AccessScope(names)
        {
            Login = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "",
            Nome = user.Identity?.Name ?? "",
            Role = role,
            All = AccessRoles.SeesAll(role),
            Setor = AccessRoles.SetorFixo(role),
        };
    }

    // Normaliza nome: minúsculo, sem acento, espaços colapsados.
    public static string Norm(string? s)
    {
        var t = (s ?? "").Trim().ToLowerInvariant()
            .Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
