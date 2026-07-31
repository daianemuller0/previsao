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
