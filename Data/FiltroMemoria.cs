namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Guarda o recorte de cada tela enquanto a pessoa está com o programa aberto.
//
// Abrir uma proposta sai da listagem, e voltar recria a tela do zero — com os
// filtros no padrão. Quem montou um recorte de vários filtros para analisar a
// carteira perdia tudo a cada ida e volta, e refazia na mão.
//
// O escopo é o circuito: vale para a sessão daquela pessoa naquele navegador e
// morre com ela. Não vai para a rede, não é gravado em lugar nenhum e não se
// mistura entre usuários — é memória de curto prazo, não configuração.
// ---------------------------------------------------------------------------
public sealed class FiltroMemoria
{
    private readonly Dictionary<string, object> _telas = new(StringComparer.Ordinal);

    public T? Ler<T>(string tela) where T : class =>
        _telas.TryGetValue(tela, out var v) ? v as T : null;

    public void Guardar(string tela, object recorte) => _telas[tela] = recorte;
}
