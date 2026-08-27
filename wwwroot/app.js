// Utilitários chamados pelo Blazor via JS interop.
window.appPrint = () => window.print();

window.appDownload = (fileName, mime, base64) => {
    const a = document.createElement('a');
    a.href = `data:${mime};base64,${base64}`;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
};

// Abre um rascunho de e-mail no cliente padrão (mailto:) sem navegar a página.
// Usado no envio de follow-ups em massa (o usuário revisa e envia).
window.openMail = (url) => {
    const a = document.createElement('a');
    a.href = url;
    a.style.display = 'none';
    document.body.appendChild(a);
    a.click();
    a.remove();
};

// Copia texto para a área de transferência (modelo de e-mail do follow-up).
window.copyText = async (text) => {
    try {
        if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); return true; }
    } catch (e) { }
    try {
        const ta = document.createElement('textarea');
        ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
        document.body.appendChild(ta); ta.focus(); ta.select();
        const ok = document.execCommand('copy'); ta.remove(); return ok;
    } catch (e) { return false; }
};

// Alterna o recolhimento da sidebar guardando a preferência do usuário.
window.appToggleSidebar = (collapsed) => {
    try { localStorage.setItem('hsf-sidebar', collapsed ? '1' : '0'); } catch { }
};
window.appSidebarState = () => {
    try { return localStorage.getItem('hsf-sidebar') === '1'; } catch { return false; }
};

// Modo apresentação: tela cheia + ROLAGEM LENTA e contínua, para o dashboard
// ficar passando numa TV. Rola alguns pixels por segundo, faz uma pausa no fim
// e volta ao topo. Sai com Esc, ao fechar a tela cheia ou clicando de novo.
window.presentation = (function () {
    let raf = null, running = false, pos = 0, ultimo = 0, pausaAte = 0;
    const PPS = 22;          // pixels por segundo — devagar, dá para ler
    const PAUSA = 4000;      // ms parado no fim antes de voltar ao topo

    function maxScroll() {
        const doc = document.scrollingElement || document.documentElement;
        return Math.max(0, doc.scrollHeight - window.innerHeight);
    }
    function passo(agora) {
        if (!running) return;
        const dt = ultimo ? (agora - ultimo) : 0;
        ultimo = agora;

        if (agora < pausaAte) { raf = requestAnimationFrame(passo); return; }

        const fim = maxScroll();
        pos += (dt / 1000) * PPS;
        if (pos >= fim) {
            // Chegou ao fim: espera um pouco e recomeça de cima.
            pos = fim;
            window.scrollTo(0, pos);
            pausaAte = agora + PAUSA;
            pos = 0;
            setTimeout(() => { if (running) window.scrollTo({ top: 0, behavior: 'smooth' }); }, PAUSA);
        } else {
            window.scrollTo(0, pos);
        }
        raf = requestAnimationFrame(passo);
    }
    function onFsChange() { if (!document.fullscreenElement) stop(); }
    function onKey(e) { if (e.key === 'Escape') stop(); }
    function start() {
        if (running) { stop(); return; }
        const el = document.documentElement;
        if (el.requestFullscreen) { try { el.requestFullscreen(); } catch (e) { } }
        running = true; pos = 0; ultimo = 0; pausaAte = 0;
        window.scrollTo(0, 0);
        document.addEventListener('fullscreenchange', onFsChange);
        document.addEventListener('keydown', onKey);
        raf = requestAnimationFrame(passo);
    }
    function stop() {
        running = false;
        if (raf) { cancelAnimationFrame(raf); raf = null; }
        document.removeEventListener('fullscreenchange', onFsChange);
        document.removeEventListener('keydown', onKey);
        if (document.fullscreenElement && document.exitFullscreen) { try { document.exitFullscreen(); } catch (e) { } }
    }
    return { start, stop };
})();

