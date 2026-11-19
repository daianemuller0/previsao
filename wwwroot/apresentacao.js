// ===========================================================================
// Performance Comercial → apresentação PowerPoint (.pptx)
// Monta os slides no navegador com o PptxGenJS (wwwroot/lib/pptxgen.bundle.js,
// carregado só na hora). Gráficos nativos do PowerPoint (editáveis, com os
// dados na planilha do próprio gráfico) e tabelas com os números da tela.
// Os dados chegam prontos do Blazor (DadosApresentacao, em VendasPropostas.razor).
// ===========================================================================
window.vpPptx = (function () {
    const W = 13.333, H = 7.5;                       // LAYOUT_WIDE (16:9)
    const FONTE = 'Segoe UI';
    const NAVY = '12355B', TXT = '172B43', MUT = '64748B', LINHA = 'E1E8F0', FUNDO = 'F3F6FA';
    const CLS = { ganha: '1F9254', perdida: '6FB3DC', aberta: '007A9E' };

    function carregarLib() {
        if (window.PptxGenJS) return Promise.resolve();
        return new Promise((ok, erro) => {
            const s = document.createElement('script');
            s.src = 'lib/pptxgen.bundle.js';
            s.onload = () => ok();
            s.onerror = () => erro(new Error('não foi possível carregar a biblioteca do PowerPoint'));
            document.head.appendChild(s);
        });
    }

    const n0 = v => v == null ? '—' : Number(v).toLocaleString('pt-BR', { maximumFractionDigits: 0 });
    const p1 = v => v == null ? '—' : Number(v).toLocaleString('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: 1 }) + '%';
    const mi = v => v == null ? '—' : 'R$ ' + Number(v).toLocaleString('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: 1 }) + ' mi';
    const corDelta = d => !d ? MUT : d.startsWith('+0') || d.startsWith('0') ? MUT : d.startsWith('+') ? '1F9254' : 'B42318';
    const seta = d => !d ? '' : d.startsWith('+0') || d.startsWith('0') ? d : (d.startsWith('+') ? '▲ ' : d.startsWith('-') ? '▼ ' : '') + d;

    function gerar(d) {
        return carregarLib().then(() => montar(d));
    }

    function montar(d) {
        const pptx = new PptxGenJS();
        pptx.layout = 'LAYOUT_WIDE';
        pptx.author = 'Howden Sales Forecast';
        pptx.company = 'Howden South America';
        pptx.title = d.titulo;
        pptx.theme = { headFontFace: FONTE, bodyFontFace: FONTE };

        pptx.defineSlideMaster({
            title: 'VP',
            background: { color: 'FFFFFF' },
            objects: [
                { rect: { x: 0, y: 0, w: W, h: 0.09, fill: { color: NAVY } } },
                { line: { x: 0.45, y: 7.0, w: W - 0.9, h: 0, line: { color: LINHA, width: 0.75 } } },
                { text: { text: 'Howden South America · Performance Comercial · ' + d.geradoEm, options: { x: 0.45, y: 7.05, w: 9, h: 0.3, fontFace: FONTE, fontSize: 9, color: '93A1B5' } } },
            ],
            slideNumber: { x: W - 1.0, y: 7.05, w: 0.6, h: 0.3, fontFace: FONTE, fontSize: 9, color: '93A1B5', align: 'right' },
        });

        const titulo = (s, t, sub) => {
            s.addText(t, { x: 0.45, y: 0.25, w: W - 0.9, h: 0.55, fontFace: FONTE, fontSize: 24, bold: true, color: NAVY, margin: 0 });
            if (sub) s.addText(sub, { x: 0.45, y: 0.8, w: W - 0.9, h: 0.35, fontFace: FONTE, fontSize: 12, color: MUT, margin: 0 });
        };
        const nota = (s, t, y) => s.addText(t, { x: 0.45, y: y ?? 6.55, w: W - 0.9, h: 0.4, fontFace: FONTE, fontSize: 10, color: MUT, margin: 0, valign: 'top' });
        const slide = () => pptx.addSlide({ masterName: 'VP' });

        const anosStr = d.anos.map(String);
        const corAno = a => d.coresAnos[a] || '8DA2BC';
        const serieAnos = mapa => anosStr.map(a => ({ name: a, labels: d.mensal.meses, values: (mapa[a] || []).map(v => v == null ? null : v) }));

        // ---------- 1. capa ----------
        {
            const s = pptx.addSlide();
            s.background = { color: NAVY };
            s.addShape(pptx.ShapeType.rect, { x: 0, y: 5.9, w: W, h: 1.6, fill: { color: '0E2A4A' } });
            s.addText('PERFORMANCE COMERCIAL', { x: 0.8, y: 1.5, w: 11, h: 0.4, fontFace: FONTE, fontSize: 14, bold: true, color: '8DB4E2', charSpacing: 4 });
            s.addText(d.titulo, { x: 0.8, y: 2.0, w: 11.8, h: 1.2, fontFace: FONTE, fontSize: 40, bold: true, color: 'FFFFFF' });
            s.addText(d.subtitulo, { x: 0.8, y: 3.2, w: 11.8, h: 0.6, fontFace: FONTE, fontSize: 20, color: 'D6E2F0' });
            s.addText(d.periodo, { x: 0.8, y: 4.1, w: 11.8, h: 0.4, fontFace: FONTE, fontSize: 14, color: 'B9C9DD' });
            s.addText([
                { text: 'Filtros: ', options: { bold: true } }, { text: d.filtros, options: { breakLine: true } },
                { text: 'Taxa de conversão sobre: ', options: { bold: true } }, { text: d.baseCurta, options: { breakLine: true } },
                { text: 'Gerado em ' + d.geradoEm },
            ], { x: 0.8, y: 6.05, w: 11.8, h: 1.2, fontFace: FONTE, fontSize: 12, color: 'D6E2F0', valign: 'top' });
        }

        // ---------- 2. indicadores do ano em foco ----------
        {
            const s = slide();
            titulo(s, 'Indicadores de ' + d.foco, 'Comparações com o mesmo período do ano anterior · ' + d.filtros);
            const bw = 3.95, bh = 2.35, gx = 0.25, gy = 0.3, x0 = 0.45, y0 = 1.45;
            d.kpis.forEach((k, i) => {
                const x = x0 + (i % 3) * (bw + gx), y = y0 + Math.floor(i / 3) * (bh + gy);
                s.addShape(pptx.ShapeType.roundRect, { x, y, w: bw, h: bh, fill: { color: 'FFFFFF' }, line: { color: LINHA, width: 1 }, rectRadius: 0.08 });
                s.addShape(pptx.ShapeType.rect, { x, y: y + 0.08, w: 0.07, h: bh - 0.16, fill: { color: k.cor } });
                s.addText(k.rot.toUpperCase(), { x: x + 0.25, y: y + 0.18, w: bw - 0.4, h: 0.35, fontFace: FONTE, fontSize: 11, bold: true, color: MUT, margin: 0 });
                s.addText(k.valor, { x: x + 0.25, y: y + 0.55, w: bw - 0.4, h: 0.8, fontFace: FONTE, fontSize: 32, bold: true, color: TXT, margin: 0 });
                s.addText(k.sub || '', { x: x + 0.25, y: y + 1.38, w: bw - 0.4, h: 0.45, fontFace: FONTE, fontSize: 11, color: MUT, margin: 0, valign: 'top' });
                if (k.delta) s.addText(seta(k.delta), { x: x + 0.25, y: y + 1.85, w: bw - 0.4, h: 0.35, fontFace: FONTE, fontSize: 12, bold: true, color: corDelta(k.delta), margin: 0 });
            });
            nota(s, d.regra);
        }

        // ---------- 3–6. séries mensais (uma linha por ano) ----------
        const mensal = (t, sub, mapa, eixo, fmt) => {
            const s = slide();
            titulo(s, t, sub);
            s.addChart(pptx.ChartType.line, serieAnos(mapa), {
                x: 0.45, y: 1.3, w: W - 0.9, h: 4.6,
                chartColors: anosStr.map(corAno), lineSize: 2.25, lineDataSymbol: 'circle', lineDataSymbolSize: 6,
                displayBlanksAs: 'gap', showLegend: true, legendPos: 't', legendFontSize: 12, legendFontFace: FONTE,
                catAxisLabelFontSize: 12, catAxisLabelFontFace: FONTE, catAxisLabelColor: MUT,
                valAxisLabelFontSize: 11, valAxisLabelFontFace: FONTE, valAxisLabelColor: MUT, valAxisLabelFormatCode: eixo,
                valGridLine: { color: 'EEF2F7', size: 0.75 }, catGridLine: { style: 'none' },
            });
            // totais de cada ano, embaixo
            const tot = anosStr.map(a => {
                const vs = (mapa[a] || []).filter(v => v != null);
                return { a, v: vs.length ? fmt(vs) : '—' };
            });
            s.addTable([[{ text: 'Ano', options: { bold: true, color: MUT } }, ...tot.map(x => ({ text: x.a, options: { bold: true, color: corAno(x.a), align: 'center' } }))],
                        [{ text: 'No ano', options: { color: MUT } }, ...tot.map(x => ({ text: x.v, options: { bold: true, color: TXT, align: 'center' } }))]],
                { x: 0.45, y: 6.0, w: W - 0.9, fontFace: FONTE, fontSize: 12, border: { type: 'none' }, rowH: 0.26 });
        };
        const soma = vs => vs.reduce((a, b) => a + b, 0);
        mensal('Propostas enviadas por mês', 'Uma linha por ano · mês de envio da proposta · ' + d.filtros, d.mensal.qtd, '#,##0', vs => n0(soma(vs)));
        mensal('Valor enviado por mês (R$ milhões)', 'Uma linha por ano · soma do valor das propostas enviadas no mês', d.mensal.valor, '#,##0.0', vs => mi(soma(vs)));
        mensal('Vendas por mês (R$ milhões)', 'Uma linha por ano · vendas da empresa no mês (não seguem os filtros)', d.mensal.vendas, '#,##0.0', vs => mi(soma(vs)));
        mensal('Taxa de conversão por mês (%)', 'Uma linha por ano · ' + (d.baseCurta === 'decididas' ? 'ganhas ÷ decididas do mês' : 'ganhas ÷ todas as propostas enviadas no mês'), d.mensal.taxa, '0"%"',
            vs => p1(soma(vs) / vs.length) + ' (média)');

        // ---------- 7. taxa de conversão e resultados, ano a ano (tabela) ----------
        {
            const s = slide();
            titulo(s, 'Taxa de conversão e resultados, ano a ano', d.regra);
            const cab = [{ text: '', options: {} }, ...d.anoAno.map(a => ({ text: a.ano + (a.periodo ? '\n' + a.periodo : ''), options: { bold: true, color: 'FFFFFF', fill: { color: corAno(String(a.ano)) }, align: 'center' } }))];
            const lin = (rot, f, opt) => [{ text: rot, options: Object.assign({ color: TXT }, opt || {}) }, ...d.anoAno.map(a => ({ text: f(a), options: Object.assign({ align: 'center', color: TXT }, opt || {}) }))];
            const destaque = { bold: true, fontSize: 16, fill: { color: 'F2F6FB' } };
            const rows = [
                cab,
                lin('Taxa de conversão', a => p1(a.taxa), destaque),
                lin('Variação (p.p.)', a => a.delta ? seta(a.delta) : '—'),
                lin('Ganhas / ' + d.denRot, a => n0(a.ganhas) + ' / ' + n0(a.base)),
                lin('Propostas enviadas', a => n0(a.total), { bold: true }),
                lin('Ganhas · Perdidas · Abertas', a => n0(a.ganhas) + ' · ' + n0(a.perdidas) + ' · ' + n0(a.abertas)),
                lin('AFM: taxa · propostas', a => p1(a.afm.taxa) + ' · ' + n0(a.afm.total)),
                lin('NB: taxa · propostas', a => p1(a.nb.taxa) + ' · ' + n0(a.nb.total)),
                lin('Valor ganho · por valor', a => a.valorGanho + ' · ' + a.porValor),
                lin('Valor enviado', a => a.valorEnviado + (a.parcial ? ' (parcial)' : '')),
                lin('Valor médio', a => a.valorMedio),
                lin('Vendas', a => a.vendas + (a.periodo ? ' (' + a.vendasRot.replace('Vendas ', '').replace(/[()]/g, '') + ')' : ''), { bold: true }),
            ];
            s.addTable(rows, { x: 0.45, y: 1.35, w: W - 0.9, colW: [2.9, ...d.anoAno.map(() => (W - 0.9 - 2.9) / d.anoAno.length)],
                fontFace: FONTE, fontSize: 12, border: { type: 'solid', color: LINHA, pt: 0.75 }, rowH: 0.4, valign: 'middle' });
        }

        // ---------- 8. taxa de conversão por ano: total, AFM e NB ----------
        {
            const s = slide();
            titulo(s, 'Taxa de conversão por ano · total, AFM e NB', 'Taxa sobre ' + d.baseCurta + ' · ' + d.filtros);
            const labels = d.anoAno.map(a => String(a.ano));
            s.addChart(pptx.ChartType.bar, [
                { name: 'Total', labels, values: d.anoAno.map(a => a.taxa) },
                { name: 'AFM', labels, values: d.anoAno.map(a => a.afm.taxa) },
                { name: 'NB', labels, values: d.anoAno.map(a => a.nb.taxa) },
            ], {
                x: 0.45, y: 1.3, w: 7.6, h: 5.1, barDir: 'col', barGrouping: 'clustered', barGapWidthPct: 60,
                chartColors: [NAVY, d.cores.afm === NAVY ? '4F7AA8' : d.cores.afm, d.cores.nb],
                showValue: true, dataLabelFormatCode: '0.0"%"', dataLabelFontSize: 10, dataLabelFontFace: FONTE, dataLabelColor: TXT, dataLabelPosition: 'outEnd',
                showLegend: true, legendPos: 't', legendFontSize: 12, legendFontFace: FONTE,
                catAxisLabelFontSize: 12, valAxisLabelFontSize: 11, valAxisLabelFormatCode: '0"%"', valAxisMinVal: 0,
                valGridLine: { color: 'EEF2F7', size: 0.75 }, catGridLine: { style: 'none' },
            });
            // propostas por setor (participação)
            s.addChart(pptx.ChartType.bar, [
                { name: 'AFM', labels, values: d.anoAno.map(a => a.afm.total) },
                { name: 'NB', labels, values: d.anoAno.map(a => a.nb.total) },
            ], {
                x: 8.3, y: 1.3, w: 4.6, h: 5.1, barDir: 'bar', barGrouping: 'percentStacked', barGapWidthPct: 50,
                chartColors: [d.cores.afm, d.cores.nb], showValue: true, dataLabelFontSize: 10, dataLabelColor: 'FFFFFF', dataLabelFormatCode: '#,##0',
                showLegend: true, legendPos: 't', legendFontSize: 12, catAxisLabelFontSize: 12, valAxisLabelFontSize: 10, valAxisLabelFormatCode: '0%',
                showTitle: true, title: 'Propostas enviadas: AFM × NB', titleFontSize: 13, titleColor: TXT,
            });
        }

        // ---------- 9. vendas totais × metas ----------
        {
            const s = slide();
            titulo(s, 'Vendas totais × metas, ano a ano', 'Vendas da empresa × Meta Grupo e Meta HSA · R$ milhões · atingimento ' + d.foco + ': Grupo ' + d.atingGrupo + ' · HSA ' + d.atingHsa);
            const labels = d.metas.map(m => m.ano + (m.periodo ? ' (' + m.periodo + ')' : ''));
            s.addChart(pptx.ChartType.bar, [
                { name: 'Vendas totais', labels, values: d.metas.map(m => m.vendas) },
                { name: 'Meta Grupo', labels, values: d.metas.map(m => m.grupo) },
                { name: 'Meta HSA', labels, values: d.metas.map(m => m.hsa) },
            ], {
                x: 0.45, y: 1.3, w: W - 0.9, h: 3.6, barDir: 'col', barGrouping: 'clustered', barGapWidthPct: 70,
                chartColors: [d.cores.vendas, d.cores.grupo, d.cores.hsa],
                showValue: true, dataLabelFormatCode: '#,##0.0', dataLabelFontSize: 10, dataLabelColor: TXT, dataLabelPosition: 'outEnd',
                showLegend: true, legendPos: 't', legendFontSize: 12, catAxisLabelFontSize: 12, valAxisLabelFontSize: 11, valAxisLabelFormatCode: '#,##0',
                valGridLine: { color: 'EEF2F7', size: 0.75 }, catGridLine: { style: 'none' },
            });
            const rows = [
                ['Ano', 'Vendas', 'Meta Grupo', 'Atingido Grupo', 'Meta HSA', 'Atingido HSA'].map(t => ({ text: t, options: { bold: true, color: 'FFFFFF', fill: { color: NAVY }, align: 'center' } })),
                ...d.metas.map(m => [m.ano + (m.periodo ? ' (' + m.periodo + ')' : ''), m.vendasTxt, m.grupoTxt, m.pctGrupo, m.hsaTxt, m.pctHsa].map((t, i) => ({ text: String(t), options: { align: i ? 'center' : 'left', bold: i === 0 || i === 3 || i === 5, color: TXT, fill: { color: m.ano === d.foco ? 'F2F6FB' : 'FFFFFF' } } }))),
            ];
            s.addTable(rows, { x: 0.45, y: 5.0, w: W - 0.9, fontFace: FONTE, fontSize: 11, border: { type: 'solid', color: LINHA, pt: 0.75 }, rowH: 0.27 });
        }

        // ---------- 10–14. rankings (BU, mercado, produto, vendedor, país) ----------
        const ranking = (t, sub, lista, rotNome) => {
            if (!lista || !lista.length) return;
            const s = slide();
            titulo(s, t, sub);
            const top = lista.slice(0, 15);
            s.addChart(pptx.ChartType.bar, [{ name: 'Taxa de conversão', labels: top.map(x => x.nome), values: top.map(x => x.taxa) }], {
                x: 0.45, y: 1.3, w: 4.85, h: 5.25, barDir: 'bar', chartColors: [NAVY], barGapWidthPct: 40,
                catAxisOrientation: 'maxMin', catAxisLabelFontSize: top.length > 10 ? 9 : 11, catAxisLabelFontFace: FONTE,
                valAxisMinVal: 0, valAxisMaxVal: 100, valAxisLabelFormatCode: '0"%"', valAxisLabelFontSize: 9,
                showValue: true, dataLabelFormatCode: '0.0"%"', dataLabelFontSize: 9, dataLabelColor: TXT, dataLabelPosition: 'outEnd',
                valGridLine: { color: 'EEF2F7', size: 0.75 }, catGridLine: { style: 'none' },
            });
            const rows = [
                ['#', rotNome, 'Prop.', 'Decid.', 'Ganhas', 'Taxa', 'Valor ganho', 'Por valor'].map((x, i) => ({ text: x, options: { bold: true, color: 'FFFFFF', fill: { color: NAVY }, align: i > 1 ? 'center' : 'left', fontSize: 9.5 } })),
                ...top.map((x, i) => [String(i + 1), x.nome, n0(x.total), n0(x.decididas), n0(x.ganhas), p1(x.taxa), x.valorGanho, p1(x.porValor)]
                    .map((v, j) => ({ text: v, options: { align: j > 1 ? 'center' : 'left', bold: j === 5, color: TXT, fill: { color: i % 2 ? 'F7F9FC' : 'FFFFFF' } } }))),
            ];
            s.addTable(rows, { x: 5.45, y: 1.3, w: 7.45, colW: [0.35, 2.4, 0.7, 0.7, 0.7, 0.75, 1.1, 0.75], fontFace: FONTE, fontSize: top.length > 10 ? 9 : 10.5,
                border: { type: 'solid', color: LINHA, pt: 0.5 }, rowH: top.length > 10 ? 0.3 : 0.36, autoPage: false });
            if (lista.length > top.length) nota(s, `Mostrando ${top.length} de ${lista.length}, da maior taxa para a menor.`, 6.65);
        };
        const subR = 'Taxa sobre ' + d.baseCurta + ' · ' + d.filtros;
        ranking('Taxa de conversão por Business Unit', subR, d.bu, 'BU');
        ranking('Taxa de conversão por mercado', subR, d.mercado, 'Mercado');
        ranking('Taxa de conversão por produto', subR, d.produto, 'Produto');
        ranking('Taxa de conversão por vendedor', subR + ' · vendedores com 5 ou mais propostas decididas', d.vendedores, 'Vendedor');
        ranking('Taxa de conversão por país', subR, d.paises, 'País');

        // ---------- 15. perdas ----------
        if (d.perdas && d.perdas.length) {
            const s = slide();
            titulo(s, 'Status das perdidas por impacto', `${n0(d.totalPerdidas)} propostas perdidas · status no CRM (não é a causa comercial) · ${d.filtros}`);
            const top = d.perdas.slice(0, 12);
            s.addChart(pptx.ChartType.bar, [{ name: 'Propostas', labels: top.map(x => x.status), values: top.map(x => x.qtd) }], {
                x: 0.45, y: 1.3, w: 6.2, h: 5.2, barDir: 'bar', chartColors: ['1D4E89'], catAxisOrientation: 'maxMin',
                catAxisLabelFontSize: 11, valAxisLabelFontSize: 9, showValue: true, dataLabelFormatCode: '#,##0', dataLabelFontSize: 10, dataLabelColor: TXT,
                valGridLine: { color: 'EEF2F7', size: 0.75 }, catGridLine: { style: 'none' },
            });
            const rows = [
                ['Status', 'Prop.', '% perdidas', 'Acumulado', 'Valor perdido'].map((x, i) => ({ text: x, options: { bold: true, color: 'FFFFFF', fill: { color: NAVY }, align: i ? 'center' : 'left' } })),
                ...top.map((x, i) => [x.status, n0(x.qtd), x.pct, x.acum, x.valor].map((v, j) => ({ text: v, options: { align: j ? 'center' : 'left', color: TXT, fill: { color: i % 2 ? 'F7F9FC' : 'FFFFFF' } } }))),
            ];
            s.addTable(rows, { x: 6.85, y: 1.3, w: 6.05, colW: [2.15, 0.85, 1.0, 0.95, 1.1], fontFace: FONTE, fontSize: 10, border: { type: 'solid', color: LINHA, pt: 0.5 }, rowH: 0.34 });
        }

        // ---------- 16. acumulado no ano × budgets ----------
        {
            const s = slide();
            titulo(s, 'Vendas acumuladas no ano × budgets de ' + d.acumulado.foco, 'R$ milhões · vendas somadas mês a mês, uma linha por ano');
            const anosAc = Object.keys(d.acumulado.anos).sort();
            const series = anosAc.map(a => ({ name: a, labels: d.mensal.meses, values: d.acumulado.anos[a] }));
            const cores = anosAc.map(a => a === d.acumulado.foco ? NAVY : a === String(Number(d.acumulado.foco) - 1) ? '4F7AA8' : 'C9D3DE');
            if (d.acumulado.budgetHsa != null) { series.push({ name: 'Budget HSA ' + d.acumulado.foco, labels: d.mensal.meses, values: d.mensal.meses.map(() => d.acumulado.budgetHsa) }); cores.push('172B43'); }
            if (d.acumulado.budgetGrupo != null) { series.push({ name: 'Budget Grupo ' + d.acumulado.foco, labels: d.mensal.meses, values: d.mensal.meses.map(() => d.acumulado.budgetGrupo) }); cores.push('8493A3'); }
            s.addChart(pptx.ChartType.line, series, {
                x: 0.45, y: 1.3, w: W - 0.9, h: 5.3, chartColors: cores, lineSize: 2, lineDataSymbol: 'none', displayBlanksAs: 'gap',
                showLegend: true, legendPos: 'r', legendFontSize: 11, catAxisLabelFontSize: 12, valAxisLabelFontSize: 11, valAxisLabelFormatCode: '#,##0',
                valGridLine: { color: 'EEF2F7', size: 0.75 }, catGridLine: { style: 'none' },
            });
        }

        // ---------- 17. vendas mês × ano ----------
        {
            const s = slide();
            titulo(s, 'Vendas mês × ano', 'Como a planilha da equipe · valores em R$ (mi)');
            const ma = d.mesAno;
            const head = [{ text: 'Mês', options: { bold: true, color: 'FFFFFF', fill: { color: NAVY } } }, ...ma.anos.map(a => ({ text: String(a), options: { bold: true, color: 'FFFFFF', fill: { color: a === d.foco ? '1D4E89' : NAVY }, align: 'center' } }))];
            const lin = (rot, vals, opt) => [{ text: rot, options: Object.assign({ bold: true, color: TXT }, opt || {}) }, ...vals.map((v, i) => ({ text: v, options: Object.assign({ align: 'center', color: TXT, fill: { color: ma.anos[i] === d.foco ? 'F2F6FB' : 'FFFFFF' } }, opt || {}) }))];
            const rows = [head, ...ma.linhas.map(l => lin(l.mes, l.valores)),
                lin('Total', ma.total, { bold: true, fill: { color: 'E9EFF7' } }),
                lin('Budget HSA', ma.budgetHsa), lin('% do budget HSA', ma.pctHsa), lin('Budget Grupo', ma.budgetGrupo), lin('% do budget Grupo', ma.pctGrupo)];
            s.addTable(rows, { x: 0.45, y: 1.25, w: W - 0.9, colW: [2.2, ...ma.anos.map(() => (W - 0.9 - 2.2) / ma.anos.length)], fontFace: FONTE, fontSize: 10.5, border: { type: 'solid', color: LINHA, pt: 0.5 }, rowH: 0.29 });
        }

        // ---------- 18. notas ----------
        {
            const s = slide();
            titulo(s, 'Notas e critérios');
            s.addText([
                { text: 'Taxa de conversão', options: { bold: true, breakLine: true } }, { text: d.regra, options: { breakLine: true } }, { text: ' ', options: { breakLine: true } },
                { text: 'Filtros aplicados', options: { bold: true, breakLine: true } }, { text: d.filtros, options: { breakLine: true } }, { text: ' ', options: { breakLine: true } },
                { text: 'Fontes', options: { bold: true, breakLine: true } }, { text: d.fonte, options: { breakLine: true } }, { text: ' ', options: { breakLine: true } },
                { text: 'Valores em R$: propostas do AFM em US$ convertidas pela taxa do ano de envio (guia Conversão → Taxas de câmbio); NB já em R$. Anos com propostas do AFM sem taxa aparecem como “parcial”.' },
            ], { x: 0.45, y: 1.2, w: W - 0.9, h: 5.4, fontFace: FONTE, fontSize: 14, color: TXT, valign: 'top', paraSpaceAfter: 4 });
        }

        return pptx.writeFile({ fileName: d.arquivo });
    }

    return { gerar };
})();
