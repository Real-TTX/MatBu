(function () {
  const modes = ['system', 'light', 'dark'];
  const labels = { system: 'System', light: 'Hell', dark: 'Dunkel' };
  const media = window.matchMedia('(prefers-color-scheme: dark)');
  const buttons = () => document.querySelectorAll('[data-theme-toggle]');
  function apply(mode) {
    const actual = mode === 'system' ? (media.matches ? 'dark' : 'light') : mode;
    document.documentElement.dataset.theme = actual;
    for (const control of buttons()) {
      control.textContent = actual === 'dark' ? '☾' : actual === 'light' ? '☀' : '◐';
      control.setAttribute('aria-label', `Darstellung: ${labels[mode]}`);
      control.title = `Darstellung: ${labels[mode]} (klicken zum Wechseln)`;
      control.dataset.mode = mode;
    }
  }
  function setMode(mode) { localStorage.setItem('matbu-theme', mode); apply(mode); }
  const initial = localStorage.getItem('matbu-theme');
  apply(modes.includes(initial) ? initial : 'system');
  document.addEventListener('click', event => {
    const control = event.target.closest('[data-theme-toggle]');
    if (!control) return;
    const current = control.dataset.mode || 'system';
    setMode(modes[(modes.indexOf(current) + 1) % modes.length]);
  });
  media.addEventListener?.('change', () => {
    if ((localStorage.getItem('matbu-theme') || 'system') === 'system') apply('system');
  });
})();