// Comando por voz (Web Speech API · Edge/Chrome). Escuta em pt-BR e manda o
// texto reconhecido para o componente Blazor (ApplyVoiceCommand), que interpreta
// e aplica os filtros. Callbacks de status/parcial alimentam o indicador na tela.
window.voicefilter = (function () {
    let rec = null, ref = null, active = false;
    function supported() { return 'webkitSpeechRecognition' in window || 'SpeechRecognition' in window; }
    function start(dotref) {
        ref = dotref || ref;
        if (!supported()) { if (ref) ref.invokeMethodAsync('VoiceStatus', 'unsupported'); return; }
        if (active) { stop(); return; }
        const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
        rec = new SR();
        rec.lang = 'pt-BR';
        rec.interimResults = true;
        rec.continuous = false;
        rec.maxAlternatives = 1;
        active = true;
        if (ref) ref.invokeMethodAsync('VoiceStatus', 'listening');
        rec.onresult = e => {
            let fin = '', interim = '';
            for (let i = e.resultIndex; i < e.results.length; i++) {
                const r = e.results[i];
                if (r.isFinal) fin += r[0].transcript; else interim += r[0].transcript;
            }
            if (interim && ref) ref.invokeMethodAsync('VoiceInterim', interim);
            if (fin && ref) ref.invokeMethodAsync('ApplyVoiceCommand', fin);
        };
        rec.onerror = e => { if (ref) ref.invokeMethodAsync('VoiceStatus', 'error:' + (e && e.error ? e.error : '')); };
        rec.onend = () => { active = false; if (ref) ref.invokeMethodAsync('VoiceStatus', 'idle'); };
        try { rec.start(); } catch (err) { active = false; }
    }
    function stop() { active = false; if (rec) { try { rec.stop(); } catch (e) { } } }

    // Não dispara push-to-talk enquanto o foco está num campo de digitação.
    function inField() {
        const el = document.activeElement;
        if (!el) return false;
        const tag = (el.tagName || '').toLowerCase();
        return tag === 'input' || tag === 'textarea' || tag === 'select' || el.isContentEditable;
    }
    // Segure a tecla (padrão: barra de espaço) por HOLD_MS para ATIVAR a voz.
    // Soltar antes disso cancela; depois de ativada, o reconhecimento roda sozinho.
    const HOLD_MS = 3000;
    let pttKey = 'Space', pttTimer = null, pttArmed = false, pttBound = false;
    function pushToTalk(key, dotref) {
        ref = dotref || ref;
        if (key) pttKey = key;
        if (pttBound) return;
        pttBound = true;
        window.addEventListener('keydown', e => {
            if ((e.key !== pttKey && e.code !== pttKey) || inField()) return;
            e.preventDefault();                       // impede a rolagem enquanto segura
            if (e.repeat || pttTimer || pttArmed) return;
            if (ref) ref.invokeMethodAsync('VoiceStatus', 'arming');
            pttTimer = setTimeout(() => {
                pttTimer = null; pttArmed = true;
                start(ref);                           // ativa após 3s de tecla segurada
            }, HOLD_MS);
        });
        window.addEventListener('keyup', e => {
            if (e.key !== pttKey && e.code !== pttKey) return;
            if (pttTimer) {                           // soltou antes dos 3s → cancela
                clearTimeout(pttTimer); pttTimer = null;
                if (ref) ref.invokeMethodAsync('VoiceStatus', 'idle');
            }
            pttArmed = false;
        });
    }
    return { start, stop, supported, pushToTalk };
})();

// Cross-filter genérico: delega cliques de qualquer elemento com data-xf-dim
// dentro de um container para o componente Blazor (ApplyCrossFilter).
window.xfilter = (function () {
    const bound = {};
    function init(rootId, ref) {
        const root = document.getElementById(rootId);
        if (!root) return;
        if (root._xfRef !== undefined) { root._xfRef = ref || root._xfRef; return; }
        root._xfRef = ref || null;
        root.addEventListener('click', e => {
            const el = e.target.closest && e.target.closest('[data-xf-dim]');
            if (!el || !root._xfRef) return;
            const dim = el.getAttribute('data-xf-dim');
            const val = el.getAttribute('data-xf-val');
            if (dim && val !== null) root._xfRef.invokeMethodAsync('ApplyCrossFilter', dim, val);
        });
    }
    return { init };
})();

