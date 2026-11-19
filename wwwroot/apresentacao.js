// ===========================================================================
// Performance Comercial → apresentação PowerPoint (.pptx)
// Cada slide leva a FOTO em alta resolução do quadro correspondente da tela
// (mesmo desenho, cores e fontes do sistema), tirada no navegador com o
// html-to-image; os slides são montados com o PptxGenJS. As duas bibliotecas
// ficam em wwwroot/lib (funciona sem internet) e só carregam na hora.
// O Blazor (VendasPropostas.razor → ExportarPowerPoint) chama, em ordem:
//   vpPptx.inicio() → vpPptx.capturar(chave, seletor) … → vpPptx.gerar(dados) → vpPptx.fim()
// ===========================================================================
window.vpPptx = (function () {
    const W = 13.333, H = 7.5;                       // LAYOUT_WIDE (16:9)
    const FONTE = 'Segoe UI';
    const NAVY = '12355B', MUT = '64748B', LINHA = 'E1E8F0';
    let fotos = {};

    function script(src, global) {
        if (window[global]) return Promise.resolve();
        return new Promise((ok, erro) => {
            const s = document.createElement('script');
            s.src = src;
            s.onload = () => ok();
            s.onerror = () => erro(new Error('não foi possível carregar ' + src));
            document.head.appendChild(s);
        });
    }
    const quadro = () => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));

    /** Prepara a captura: carrega as bibliotecas e põe a página no "modo foto" (sem botões, listas abertas). */
    async function inicio() {
        await script('lib/html-to-image.js', 'htmlToImage');
        await script('lib/pptxgen.bundle.js', 'PptxGenJS');
        fotos = {};
        document.body.classList.add('vp-capturando');
        await quadro();
    }

    /** Fotografa o elemento (2× a resolução da tela) e guarda com a chave. Devolve false se o elemento não existe. */
    async function capturar(chave, seletor) {
        await quadro();
        const el = document.querySelector(seletor);
        if (!el || !el.offsetWidth || !el.offsetHeight) return false;
        const w = el.offsetWidth, h = el.offsetHeight;
        const data = await htmlToImage.toPng(el, { pixelRatio: 2, backgroundColor: '#ffffff', skipFonts: true, cacheBust: true });
        fotos[chave] = { data, w, h };
        return true;
    }

    function fim() { document.body.classList.remove('vp-capturando'); }

    function gerar(d) {
        const pptx = new PptxGenJS();
        pptx.layout = 'LAYOUT_WIDE';
        pptx.author = 'Howden Sales Forecast';
        pptx.company = 'Howden South America';
        pptx.title = d.titulo;

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
            s.addText(t, { x: 0.45, y: 0.22, w: W - 0.9, h: 0.55, fontFace: FONTE, fontSize: 24, bold: true, color: NAVY, margin: 0 });
            if (sub) s.addText(sub, { x: 0.45, y: 0.76, w: W - 0.9, h: 0.35, fontFace: FONTE, fontSize: 12, color: MUT, margin: 0 });
        };

        // um slide com a foto do quadro, do maior tamanho que couber (sem distorcer)
        const comFoto = (chave, t, sub) => {
            const f = fotos[chave];
            if (!f) return;
            const s = pptx.addSlide({ masterName: 'VP' });
            titulo(s, t, sub);
            const bx = 0.45, by = 1.22, bw = W - 0.9, bh = 6.85 - by;
            const r = Math.min(bw / f.w, bh / f.h);
            const w = f.w * r, h = f.h * r;
            s.addImage({ data: f.data, x: bx + (bw - w) / 2, y: by + (bh - h) / 2, w, h });
        };

        // ---------- capa ----------
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

        // ---------- os quadros da tela, na ordem da página ----------
        const f = d.filtros;
        comFoto('kpis', 'Indicadores de ' + d.foco, 'Comparações com o mesmo período do ano anterior · ' + f);
        comFoto('mensal-qtd', 'Propostas enviadas por mês, comparando os anos', 'Uma linha por ano · mês de envio da proposta · ' + f);
        comFoto('mensal-valor', 'Valor das propostas enviadas por mês (R$)', 'Uma linha por ano · mês de envio da proposta · ' + f);
        comFoto('mensal-vendas', 'Vendas por mês (R$), comparando os anos', 'Uma linha por ano · vendas da empresa no mês (não seguem os filtros)');
        comFoto('mensal-taxa', 'Taxa de conversão por mês, comparando os anos', 'Uma linha por ano · taxa sobre ' + d.baseCurta + ' · ' + f);
        comFoto('anos', 'Taxa de conversão e resultados, ano a ano', d.regra);
        comFoto('metas', 'Vendas totais × metas, ano a ano', 'Vendas da empresa × Meta Grupo e Meta HSA · R$');
        comFoto('bu', 'Conversão por Business Unit', 'Taxa sobre ' + d.baseCurta + ' · ' + f);
        comFoto('mercado', 'Conversão por mercado', 'Os 8 primeiros, da maior taxa para a menor · taxa sobre ' + d.baseCurta + ' · ' + f);
        comFoto('produto', 'Conversão por produto', 'Os 8 primeiros, da maior taxa para a menor · taxa sobre ' + d.baseCurta + ' · ' + f);
        comFoto('vendedores', 'Desempenho dos vendedores', 'Ranking por taxa de conversão · taxa sobre ' + d.baseCurta + ' · ' + f);
        comFoto('pais', 'Conversão por país', 'Taxa sobre ' + d.baseCurta + ' · ' + f);
        comFoto('perdas', 'Análise das propostas perdidas', 'Pareto por status no CRM (não é a causa comercial) · ' + f);
        comFoto('acumulado', 'Vendas acumuladas no ano × budgets', 'O ritmo de ' + d.foco + ' contra os anos anteriores e os budgets HSA e Grupo');
        comFoto('mes-ano', 'Vendas mês × ano', 'Como a planilha da equipe · quanto mais escuro, maior a venda do mês');

        // ---------- notas ----------
        {
            const s = pptx.addSlide({ masterName: 'VP' });
            titulo(s, 'Notas e critérios');
            s.addText([
                { text: 'Taxa de conversão', options: { bold: true, breakLine: true } }, { text: d.regra, options: { breakLine: true } }, { text: ' ', options: { breakLine: true } },
                { text: 'Filtros aplicados', options: { bold: true, breakLine: true } }, { text: d.filtros, options: { breakLine: true } }, { text: ' ', options: { breakLine: true } },
                { text: 'Fontes', options: { bold: true, breakLine: true } }, { text: d.fonte, options: { breakLine: true } }, { text: ' ', options: { breakLine: true } },
                { text: 'Valores em R$: propostas do AFM em US$ convertidas pela taxa do ano de envio (guia Conversão → Taxas de câmbio); NB já em R$. Anos com propostas do AFM sem taxa aparecem como “parcial”.' },
            ], { x: 0.45, y: 1.2, w: W - 0.9, h: 5.4, fontFace: FONTE, fontSize: 14, color: '172B43', valign: 'top', paraSpaceAfter: 4 });
        }

        return pptx.writeFile({ fileName: d.arquivo });
    }

    return { inicio, capturar, gerar, fim };
})();
