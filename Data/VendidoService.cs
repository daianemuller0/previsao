using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Guia "Vendido": a planilha de pedidos (o valor vendido de cada pedido), lida da
// pasta de rede Vendido:Path — a única planilha .xlsm da pasta (se houver mais de
// uma, a mais recente). Colunas pela LETRA do Excel (Vendido:Colunas sobrepõe):
//   BU = A · Data do pedido = E · Valor vendido = L · Categoria = R · Produto = S ·
//   KAM = Y · Segmento = pelo título da coluna ("Segmento"/"Segment"); sem título,
//   a letra configurada (padrão S, a mesma do Produto, até a equipe confirmar).
// Linhas sem data do pedido ou sem valor numérico ficam de fora (totais, títulos).
// Relê só quando o arquivo muda (nome, data de gravação e tamanho).
// ---------------------------------------------------------------------------
public sealed record PedidoVendido(DateTime Data, double Valor, string Bu, string Categoria, string Produto, string Segmento, string Kam);

public sealed class VendidoService
{
    private static readonly CultureInfo Br = new("pt-BR");
    private static readonly Dictionary<string, string> ColunasPadrao = new()
    {
        ["Bu"] = "A", ["Data"] = "E", ["Valor"] = "L", ["Categoria"] = "R", ["Produto"] = "S", ["Segmento"] = "S", ["Kam"] = "Y",
    };

    private readonly IConfiguration _cfg;
    private readonly object _lock = new();
    private string _marca = "";

    public VendidoService(IConfiguration cfg) => _cfg = cfg;

    public List<PedidoVendido> Pedidos { get; private set; } = new();
    public bool Ok { get; private set; }
    public string Mensagem { get; private set; } = "Planilha de vendido ainda não lida.";
    public string Arquivo { get; private set; } = "";
    public DateTime? LidoEm { get; private set; }
    /// <summary>De onde veio cada campo ("Segmento: coluna T · \"Segmento\""), para conferência na tela.</summary>
    public List<string> Mapeamento { get; private set; } = new();
    public int Version { get; private set; }

    public string Pasta => (_cfg["Vendido:Path"] ?? "").Trim();

