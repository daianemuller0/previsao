# Previsão · Aftermarket Intelligence

Starter de projeto com a **identidade visual** reaproveitada do sistema de Licenças
(Howden Aftermarket Intelligence). Serve como ponto de partida para construir a nova
aplicação já com o mesmo design system.

## Como abrir

É um projeto estático — basta abrir `index.html` no navegador (ou servir a pasta):

```bash
python3 -m http.server 8080
# depois acesse http://localhost:8080
```

## Estrutura

```
index.html   Shell de exemplo (sidebar + topbar + painel demonstrando os componentes)
app.css      Design system completo (tokens, layout, componentes)
```

## Design system

### Paleta (CSS variables em `:root`)

| Token              | Cor        | Uso                                  |
| ------------------ | ---------- | ------------------------------------ |
| `--primary`        | `#004785`  | Azul principal — títulos, botões     |
| `--primary-dark`   | `#06345d`  | Hover de botões primários            |
| `--secondary`      | `#007A9E`  | Links, foco de inputs, chips ativos  |
| `--accent`         | `#009496`  | Destaques, botão "accent" (teal)     |
| `--sidebar-top`    | `#14315c`  | Topo do gradiente da sidebar         |
| `--sidebar-bottom` | `#172d54`  | Base do gradiente da sidebar         |
| `--sidebar-active` | `#2f62b4`  | Item de menu ativo                   |
| `--bg`             | `#f2f5f9`  | Fundo da página                      |
| `--panel`          | `#ffffff`  | Cards e painéis                      |
| `--border`         | `#e4e9f1`  | Bordas                               |
| `--text`           | `#1c2a3a`  | Texto principal                      |
| `--muted`          | `#64748b`  | Texto secundário                     |

Raios: `--radius: 14px`, `--radius-sm: 10px`. Sombras: `--shadow-sm`, `--shadow`.

### Tipografia

`"Segoe UI", system-ui, -apple-system, sans-serif` — base **14px**.

### Componentes prontos (classes)

- **Layout:** `.app-shell`, `.sidebar`, `.topbar`, `.content`
- **Marca:** `.brand`, `.brand-mark`, `.brand-name`, `.brand-sub`
- **Navegação:** `.nav`, `.nav-item` (`.active`), `.nav-section`
- **Cards / KPIs:** `.cards` + `.card`, `.kpis` + `.kpi` (variações `.k-ren`, `.k-urg`, `.k-aten`, `.k-risk`…)
- **Tabelas:** `.grid` (com `th`/`td`, `.right`, `.center`, `.nowrap`)
- **Badges de status:** `.badge` + `.st-ok`, `.st-atencao`, `.st-urgente`, `.st-critica`, `.st-vencida`, `.st-plan`
- **Botões:** `.btn-primary`, `.btn-ghost`, `.btn-accent`, `.btn-link`, `.btn-routine`
- **Faixa de destaque:** `.routine` (usa o gradiente da marca)
- **Abas / chips / filtros:** `.tabs` + `.tab`, `.views` + `.view`, `.chips` + `.chip`
- **Barras de progresso:** `.bar-row`, `.bar-track`, `.bar`
- **Modal e login:** `.modal-backdrop` + `.modal`, `.login-wrap` + `.login-card`

Consulte `index.html` para exemplos de uso de cada bloco.

---

Identidade visual originada do repositório `daianemuller0/Licencas_HSA`.
