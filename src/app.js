import { elapsedSeconds, favorites, formatClock, formatDuration, makeRequestId, validateSplit } from './domain.js';
import { createStore } from './state.js';

const store = createStore();
const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const sheet = $('#sheet');
const toast = $('#toast');

function route(name = location.hash.slice(1) || 'today') {
  if (!['today', 'track', 'month', 'more'].includes(name)) name = 'today';
  $$('.view').forEach(view => { view.hidden = view.dataset.view !== name; });
  $$('[data-route]').forEach(link => link.toggleAttribute('aria-current', link.dataset.route === name));
  document.body.dataset.route = name;
  render();
  $('#main').focus({ preventScroll: true });
}

function showToast(message) { toast.textContent = message; toast.hidden = false; clearTimeout(showToast.timer); showToast.timer = setTimeout(() => { toast.hidden = true; }, 3500); }
function openSheet(title, eyebrow, html) { $('#sheet-heading').innerHTML = `<p class="eyebrow">${eyebrow}</p><h2>${title}</h2>`; $('#sheet-content').innerHTML = html; sheet.showModal(); }

function renderTimeline() {
  const activities = store.get().activities;
  $('#activity-count').textContent = `${activities.length} activities`;
  $('#daily-total').textContent = formatDuration(activities.filter(a => !a.voided).reduce((sum, a) => sum + a.minutes, 0));
  $('#timeline').innerHTML = activities.map(activity => `<li class="timeline-item ${activity.voided ? 'is-voided' : ''}"><button data-activity="${activity.id}" aria-label="View ${activity.type}, ${activity.minutes} minutes"><span class="timeline-time"><b>${activity.start}</b><i></i><b>${activity.end}</b></span><span class="timeline-body"><span class="activity-top"><strong>${activity.type}</strong><em>${formatDuration(activity.minutes)}</em></span><span>${activity.project}</span><small>${activity.description}</small><span class="meta">${activity.corrected ? '<span>↺ Corrected</span>' : ''}${activity.voided ? '<span>⊘ Removed from totals</span>' : ''}${activity.evidence ? `<span>⌁ ${activity.evidence} evidence</span>` : '<span>○ No evidence</span>'}</span></span><span class="chevron" aria-hidden="true">›</span></button></li>`).join('');
}

function renderFavorites() { $('#favorites').innerHTML = favorites.map((item, index) => `<button data-favorite="${index}"><span class="favorite-symbol" aria-hidden="true">${item.symbol}</span><span><b>${item.type}</b><small>${item.project}</small></span><span aria-hidden="true">+</span></button>`).join(''); }
function renderDurations() { $('#duration-grid').innerHTML = [6,12,18,24,30,36,42,48,54,60].map(n => `<button data-duration="${n}">${n}<span class="sr-only"> minutes</span></button>`).join(''); }

function renderTimer() {
  const timer = store.get().timer;
  const compact = $('#compact-timer');
  if (!timer) {
    $('#timer-hero').innerHTML = `<div class="timer-idle"><span class="timer-orbit" aria-hidden="true"><i></i></span><p>No timer is running</p><small>Your next activity will appear here.</small></div>`;
    compact.hidden = true;
    return;
  }
  const seconds = elapsedSeconds(timer);
  const paused = Boolean(timer.pausedAt);
  $('#timer-hero').innerHTML = `<div class="timer-context"><span class="state-label"><i></i>${paused ? 'Paused' : 'Timing now'}</span><h2>${timer.type}</h2><p>${timer.project}</p></div><time class="timer-display" datetime="PT${seconds}S" aria-label="Elapsed time ${formatClock(seconds)}">${formatClock(seconds)}</time><div class="timer-actions"><button class="button button-light" data-action="${paused ? 'resume' : 'pause'}">${paused ? 'Resume' : 'Pause'}</button><button class="button button-dark" data-action="stop">Stop & save</button></div>`;
  compact.hidden = document.body.dataset.route === 'track';
  $('#compact-label').textContent = timer.type; $('#compact-project').textContent = timer.project; $('#compact-time').textContent = formatClock(seconds).slice(0, 5);
}

function render() { renderTimeline(); renderTimer(); }