// ---- Mapa-múndi interativo (tooltip, zoom com a roda, arrastar p/ mover) ----
window.worldMap = (function () {
    function estado(id) {
        const wrap = document.getElementById(id);
        return wrap ? wrap.__wm : null;
    }
    function apply(s) { s.inner.style.transform = `translate(${s.tx}px, ${s.ty}px) scale(${s.scale})`; }
    function zoomAt(s, factor, clientX, clientY) {
        const ns = Math.min(Math.max(s.scale * factor, 1), 14);
        factor = ns / s.scale;
        const r = s.wrap.getBoundingClientRect();
        const px = clientX - r.left, py = clientY - r.top;
        s.tx = px - factor * (px - s.tx);
        s.ty = py - factor * (py - s.ty);
        s.scale = ns;
        if (ns === 1) { s.tx = 0; s.ty = 0; }
        apply(s);
    }
    function init(id, ref) {
        const wrap = document.getElementById(id);
        if (!wrap) return;
        const inner = wrap.querySelector('.worldmap-inner');
        if (!inner) return;

        // O estado mora NO ELEMENTO. Guardá-lo num mapa por id fazia o mapa
        // parar de responder ao voltar para a página: o id continuava o mesmo,
        // mas o <div> era outro e os eventos ficavam presos ao antigo.
        if (wrap.__wm) {
            const s = wrap.__wm;
            s.inner = inner;
            if (ref) s.ref = ref;
            apply(s);
            return;
        }

        const s = { wrap, inner, scale: 1, tx: 0, ty: 0, drag: null, ref: ref || null, moved: 0, alvo: null };
        wrap.__wm = s;
        inner.style.transformOrigin = '0 0';
        let tip = wrap.querySelector('.wm-tip');
        if (!tip) { tip = document.createElement('div'); tip.className = 'wm-tip'; wrap.appendChild(tip); }

        wrap.addEventListener('mousemove', e => {
            const p = e.target.closest && e.target.closest('path[data-country]');
            if (p && !s.drag) {
                const v = p.getAttribute('data-value');
                tip.innerHTML = `<div class="wm-tip-name">${p.getAttribute('data-country')}</div>` +
                    (v ? `<div class="wm-tip-v">${v}</div><div class="wm-tip-p">${p.getAttribute('data-pct')} · ${p.getAttribute('data-metric') || ''}</div>`
                       : `<div class="wm-tip-p">sem dados no recorte</div>`);
                tip.style.display = 'block';
                const r = wrap.getBoundingClientRect();
                let x = e.clientX - r.left + 14, y = e.clientY - r.top + 14;
                if (x + tip.offsetWidth > r.width) x = e.clientX - r.left - tip.offsetWidth - 14;
                if (y + tip.offsetHeight > r.height) y = e.clientY - r.top - tip.offsetHeight - 14;
                tip.style.left = x + 'px'; tip.style.top = y + 'px';
            } else { tip.style.display = 'none'; }
        });
        wrap.addEventListener('mouseleave', () => { tip.style.display = 'none'; });
        wrap.addEventListener('wheel', e => { e.preventDefault(); zoomAt(s, e.deltaY < 0 ? 1.2 : 1 / 1.2, e.clientX, e.clientY); }, { passive: false });

        wrap.addEventListener('pointerdown', e => {
            // Guarda QUAL país estava sob o cursor: depois de capturar o ponteiro
            // o clique é entregue no wrapper, não no <path>, e procurar pelo
            // e.target do clique não acharia mais o país.
            s.alvo = (e.target.closest && e.target.closest('path[data-xf-val]')) || null;
            s.drag = { x: e.clientX, y: e.clientY, tx: s.tx, ty: s.ty, capturado: false, id: e.pointerId };
            s.moved = 0;
            tip.style.display = 'none';
        });
        wrap.addEventListener('pointermove', e => {
            if (!s.drag) return;
            s.moved = Math.max(s.moved, Math.abs(e.clientX - s.drag.x) + Math.abs(e.clientY - s.drag.y));
            // Só vira arraste depois de sair do lugar: assim um clique simples
            // nunca captura o ponteiro e o navegador entrega o click normalmente.
            if (!s.drag.capturado) {
                if (s.moved <= 4) return;
                s.drag.capturado = true;
                try { wrap.setPointerCapture(s.drag.id); } catch (_) { }
                wrap.classList.add('grabbing');
            }
            s.tx = s.drag.tx + (e.clientX - s.drag.x);
            s.ty = s.drag.ty + (e.clientY - s.drag.y);
            apply(s);
        });
        const end = () => {
            if (!s.drag) return;
            if (s.drag.capturado) { try { wrap.releasePointerCapture(s.drag.id); } catch (_) { } }
            s.drag = null;
            wrap.classList.remove('grabbing');
        };
        wrap.addEventListener('pointerup', end);
        wrap.addEventListener('pointercancel', end);

        // Clique num país (sem arrastar) dispara o cross-filter no componente.
        wrap.addEventListener('click', e => {
            if (s.moved > 6 || !s.ref) return;
            const p = (e.target.closest && e.target.closest('path[data-xf-val]')) || s.alvo;
            const val = p && p.getAttribute('data-xf-val');
            if (val) s.ref.invokeMethodAsync('ApplyCrossFilter', 'country', val);
        });
        // Duplo clique fora de qualquer país (oceano / país sem dados): limpa
        // todos os filtros da página — a saída rápida depois de ir clicando.
        wrap.addEventListener('dblclick', e => {
            if (!s.ref) return;
            const p = (e.target.closest && e.target.closest('path[data-xf-val]')) || s.alvo;
            if (p) return;
            s.ref.invokeMethodAsync('ClearAllFilters');
        });
        apply(s);
    }
    function zoom(id, factor) {
        const s = estado(id); if (!s) return;
        const r = s.wrap.getBoundingClientRect();
        zoomAt(s, factor, r.left + r.width / 2, r.top + r.height / 2);
    }
    function reset(id) { const s = estado(id); if (!s) return; s.scale = 1; s.tx = 0; s.ty = 0; apply(s); }
    return { init, zoom, reset };
})();

