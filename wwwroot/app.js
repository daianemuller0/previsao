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

// ---- Mapa-múndi interativo (tooltip, zoom com a roda, arrastar p/ mover) ----
// Modo apresentação: tela cheia + troca de tela automática. Em vez de rolar
// devagar, salta uma tela inteira por vez (como slides), fica parado alguns
// segundos em cada uma, e ao chegar no fim volta ao topo e repete. Ideal para
// exibir o dashboard numa TV. Sai com Esc, ao fechar a tela cheia ou no toggle.
window.presentation = (function () {
    let timer = null, running = false, page = 0;
    const DWELL = 7000;    // ms parado em cada tela antes de trocar

    function maxScroll() {
        const doc = document.scrollingElement || document.documentElement;
        return Math.max(0, doc.scrollHeight - window.innerHeight);
    }
    // Nº de telas (uma altura de viewport cada), incluindo o resto final.
    function pageCount() {
        return Math.max(1, Math.ceil(maxScroll() / window.innerHeight) + 1);
    }
    function show(i) {
        const y = Math.min(i * window.innerHeight, maxScroll());
        window.scrollTo({ top: y, behavior: 'smooth' });
    }
    function tick() {
        if (!running) return;
        page = (page + 1) % pageCount();   // avança; ao passar do fim, volta ao topo
        show(page);
    }
    function onFsChange() { if (!document.fullscreenElement) stop(); }
    function onKey(e) { if (e.key === 'Escape') stop(); }
    function start() {
        if (running) { stop(); return; }
        const el = document.documentElement;
        if (el.requestFullscreen) { try { el.requestFullscreen(); } catch (e) {} }
        running = true; page = 0;
        window.scrollTo({ top: 0, behavior: 'auto' });
        document.addEventListener('fullscreenchange', onFsChange);
        document.addEventListener('keydown', onKey);
        timer = setInterval(tick, DWELL);
    }
    function stop() {
        running = false;
        if (timer) { clearInterval(timer); timer = null; }
        document.removeEventListener('fullscreenchange', onFsChange);
        document.removeEventListener('keydown', onKey);
        if (document.fullscreenElement && document.exitFullscreen) { try { document.exitFullscreen(); } catch (e) {} }
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

window.worldMap = (function () {
    const S = {};
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
        if (S[id]) { S[id].inner = inner; if (ref) S[id].ref = ref; apply(S[id]); return; }
        const s = { wrap, inner, scale: 1, tx: 0, ty: 0, drag: null, ref: ref || null, downX: 0, downY: 0, moved: 0 };
        S[id] = s;
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
            s.drag = { x: e.clientX, y: e.clientY, tx: s.tx, ty: s.ty };
            s.downX = e.clientX; s.downY = e.clientY; s.moved = 0;
            wrap.setPointerCapture(e.pointerId); wrap.classList.add('grabbing'); tip.style.display = 'none';
        });
        wrap.addEventListener('pointermove', e => {
            if (!s.drag) return;
            s.tx = s.drag.tx + (e.clientX - s.drag.x);
            s.ty = s.drag.ty + (e.clientY - s.drag.y);
            s.moved = Math.max(s.moved, Math.abs(e.clientX - s.downX) + Math.abs(e.clientY - s.downY));
            apply(s);
        });
        const end = () => { if (s.drag) { s.drag = null; wrap.classList.remove('grabbing'); } };
        wrap.addEventListener('pointerup', end);
        wrap.addEventListener('pointercancel', end);
        // Clique num país (sem arrastar) dispara o cross-filter no componente Blazor.
        wrap.addEventListener('click', e => {
            if (s.moved > 6 || !s.ref) return;
            const p = e.target.closest && e.target.closest('path[data-xf-val]');
            if (!p) return;
            const val = p.getAttribute('data-xf-val');
            if (val) s.ref.invokeMethodAsync('ApplyCrossFilter', 'country', val);
        });
        apply(s);
    }
    function zoom(id, factor) {
        const s = S[id]; if (!s) return;
        const r = s.wrap.getBoundingClientRect();
        zoomAt(s, factor, r.left + r.width / 2, r.top + r.height / 2);
    }
    function reset(id) { const s = S[id]; if (!s) return; s.scale = 1; s.tx = 0; s.ty = 0; apply(s); }
    return { init, zoom, reset };
})();
