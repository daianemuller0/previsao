using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Busca do CONTATO do cliente (nome + e-mail) a partir dos arquivos de proposta
// nas pastas de rede — porte para C# da rotina VBA "BuscarNomeEEmailTodasAsLinhas".
//
// Não buscamos mais as propostas em planilhas (já estão na base); só precisamos
// do contato, que fica DENTRO dos arquivos de proposta, em células conhecidas:
//   • C_{codigo}-*.xlsm   → aba "Proposta"  D25 (nome) / D26 (e-mail)
//                            (fallback aba "RESUMO" G3 / G4)
//   • P_{codigo}-C*.xlsm  → aba "PROPOSTA"  C7  (nome) / C8  (e-mail)
//   • P_{codigo}-*.xlsm   → aba "PROPOSTA"  C7  (nome) / C8  (e-mail)  (sem "-C")
//   • R_{codigo}-R*.xlsm  → aba "Oferta"    B6  (e-mail)
// Sempre o arquivo de MAIOR revisão. A pasta varia conforme a BU (Brasil/Chile/
// Peru); BU indefinida tenta as três. Os "roots" das pastas são configuráveis
// em appsettings (seção "Contatos") para não ficarem fixos no código.
// ---------------------------------------------------------------------------
public class ContactLookupService
{
    private readonly bool _enabled;
    private readonly string _brAfmRoot;   // H:\AFTERMARKET\00 PROPOSTAS
    private readonly string _brNbRoot;    // H:\ENG_APLICACAO\HSA\03 PROPOSTA
    private readonly string _clRoot;      // G:\COMERCIAL\01 PROPUESTAS

    public ContactLookupService(IConfiguration cfg)
    {
        _enabled  = cfg.GetValue("Contatos:Enabled", true);
        _brAfmRoot = cfg["Contatos:BrasilAfmRoot"] ?? @"H:\AFTERMARKET\00 PROPOSTAS";
        _brNbRoot  = cfg["Contatos:BrasilNbRoot"]  ?? @"H:\ENG_APLICACAO\HSA\03 PROPOSTA";
        _clRoot    = cfg["Contatos:ChileRoot"]     ?? @"G:\COMERCIAL\01 PROPUESTAS";
    }

    public bool Enabled => _enabled;

    // Resultado da busca. Found=false quando nada foi localizado; Reason explica
    // (pasta não encontrada, arquivo não encontrado, contato vazio…).
    public sealed record Hit(bool Found, string Name, string Email, string Source, string Reason);

    private static readonly Hit NotFound = new(false, "", "", "", "sem contato localizado");

    // ---- Busca principal: código da proposta + código da BU (HSA/HCHL/HPU/…) ----
    public Hit Find(string codigo, string? buCode)
    {
        codigo = (codigo ?? "").Trim();
        if (!_enabled) return NotFound with { Reason = "busca de contatos desativada (Contatos:Enabled=false)" };
        if (codigo == "") return NotFound with { Reason = "código vazio" };

        var bu = (buCode ?? "").Trim().ToUpperInvariant();
        var isBr  = bu is "HSA" || bu.StartsWith("HSA:") || bu.StartsWith("HSA ");
        var isCl  = bu is "HCHL" || bu.StartsWith("HCHL");
        var isPe  = bu is "HPU" || bu.StartsWith("HPU");
        var isUnd = bu.Length == 0;   // BU indefinida → tenta todas

        // Pastas (mesma estrutura do macro).
        string PastaBr()   => Path.Combine(_brAfmRoot, codigo, "CUSTO_&_PRICING");
        string PastaHpu()  => Path.Combine(_brAfmRoot, "0_HPU", codigo, "CUSTO_&_PRICING");
        string PastaBrNb() => Path.Combine(_brNbRoot, codigo, "CUSTO_&_PRICING");
        string PastaClNb1()=> Path.Combine(_clRoot, "01 NB", codigo, "COSTO_&_PRICING");
        string PastaClNb2()=> Path.Combine(_clRoot, "01 NB", codigo, "COSTO & PRICING");
        string PastaCl()   => Path.Combine(_clRoot, "02 AFM", codigo, "COSTO & PRICING");
        string PastaClSv() => Path.Combine(_clRoot, "03 SV", codigo, "COSTO & PRICING");

        Hit h;

        if (isBr || isUnd)
        {
            if ((h = FromC(PastaBr(), codigo)).Found) return h;
            if ((h = FromP_C(PastaBr(), codigo)).Found) return h;
            if ((h = FromC(PastaBrNb(), codigo)).Found) return h;
            if ((h = FromP_C(PastaBrNb(), codigo)).Found) return h;
            if ((h = FromP_SemC(PastaBr(), codigo)).Found) return h;
            if ((h = FromOferta(PastaBr(), codigo)).Found) return h;
        }

        if (isCl || isUnd)
        {
            if ((h = FromC(PastaClNb1(), codigo)).Found) return h;
            if ((h = FromC(PastaClNb2(), codigo)).Found) return h;
            if ((h = FromP_C(PastaClNb1(), codigo)).Found) return h;
            if ((h = FromP_C(PastaClNb2(), codigo)).Found) return h;
            if ((h = FromOferta(PastaCl(), codigo)).Found) return h;
            if ((h = FromOferta(PastaClSv(), codigo)).Found) return h;
        }

        if (isPe || isUnd)
        {
            if ((h = FromC(PastaHpu(), codigo)).Found) return h;
            if ((h = FromOferta(PastaHpu(), codigo)).Found) return h;
        }

        return NotFound;
    }

