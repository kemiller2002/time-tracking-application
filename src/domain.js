export const favorites = [
  { type: 'Research', project: 'Visual Engineering', symbol: '⌁' },
  { type: 'LinkedIn', project: 'Echelon Foundry', symbol: '↗' },
  { type: 'Development', project: 'HelixNote', symbol: '⌘' },
  { type: 'Meeting', project: 'General', symbol: '◎' }
];

export function elapsedSeconds(timer, now = Date.now()) {
  if (!timer) return 0;
  const end = timer.pausedAt ?? now;
  return Math.max(0, Math.floor((end - timer.startedAt - timer.pausedMs) / 1000));
}

export function formatClock(seconds) {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  return [h, m, s].map(value => String(value).padStart(2, '0')).join(':');
}

export function formatDuration(minutes) {
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  return remainder ? `${hours}h ${remainder}m` : `${hours}h`;
}

export function validateSplit(total, parts) {
  return parts.every(value => Number.isInteger(value) && value > 0) && parts.reduce((sum, value) => sum + value, 0) === total;
}

export function makeRequestId() {
  return globalThis.crypto?.randomUUID?.() ?? `req-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}
