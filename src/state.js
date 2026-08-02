const seed = {
  timer: null,
  activities: [
    { id: 'a1', start: '9:00', end: '9:42', minutes: 42, type: 'Research', project: 'Visual Engineering', description: 'Reviewed composition research', purpose: 'Apply visual evidence to product direction', evidence: 2, status: 'saved' },
    { id: 'a2', start: '10:15', end: '10:51', minutes: 36, type: 'LinkedIn marketing', project: 'Echelon Foundry', description: 'Published and discussed product note', purpose: 'Develop qualified business relationships', evidence: 1, corrected: true, status: 'saved' },
    { id: 'a3', start: '11:00', end: '12:12', minutes: 72, type: 'Software development', project: 'HelixNote', description: 'Implemented activity detail view', purpose: '', evidence: 0, status: 'saved' }
  ],
  queue: []
};

export function createStore(storage = localStorage) {
  let state;
  try { state = JSON.parse(storage.getItem('echelon-ledger-state')) ?? seed; } catch { state = seed; }
  const listeners = new Set();
  const persist = () => storage.setItem('echelon-ledger-state', JSON.stringify(state));
  return {
    get: () => state,
    update(mutator) { state = structuredClone(state); mutator(state); persist(); listeners.forEach(fn => fn(state)); },
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    reset() { state = structuredClone(seed); persist(); listeners.forEach(fn => fn(state)); }
  };
}