    /// <summary>Lê a planilha se ela mudou (ou se forcar). Nunca lança: falha vira Mensagem e o que já estava lido continua.</summary>
    public void Carregar(bool forcar = false)
    {
        lock (_lock)
        {
            try
            {
                if (Pasta == "") { Falhou("Pasta do vendido não configurada (appsettings: Vendido:Path)."); return; }
                if (!Directory.Exists(Pasta)) { Falhou($"Pasta do vendido não encontrada: {Pasta}"); return; }
                var arq = new DirectoryInfo(Pasta).GetFiles("*.xlsm")
                    .Concat(new DirectoryInfo(Pasta).GetFiles("*.xlsx"))
                    .Where(f => !f.Name.StartsWith("~$"))
                    .OrderByDescending(f => f.Extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (arq is null) { Falhou($"Nenhuma planilha .xlsm na pasta {Pasta}."); return; }
                var marca = $"{arq.Name}|{arq.LastWriteTimeUtc.Ticks}|{arq.Length}";
                if (!forcar && Ok && marca == _marca) return;

                var bytes = LeituraSegura.Bytes(arq.FullName);
                using var ms = new MemoryStream(bytes);
                var grade = OpportunityImporter.LerGrade(arq.Name, ms, manterLinhasVazias: true,
                    aba: _cfg["Vendido:Aba"], maiorAba: true);
                var (pedidos, mapa, ignoradas) = Montar(grade);
                Pedidos = pedidos; Mapeamento = mapa;
                Arquivo = arq.Name; _marca = marca; Ok = true; LidoEm = DateTime.Now;
                Mensagem = $"{pedidos.Count:N0} pedido(s) lido(s) de {arq.Name}" + (ignoradas > 0 ? $" · {ignoradas:N0} linha(s) sem data ou sem valor ignorada(s)" : "") + ".";
                Version++;
            }
            catch (Exception ex)
            {
                Falhou("Não foi possível ler a planilha de vendido agora." + (ex is IOException ? " O arquivo pode estar aberto ou sendo gravado; tente de novo em instantes." : " " + ex.Message));
            }
        }
    }

    private void Falhou(string msg)
    {
        Ok = Pedidos.Count > 0 && Ok;
        Mensagem = Pedidos.Count > 0 && LidoEm is { } t ? $"{msg} Exibindo os dados de {t:dd/MM/yyyy HH:mm}." : msg;
        Version++;
    }

    private int Col(string nome)
    {
        var letra = (_cfg[$"Vendido:Colunas:{nome}"] ?? ColunasPadrao[nome]).Trim();
        return letra == "" ? -1 : AplicacaoService.Idx(letra);
    }

    private (List<PedidoVendido>, List<string>, int) Montar(List<OpportunityImporter.Cel[]> grade)
    {
        int iBu = Col("Bu"), iData = Col("Data"), iValor = Col("Valor"), iCat = Col("Categoria"), iProd = Col("Produto"), iSeg = Col("Segmento"), iKam = Col("Kam");
        string Txt(OpportunityImporter.Cel[] l, int i) => i >= 0 && i < l.Length ? (l[i].Text ?? "").Trim() : "";

        // cabeçalho: a última linha antes da primeira linha de dados (data no E e valor no L)
        var iPrimeira = grade.FindIndex(l => Data(l, iData) is not null && Valor(l, iValor) is not null);
        var cab = iPrimeira > 0 ? grade.Take(iPrimeira).LastOrDefault(l => l.Any(c => (c.Text ?? "").Trim() != "")) ?? Array.Empty<OpportunityImporter.Cel>() : Array.Empty<OpportunityImporter.Cel>();
        // Segmento pelo título, se a configuração não fixou a letra
        if (_cfg["Vendido:Colunas:Segmento"] is null)
            for (var i = 0; i < cab.Length; i++)
            {
                var t = OpportunityImporter.Normalizar(cab[i].Text ?? "");
                if (t.StartsWith("segment")) { iSeg = i; break; }
            }
        string Tit(int i) => i >= 0 && i < cab.Length && (cab[i].Text ?? "").Trim() != "" ? $" · \"{cab[i].Text.Trim()}\"" : "";
        var mapa = new List<string>
        {
            $"BU: coluna {AplicacaoService.Letra(iBu)}{Tit(iBu)}", $"Data do pedido: coluna {AplicacaoService.Letra(iData)}{Tit(iData)}",
            $"Valor vendido: coluna {AplicacaoService.Letra(iValor)}{Tit(iValor)}", $"Categoria: coluna {AplicacaoService.Letra(iCat)}{Tit(iCat)}",
            $"Produto: coluna {AplicacaoService.Letra(iProd)}{Tit(iProd)}", $"Segmento: coluna {AplicacaoService.Letra(iSeg)}{Tit(iSeg)}",
            $"KAM: coluna {AplicacaoService.Letra(iKam)}{Tit(iKam)}",
        };

        var lista = new List<PedidoVendido>(); var ignoradas = 0;
        foreach (var l in grade.Skip(Math.Max(0, iPrimeira)))
        {
            if (l.Length == 0) continue;
            var d = Data(l, iData); var v = Valor(l, iValor);
            if (d is null || v is null) { if (l.Any(c => (c.Text ?? "").Trim() != "")) ignoradas++; continue; }
            string V(int i) => Txt(l, i) is { Length: > 0 } s ? s : "(não informado)";
            lista.Add(new PedidoVendido(d.Value.Date, v.Value, V(iBu), V(iCat), V(iProd), V(iSeg), V(iKam)));
        }
        return (lista, mapa, ignoradas);
    }

    private static DateTime? Data(OpportunityImporter.Cel[] l, int i)
    {
        if (i < 0 || i >= l.Length) return null;
        var c = l[i];
        if (c.Num is double n && n > 20000 && n < 80000) return DateTime.FromOADate(n);
        var t = (c.Text ?? "").Trim();
        if (t == "") return null;
        return DateTime.TryParse(t, Br, DateTimeStyles.None, out var d) && d.Year is > 2000 and < 2100 ? d : null;
    }

    private static double? Valor(OpportunityImporter.Cel[] l, int i)
    {
        if (i < 0 || i >= l.Length) return null;
        if (l[i].Num is double n) return n;
        return VendasMensaisRepository.Numero(l[i].Text) is decimal v ? (double)v : null;
    }
}
