using System.Globalization;
using System.Security.Claims;
using System.Text;
using HowdenSalesForecast.Components;
using HowdenSalesForecast.Data;
using HowdenSalesForecast.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Porta padrão (modo por-usuário). Para "servidor central", rode com:
//   "Howden Sales Forecast.exe" --urls http://0.0.0.0:5080
// Se a 5080 já estiver ocupada (o programa aberto noutra janela, por exemplo),
// usa a próxima porta livre em vez de encerrar com erro de bind do Kestrel.
if (string.IsNullOrEmpty(builder.Configuration["urls"]) &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls($"http://localhost:{PortaLivre(5080)}");
}

// Primeira porta livre a partir de "inicial" (tenta 20; 0 = o SO escolhe).
static int PortaLivre(int inicial)
{
    for (var p = inicial; p < inicial + 20; p++)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
            l.Start();
            l.Stop();
            return p;
        }
        catch (System.Net.Sockets.SocketException) { /* ocupada: tenta a próxima */ }
    }
    return 0;
}

// --- Blazor Server (componentes interativos no servidor) ---
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- Autenticação por cookie (sessão no navegador do usuário) ---
builder.Services.AddCascadingAuthenticationState();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

// --- Dados: DuckDB sobre Parquet numa pasta (padrão aprovado internamente) ---
var dataFolder = builder.Configuration["Data:Folder"] ?? "data";
builder.Services.AddSingleton(new ParquetStore(dataFolder));
builder.Services.AddSingleton<Catalog>();
// Singleton: mantém o cache das oportunidades compartilhado entre as páginas.
builder.Services.AddSingleton<OpportunityRepository>();
// Importador de planilha (.xlsx/.csv) da base de oportunidades.
builder.Services.AddScoped<OpportunityImporter>();
// Listas de preenchimento (dados-mestre editáveis).
builder.Services.AddSingleton<ListRepository>();
// Controle Orçamentário (valores, auditoria e observações).
builder.Services.AddSingleton<ControleRepository>();
// Follow-up das propostas em aberto (estado + histórico de interações).
builder.Services.AddSingleton<FollowUpRepository>();
// Busca do contato do cliente (nome + e-mail) nos arquivos de proposta da rede.
builder.Services.AddSingleton<ContactLookupService>();
// Contas de acesso (login/senha + carteira permitida por vendedor).
builder.Services.AddSingleton<UserRepository>();
// Abertura de rascunhos no Outlook (automação COM) para o follow-up em massa.
builder.Services.AddSingleton<OutlookMailService>();
// Gráficos personalizados do estúdio da Visão Executiva.
builder.Services.AddSingleton<ChartConfigRepository>();
// Identidade visual (logos da capa e da página interna).
builder.Services.AddSingleton<BrandRepository>();
// Compactação periódica dos Parquet em segundo plano.
builder.Services.AddHostedService<CompactionService>();
// Sincronização das bases (planilhas do CRM na rede): New Business + Aftermarket.
builder.Services.AddSingleton<DataSyncService>();

var app = builder.Build();

// Semente dos dados demonstrativos na primeira execução (pasta vazia).
using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<ParquetStore>();
    DbInitializer.Initialize(store, scope.ServiceProvider.GetRequiredService<Catalog>());
    scope.ServiceProvider.GetRequiredService<ListRepository>().SeedIfEmpty();
    var controleRepo = scope.ServiceProvider.GetRequiredService<ControleRepository>();
    controleRepo.SeedIfEmpty();
    controleRepo.SeedRegistrosIfEmpty();
    scope.ServiceProvider.GetRequiredService<ChartConfigRepository>().SeedIfEmpty();
    // Contas de acesso: semeia os logins dos vendedores na primeira execução.
    scope.ServiceProvider.GetRequiredService<UserRepository>().SeedIfEmpty();
}

// Aquece o cache em SEGUNDO PLANO (compacta + lê a base). Não bloqueia a subida
// do app — mesmo com a pasta de rede lenta, a tela de login abre imediatamente.
_ = Task.Run(() =>
{
    try
    {
        var oppRepo = app.Services.GetRequiredService<OpportunityRepository>();
        oppRepo.RemoveDemo();                 // remove oportunidades demo antigas
        oppRepo.Refresh();
        // A compactação NÃO roda aqui: reescrever a base inteira na rede a cada
        // abertura é caro. O CompactionService cuida disso em segundo plano,
        // só quando há arquivos demais.

        // Atualiza as bases (New Business + Aftermarket) a partir das planilhas do
        // CRM na rede. Os vendedores atualizam o CRM e a exportação cai nesses
        // caminhos; a cada abertura do programa reimportamos o estado mais recente.
        app.Services.GetRequiredService<DataSyncService>().SyncAll();
    }
    catch { /* pasta indisponível ou lenta: o app segue no ar */ }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

// --- Login/logout (precisam do HttpContext para gravar o cookie) ---
// Credencial única da equipe (Auth:Usuario / Auth:Senha). O "perfil" escolhido
// no formulário vira o papel (role) usado para adaptar a navegação — demonstra
// os três níveis de experiência (executivo, gestão, operação).
app.MapPost("/auth/login", async (HttpContext http, IConfiguration cfg, UserRepository users) =>
{
    var form = await http.Request.ReadFormAsync();
    var usuario = form["usuario"].ToString().Trim();
    var senha = form["senha"].ToString();

    List<Claim>? claims = null;

    // 1) Conta de acesso individual (vendedor / controle / diretor / admin).
    var login = usuario.ToLowerInvariant();
    if (login.Contains('@')) login = login.Split('@')[0];   // aceita usuario@dominio
    var user = users.Get(login);
    if (user is not null && user.IsActive && users.Verify(user, senha))
    {
        claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Login),
            new(ClaimTypes.Name, user.Nome),
            new(ClaimTypes.Role, AccessRoles.Normalize(user.Role)),
            new(AccessScope.VendedoresClaim, user.Vendedores),
        };
    }

    // 2) Credencial mestre de administração (appsettings) — sempre disponível.
    if (claims is null)
    {
        var cfgUsuario = cfg["Auth:Usuario"] ?? "howden";
        var cfgSenha = cfg["Auth:Senha"] ?? "howden2026";
        if (usuario.Equals(cfgUsuario, StringComparison.OrdinalIgnoreCase) && senha == cfgSenha)
        {
            claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "admin"),
                new(ClaimTypes.Name, "Administrador"),
                new(ClaimTypes.Role, AccessRoles.Admin),
                new(AccessScope.VendedoresClaim, ""),
            };
        }
    }

    if (claims is null) return Results.Redirect("/login?error=1");

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

