namespace HowdenSalesForecast.Models;

// Perfis de acesso (Seção 17). O perfil escolhido no login vira o papel (role)
// e adapta a navegação — demonstrando os três níveis de experiência.
public static class Roles
{
    public const string Admin = "admin";
    public const string Diretoria = "diretoria";
    public const string Gestor = "gestor";
    public const string Kam = "kam";
    public const string Financeiro = "financeiro";
    public const string Viewer = "viewer";

    public static readonly (string Role, string Label)[] All =
    {
        (Admin, "Administrador"),
        (Diretoria, "Diretoria"),
        (Gestor, "Gestor comercial"),
        (Kam, "KAM / Vendedor"),
        (Financeiro, "Financeiro"),
        (Viewer, "Visualizador executivo"),
    };

    public static string Normalize(string? perfil)
    {
        var p = (perfil ?? "").Trim().ToLowerInvariant();
        return All.Any(x => x.Role == p) ? p : Admin;
    }

    public static string DisplayName(string role) =>
        All.FirstOrDefault(x => x.Role == role).Label ?? "Equipe Howden";
}
