namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Leitura de arquivo da rede que NÃO atrapalha quem grava.
//
// As planilhas chegam por uma macro do Outlook que exporta e salva na pasta.
// Se o nosso programa estiver com o arquivo aberto no instante exato em que a
// macro salva, o Excel pode falhar ao gravar (ele troca o arquivo: grava um
// temporário, apaga o antigo e renomeia). Daí as regras aqui:
//
//  • abre só para leitura, compartilhando leitura, escrita E exclusão
//    (FileShare.Delete): a macro consegue apagar/renomear mesmo com o nosso
//    handle aberto — nós seguimos lendo a cópia antiga, que é consistente;
//  • copia os bytes para a memória e fecha na hora: o arquivo fica preso só
//    pelo tempo da cópia, nunca pelo tempo da leitura da planilha;
//  • se o arquivo estiver sendo gravado naquele instante (IOException), espera
//    um pouco e tenta de novo, poucas vezes — quem cede somos nós, não a macro.
// ---------------------------------------------------------------------------
public static class LeituraSegura
{
    private const int Tentativas = 4;
    private static readonly TimeSpan Espera = TimeSpan.FromMilliseconds(700);

    /// <summary>Conteúdo inteiro do arquivo, lido com o mínimo de interferência.</summary>
    public static byte[] Bytes(string path)
    {
        IOException? ultima = null;
        for (var i = 0; i < Tentativas; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 16, FileOptions.SequentialScan);
                using var ms = new MemoryStream(fs.CanSeek && fs.Length < int.MaxValue ? (int)fs.Length : 0);
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException ex)
            {
                ultima = ex;
                if (i < Tentativas - 1) Thread.Sleep(Espera);
            }
        }
        throw ultima ?? new IOException("Falha ao ler " + path);
    }
}