// Exporta o forecast consolidado em CSV (BOM UTF-8, separador ';' p/ Excel pt-BR).
app.MapGet("/forecast/export", (HttpContext http, OpportunityRepository repo, Catalog cat) =>
{
    var today = DateTime.Today;
    // Recorte de acesso: cada login só exporta a carteira que pode enxergar, e
    // nunca as oportunidades já movidas para o Controle (venda indicada).
    var scope = AccessScope.Resolve(http.User);
    static string Campo(string s) => s.Contains(';') || s.Contains('"') || s.Contains('\n')
        ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    string Inv(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    var sb = new StringBuilder();
    sb.AppendLine("Proposta;Oportunidade;Categoria;Status pipeline;Data prevista;Quarter;Pais;Mercado;" +
                  "Produto;KAM;Cliente;Planta;Moeda;Valor USD;Valor BRL;GM%;Prob ganho;Prob periodo;" +
                  "Forecast ponderado USD;Margem USD;Risco;Categoria comercial;Proxima acao;Atualizado em");
    var exportaveis = repo.All()
        .Where(o => !o.MovidaControleValue)
        .Where(o => scope.CanSeeVendedor(OpportunityImporter.CanonicalVendedor(cat.KamName(o.KamId))));
    foreach (var o in exportaveis)
    {
        var d = o.ExpectedDateValue ?? today;
        sb.AppendLine(string.Join(';', new[]
        {
            Campo(o.ProposalNumber), Campo(o.Name), ForecastCategories.Label(o.Category),
            Campo(cat.StageName(o.PipelineStageId)), Fmt.Date(o.ExpectedDate), Fmt.Quarter(d),
            Campo(cat.CountryName(o.CountryId)), Campo(cat.MarketName(o.MarketId)),
            Campo(cat.ProductName(o.ProductId)), Campo(cat.KamName(o.KamId)),
            Campo(cat.CustomerName(o.CustomerId)), Campo(cat.PlantName(o.PlantId)),
            o.CurrencyCode, Inv(o.AmountUsd), Inv(o.AmountBrl), Inv(o.GmPercentValue),
            Inv(o.WinProbabilityValue), Inv(o.CloseProbabilityValue),
            Inv(ForecastCalc.WeightedUsd(o)), Inv(ForecastCalc.MarginUsd(o)),
            RiskLevels.Label(ForecastCalc.Level(o, today)), o.CommercialCategory,
            Campo(o.NextAction), Fmt.Date(o.UpdatedAt),
        }));
    }
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    return Results.File(bytes, "text/csv; charset=utf-8", "forecast.csv");
}).RequireAuthorization();

// Exporta o Controle Orçamentário em CSV (separador ';' p/ Excel pt-BR).
app.MapGet("/controle/export", (ControleRepository repo, HttpRequest req) =>
{
    var year = int.TryParse(req.Query["ano"], out var y) ? y : DateTime.Today.Year;
    string Inv(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    var status = repo.MonthStatus(year);
    var sb = new StringBuilder();
    sb.AppendLine("Ano;Mes;Categoria;Orcamento;Realizado;Forecast;StatusMes;AtualizadoPor;AtualizadoEm");
    foreach (var e in repo.Entries(year).OrderBy(e => e.MonthV).ThenBy(e => e.Category))
        sb.AppendLine(string.Join(';', new[]
        {
            e.Year, e.Month, e.Category, Inv(e.BudgetV), Inv(e.RealizadoV),
            e.HasForecast ? Inv(e.ForecastV) : "", status.GetValueOrDefault(e.MonthV, "Aberto"),
            e.UpdatedBy, e.UpdatedAt,
        }));
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    return Results.File(bytes, "text/csv; charset=utf-8", $"controle_{year}.csv");
    // Mesma restrição da aba Controle: só Controle e Administrador.
}).RequireAuthorization(p => p.RequireRole(AccessRoles.Controle, AccessRoles.Admin));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Modo por-usuário: abre o navegador ao iniciar. Desligue com "OpenBrowser": false.
if (builder.Configuration.GetValue("OpenBrowser", true))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            var url = (app.Urls.FirstOrDefault() ?? "http://localhost:5080")
                .Replace("0.0.0.0", "localhost").Replace("[::]", "localhost");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* sem navegador disponível: apenas ignora */ }
    });
}

app.Run();