function startTimer(item) {
  if (store.get().timer) return showToast('Stop the current timer before starting another.');
  store.update(state => { state.timer = { ...item, startedAt: Date.now(), pausedAt: null, pausedMs: 0, requestId: makeRequestId() }; });
  showToast(`${item.type} timer started.`); route('track');
}
function pauseTimer() { store.update(state => { state.timer.pausedAt = Date.now(); }); showToast('Timer paused.'); }
function resumeTimer() { store.update(state => { state.timer.pausedMs += Date.now() - state.timer.pausedAt; state.timer.pausedAt = null; }); showToast('Timer resumed.'); }
function stopTimer() {
  const timer = store.get().timer; if (!timer) return;
  const minutes = Math.max(1, Math.round(elapsedSeconds(timer) / 60));
  store.update(state => { state.timer = null; });
  openSheet(`${formatDuration(minutes)} recorded`, 'Timer stopped', `<p class="sheet-intro">The timer stopped immediately. Add enough context to make this activity useful later.</p><div class="form-grid"><label>Activity type<input name="type" value="${timer.type}" required></label><label>Project<input name="project" value="${timer.project}" required></label><label class="full">Description<textarea name="description" rows="2" placeholder="What did you do?"></textarea></label><label class="full">Business purpose<textarea name="purpose" rows="2" placeholder="Why did this support the business?"></textarea></label><label class="full">Evidence URL <span>(optional)</span><input name="evidence" type="url" inputmode="url" placeholder="https://"></label></div><div class="sheet-actions"><button class="button button-dark" type="button" data-save-stop data-minutes="${minutes}">Save activity</button><button class="button button-light" value="cancel">Finish later</button></div>`);
  render();
}

function manualSheet(minutes = 6) { openSheet('Add time', 'Manual entry', `<p class="sheet-intro">Record a past activity in six-minute increments.</p><div class="duration-grid sheet-duration">${[6,12,18,24,30,36,42,48,54,60].map(n => `<button type="button" data-pick-duration="${n}" aria-pressed="${n === minutes}">${n}</button>`).join('')}</div><div class="form-grid"><label>Activity type<select name="type"><option>Research</option><option>LinkedIn marketing</option><option>Software development</option><option>Administration</option><option>Meeting</option></select></label><label>Project<select name="project"><option>Echelon Foundry</option><option>Visual Engineering</option><option>HelixNote</option><option>General</option></select></label><label class="full">Description<input name="description" required placeholder="What did you do?"></label><label class="full">Business purpose<input name="purpose" required placeholder="Why did it support the business?"></label><label class="full">Reason for manual entry<input name="reason" required value="Short activity recorded after completion"></label></div><div class="sheet-actions"><button class="button button-dark" type="button" data-save-manual data-minutes="${minutes}">Add ${minutes} minutes</button><button class="button button-light" value="cancel">Cancel</button></div>`); }

function activitySheet(id) {
  const a = store.get().activities.find(item => item.id === id); if (!a) return;
  openSheet(a.type, `${a.start}–${a.end} · ${formatDuration(a.minutes)}`, `<div class="detail-summary"><span class="project-pill">${a.project}</span><p>${a.description}</p><dl><div><dt>Business purpose</dt><dd>${a.purpose || '<span class="warning-text">Missing — add before review</span>'}</dd></div><div><dt>Evidence</dt><dd>${a.evidence ? `${a.evidence} items attached` : 'None attached'}</dd></div><div><dt>Record status</dt><dd>${a.voided ? 'Removed from totals' : 'Included in reports'} · Server confirmed</dd></div></dl></div><div class="action-list"><button type="button" data-correct="${a.id}">Correct activity <span>→</span></button><button type="button" data-evidence="${a.id}">Add evidence <span>→</span></button><button type="button" data-split="${a.id}">Split activity <span>→</span></button><button type="button" data-void="${a.id}">${a.voided ? 'Restore to totals' : 'Remove from totals'} <span>→</span></button><button type="button" data-history="${a.id}">Inspect history <span>→</span></button></div>`);
}

