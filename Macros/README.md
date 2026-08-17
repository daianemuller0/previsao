# Macro: salvar anexos de previsão do Outlook

Salva automaticamente os anexos Excel dos 6 relatórios de previsão nas pastas da rede,
sempre com **nome fixo** (sobrescreve o arquivo anterior).

| Assunto do e-mail | Pasta | Arquivo salvo |
|---|---|---|
| `Report results (FUP-HSA_Salesforce)` | `...\previsao\FUP\` | `FUP_NB.xlsx` |
| `Report results (NB_WON_SF)` | `...\previsao\FUP\` | `WON_NB.xlsx` |
| `Report results (Funil de Vendas HSA)` | `...\previsao\NB\` | `Funil_NB.xlsx` |
| `AFM_FUP (Daily)` | `...\previsao\FUP\` | `FUP_AFM.xlsx` |
| `AFM_Won (Daily)` | `...\previsao\FUP\` | `WON_AFM.xlsx` |
| `Funil_AFM (Daily)` | `...\previsao\planilha\AFM\` | `Funil_AFM.xlsx` |

---

## Por que a sua macro não estava rodando

O código em si estava correto. O problema é **como ela é acionada**.

Uma `Sub` com a assinatura `SalvarPrevisao(MItem As Outlook.MailItem)` nunca roda sozinha —
ela só é chamada pela ação **"executar um script"** de uma regra do Outlook. E aí vem o
motivo principal:

1. **A ação "executar um script" foi desativada pela Microsoft.** Desde as atualizações de
   segurança de 2019 (Outlook 2016/2019/365), essa ação some da lista de regras e as regras
   existentes param de disparar. Ela só volta com uma chave de registro (ver abaixo).
2. **Segurança de macros.** Se estiver em "Desabilitar todas as macros", nada roda.
   Arquivo → Opções → Central de Confiabilidade → Configurações → Configurações de Macro.
3. **A macro precisa estar em `ThisOutlookSession`** para ser vista pela regra. Num módulo
   comum, a regra não a enxerga.
4. **Caminho de rede indisponível.** Como o código original não tem tratamento de erro, se o
   `\\BZVCPFIL003\...` não estiver acessível a macro morre em silêncio — parece "não rodar".
5. **Assunto diferente do esperado.** Um acento, um prefixo `RES:`/`ENC:` ou espaço extra já
   faz cair no `Case Else` e sair sem fazer nada.
6. **Arquivo aberto no Excel.** Se alguém estiver com `FUP_NB.xlsx` aberto, o `SaveAsFile`
   falha — e, sem tratamento, some sem aviso.

A versão nova resolve tudo isso: **não depende de regra**, valida a pasta, trata erro,
grava log e tem rotinas de diagnóstico.

---

## Instalação

1. Outlook → `Alt + F11` (abre o editor VBA).
2. Menu **Inserir → Módulo**. Cole todo o conteúdo de **`modPrevisao.bas`**.
3. No painel da esquerda, dê duplo clique em **`ThisOutlookSession`** e cole o conteúdo de
   **`ThisOutlookSession.txt`**.
4. Salve (`Ctrl + S`).
5. Libere as macros: **Arquivo → Opções → Central de Confiabilidade → Configurações da
   Central de Confiabilidade → Configurações de Macro** → *"Notificações para todas as
   macros"* (ou assine o projeto digitalmente, se a TI exigir).
6. Feche e reabra o Outlook.

---

## Antes de tudo: teste

No editor VBA, clique dentro da Sub e aperte `F5`.

| Rotina | Para que serve |
|---|---|
| `TestarPastas` | Confirma se os 3 caminhos de rede estão acessíveis. **Rode esta primeiro.** |
| `ListarAssuntos` | Lista os assuntos recebidos e marca quais bateram no mapeamento. Use se a pasta está OK mas nada é salvo. |
| `VarrerCaixaDeEntrada` | Faz o trabalho: varre os últimos 7 dias e salva tudo. |

Se `TestarPastas` mostrar **NÃO ACESSÍVEL**, o problema é rede/permissão, não a macro —
abra o caminho no Explorer para confirmar.

---

## As 3 formas de acionar

### 1. Varredura manual — a mais confiável (recomendada)

Rode `VarrerCaixaDeEntrada` quando quiser. Ela varre os últimos 7 dias, salva tudo o que
bater no mapeamento e mostra um resumo no final. Não depende de regra nem de evento.

Para agendar: crie uma tarefa no **Agendador de Tarefas do Windows** apontando para um
`.vbs` com o conteúdo abaixo (o Outlook precisa estar aberto ou será aberto):

```vbs
Set oApp = CreateObject("Outlook.Application")
oApp.Session.Logon
oApp.Application.Run "modPrevisao.VarrerCaixaDeEntrada"
```

### 2. Automático ao chegar o e-mail (`ItemAdd`)

É o que o `ThisOutlookSession.txt` faz. Com o Outlook aberto, todo e-mail novo que cai na
Caixa de Entrada passa pela macro. **Importante:** se uma regra move o e-mail para uma
subpasta antes de você ver, o `ItemAdd` da Caixa de Entrada pode não disparar — nesse caso
use a varredura manual, ou aponte o monitor para a subpasta.

### 3. Regra "executar um script" (só se a TI liberar)

Precisa desta chave de registro (peça para a TI — mexer no registro costuma exigir admin):

```
HKEY_CURRENT_USER\Software\Microsoft\Office\16.0\Outlook\Security
    EnableUnsafeClientMailRules  (DWORD) = 1
```

`16.0` vale para Outlook 2016/2019/365. Depois, na regra, escolha
`RegraSalvarPrevisao` (o wrapper já incluído no módulo).

> A Microsoft desativou isso por segurança e pode voltar a bloquear em atualizações
> futuras. Por isso a varredura manual é o caminho mais estável.

---

## Ajustes comuns

Tudo no topo de `modPrevisao.bas`:

- **Pastas de destino** — constantes `PASTA_NB`, `PASTA_AFM`, `PASTA_FUP`.
- **Janela da varredura** — `DIAS_VARREDURA` (padrão 7).
- **Log** — `ARQUIVO_LOG`. Deixe `""` para não gravar. Registra cada salvamento, falha e
  e-mail sem anexo, com data/hora.
- **Assuntos** — o `Select Case` dentro de `SalvarPrevisao`. A comparação é por
  *"contém"* e ignora maiúsculas/minúsculas, então `RES:` e `ENC:` na frente não atrapalham.

Outras diferenças em relação à versão original: aceita também `.xlsm` e `.csv`, ignora
imagens embutidas na assinatura, trata anexo sem extensão, e avisa (no log) quando o
arquivo de destino está travado por alguém com a planilha aberta.
