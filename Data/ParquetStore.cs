using System.Data;
using DuckDB.NET.Data;

namespace HowdenSalesForecast.Data;

/// <summary>
/// Camada de dados no padrão recomendado pela equipe de melhoria (Edson):
/// DuckDB como MOTOR sobre arquivos Parquet numa pasta de rede.
///
/// Cada gravação (adição/edição) cria um pequeno arquivo .parquet novo dentro
/// da subpasta da entidade (ex.: companies/). Nada de "um único banco na rede"
/// (que com SQLite trava e fica lento). Na leitura, o DuckDB lê a pasta inteira
/// e CONSOLIDA: para cada id, mantém a versão mais recente (_ts) e ignora os
/// registros marcados como apagados (_deleted).
///
/// Como cada operação abre um DuckDB em memória (Data Source=:memory:) e só
/// toca os Parquet, não há arquivo de banco compartilhado sendo travado —
/// vários usuários podem gravar ao mesmo tempo, cada um no seu arquivinho.
/// </summary>
public sealed class ParquetStore
{
    public string Folder { get; }

    public ParquetStore(string folder)
    {
        var configured = Path.GetFullPath(folder);
        try
        {
            Directory.CreateDirectory(configured);
            Folder = configured;
        }
        catch (Exception ex)
        {
            // Caminho configurado indisponível (ex.: pasta de rede sem acesso
            // nesta máquina). Em vez de derrubar a aplicação, cai para uma pasta
            // local "data" ao lado do executável, para que rode em qualquer lugar.
            var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data"));
            try
            {
                Directory.CreateDirectory(fallback);
                Folder = fallback;
                Console.Error.WriteLine(
                    $"[ParquetStore] Pasta de dados '{configured}' inacessível ({ex.Message}). " +
                    $"Usando pasta local '{fallback}'.");
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException(
                    $"Não foi possível acessar a pasta de dados '{configured}' nem a alternativa local '{fallback}'. " +
                    "Verifique a chave \"Data:Folder\" no appsettings.", ex2);
            }
        }
    }

    // Diretórios já garantidos nesta execução. Em pasta de rede (ainda mais por
    // VPN) cada CreateDirectory é uma ida ao servidor, e EntityDir é chamado em
    // toda leitura e toda gravação.
    private readonly HashSet<string> _dirsOk = new(StringComparer.OrdinalIgnoreCase);

    private string EntityDir(string entity)
    {
        var dir = Path.Combine(Folder, entity);
        lock (_dirsOk)
        {
            if (_dirsOk.Add(dir)) Directory.CreateDirectory(dir);
        }
        return dir;
    }