    // C_{codigo}-*.xlsm → aba "Proposta" D25/D26 (fallback "RESUMO" G3/G4)
    private Hit FromC(string pasta, string codigo)
    {
        var file = BestFile(pasta, $"C_{codigo}-*.xlsm", $"C_{codigo}-");
        if (file is null) return NotFound;
        return Read(file, wb =>
        {
            var (n, e) = Cells(wb, "Proposta", "D25", "D26");
            if (n == "" && e == "") (n, e) = Cells(wb, "RESUMO", "G3", "G4");
            return (n, e);
        });
    }

    // P_{codigo}-C*.xlsm → aba "PROPOSTA" C7/C8
    private Hit FromP_C(string pasta, string codigo)
    {
        var file = BestFile(pasta, $"P_{codigo}-C*.xlsm", $"P_{codigo}-C");
        if (file is null) return NotFound;
        return Read(file, wb => Cells(wb, "PROPOSTA", "C7", "C8"));
    }

    // P_{codigo}-*.xlsm (SEM "-C") → aba "PROPOSTA" C7/C8
    private Hit FromP_SemC(string pasta, string codigo)
    {
        var file = BestFile(pasta, $"P_{codigo}-*.xlsm", $"P_{codigo}-",
                            excludeContains: "-C");
        if (file is null) return NotFound;
        return Read(file, wb => Cells(wb, "PROPOSTA", "C7", "C8"));
    }

    // R_{codigo}-R*.xlsm → aba "Oferta" B6 (só e-mail)
    private Hit FromOferta(string pasta, string codigo)
    {
        var file = BestFile(pasta, $"R_{codigo}-R*.xlsm", $"R_{codigo}-R");
        if (file is null) return NotFound;
        return Read(file, wb =>
        {
            var (v, _) = Cells(wb, "Oferta", "B6", "B6");   // B6 = e-mail (só e-mail nesta aba)
            return ("", v);
        });
    }

    // Lê nome/e-mail de uma aba (por nome, sem diferenciar maiúsc/minúsc).
    private static (string Name, string Email) Cells(IXLWorkbook wb, string sheet, string cellName, string cellEmail)
    {
        var ws = wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, sheet, StringComparison.OrdinalIgnoreCase));
        if (ws is null) return ("", "");
        string V(string a) => ws.Cell(a).GetString().Trim();
        return (V(cellName), cellName == cellEmail ? "" : V(cellEmail));
    }

    // Abre o arquivo (somente leitura, permitindo que outros o mantenham aberto)
    // e aplica o leitor. Erros (arquivo travado/corrompido) devolvem NotFound.
    private static Hit Read(string path, Func<IXLWorkbook, (string Name, string Email)> reader)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(fs);
            var (n, e) = reader(wb);
            n = (n ?? "").Trim();
            e = (e ?? "").Trim();
            if (n == "" && e == "") return NotFound with { Reason = "contato vazio no arquivo" };
            return new Hit(true, n, e, Path.GetFileName(path), "");
        }
        catch
        {
            return NotFound with { Reason = "falha ao abrir o arquivo" };
        }
    }

    // Arquivo de MAIOR revisão que casa com a máscara: a parte após o prefixo (e
    // antes de ".xlsm") é numérica; vence o maior número. excludeContains descarta
    // nomes que contêm o trecho (ex.: "-C" para pegar só P_{codigo}-{rev}).
    private static string? BestFile(string pasta, string pattern, string prefix, string? excludeContains = null)
    {
        if (string.IsNullOrWhiteSpace(pasta) || !Directory.Exists(pasta)) return null;
        string? best = null;
        long bestRev = -1;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(pasta, pattern); }
        catch { return null; }

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (excludeContains is not null && name.IndexOf(excludeContains, StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            var part = name;
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) part = part[prefix.Length..];
            part = Regex.Replace(part, @"\.xlsm$", "", RegexOptions.IgnoreCase).Trim();
            if (long.TryParse(part, out var rev) && rev > bestRev) { bestRev = rev; best = path; }
        }
        return best;
    }
}