// ---------------------------------------------------------------------------
// Reordenar por arraste: barras de um gráfico (vertical) e colunas de uma
// tabela (horizontal, arrastando o cabeçalho).
//
// Regra de ouro: o DOM é do Blazor. Durante o arraste só mexemos em CLASSES
// (que o Blazor reescreve no próximo render, sem estrago); a nova ordem é
// calculada em memória e devolvida ao componente, que redesenha a lista já
// ordenada. Mover nós de verdade embaralharia o diff do Blazor.
// ---------------------------------------------------------------------------
window.dragOrder = (function () {
    function itens(box) { return Array.from(box.querySelectorAll('[data-key]')); }
    function limpar(box) {
        itens(box).forEach(x => x.classList.remove('co-drag', 'co-before', 'co-after'));
    }

    // metodo   = método [JSInvokable] que recebe (chave, ordem)
    // horizontal = true para cabeçalho de tabela (antes/depois pelo eixo X)
    function init(id, ref, chave, metodo, horizontal) {
        const box = document.getElementById(id);
        if (!box) return;
        if (box.__co) { box.__co.ref = ref; box.__co.chave = chave; return; }

        const st = { ref, chave, metodo: metodo || 'SaveChartOrder', horizontal: !!horizontal, key: null, alvo: null, depois: false };
        box.__co = st;

        box.addEventListener('dragstart', e => {
            const it = e.target.closest('[data-key]');
            if (!it || !box.contains(it)) return;
            st.key = it.getAttribute('data-key');
            st.alvo = null;
            it.classList.add('co-drag');
            e.dataTransfer.effectAllowed = 'move';
            try { e.dataTransfer.setData('text/plain', st.key); } catch (_) { }
        });

        box.addEventListener('dragover', e => {
            if (st.key === null) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            const over = e.target.closest('[data-key]');
            if (!over || !box.contains(over) || over.getAttribute('data-key') === st.key) return;
            const r = over.getBoundingClientRect();
            st.alvo = over.getAttribute('data-key');
            st.depois = st.horizontal
                ? (e.clientX - r.left) > r.width / 2
                : (e.clientY - r.top) > r.height / 2;
            itens(box).forEach(x => x.classList.remove('co-before', 'co-after'));
            over.classList.add(st.depois ? 'co-after' : 'co-before');
        });

        function soltar() {
            const key = st.key, alvo = st.alvo, depois = st.depois;
            st.key = null; st.alvo = null;
            limpar(box);
            if (key === null || alvo === null || !st.ref) return;

            const ordem = itens(box).map(x => x.getAttribute('data-key'));
            const de = ordem.indexOf(key);
            if (de < 0) return;
            ordem.splice(de, 1);
            let para = ordem.indexOf(alvo);
            if (para < 0) return;
            if (depois) para++;
            ordem.splice(para, 0, key);
            st.ref.invokeMethodAsync(st.metodo, st.chave, ordem);
        }

        box.addEventListener('drop', e => { e.preventDefault(); soltar(); });
        box.addEventListener('dragend', () => { st.alvo = null; st.key = null; limpar(box); });
        box.addEventListener('dragleave', e => {
            if (!box.contains(e.relatedTarget)) itens(box).forEach(x => x.classList.remove('co-before', 'co-after'));
        });
    }
    return { init };
})();

