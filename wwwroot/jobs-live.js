(() => {
  const formatSpeed = value => value <= 0 ? '—' : value >= 1024 * 1024 ? `${(value / 1024 / 1024).toFixed(1)} MB/s` : `${(value / 1024).toFixed(1)} KB/s`;
  const formatDate = value => new Date(value).toLocaleString('de-DE');
  const formatBytes = value => value >= 1024 ** 4 ? `${(value / 1024 ** 4).toFixed(2)} TiB` : value >= 1024 ** 3 ? `${(value / 1024 ** 3).toFixed(2)} GiB` : value >= 1024 ** 2 ? `${(value / 1024 ** 2).toFixed(2)} MiB` : value >= 1024 ? `${(value / 1024).toFixed(1)} KiB` : `${value || 0} Bytes`;
  const formatRemaining = job => {
    if (!job.totalBytes) return '—';
    const remainingBytes = Math.max(0, job.totalBytes - job.bytesTransferred);
    if (remainingBytes === 0) return 'Fertig';
    if (!job.speedBytesPerSecond) return '—';
    let seconds = Math.ceil(remainingBytes / job.speedBytesPerSecond);
    const days = Math.floor(seconds / 86400);
    seconds %= 86400;
    const hours = Math.floor(seconds / 3600);
    seconds %= 3600;
    const minutes = Math.floor(seconds / 60);
    seconds %= 60;
    if (days > 0) return `${days}T ${hours}Std`;
    if (hours > 0) return `${hours}Std ${minutes}Min`;
    if (minutes > 0) return `${minutes}Min ${seconds}Sek`;
    return `${seconds}Sek`;
  };
  const refresh = async () => {
    const response = await fetch('/api/transfer-jobs');
    if (!response.ok) return;
    const jobs = await response.json();
    for (const job of jobs) {
      const row = document.querySelector(`[data-job-id="${job.id}"]`);
      if (!row) continue;
      const percent = job.totalBytes ? Math.min(100, Math.round(job.bytesTransferred * 100 / job.totalBytes)) : 0;
      row.querySelector('[data-job-bar]').style.width = `${percent}%`;
      const progress = row.querySelector('[data-job-progress]');
      if (progress) progress.textContent = progress.dataset.jobProgressMode === 'compact'
        ? `${percent}%`
        : `${percent}% (${job.bytesTransferred} / ${job.totalBytes} bytes)`;
      row.querySelector('[data-job-speed]').textContent = formatSpeed(job.speedBytesPerSecond);
      const state = row.querySelector('[data-job-state] .status');
      if (state) {
        state.textContent = job.state;
        const failed = job.state === 'Failed' || job.state === 'Fehler';
        const active = job.state === 'Running' || job.state === 'Queued';
        state.className = `status ${failed ? 'offline' : active ? 'warning' : ''}`;
      }
      const remaining = row.querySelector('[data-job-remaining]');
      if (remaining) remaining.textContent = formatRemaining(job);
      const checkpoint = row.querySelector('[data-job-checkpoint]');
      if (checkpoint) checkpoint.textContent = job.checkpointPath || '';

      const destination = row.querySelector('[data-job-destination] code');
      if (destination && job.resolvedDestination) destination.textContent = job.resolvedDestination;

      const updated = row.querySelector('[data-job-updated]');
      if (updated) updated.textContent = formatDate(job.updateDate);

      const error = row.querySelector('[data-job-error]');
      if (error) error.textContent = job.error || '';
      const sizes = row.querySelector('[data-job-sizes]');
      if (sizes) sizes.textContent = `Quelle ${formatBytes(job.sourceBytes)} · gespeichert ${formatBytes(job.storedBytes)}`;
      const efficiency = row.querySelector('[data-job-efficiency]');
      if (efficiency && (job.method === 0 || job.method === 'Full')) {
        const rate = job.sourceBytes ? `${(job.storedBytes * 100 / job.sourceBytes).toFixed(1)}%` : '—';
        efficiency.textContent = `Geschätzt ${formatBytes(job.estimatedStoredBytes)} · Rate ${rate}`;
      }
      const estimate = row.querySelector('[data-job-estimate]');
      if (estimate) estimate.textContent = `≈ ${formatBytes(job.estimatedStoredBytes)} gespeichert`;
    }
  };
  refresh();
  window.setInterval(refresh, 2000);
})();
