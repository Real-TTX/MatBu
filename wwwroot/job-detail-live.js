(() => {
  const root = document.querySelector('.job-live[data-job-id]');
  if (!root) return;
  const jobId = root.dataset.jobId;
  const isCross = root.dataset.jobCross === '1';

  const TERMINAL = new Set(['Completed', 'Failed', 'Fehler', 'Cancelled', 'Abgebrochen']);
  const fmtBytes = v => {
    v = v || 0;
    return v >= 1024 ** 4 ? `${(v / 1024 ** 4).toFixed(2)} TiB`
      : v >= 1024 ** 3 ? `${(v / 1024 ** 3).toFixed(2)} GiB`
      : v >= 1024 ** 2 ? `${(v / 1024 ** 2).toFixed(1)} MiB`
      : v >= 1024 ? `${(v / 1024).toFixed(1)} KiB` : `${v} B`;
  };
  const fmtSpeed = v => v <= 0 ? '—' : v >= 1024 ** 2 ? `${(v / 1024 ** 2).toFixed(1)} MB/s` : `${(v / 1024).toFixed(1)} KB/s`;
  const fmtDuration = seconds => {
    if (!isFinite(seconds) || seconds <= 0) return '—';
    seconds = Math.ceil(seconds);
    const d = Math.floor(seconds / 86400); seconds %= 86400;
    const h = Math.floor(seconds / 3600); seconds %= 3600;
    const m = Math.floor(seconds / 60); seconds %= 60;
    if (d > 0) return `${d}T ${h}Std`;
    if (h > 0) return `${h}Std ${m}Min`;
    if (m > 0) return `${m}Min ${seconds}Sek`;
    return `${seconds}Sek`;
  };
  const pct = (value, total) => total > 0 ? Math.min(100, Math.round(value * 100 / total)) : 0;
  const set = (sel, text) => { const el = root.querySelector(sel); if (el) el.textContent = text; };
  const bar = (sel, percent) => { const el = root.querySelector(sel); if (el) el.style.width = `${percent}%`; };

  const stage = (name, value, total, speed) => {
    const p = pct(value, total);
    set(`[data-stage-${name}-percent]`, `${p}%`);
    bar(`[data-stage-${name}-bar]`, p);
    set(`[data-stage-${name}-bytes]`, `${fmtBytes(value)} / ${fmtBytes(total)}`);
    set(`[data-stage-${name}-speed]`, fmtSpeed(speed));
  };

  const applyState = job => {
    const stateEl = root.querySelector('[data-job-state]');
    if (stateEl) {
      stateEl.textContent = job.state;
      const failed = job.state === 'Failed' || job.state === 'Fehler' || job.state === 'Abgebrochen';
      const active = job.state === 'Running' || job.state === 'Queued';
      stateEl.className = `status ${failed ? 'offline' : active ? 'warning' : ''}`;
    }
    document.querySelectorAll('[data-job-cancel]').forEach(btn => { btn.hidden = job.state !== 'Running'; });
    const phaseEl = root.querySelector('[data-job-phase]');
    if (phaseEl) {
      const phase = job.phase && job.phase.trim() ? job.phase : job.state;
      phaseEl.textContent = phase;
      phaseEl.classList.toggle('is-paused', phase.includes('pausiert'));
    }
    root.classList.toggle('is-active', job.state === 'Running' || job.state === 'Queued');
  };

  const overall = job => {
    const est = job.estimatedStoredBytes || job.totalBytes || 0;
    // The frontier stage drives the overall bar, using each stage's own unit/denominator so read
    // (uncompressed source bytes) is never divided by the compressed stored estimate.
    let produced, target;
    if (job.bytesWritten > 0) { produced = job.bytesWritten; target = est; }
    else if (isCross && job.bytesTransferred > 0) { produced = job.bytesTransferred; target = est; }
    else if (job.bytesRead > 0) { produced = job.bytesRead; target = job.estimatedSourceBytes || 0; }
    else { produced = 0; target = est; }

    const percent = (TERMINAL.has(job.state) && job.state === 'Completed') ? 100 : pct(produced, target);
    set('[data-job-overall-percent]', `${percent}%`);
    bar('[data-job-overall-bar]', percent);
    set('[data-job-overall-bytes]', `${fmtBytes(job.bytesWritten)} / ${fmtBytes(est)}`);

    // Average throughput = finished bytes of the frontier stage / elapsed since start, per the user's
    // "based on what was finally written and how much time elapsed" definition.
    const elapsed = (Date.now() - new Date(job.createDate).getTime()) / 1000;
    const remaining = target - produced;
    const avg = elapsed > 0 ? produced / elapsed : 0;
    set('[data-job-overall-speed]', avg > 0 ? `Ø ${fmtSpeed(avg)}` : '—');

    const remainingEl = root.querySelector('[data-job-remaining]');
    if (remainingEl) {
      if (job.state === 'Completed') remainingEl.textContent = 'Fertig';
      else if (job.state === 'Abgebrochen') remainingEl.textContent = 'Abgebrochen';
      else if (job.state === 'Failed' || job.state === 'Fehler') remainingEl.textContent = 'Fehlgeschlagen';
      else if (target > 0 && avg > 0 && remaining > 0) remainingEl.textContent = `noch ${fmtDuration(remaining / avg)}`;
      else remainingEl.textContent = '—';
    }
  };

  const stepClass = s => (s === 'Failed' || s === 'Cancelled') ? 'offline' : (s === 'Started' || s === 'Queued' || s === 'Resumed') ? 'warning' : '';
  const appendSteps = steps => {
    const list = root.parentElement.querySelector('[data-job-timeline]') || document.querySelector('[data-job-timeline]');
    if (!list) return;
    const empty = document.querySelector('[data-timeline-empty]');
    const known = new Set([...list.querySelectorAll('li[data-step-id]')].map(li => li.dataset.stepId));
    let added = 0;
    for (const step of steps) {
      if (known.has(String(step.id))) continue;
      const li = document.createElement('li');
      li.className = (step.state || '').toLowerCase();
      li.dataset.stepId = step.id;
      const meta = [];
      if (step.instanceName) meta.push(`<span>⇄ ${step.instanceName}</span>`);
      if (step.location) meta.push(`<code>${step.location}</code>`);
      if (step.totalBytes > 0) meta.push(`<span>${step.bytesTransferred} / ${step.totalBytes} Bytes</span>`);
      const time = new Date(step.createDate).toLocaleString('de-DE');
      li.innerHTML = `<div class="timeline-marker"></div><div class="timeline-content">`
        + `<div class="timeline-heading"><div><span class="status ${stepClass(step.state)}">${step.stage}</span><strong>${step.state}</strong></div><time>${time}</time></div>`
        + `<p>${step.message || ''}</p><div class="timeline-meta">${meta.join('')}</div></div>`;
      list.appendChild(li);
      added++;
    }
    if (added > 0) {
      list.hidden = false;
      if (empty) empty.hidden = true;
    }
    const count = document.querySelector('[data-step-count]');
    if (count) count.textContent = list.querySelectorAll('li[data-step-id]').length;
  };

  let timer = null;
  const refresh = async () => {
    let payload;
    try {
      const res = await fetch(`/api/transfer-jobs/${jobId}`, { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      payload = await res.json();
    } catch { return; }
    const job = payload.job;
    if (!job) return;

    applyState(job);
    overall(job);
    stage('read', job.bytesRead, job.estimatedSourceBytes, job.readSpeedBytesPerSecond);
    if (isCross) stage('transfer', job.bytesTransferred, job.estimatedStoredBytes, job.speedBytesPerSecond);
    stage('write', job.bytesWritten, job.estimatedStoredBytes, job.writeSpeedBytesPerSecond);

    const updated = root.querySelector('[data-job-updated]');
    if (updated) updated.textContent = new Date(job.updateDate).toLocaleTimeString('de-DE');

    document.querySelectorAll('[data-job-source]').forEach(e => e.textContent = fmtBytes(job.sourceBytes));
    document.querySelectorAll('[data-job-stored]').forEach(e => e.textContent = fmtBytes(job.storedBytes));
    document.querySelectorAll('[data-job-estimated]').forEach(e => e.textContent = fmtBytes(job.estimatedStoredBytes));

    const errPanel = document.querySelector('[data-job-error-panel]');
    if (errPanel) {
      const hasError = job.error && job.error.trim();
      errPanel.hidden = !hasError;
      const errText = errPanel.querySelector('[data-job-error]');
      if (errText && hasError) errText.textContent = job.error;
    }

    if (Array.isArray(payload.steps)) appendSteps(payload.steps);

    if (TERMINAL.has(job.state) && timer) { clearInterval(timer); timer = null; }
  };

  document.addEventListener('click', async event => {
    const btn = event.target.closest('[data-job-cancel]');
    if (!btn) return;
    event.preventDefault();
    if (!window.confirm('Diese Ausführung wirklich abbrechen?')) return;
    btn.disabled = true;
    try {
      const res = await fetch(`/api/transfer-jobs/${btn.dataset.jobCancel}/cancel`, { method: 'POST' });
      if (!res.ok) btn.disabled = false;
      else refresh();
    } catch {
      btn.disabled = false;
    }
  });
  refresh();
  timer = window.setInterval(refresh, 1000);
})();