// Compatibilidade: gráficos continuam chamando chartOrder.init(id, ref, chave).
window.chartOrder = {
    init: (id, ref, chave) => window.dragOrder.init(id, ref, chave, 'SaveChartOrder', false)
};

// Colunas de tabela: arrasta o <th> e devolve a nova ordem ao componente.
window.colOrder = {
    init: (id, ref, tabela) => window.dragOrder.init(id, ref, tabela, 'SaveColumnOrder', true)
};

// ---------------------------------------------------------------------------
// Ampliar um painel de gráfico.
//
// Não usa a tela cheia do navegador: ela esconderia a barra de filtros, que é
// justamente o que a pessoa quer continuar mexendo com o gráfico grande. O
// painel vira um bloco fixo que ocupa a tela ABAIXO do topo e dos filtros — a
// altura deles é medida na hora, porque a barra de filtros muda de altura
// conforme quebra em mais linhas.
//
// Só mexe em CLASSES de elementos que o Blazor já criou; não insere nem move
// nós, senão o diff do Blazor se perderia.
// ---------------------------------------------------------------------------
window.appAmpliar = function (btn) {
    const card = btn.closest('.xcard, .panel');
    if (!card) return;
    if (card.classList.contains('zoom-card')) { window.appAmpliarSair(); return; }

    window.appAmpliarSair();                       // um painel ampliado por vez

    // Sobe a página primeiro: com ela rolada, a barra de filtros pode estar em
    // qualquer altura, e a medida sairia errada.
    window.scrollTo(0, 0);

    // Onde termina o que precisa continuar visível (topo + barra de filtros).
    const barra = document.querySelector('.filterbar');
    const topo = document.querySelector('.topbar');
    let y = 0;
    if (barra) y = Math.max(y, barra.getBoundingClientRect().bottom);
    else if (topo) y = Math.max(y, topo.getBoundingClientRect().bottom);
    document.documentElement.style.setProperty('--zoom-top', Math.round(Math.max(y, 0)) + 'px');

    // O menu lateral continua visível: o painel começa onde a área de conteúdo
    // começa (e o menu pode estar recolhido, então a largura é medida também).
    const conteudo = document.querySelector('.content') || document.querySelector('.main-col');
    const x = conteudo ? Math.max(0, conteudo.getBoundingClientRect().left) : 0;
    document.documentElement.style.setProperty('--zoom-left', Math.round(x) + 'px');

    card.classList.add('zoom-card');
    document.body.classList.add('zoom-mode');
    document.addEventListener('keydown', sairComEsc);
};

window.appAmpliarSair = function () {
    document.querySelectorAll('.zoom-card').forEach(c => c.classList.remove('zoom-card'));
    document.body.classList.remove('zoom-mode');
    document.removeEventListener('keydown', sairComEsc);
};

function sairComEsc(e) { if (e.key === 'Escape') window.appAmpliarSair(); }

