# Howden Sales Forecast

Sistema web de **gestão e previsão de vendas** da Howden Brasil — substitui o
processo atual em Excel por uma plataforma corporativa de *revenue intelligence*.

Construído com a **mesma stack e arquitetura do projeto de Licenças**
(`Licencas_HSA`): C# / .NET 8 / Blazor Server, com persistência em **arquivos
Parquet consolidados pelo DuckDB** — sem servidor de banco de dados.

> ⚠️ **Compilação:** este ambiente não possui o SDK .NET (download bloqueado por
> política de rede), então o projeto **não foi compilado aqui**. O código segue
> fielmente as convenções já comprovadas do projeto de Licenças. Rode localmente:
> `dotnet run` (requer .NET 8 SDK). Podem ser necessários pequenos ajustes de
> compilação — veja o roadmap.

## Como rodar

```bash
dotnet run
# abre em http://localhost:5080  ·  login: howden / howden2026
```

O perfil escolhido no login (Diretoria, Gestor, KAM, Financeiro, Visualizador,
Admin) adapta a navegação aos três níveis de experiência.

## Stack

| Camada | Tecnologia |
|---|---|
| Runtime | .NET 8, ASP.NET Core |
| UI | Blazor Server (Razor Components, render interativo no servidor) |
| Dados | DuckDB em memória sobre Parquet (`DuckDB.NET.Data.Full` 1.1.3) |
| Importação | `ClosedXML` 0.102.2 (leitura de `.xlsx`) |
| Autenticação | Cookie + perfis de acesso |
| Estilo | CSS puro (`wwwroot/app.css`), identidade visual Howden |

## Arquitetura em camadas

```
Program.cs            Composição: DI, autenticação, seed, endpoints (login, export CSV)
Components/
  Layout/             MainLayout (topbar) + NavMenu (11 áreas, recolhível, por perfil)
  Shared/             Componentes reutilizáveis: StatCard, Sparkline, Donut, Waterfall
  Pages/              Telas (11 áreas do menu + detalhe da oportunidade + login)
Data/
  ParquetStore.cs     Núcleo da persistência (DuckDB sobre Parquet, append-only)
  Catalog.cs          Dados-mestre (mercados, produtos, KAMs, clientes, metas…)
  ForecastCalc.cs     Regras de negócio (ponderado, margem, cobertura, gap, risco)
  Fmt.cs              Formatação corporativa (USD, BRL, %, datas, quarter)
  DemoSeed.cs         40 oportunidades demonstrativas realistas
  OpportunityRepository.cs / DbInitializer.cs
Models/               Opportunity, Catalog (entidades), ForecastCategory, Roles
Icons.cs              Conjunto central de ícones SVG
```

- **UI**: páginas Razor `InteractiveServer`; todas exigem login (`[Authorize]`).
- **Domínio**: classes estáticas puras (`ForecastCalc`, `Fmt`).
- **Dados**: repositório fino sobre um `ParquetStore` singleton; catálogo de
  dados-mestre injetado como singleton.

### Cálculos do forecast (`ForecastCalc`)

| Métrica | Fórmula |
|---|---|
| Valor USD | Valor BRL ÷ taxa de câmbio (ou valor direto em USD) |
| Forecast ponderado | valor × prob. de ganho × prob. de fechamento no período |
| Margem prevista | valor × GM% |
| Pipeline coverage | pipeline elegível ÷ meta restante |
| Gap para meta | meta − realizado − forecast elegível |
| Score de risco | composto (probabilidade, tempo sem atualização, postergações, ausência de ação, margem, divergência vendedor×gestor) → Baixo/Moderado/Alto/Crítico |

### Forecast Categories

Commit · Best Case · Pipeline · Upside · Risk · Closed Won · Closed Lost · Postponed
(cores discretas e corporativas).

## Telas implementadas

| Rota | Tela | Status |
|---|---|---|
| `/executivo` | **Visão Executiva** — 12 KPIs, Forecast×Meta×Realizado, cobertura, mercado/produto/KAM, waterfall de variação, Executive Attention | ✅ completa |
| `/forecast` | **Forecast** — tabela com agrupamento, subtotais, ordenação, filtros e edição rápida (persistência) | ✅ completa |
| `/pipeline` | **Pipeline** — funil por etapa, conversão, aging, distribuição | ✅ completa |
| `/oportunidades` | **Oportunidades** — lista com filtros + cadastro | ✅ completa |
| `/oportunidades/{id}` | **Detalhe** — 8 abas (resumo, comercial, valores, forecast, atividades, riscos, histórico, documentos) | ✅ completa |
| `/revisao` | **Revisão Comercial** — revisão por KAM + modo reunião | ✅ completa |
| `/analises` | **Análises** — win rate, bias, accuracy, ciclo, performance | ✅ completa |
| `/historico` | **Histórico** — snapshots e comparação | ◑ base |
| `/clientes` | **Clientes e Plantas** | ✅ completa |
| `/importacoes` | **Importação** — fluxo, mapeamento e validações | ◑ UI (parser a conectar) |
| `/cadastros` | **Cadastros mestres** | ✅ completa |
| `/administracao` | **Administração** — perfis e governança | ✅ completa |

## Roadmap (próximos passos)

- **Compilar e validar** localmente (`dotnet build`); ajustar detalhes se necessário.
- **Persistir snapshots** de forecast (hoje a comparação é indicativa).
- **Conectar o parser do Excel** (ClosedXML) ao módulo de Importação, reaproveitando
  o `LicenseImport` do projeto de Licenças.
- **Filtros globais** funcionais (hoje demonstrativos na Visão Executiva).
- **Persistir dados-mestre** dos Cadastros (hoje em catálogo em memória).
- **Controle de acesso no nível dos dados** (cada KAM vê sua carteira).

---

Identidade visual e arquitetura derivadas do repositório `daianemuller0/Licencas_HSA`.
