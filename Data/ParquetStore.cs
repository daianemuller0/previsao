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
        Folder = Path.GetFullPath(folder);
        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Não foi possível acessar a pasta de dados '{Folder}'. " +
                "Verifique a chave \"Data:Folder\" no appsettings e se o caminho de rede " +
                "está acessível a partir desta máquina.", ex);
        }
    }

    private string EntityDir(string entity)
    {
        var dir = Path.Combine(Folder, entity);
        Directory.CreateDirectory(dir);
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
            cmd.CommandText = $"COPY t TO '{full}' (FORMAT PARQUET);";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Lê a entidade já consolidada: versão mais recente por id, sem os apagados.
    /// Retorna vazio se ainda não houver nenhum arquivo (primeira execução).
    /// </summary>
    public List<T> ReadLatest<T>(string entity, string selectCols, Func<IDataReader, T> map, string orderBy = "")
    {
        var dir = EntityDir(entity);
        if (!Directory.EnumerateFiles(dir, "*.parquet").Any())
            return new List<T>();

        var glob = Duck(Path.Combine(dir, "*.parquet"));
        using var conn = Open();

        // Evolução de esquema: arquivos antigos podem não ter colunas novas.
        // union_by_name junta esquemas diferentes entre arquivos, e as colunas
        // pedidas que não existirem em NENHUM arquivo viram NULL no SELECT.
        var presentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var describe = conn.CreateCommand())
        {
            describe.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{glob}', union_by_name=true);";
            using var dr = describe.ExecuteReader();
            while (dr.Read()) presentes.Add(dr.GetString(0));
        }

        var cols = string.Join(", ", selectCols.Split(',')
            .Select(c => c.Trim())
            .Select(c => presentes.Contains(c) ? c : $"NULL AS {c}"));

        var order = string.IsNullOrWhiteSpace(orderBy) ? "" : $" ORDER BY {orderBy}";
        var sql = $@"
SELECT {cols}
FROM (
    SELECT *, row_number() OVER (PARTITION BY id ORDER BY _ts DESC) AS _rn
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
) TO '{tmpDuck}' (FORMAT PARQUET);";
            cmd.ExecuteNonQuery();
        }

        // Promove o temporário a Parquet final (passa a valer para os leitores).
        var finalName = Path.Combine(dir, $"{DateTime.UtcNow.Ticks:D19}_{Guid.NewGuid():N}.parquet");
        File.Move(tmpPath, finalName);

        // Remove apenas os arquivos existentes no início (preserva escritas novas).
        foreach (var f in oldFiles)
        {
            try { File.Delete(f); } catch { /* em uso por outro leitor: ignora, próxima rodada limpa */ }
        }
        return 1;
    }

    /// <summary>
    /// Apaga todos os Parquet da entidade (usado pela importação em modo "substituir").
    /// </summary>
    public void Clear(string entity)
    {
        var dir = EntityDir(entity);
        foreach (var f in Directory.EnumerateFiles(dir, "*.parquet"))
            File.Delete(f);
    }
}
