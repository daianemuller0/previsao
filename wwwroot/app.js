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

// Alterna o recolhimento da sidebar guardando a preferência do usuário.
window.appToggleSidebar = (collapsed) => {
    try { localStorage.setItem('hsf-sidebar', collapsed ? '1' : '0'); } catch { }
};
window.appSidebarState = () => {
    try { return localStorage.getItem('hsf-sidebar') === '1'; } catch { return false; }
};

// ---- Mapa-múndi interativo (tooltip, zoom com a roda, arrastar p/ mover) ----
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
