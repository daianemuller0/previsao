// ===========================================================================
// Performance Comercial → apresentação PowerPoint (.pptx) no MODELO da empresa
// (wwwroot/modelos/apresentacao_padrao.pptx, 3 páginas):
//   página 1 = capa (o nome do modelo é trocado pelo nome de quem gera),
//   página 2 = padrão dos slides (copiada para cada quadro da tela, com título e a foto),
//   página 3 = fechamento (fica no fim, como está).
// As fotos dos quadros são tiradas no navegador com o html-to-image (mesmo visual
// do sistema); o .pptx é editado com o JSZip. As duas bibliotecas ficam em
// wwwroot/lib (funciona sem internet) e só carregam na hora.
// O Blazor (VendasPropostas.razor → ExportarPowerPoint) chama, em ordem:
//   vpPptx.inicio() → vpPptx.capturar(chave, seletor) … → vpPptx.gerar(dados) → vpPptx.fim()
// ===========================================================================
window.vpPptx = (function () {
    const MODELO = 'modelos/apresentacao_padrao.pptx';
    const NOME_NO_MODELO = 'Edson Luis Geraldini';     // o texto da capa que vira o nome de quem gera
    const EMU = 914400;                                 // 1 polegada
    const SLIDE_W = 12192000, SLIDE_H = 6858000;        // 13,333 × 7,5 pol. (o tamanho do modelo)
    const COR_TITULO = '003D32', COR_SUB = '5D6B70';    // as cores do texto da capa do modelo
    const FONTE_TITULO = 'Aptos Display';
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
        await script('lib/jszip.min.js', 'JSZip');
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

    // ---------------- XML ----------------
    const esc = s => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    const run = (texto, sz, cor, extra) =>
        `<a:r><a:rPr lang="pt-BR" sz="${sz}"${extra || ''} dirty="0"><a:solidFill><a:srgbClr val="${cor}"/></a:solidFill>` +
        `${extra && extra.includes('b="1"') ? `<a:latin typeface="${FONTE_TITULO}"/>` : ''}</a:rPr><a:t>${esc(texto)}</a:t></a:r>`;
    const caixa = (id, nome, x, y, w, h, paragrafos, ancora) =>
        `<p:sp><p:nvSpPr><p:cNvPr id="${id}" name="${nome}"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>` +
        `<p:spPr><a:xfrm><a:off x="${Math.round(x)}" y="${Math.round(y)}"/><a:ext cx="${Math.round(w)}" cy="${Math.round(h)}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>` +
        `<p:txBody><a:bodyPr wrap="square" lIns="0" tIns="0" rIns="0" bIns="0" rtlCol="0" anchor="${ancora || 't'}"><a:normAutofit/></a:bodyPr><a:lstStyle/>${paragrafos}</p:txBody></p:sp>`;
    const imagem = (id, rid, x, y, w, h) =>
        `<p:pic><p:nvPicPr><p:cNvPr id="${id}" name="Quadro ${id}"/><p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>` +
        `<p:blipFill><a:blip r:embed="${rid}"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>` +
        `<p:spPr><a:xfrm><a:off x="${Math.round(x)}" y="${Math.round(y)}"/><a:ext cx="${Math.round(w)}" cy="${Math.round(h)}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic>`;

    // área útil da página padrão (fora do logo e do "Confidential" do rodapé)
    const MX = 528673, TOPO_TIT = 300000, ALT_TIT = 520000, TOPO_SUB = 860000, ALT_SUB = 300000;
    const AREA = { x: MX, y: 1280000, w: SLIDE_W - 2 * MX, h: 6200000 - 1280000 };

    /** O conteúdo que entra na página padrão: título, subtítulo e a foto (ou um texto). */
    function formas(item, ridFoto) {
        let xml = caixa(20, 'Título', MX, TOPO_TIT, SLIDE_W - 2 * MX, ALT_TIT, `<a:p>${run(item.titulo, 2400, COR_TITULO, ' b="1"')}</a:p>`, 'b');
        if (item.sub) xml += caixa(21, 'Subtítulo', MX, TOPO_SUB, SLIDE_W - 2 * MX, ALT_SUB, `<a:p>${run(item.sub, 1200, COR_SUB)}</a:p>`);
        if (item.foto && ridFoto) {
            const f = item.foto;
            const r = Math.min(AREA.w / f.w, AREA.h / f.h);
            const w = f.w * r, h = f.h * r;
            xml += imagem(22, ridFoto, AREA.x + (AREA.w - w) / 2, AREA.y + (AREA.h - h) / 2, w, h);
        }
        if (item.texto) {
            const ps = item.texto.map(t => t.titulo
                ? `<a:p><a:pPr><a:spcBef><a:spcPts val="1400"/></a:spcBef></a:pPr>${run(t.titulo, 1600, COR_TITULO, ' b="1"')}</a:p>`
                : `<a:p><a:pPr><a:spcBef><a:spcPts val="300"/></a:spcBef></a:pPr>${run(t.linha, 1400, '172B43')}</a:p>`).join('');
            xml += caixa(23, 'Texto', AREA.x, AREA.y, AREA.w, AREA.h, ps);
        }
        return xml;
    }

    async function gerar(d) {
        const resp = await fetch(MODELO, { cache: 'no-store' });
        if (!resp.ok) throw new Error('modelo da apresentação não encontrado (' + MODELO + ')');
        const zip = await JSZip.loadAsync(await resp.arrayBuffer());
        const ler = p => zip.file(p).async('string');

        // ---------- 1. capa: o nome de quem gera ----------
        let capa = await ler('ppt/slides/slide1.xml');
        capa = capa.split('>' + NOME_NO_MODELO + '<').join('>' + esc(d.usuario) + '<');
        zip.file('ppt/slides/slide1.xml', capa);

        // ---------- 2. os quadros, cada um numa cópia da página padrão ----------
        const ff = d.filtros;
        const itens = [
            ['kpis', 'Indicadores de ' + d.foco, 'Comparações com o mesmo período do ano anterior · ' + ff],
            ['mes-a-mes', 'Vendas e propostas, mês a mês · ' + d.foco, 'Em cima, quanto foi vendido no mês (' + (d.moeda || 'R$') + ', empresa toda); embaixo, quantas propostas foram enviadas · ' + ff],
            ['mes-a-mes-setor', 'Vendas por setor (NB e AFM), propostas e conversão · ' + d.foco, 'Vendas do relatório por setor (seguem o filtro de Setor); embaixo, as propostas e a conversão por quantidade e por valor · ' + ff],
            ['mensal-qtd', 'Propostas enviadas por mês, comparando os anos', 'Uma linha por ano · mês de envio da proposta · ' + ff],
            ['mensal-valor', 'Valor das propostas enviadas por mês (' + (d.moeda || 'R$') + ')', 'Uma linha por ano · mês de envio da proposta · ' + ff],
            ['mensal-vendas', 'Vendas por mês (' + (d.moeda || 'R$') + '), comparando os anos', 'Uma linha por ano · vendas da empresa no mês (não seguem os filtros)'],
            ['mensal-taxa', 'Taxa de conversão por mês, comparando os anos', 'Uma linha por ano · taxa sobre ' + d.baseCurta + ' · ' + ff],
            ['anos', 'Taxa de conversão e resultados, ano a ano', d.regra],
            ['setores', 'AFM × NB e valores, ano a ano', 'AFM = Aftermarket · NB = New Business · barra: divisão das propostas enviadas · ' + ff],
            ['metas', 'Vendas totais × metas, ano a ano', 'Vendas da empresa × Meta Grupo e Meta HSA · ' + (d.moeda || 'R$')],
            ['bu', 'Conversão por Business Unit', 'Taxa sobre ' + d.baseCurta + ' · ' + ff],
            ['mercado', 'Conversão por mercado', 'Os 8 primeiros, da maior taxa para a menor · taxa sobre ' + d.baseCurta + ' · ' + ff],
            ['produto', 'Conversão por produto', 'Os 8 primeiros, da maior taxa para a menor · taxa sobre ' + d.baseCurta + ' · ' + ff],
            ['vendedores', 'Desempenho dos vendedores', 'Ranking por taxa de conversão · taxa sobre ' + d.baseCurta + ' · ' + ff],
            ['pais', 'Conversão por país', 'Taxa sobre ' + d.baseCurta + ' · ' + ff],
            ['perdas', 'Análise das propostas perdidas', 'Pareto por status no CRM (não é a causa comercial) · ' + ff],
            ['acumulado', 'Vendas acumuladas no ano × budgets', 'O ritmo de ' + d.foco + ' contra os anos anteriores e os budgets HSA e Grupo'],
            ['mes-ano', 'Vendas mês × ano', 'Como a planilha da equipe · quanto mais escuro, maior a venda do mês'],
        ].filter(([k]) => fotos[k]).map(([k, titulo, sub]) => ({ titulo, sub, foto: fotos[k] }));
        itens.push({
            titulo: 'Notas e critérios', sub: 'Gerado por ' + d.usuario + ' em ' + d.geradoEm,
            texto: [
                { titulo: 'Taxa de conversão' }, { linha: d.regra },
                { titulo: 'Filtros aplicados' }, { linha: d.filtros },
                { titulo: 'Fontes' }, { linha: d.fonte },
                { linha: 'Valores em R$: propostas do AFM em US$ convertidas pela taxa do ano de envio (guia Conversão → Taxas de câmbio); NB já em R$. Anos com propostas do AFM sem taxa aparecem como “parcial”.' },
            ],
        });

        const padraoXml = await ler('ppt/slides/slide2.xml');
        const padraoRels = await ler('ppt/slides/_rels/slide2.xml.rels');
        const relsSemNotas = padraoRels.replace(/<Relationship [^>]*notesSlide[^>]*\/>/g, '');
        const TIPO_IMG = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships/image';
        const TIPO_SLIDE = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide';
        const CT_SLIDE = 'application/vnd.openxmlformats-officedocument.presentationml.slide+xml';

        let pres = await ler('ppt/presentation.xml');
        let presRels = await ler('ppt/_rels/presentation.xml.rels');
        let tipos = await ler('[Content_Types].xml');

        // o id do slide da página padrão na lista de slides (os novos entram logo depois dele, antes do fechamento)
        const ridPadrao = (presRels.match(/<Relationship [^>]*Id="([^"]+)"[^>]*Target="slides\/slide2\.xml"/) ||
                           presRels.match(/<Relationship [^>]*Target="slides\/slide2\.xml"[^>]*Id="([^"]+)"/))[1];
        const sldPadrao = pres.match(new RegExp(`<p:sldId [^>]*r:id="${ridPadrao}"[^>]*/>`))[0];
        let proximoId = Math.max(...[...pres.matchAll(/<p:sldId id="(\d+)"/g)].map(m => +m[1])) + 1;

        const novosSld = [];
        itens.forEach((item, i) => {
            const midia = item.foto ? `ppt/media/vp_quadro${i + 1}.png` : null;
            if (midia) zip.file(midia, item.foto.data.split(',')[1], { base64: true });
            const relFoto = midia ? `<Relationship Id="rIdVpFoto" Type="${TIPO_IMG}" Target="../media/vp_quadro${i + 1}.png"/>` : '';
            const xml = padraoXml.replace('</p:spTree>', formas(item, midia ? 'rIdVpFoto' : null) + '</p:spTree>');
            if (i === 0) {
                // o primeiro quadro usa a própria página 2 do modelo
                zip.file('ppt/slides/slide2.xml', xml);
                zip.file('ppt/slides/_rels/slide2.xml.rels', padraoRels.replace('</Relationships>', relFoto + '</Relationships>'));
                return;
            }
            const n = 100 + i, nome = `slide${n}.xml`, rid = `rIdVpSl${n}`;
            zip.file(`ppt/slides/${nome}`, xml);
            zip.file(`ppt/slides/_rels/${nome}.rels`, relsSemNotas.replace('</Relationships>', relFoto + '</Relationships>'));
            tipos = tipos.replace('</Types>', `<Override PartName="/ppt/slides/${nome}" ContentType="${CT_SLIDE}"/></Types>`);
            presRels = presRels.replace('</Relationships>', `<Relationship Id="${rid}" Type="${TIPO_SLIDE}" Target="slides/${nome}"/></Relationships>`);
            novosSld.push(`<p:sldId id="${proximoId++}" r:id="${rid}"/>`);
        });
        pres = pres.replace(sldPadrao, sldPadrao + novosSld.join(''));
        zip.file('ppt/presentation.xml', pres);
        zip.file('ppt/_rels/presentation.xml.rels', presRels);
        zip.file('[Content_Types].xml', tipos);

        // número de slides nas propriedades do arquivo
        const app = zip.file('docProps/app.xml');
        if (app) {
            const total = (pres.match(/<p:sldId /g) || []).length;
            zip.file('docProps/app.xml', (await app.async('string')).replace(/<Slides>\d+<\/Slides>/, `<Slides>${total}</Slides>`));
        }

        const blob = await zip.generateAsync({ type: 'blob', compression: 'DEFLATE', mimeType: 'application/vnd.openxmlformats-officedocument.presentationml.presentation' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url; a.download = d.arquivo;
        document.body.appendChild(a); a.click(); a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 30000);
    }

    return { inicio, capturar, gerar, fim };
})();