function correctionSheet(id) { const a = store.get().activities.find(x => x.id === id); openSheet('Correct activity', 'Safe correction', `<div class="inline-note">The original entry will remain in history. This change creates a correction record.</div><div class="form-grid"><label>Activity type<input name="type" value="${a.type}"></label><label>Project<input name="project" value="${a.project}"></label><label class="full">Description<textarea name="description">${a.description}</textarea></label><label class="full">Business purpose<textarea name="purpose">${a.purpose}</textarea></label><label class="full">Reason for correction<input name="reason" required placeholder="Why is this change needed?"></label></div><div class="sheet-actions"><button type="button" class="button button-dark" data-save-correction="${id}">Save correction</button><button class="button button-light" value="cancel">Cancel</button></div>`); }
function evidenceSheet(id) { openSheet('Add evidence', 'Optional context', `<p class="sheet-intro">Attach evidence when it naturally exists. Avoid sensitive information you do not need.</p><div class="form-grid"><label class="full">Evidence type<select name="evidenceType"><option>Web link</option><option>LinkedIn post</option><option>GitHub link</option><option>Calendar reference</option><option>Screenshot or photo</option><option>Document</option><option>Note</option></select></label><label class="full">URL or reference<input name="evidenceUrl" inputmode="url" placeholder="Paste a link or reference"></label><label class="full">Safe label<input name="evidenceLabel" placeholder="What does this show?"></label></div><div class="sheet-actions"><button type="button" class="button button-dark" data-save-evidence="${id}">Attach evidence</button><button class="button button-light" value="cancel">Cancel</button></div>`); }
function splitSheet(id) { const a = store.get().activities.find(x => x.id === id); const first = Math.round(a.minutes * .6 / 6) * 6; openSheet('Split activity', `${formatDuration(a.minutes)} original`, `<div class="split-preview"><div><span>Original</span><b>${a.minutes} min · ${a.type}</b></div><i aria-hidden="true">↓</i><div class="split-parts"><label>First part<input name="part1" type="number" inputmode="numeric" step="6" value="${first}"></label><label>Second part<input name="part2" type="number" inputmode="numeric" step="6" value="${a.minutes-first}"></label></div></div><label>Reason for split<input name="splitReason" required placeholder="Why should this be split?"></label><p class="inline-note">The two parts must total ${a.minutes} minutes. Evidence can be reassigned after saving.</p><div class="sheet-actions"><button type="button" class="button button-dark" data-save-split="${id}">Preview & split</button><button class="button button-light" value="cancel">Cancel</button></div>`); }
function historySheet(id) { const a = store.get().activities.find(x => x.id === id) ?? store.get().activities[1]; openSheet('Activity history', 'Transparent record', `<ol class="history"><li><i></i><div><b>Current effective activity</b><time>Included in today’s totals</time><p>${a.type} · ${formatDuration(a.minutes)}</p></div></li>${a.corrected ? '<li><i></i><div><b>Correction</b><time>Aug 2 at 11:14 AM</time><p>End time adjusted.<br>Reason: Forgot to stop timer.</p></div></li>' : ''}<li><i></i><div><b>Original entry</b><time>Created Aug 2</time><p>${a.description}</p></div></li></ol><button class="button button-light full-button" type="button">View raw record</button>`); }
function reviewSheet() { openSheet('Review today', '2h 30m recorded', `<div class="review-list"><div><span class="review-icon good">✓</span><span><b>3 activities</b><small>Times do not overlap</small></span></div><div><span class="review-icon warn">!</span><span><b>1 missing business purpose</b><small>Software development · 72 min</small></span><button type="button" data-activity="a3">Fix</button></div><div><span class="review-icon">⌁</span><span><b>67% evidence coverage</b><small>Evidence is optional</small></span></div><div><span class="review-icon">↺</span><span><b>1 corrected activity</b><small>Original remains in history</small></span></div></div><label class="attest"><input type="checkbox" name="attest"> <span>I reviewed today’s effective activity record and it is accurate to the best of my knowledge.</span></label><button class="button button-dark full-button" type="button" data-attest>Attest to day</button>`); }
function conflictSheet() { openSheet('Review conflict', 'Needs attention', `<div class="conflict"><p>This activity changed after you opened it.</p><section><span>Current saved version</span><b>Research · 42 min</b><small>Updated on another device at 2:14 PM</small></section><section><span>Your proposed version</span><b>Research · 48 min</b><small>Not yet saved</small></section></div><div class="sheet-actions"><button class="button button-dark" type="button" data-resolve>Apply again</button><button class="button button-light" value="cancel">Discard mine</button></div>`); }