// ---------------------------------------------------------------------------
// Régua de rolagem horizontal ACIMA da tabela.
//
// Com dezenas de colunas, a barra nativa fica no rodapé: para deslocar as
// colunas a pessoa precisa descer a lista inteira. Aqui um segundo trilho fica
// no topo, sincronizado nos dois sentidos com o container real.
//
// Feito com dois elementos que o Blazor já criou (a régua vem no markup), sem
// inserir nós — o DOM é dele.
// ---------------------------------------------------------------------------
window.appRolagemTopo = function (idRegua, idTabela) {
    const regua = document.getElementById(idRegua);
    const tabela = document.getElementById(idTabela);
    if (!regua || !tabela) return;

    const medida = regua.firstElementChild;
    if (!medida) return;

    // Largura do conteúdo: é o que define o tamanho do "polegar" da régua.
    const ajustar = () => {
        medida.style.width = tabela.scrollWidth + 'px';
        // Sem rolagem horizontal, a régua não tem por que aparecer.
        regua.style.display = tabela.scrollWidth > tabela.clientWidth + 1 ? '' : 'none';
    };
    ajustar();

    if (regua.__sync) { regua.__sync(); return; }   // já ligado: só remede

    let de = null;                                  // evita o eco de um no outro
    regua.addEventListener('scroll', () => {
        if (de === 'tabela') { de = null; return; }
        de = 'regua'; tabela.scrollLeft = regua.scrollLeft;
    });
    tabela.addEventListener('scroll', () => {
        if (de === 'regua') { de = null; return; }
        de = 'tabela'; regua.scrollLeft = tabela.scrollLeft;
    });
    window.addEventListener('resize', ajustar);
    regua.__sync = ajustar;
};

// ---------------------------------------------------------------------------
// Tamanho da letra de tudo. Guardado por máquina (localStorage): é preferência
// de quem está olhando a tela, não do cadastro da pessoa.
// ---------------------------------------------------------------------------
window.appFonte = (function () {
    const MIN = 80, MAX = 160, PASSO = 10, CHAVE = 'hsf-fonte';

    function ler() {
        try { return Math.min(MAX, Math.max(MIN, parseInt(localStorage.getItem(CHAVE) || '100', 10))); }
        catch { return 100; }
    }
    function aplicar(v) {
        document.documentElement.style.zoom = v === 100 ? '' : (v / 100);
        const rot = document.getElementById('fonte-nivel');
        if (rot) rot.textContent = v + '%';
    }
    function mudar(delta) {
        const v = Math.min(MAX, Math.max(MIN, ler() + delta * PASSO));
        try { localStorage.setItem(CHAVE, String(v)); } catch { }
        aplicar(v);
    }
    // Ao carregar a página, restaura o tamanho escolhido.
    document.addEventListener('DOMContentLoaded', () => aplicar(ler()));
    return { mudar, aplicar, ler };
})();

