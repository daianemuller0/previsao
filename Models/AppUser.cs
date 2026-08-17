namespace HowdenSalesForecast.Models;

// ---------------------------------------------------------------------------
// Conta de acesso à plataforma (controle de acesso por vendedor).
// Cada vendedor tem login/senha e enxerga apenas a carteira permitida
// (a dele + as que o PDF de regras autoriza). Diretor/Controle veem tudo.
// ---------------------------------------------------------------------------
public class AppUser
{
    public string Login { get; set; } = "";       // chave única (minúsculo)
    public string Nome { get; set; } = "";         // nome de exibição
    public string Role { get; set; } = AccessRoles.Vendedor;
    public string Salt { get; set; } = "";         // sal do hash da senha
    public string Hash { get; set; } = "";         // SHA-256(sal + senha)
    public string Vendedores { get; set; } = "";   // canônicos separados por ';' (só role vendedor)
    public string Ativo { get; set; } = "Sim";
    public string UpdatedBy { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public bool IsActive => !string.Equals(Ativo, "Nao", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(Ativo, "Não", StringComparison.OrdinalIgnoreCase);

    // Diretor/Controle/Admin enxergam TODOS os vendedores.
    public bool ScopeAll => AccessRoles.SeesAll(Role);

    public List<string> VendedorList =>
        Vendedores.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

// Papéis de acesso e as abas que cada um enxerga.
public static class AccessRoles
{
    public const string Vendedor = "vendedor";
    public const string Controle = "controle";
    public const string Diretor = "diretor";
    public const string Admin = "admin";

    public static readonly (string Role, string Label)[] All =
    {
        (Vendedor, "Vendedor"),
        (Controle, "Controle"),
        (Diretor, "Diretor"),
        (Admin, "Administrador"),
    };

    public static string Label(string role) => All.FirstOrDefault(x => x.Role == role).Label ?? "Vendedor";

    public static string Normalize(string? role)
    {
        var r = (role ?? "").Trim().ToLowerInvariant();
        return All.Any(x => x.Role == r) ? r : Vendedor;
    }

    // Roles que enxergam a base inteira (sem recorte por vendedor).
    public static bool SeesAll(string role) => role is Controle or Diretor or Admin;

    // Abas (chaves iguais às do NavMenu) permitidas por papel.
    public static readonly Dictionary<string, string[]> Tabs = new()
    {
        [Vendedor] = new[] { "executivo", "oportunidades", "followup" },
        [Diretor]  = new[] { "executivo", "oportunidades", "followup" },
        [Controle] = new[] { "executivo", "oportunidades", "controle" },
        [Admin]    = new[] { "executivo", "oportunidades", "followup", "controle", "listas", "configuracoes", "identidade", "administracao" },
    };

    public static bool CanTab(string role, string tabKey) =>
        Tabs.TryGetValue(Normalize(role), out var keys) && keys.Contains(tabKey);
}