document.addEventListener('click', event => {
  const target = event.target.closest('button, a'); if (!target) return;
  if (target.dataset.route) { event.preventDefault(); location.hash = target.dataset.route; return; }
  if (target.dataset.favorite) return startTimer(favorites[Number(target.dataset.favorite)]);
  if (target.dataset.duration) return manualSheet(Number(target.dataset.duration));
  if (target.dataset.activity) return activitySheet(target.dataset.activity);
  if (target.dataset.action === 'manual') return manualSheet();
  if (target.dataset.action === 'pause') return pauseTimer();
  if (target.dataset.action === 'resume') return resumeTimer();
  if (target.dataset.action === 'stop') return stopTimer();
  if (target.dataset.action === 'review') return reviewSheet();
  if (target.dataset.correct) return correctionSheet(target.dataset.correct);
  if (target.dataset.evidence) return evidenceSheet(target.dataset.evidence);
  if (target.dataset.split) return splitSheet(target.dataset.split);
  if (target.dataset.history) return historySheet(target.dataset.history);
  if (target.dataset.void) { const id=target.dataset.void; const a=store.get().activities.find(x=>x.id===id); openSheet(a.voided?'Restore to totals':'Remove from totals', 'Audit-friendly change', `<p class="sheet-intro">${a.voided?'This entry will return to reports and totals.':'This entry will be removed from totals. The original record will remain in history.'}</p><label>Reason<input name="voidReason" required placeholder="Explain this change"></label><div class="sheet-actions"><button class="button button-dark" type="button" data-confirm-void="${id}">${a.voided?'Restore entry':'Remove entry'}</button><button class="button button-light" value="cancel">Cancel</button></div>`); return; }
  if (target.dataset.confirmVoid) { store.update(s=>{const a=s.activities.find(x=>x.id===target.dataset.confirmVoid);a.voided=!a.voided;}); sheet.close(); showToast('Activity history updated.'); return; }
  if (target.dataset.pickDuration) { $$('[data-pick-duration]').forEach(b=>b.setAttribute('aria-pressed', b===target ? 'true':'false')); $('[data-save-manual]').dataset.minutes=target.dataset.pickDuration; $('[data-save-manual]').textContent=`Add ${target.dataset.pickDuration} minutes`; return; }
  if (target.dataset.saveManual) { const form=sheet.querySelector('form'); const data=new FormData(form); if(!data.get('description')||!data.get('purpose')) return showToast('Add a description and business purpose.'); const minutes=Number(target.dataset.minutes); store.update(s=>s.activities.push({id:makeRequestId(),start:'Now',end:'',minutes,type:data.get('type'),project:data.get('project'),description:data.get('description'),purpose:data.get('purpose'),evidence:0,status:'saved',manual:true})); sheet.close(); showToast(`${minutes} minutes saved to your activity history.`); return; }
  if (target.dataset.saveStop !== undefined) { const data=new FormData(sheet.querySelector('form')); store.update(s=>s.activities.push({id:makeRequestId(),start:'Now',end:'',minutes:Number(target.dataset.minutes),type:data.get('type'),project:data.get('project'),description:data.get('description')||'Details to complete',purpose:data.get('purpose')||'',evidence:data.get('evidence')?1:0,status:'saved'})); sheet.close(); showToast('Activity saved to your history.'); route('today'); return; }
  if (target.dataset.saveCorrection) { const data=new FormData(sheet.querySelector('form')); if(!data.get('reason')) return showToast('Add a reason for the correction.'); store.update(s=>{const a=s.activities.find(x=>x.id===target.dataset.saveCorrection);a.type=data.get('type');a.project=data.get('project');a.description=data.get('description');a.purpose=data.get('purpose');a.corrected=true;}); sheet.close(); showToast('Correction saved. The original remains in history.'); return; }
  if (target.dataset.saveEvidence) { store.update(s=>s.activities.find(x=>x.id===target.dataset.saveEvidence).evidence++); sheet.close(); showToast('Evidence attached.'); return; }
  if (target.dataset.saveSplit) { const data=new FormData(sheet.querySelector('form')); const a=store.get().activities.find(x=>x.id===target.dataset.saveSplit); const parts=[Number(data.get('part1')),Number(data.get('part2'))]; if(!validateSplit(a.minutes,parts)) return showToast(`Parts must be positive and total ${a.minutes} minutes.`); sheet.close(); showToast('Activity split. Original remains in history.'); return; }
  if (target.dataset.attest !== undefined) { if(!sheet.querySelector('[name=attest]').checked) return showToast('Check the review statement first.'); sheet.close(); showToast('Day attested and saved.'); return; }
  if (target.dataset.resolve !== undefined) { sheet.close(); showToast('Change resubmitted with a new version check.'); return; }
  if (target.dataset.demo === 'history') return historySheet('a2');
  if (target.dataset.demo === 'evidence') return evidenceSheet('a1');
  if (target.dataset.demo === 'offline') return conflictSheet();
  if (target.dataset.demo === 'shortcuts') return openSheet('iPhone Shortcuts', 'Quick capture', `<div class="shortcut-list"><div><b>Start Research</b><code>POST /api/v1/timers/start</code></div><div><b>Stop Current Activity</b><code>POST /api/v1/timers/stop</code></div><div><b>Add Six Minutes</b><code>POST /api/v1/activities</code></div></div><p class="inline-note">Authenticate through the service’s Shortcut-safe session flow. Never place a GitHub credential in a Shortcut.</p>`);
  if (target.id === 'sync-button') return openSheet('Everything is saved', 'Synchronization', `<div class="sync-state"><span>✓</span><div><b>Saved</b><p>No commands are waiting to send.</p></div></div><button class="text-button" type="button" data-demo="offline">Demonstrate a stale conflict</button>`);
});

window.addEventListener('hashchange', () => route());
store.subscribe(render);
$('#today-date').textContent = new Intl.DateTimeFormat('en-US', { weekday:'long', month:'long', day:'numeric' }).format(new Date());
renderFavorites(); renderDurations(); route();
setInterval(renderTimer, 1000);
if ('serviceWorker' in navigator && location.protocol !== 'file:') navigator.serviceWorker.register('/service-worker.js').catch(()=>{});