// ---------------------------------------------------------------------------
// Painel montável da Visão Executiva.
//
// Cada peça é um elemento com data-pnl. A posição e a largura vêm de uma
// preferência gravada por usuário, no formato "chave:largura|chave:largura".
// Aplicar é só mexer em style.order e style.gridColumn — nada de mover nós,
// que o DOM é do Blazor.
//
// No modo organizar: arrastar move a peça e o duplo clique alterna entre meia
// largura e largura inteira.
// ---------------------------------------------------------------------------
window.appLayout = (function () {
    let ref = null, organizando = false;

    function board() { return document.getElementById('exec-board'); }
    function pecas() {
        const b = board();
        return b ? Array.from(b.querySelectorAll('[data-pnl]')) : [];
    }

    // "chave:12|outra:6" → Map(chave → largura)
    function ler(pref) {
        const m = new Map();
        (pref || '').split('|').filter(Boolean).forEach(par => {
            const [k, l] = par.split(':');
            if (k) m.set(k, Math.max(3, Math.min(12, parseInt(l, 10) || 12)));
        });
        return m;
    }

    function aplicar(pref) {
        const m = ler(pref);
        pecas().forEach(el => {
            const k = el.getAttribute('data-pnl');
            // Peça que não está na preferência (ex.: criada numa versão nova)
            // fica onde o HTML a colocou, no fim da ordem gravada.
            const i = Array.from(m.keys()).indexOf(k);
            el.style.order = i >= 0 ? i : 999;
            const larg = m.get(k);
            if (larg) el.style.gridColumn = 'span ' + larg;
        });
    }

    // Ordem atual = ordem visual (o que está na tela), com a largura de cada um.
    function serializar() {
        return pecas()
            .slice()
            .sort((a, b) => (parseInt(a.style.order || 999, 10)) - (parseInt(b.style.order || 999, 10)))
            .map(el => {
                const span = (el.style.gridColumn || '').match(/span\s+(\d+)/);
                const cls = el.classList.contains('c6') ? 6 : 12;
                return el.getAttribute('data-pnl') + ':' + (span ? span[1] : cls);
            })
            .join('|');
    }

    function gravar() {
        if (ref) ref.invokeMethodAsync('SaveLayout', serializar());
    }

    // ---- modo organizar ----
    // A largura é alternada com DUPLO CLIQUE na peça, não por um botão criado
    // aqui: inserir um filho a mais num elemento do Blazor bagunça o diff dele
    // no próximo render. Zero nós novos, zero conflito.
    function organizar(ligado, dotref) {
        ref = dotref || ref;
        const b = board();
        if (!b) return;
        organizando = ligado;
        b.classList.toggle('organizando', ligado);
        pecas().forEach(el => { el.draggable = ligado; });
    }

    // ---- arraste (grade: decide antes/depois pelo eixo que separa as peças) ----
    function ligar(dotref, pref) {
        ref = dotref || ref;
        const b = board();
        if (!b) return;
        aplicar(pref);
        if (b.__lay) return;
        b.__lay = true;

        let arrastando = null, alvo = null, depois = false;

        b.addEventListener('dragstart', e => {
            if (!organizando) return;
            const el = e.target.closest('[data-pnl]');
            if (!el) return;
            arrastando = el; alvo = null;
            el.classList.add('co-drag');
            e.dataTransfer.effectAllowed = 'move';
            try { e.dataTransfer.setData('text/plain', el.getAttribute('data-pnl')); } catch (_) { }
        });

        b.addEventListener('dragover', e => {
            if (!arrastando) return;
            e.preventDefault();
            const el = e.target.closest('[data-pnl]');
            if (!el || el === arrastando) return;
            const r = el.getBoundingClientRect();
            // Peça larga: decide por cima/baixo. Peça estreita ao lado de outra:
            // decide por esquerda/direita.
            depois = r.width < b.clientWidth * 0.7
                ? (e.clientX - r.left) > r.width / 2
                : (e.clientY - r.top) > r.height / 2;
            alvo = el;
            pecas().forEach(x => x.classList.remove('co-before', 'co-after'));
            el.classList.add(depois ? 'co-after' : 'co-before');
        });

        function soltar() {
            pecas().forEach(x => x.classList.remove('co-drag', 'co-before', 'co-after'));
            if (!arrastando || !alvo) { arrastando = null; alvo = null; return; }

            const ordem = pecas()
                .slice()
                .sort((x, y) => (parseInt(x.style.order || 999, 10)) - (parseInt(y.style.order || 999, 10)));
            const de = ordem.indexOf(arrastando);
            ordem.splice(de, 1);
            let para = ordem.indexOf(alvo);
            if (depois) para++;
            ordem.splice(para, 0, arrastando);
            ordem.forEach((el, i) => { el.style.order = i; });

            arrastando = null; alvo = null;
            gravar();
        }
        b.addEventListener('drop', e => { e.preventDefault(); soltar(); });
        b.addEventListener('dragend', () => soltar());

        b.addEventListener('dblclick', e => {
            if (!organizando) return;
            const el = e.target.closest('[data-pnl]');
            if (!el) return;
            const meia = (el.style.gridColumn || '').includes('span 6');
            el.style.gridColumn = 'span ' + (meia ? 12 : 6);
            gravar();
        });
    }

    return { ligar, aplicar, organizar };
})();