    private static DuckDBConnection Open()
    {
        var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    private static void AddParam(IDbCommand cmd, object? value)
    {
        var p = cmd.CreateParameter();
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    // Caminho no formato que o DuckDB entende (barras normais, mesmo no Windows).
    private static string Duck(string path) => path.Replace('\\', '/');

    /// <summary>Grava uma linha como um novo arquivo Parquet na subpasta da entidade.</summary>
    public void WriteRow(string entity, IReadOnlyList<KeyValuePair<string, object?>> row, bool deleted = false)
    {
        using var conn = Open();

        var colDefs = string.Join(", ", row.Select(kv => $"\"{kv.Key}\" VARCHAR")) + ", _ts BIGINT, _deleted BOOLEAN";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE TABLE t ({colDefs});";
            cmd.ExecuteNonQuery();
        }

        var placeholders = string.Join(", ", row.Select(_ => "?")) + ", ?, ?";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO t VALUES ({placeholders});";
            foreach (var kv in row) AddParam(cmd, kv.Value);
            AddParam(cmd, DateTime.UtcNow.Ticks);
            AddParam(cmd, deleted);
            cmd.ExecuteNonQuery();
        }

        var dir = EntityDir(entity);
        var fileName = $"{DateTime.UtcNow.Ticks:D19}_{Guid.NewGuid():N}.parquet";
        var full = Duck(Path.Combine(dir, fileName));
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"COPY t TO '{full}' (FORMAT PARQUET, COMPRESSION ZSTD);";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Grava VÁRIAS linhas num ÚNICO arquivo Parquet (uma escrita de rede em vez
    /// de N). Todas as linhas devem ter as mesmas colunas, na mesma ordem.
    /// Usado no seed/importação para evitar dezenas de arquivos pequenos.
    /// </summary>
    public void WriteBatch(string entity, IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> rows)
    {
        if (rows.Count == 0) return;
        using var conn = Open();
        var first = rows[0];

        var colDefs = string.Join(", ", first.Select(kv => $"\"{kv.Key}\" VARCHAR")) + ", _ts BIGINT, _deleted BOOLEAN";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE TABLE t ({colDefs});";
            cmd.ExecuteNonQuery();
        }

        // Um comando só, reaproveitado: montar e preparar um INSERT por linha
        // custava caro numa importação de mais de mil oportunidades.
        var placeholders = string.Join(", ", first.Select(_ => "?")) + ", ?, ?";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO t VALUES ({placeholders});";
            foreach (var row in rows)
            {
                cmd.Parameters.Clear();
                foreach (var kv in row) AddParam(cmd, kv.Value);
                AddParam(cmd, DateTime.UtcNow.Ticks);
                AddParam(cmd, false);
                cmd.ExecuteNonQuery();
            }
        }

        var dir = EntityDir(entity);
        var fileName = $"{DateTime.UtcNow.Ticks:D19}_{Guid.NewGuid():N}.parquet";
        var full = Duck(Path.Combine(dir, fileName));
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"COPY t TO '{full}' (FORMAT PARQUET, COMPRESSION ZSTD);";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Lê a entidade já consolidada: versão mais recente por id, sem os apagados.
    /// Retorna vazio se ainda não houver nenhum arquivo (primeira execução).
    /// </summary>
    // Esquema conhecido de cada entidade, válido enquanto o conjunto de arquivos
    // não mudar. Descobrir as colunas exige abrir o rodapé de TODOS os Parquet —
    // fazer isso em toda leitura dobrava as idas à rede sem necessidade.
    private readonly Dictionary<string, (string Chave, HashSet<string> Cols)> _esquemas = new(StringComparer.OrdinalIgnoreCase);

    public List<T> ReadLatest<T>(string entity, string selectCols, Func<IDataReader, T> map, string orderBy = "")
    {
        var dir = EntityDir(entity);
        // GetFiles traz nome, tamanho e data numa varredura só (uma ida à rede);
        // pedir isso arquivo a arquivo custaria uma ida por arquivo.
        var arquivos = new DirectoryInfo(dir).GetFiles("*.parquet");
        if (arquivos.Length == 0) return new List<T>();

        var glob = Duck(Path.Combine(dir, "*.parquet"));
        using var conn = Open();

        // Evolução de esquema: arquivos antigos podem não ter colunas novas.
        // union_by_name junta esquemas diferentes entre arquivos, e as colunas
        // pedidas que não existirem em NENHUM arquivo viram NULL no SELECT.
        var chave = $"{arquivos.Length}|{arquivos.Max(f => f.LastWriteTimeUtc.Ticks)}|{arquivos.Sum(f => f.Length)}";
        HashSet<string>? presentes;
        lock (_esquemas)
            presentes = _esquemas.TryGetValue(entity, out var e) && e.Chave == chave ? e.Cols : null;

        if (presentes is null)
        {
            presentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var describe = conn.CreateCommand();
            describe.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{glob}', union_by_name=true);";
            using var dr = describe.ExecuteReader();
            while (dr.Read()) presentes.Add(dr.GetString(0));
            lock (_esquemas) _esquemas[entity] = (chave, presentes);
        }

        var pedidas = selectCols.Split(',').Select(c => c.Trim()).Where(c => c != "").ToList();
        var cols = string.Join(", ", pedidas.Select(c => presentes.Contains(c) ? c : $"NULL AS {c}"));

        // Lê SÓ as colunas usadas (as pedidas + as três de controle). Sem isso a
        // consulta traz a linha inteira do Parquet: numa entidade de dezenas de
        // colunas, é muito byte atravessando a rede à toa.
        var internas = new[] { "id", "_ts", "_deleted" };
        var lidas = internas.All(presentes.Contains)
            ? string.Join(", ", pedidas.Where(presentes.Contains).Concat(internas)
                                       .Distinct(StringComparer.OrdinalIgnoreCase))
            : "*";   // arquivo fora do padrão: lê tudo, correção antes de economia

        var order = string.IsNullOrWhiteSpace(orderBy) ? "" : $" ORDER BY {orderBy}";
        var sql = $@"
SELECT {cols}
FROM (
    SELECT {lidas}, row_number() OVER (PARTITION BY id ORDER BY _ts DESC) AS _rn
    FROM read_parquet('{glob}', union_by_name=true)
)
WHERE _rn = 1 AND NOT _deleted{order};";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public bool IsEmpty(string entity)
    {
        var dir = EntityDir(entity);
        return !Directory.EnumerateFiles(dir, "*.parquet").Any();
    }

    /// <summary>Quantidade de arquivos Parquet da entidade (para decidir compactação).</summary>
    public int FileCount(string entity)
    {
        var dir = EntityDir(entity);
        return Directory.EnumerateFiles(dir, "*.parquet").Count();
    }

    /// <summary>Nomes das entidades existentes (subpastas com Parquet) — para compactar todas.</summary>
    public IEnumerable<string> Entities()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(Folder)
            .Select(d => Path.GetFileName(d))
            .Where(n => !string.IsNullOrEmpty(n))!;
    }

    /// <summary>
    /// Compacta a entidade: consolida a versão mais recente de cada id (sem os
    /// apagados) num ÚNICO Parquet novo e remove os arquivos antigos.
    ///
    /// Seguro para leituras concorrentes: grava primeiro um arquivo temporário
    /// (fora do padrão *.parquet), promove-o a Parquet final e só então apaga os
    /// arquivos capturados no início. Durante a janela em que ambos existem, a
    /// consolidação por id/_ts descarta as duplicatas idênticas — nada se perde.
    /// Retorna o nº de arquivos após a operação (0 se não havia nada a compactar).
    /// </summary>
    public int Compact(string entity)
    {
        var dir = EntityDir(entity);
        var oldFiles = Directory.EnumerateFiles(dir, "*.parquet").ToList();
        if (oldFiles.Count <= 1) return oldFiles.Count;

        var glob = Duck(Path.Combine(dir, "*.parquet"));
        var tmpPath = Path.Combine(dir, $"_compact_{DateTime.UtcNow.Ticks:D19}_{Guid.NewGuid():N}.tmp");
        var tmpDuck = Duck(tmpPath);

        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
COPY (
    SELECT * EXCLUDE (_rn)
    FROM (
        SELECT *, row_number() OVER (PARTITION BY id ORDER BY _ts DESC) AS _rn
        FROM read_parquet('{glob}', union_by_name=true)
    )
    WHERE _rn = 1 AND NOT _deleted
) TO '{tmpDuck}' (FORMAT PARQUET, COMPRESSION ZSTD);";
            cmd.ExecuteNonQuery();
        }

        // Promove o temporário a Parquet final (passa a valer para os leitores).
        var finalName = Path.Combine(dir, $"{DateTime.UtcNow.Ticks:D19}_{Guid.NewGuid():N}.parquet");
        File.Move(tmpPath, finalName);

        // Os arquivos capturados no início saem de circulação — mas vão para o
        // histórico em vez de sumir. A compactação só guarda a versão mais recente
        // de cada id; se alguma coisa for sobrescrita por engano, é daqui que se
        // recupera o estado anterior.
        Arquivar(dir, oldFiles);
        return 1;
    }

