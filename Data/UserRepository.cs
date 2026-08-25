using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Contas de acesso (login/senha + papel + carteira permitida). Persistidas no
// ParquetStore (padrão do projeto). Senha guardada como SHA-256(sal + senha) —
// nunca em texto puro. Singleton.
// ---------------------------------------------------------------------------
public class UserRepository
{
    // O ParquetStore consolida sempre pela coluna "id" (PARTITION BY id); por isso
    // a chave do usuário (o login) é gravada na coluna "id". Entidade nova para
    // ignorar arquivos de um esquema anterior incompatível.
    private const string Ent = "app_users";
    private const string Cols = "id, nome, role, salt, hash, vendedores, ativo, updated_by, updated_at";

    // Senha padrão inicial de todos os logins semeados (o admin troca depois).
    public const string DefaultPassword = "howden2026";

    private readonly ParquetStore _store;
    public UserRepository(ParquetStore store) => _store = store;

    private static string S(IDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    public List<AppUser> All()
    {
        var list = _store.ReadLatest(Ent, Cols, r => new AppUser
        {
            Login = S(r, 0), Nome = S(r, 1), Role = string.IsNullOrWhiteSpace(S(r, 2)) ? AccessRoles.Vendedor : S(r, 2),
            Salt = S(r, 3), Hash = S(r, 4), Vendedores = S(r, 5),
            Ativo = string.IsNullOrWhiteSpace(S(r, 6)) ? "Sim" : S(r, 6),
            UpdatedBy = S(r, 7), UpdatedAt = S(r, 8),
        });
        return list.Where(u => !string.IsNullOrWhiteSpace(u.Login))
                   .OrderBy(u => u.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public AppUser? Get(string login)
    {
        var key = (login ?? "").Trim().ToLowerInvariant();
        return All().FirstOrDefault(u => u.Login == key);
    }

    public void Save(AppUser u, string actor)
    {
        u.Login = (u.Login ?? "").Trim().ToLowerInvariant();
        if (u.Login == "") return;
        u.UpdatedBy = actor;
        u.UpdatedAt = Iso(DateTime.Now);
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[]
        {
            new("id", u.Login), new("nome", u.Nome), new("role", u.Role),
            new("salt", u.Salt), new("hash", u.Hash), new("vendedores", u.Vendedores),
            new("ativo", u.Ativo), new("updated_by", u.UpdatedBy), new("updated_at", u.UpdatedAt),
        });
    }

    public void Delete(string login)
    {
        var key = (login ?? "").Trim().ToLowerInvariant();
        if (key == "") return;
        _store.WriteRow(Ent, new KeyValuePair<string, object?>[] { new("id", key) }, deleted: true);
    }

    // ---- senha ----
    public void SetPassword(AppUser u, string senha)
    {
        u.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        u.Hash = HashOf(u.Salt, senha);
    }

    public bool Verify(AppUser u, string senha) =>
        !string.IsNullOrEmpty(u.Hash) && HashOf(u.Salt, senha) == u.Hash;

    private static string HashOf(string salt, string senha)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt + "·" + senha));
        return Convert.ToHexString(bytes);
    }

    // ---- semente / conciliação das contas a partir das regras de acesso ----
    // Cria os logins que faltam e GARANTE o acesso mínimo das regras nos que já
    // existem (une a carteira, sem remover o que o admin tenha acrescentado).
    // Idempotente: se nada muda, não grava. Não mexe em senha de conta existente.
    public void SeedIfEmpty(string actor = "sistema")
    {
        var existing = All().ToDictionary(u => u.Login, StringComparer.OrdinalIgnoreCase);
        foreach (var (nome, role, vendedores) in SeedList)
        {
            var login = LoginFor(nome);
            if (!existing.TryGetValue(login, out var u))
            {
                var novo = new AppUser
                {
                    Login = login, Nome = nome, Role = role,
                    Vendedores = string.Join(";", vendedores), Ativo = "Sim",
                };
                SetPassword(novo, DefaultPassword);
                Save(novo, actor);
                continue;
            }
            // Conta existente: promove ao papel especial das regras (Controle /
            // Diretor) quando ainda está como vendedor. Nunca rebaixa nem desfaz
            // uma promoção feita pelo admin.
            if (role != AccessRoles.Vendedor)
            {
                if (u.Role == AccessRoles.Vendedor) { u.Role = role; Save(u, actor); }
                continue;
            }

            // Vendedor: garante o acesso mínimo à carteira das regras.
            if (vendedores.Length == 0) continue;
            var set = new HashSet<string>(u.VendedorList, StringComparer.CurrentCultureIgnoreCase);
            var antes = set.Count;
            foreach (var v in vendedores) set.Add(v);
            if (set.Count != antes)
            {
                u.Vendedores = string.Join(";", set);
                Save(u, actor);
            }
        }
    }

    // login = "nome.sobrenome" sem acentos/minúsculo.
    public static string LoginFor(string nome)
    {
        var n = new string((nome ?? "").Trim().ToLowerInvariant()
            .Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return string.Join(".", n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // (Nome, papel, carteira permitida) — conforme o PDF de regras de acesso.
    private static readonly (string Nome, string Role, string[] Vendedores)[] SeedList =
    {
        // Vendedores com acesso ampliado (grupos e vice-versa).
        ("Rafael Toledo",      AccessRoles.Vendedor, new[]{ "Rafael Toledo","Andre Carvalho","Jose Moura","Leonardo Silva","Paulo Agostinho","Elmer" }),
        ("Bruno Castro",       AccessRoles.Vendedor, new[]{ "Bruno Castro","Leonardo Macachero","Emerson Barbosa" }),
        ("Leonardo Macachero", AccessRoles.Vendedor, new[]{ "Leonardo Macachero","Bruno Castro","Emerson Barbosa" }),
        ("Emerson Barbosa",    AccessRoles.Vendedor, new[]{ "Emerson Barbosa","Bruno Castro","Leonardo Macachero" }),
        ("Stephanie Cipriani", AccessRoles.Vendedor, new[]{ "Stephanie Cipriani","Jose Pereira" }),
        ("Jose Pereira",       AccessRoles.Vendedor, new[]{ "Jose Pereira","Stephanie Cipriani" }),
        ("Rodrigo Ugas",       AccessRoles.Vendedor, new[]{ "Rodrigo Ugas","Manuel Gutierrez" }),
        ("Manuel Gutierrez",   AccessRoles.Vendedor, new[]{ "Manuel Gutierrez","Rodrigo Ugas" }),
        ("Paula Vilela",       AccessRoles.Vendedor, new[]{ "Paula Vilela","Douglas","Thiago Veiga" }),
        // Vendedores com acesso só à própria carteira.
        ("Andre Carvalho",     AccessRoles.Vendedor, new[]{ "Andre Carvalho" }),
        ("Jose Moura",         AccessRoles.Vendedor, new[]{ "Jose Moura" }),
        ("Leonardo Silva",     AccessRoles.Vendedor, new[]{ "Leonardo Silva" }),
        ("Paulo Agostinho",    AccessRoles.Vendedor, new[]{ "Paulo Agostinho" }),
        ("Emilio Ruiz",        AccessRoles.Vendedor, new[]{ "Emilio Ruiz" }),
        // Perfis especiais (veem todos).
        ("Thiago Veiga",         AccessRoles.Controle, Array.Empty<string>()),
        ("Sandra Silva",         AccessRoles.Controle, Array.Empty<string>()),
        ("Marcos Pinto",         AccessRoles.Controle, Array.Empty<string>()),
        ("Thais Trevine",        AccessRoles.Controle, Array.Empty<string>()),
        ("Rogerio Silva",        AccessRoles.Controle, Array.Empty<string>()),
        ("Edson Luis Geraldini", AccessRoles.Diretor,  Array.Empty<string>()),
    };
}
