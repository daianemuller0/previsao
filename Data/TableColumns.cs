namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Colunas de uma tabela: definição (chave, rótulo, alinhamento) e a preferência
// de cada usuário (ordem + o que fica oculto). É a mesma mecânica para todas as
// tabelas do sistema — cada uma só declara a sua lista de colunas.
//
// Formato da preferência: "chave1,chave2,-chave3" — o "-" marca a coluna como
// oculta. Chaves desconhecidas são ignoradas e chaves novas (versões futuras)
// entram no fim, então a preferência nunca "quebra" ao adicionarmos colunas.
// ---------------------------------------------------------------------------
public static class TableColumns
{
    public sealed record ColDef(string Key, string Label, string Align = "");
    public sealed record ColState(ColDef Def, bool Visible);

    private static readonly HashSet<string> Nenhuma = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordem declarada, com as ocultas padrão desmarcadas.</summary>
    public static List<ColState> Default(IReadOnlyList<ColDef> todas, HashSet<string>? ocultas = null)
    {
        ocultas ??= Nenhuma;
        return todas.Select(d => new ColState(d, !ocultas.Contains(d.Key))).ToList();
    }

    /// <summary>Lê a preferência gravada; entradas desconhecidas somem e colunas
    /// novas entram no fim, preservando o que o usuário já ajustou.</summary>
    public static List<ColState> Parse(string? pref, IReadOnlyList<ColDef> todas, HashSet<string>? ocultas = null)
    {
        ocultas ??= Nenhuma;
        if (string.IsNullOrWhiteSpace(pref)) return Default(todas, ocultas);

        var byKey = todas.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        var result = new List<ColState>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in pref.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var oculta = raw.StartsWith('-');
            var key = oculta ? raw[1..] : raw;
            if (!byKey.TryGetValue(key, out var def) || !vistos.Add(def.Key)) continue;
            result.Add(new ColState(def, !oculta));
        }

        foreach (var d in todas)
            if (!vistos.Contains(d.Key))
                result.Add(new ColState(d, !ocultas.Contains(d.Key)));

        return result;
    }

    /// <summary>Serializa a preferência para gravação.</summary>
    public static string Serialize(IEnumerable<ColState> cols) =>
        string.Join(",", cols.Select(c => (c.Visible ? "" : "-") + c.Def.Key));

    /// <summary>Reordena conforme as chaves que vieram do arraste no cabeçalho.
    /// As colunas ocultas ficam logo depois da visível que as precedia, para não
    /// saltarem de lugar quando a pessoa reexibir alguma.</summary>
    public static List<ColState> Reorder(IEnumerable<ColState> cols, IEnumerable<string> chaves)
    {
        var atual = cols.ToList();
        var ordem = chaves.ToList();
        var posição = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ordem.Count; i++) posição.TryAdd(ordem[i], i);

        // Cada oculta herda a posição da visível anterior (empatando, mantém a
        // ordem relativa que já tinham).
        var chave = new List<(ColState Col, int Pos, int Orig)>();
        var última = -1;
        for (var i = 0; i < atual.Count; i++)
        {
            if (posição.TryGetValue(atual[i].Def.Key, out var p)) última = p;
            chave.Add((atual[i], última, i));
        }

        return chave.OrderBy(x => x.Pos).ThenBy(x => x.Orig).Select(x => x.Col).ToList();
    }
}