    // Pasta de histórico da entidade (não é lida pelas consultas: o glob de
    // leitura é "<entidade>/*.parquet", sem entrar em subpastas).
    private const string HistDir = "_historico";
    private static readonly TimeSpan HistRetencao = TimeSpan.FromDays(30);

    private static void Arquivar(string dir, List<string> files)
    {
        string? destino = null;
        try
        {
            destino = Path.Combine(dir, HistDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(destino);
        }
        catch { /* sem permissão para criar o histórico: segue apagando */ }

        foreach (var f in files)
        {
            try
            {
                if (destino is not null) File.Move(f, Path.Combine(destino, Path.GetFileName(f)));
                else File.Delete(f);
            }
            catch { /* em uso por outro leitor: ignora, próxima rodada limpa */ }
        }

        // Poda: histórico serve para socorro recente, não para crescer sem fim.
        try
        {
            var raiz = Path.Combine(dir, HistDir);
            if (!Directory.Exists(raiz)) return;
            var limite = DateTime.UtcNow - HistRetencao;
            foreach (var d in Directory.EnumerateDirectories(raiz))
                if (Directory.GetCreationTimeUtc(d) < limite)
                    try { Directory.Delete(d, recursive: true); } catch { }
        }
        catch { /* poda é oportunista */ }
    }

    /// <summary>
    /// Apaga todos os Parquet da entidade (usado pela importação em modo "substituir").
    /// </summary>
    public void Clear(string entity)
    {
        var dir = EntityDir(entity);
        // Vai para o histórico, não para o vazio: apagar a base é justamente o
        // tipo de operação de que alguém se arrepende dez minutos depois.
        Arquivar(dir, Directory.EnumerateFiles(dir, "*.parquet").ToList());
    }
}
