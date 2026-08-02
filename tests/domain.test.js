import test from 'node:test'; import assert from 'node:assert/strict'; import { elapsedSeconds, formatClock, formatDuration, validateSplit } from '../src/domain.js';
test('formats durations for ledger display',()=>{assert.equal(formatDuration(6),'6m');assert.equal(formatDuration(72),'1h 12m');});
test('formats timer with tabular clock fields',()=>assert.equal(formatClock(3758),'01:02:38'));
test('calculates elapsed time from authoritative timestamps and pauses',()=>assert.equal(elapsedSeconds({startedAt:1000,pausedAt:null,pausedMs:2000},8000),5));
test('freezes elapsed time while paused',()=>assert.equal(elapsedSeconds({startedAt:1000,pausedAt:7000,pausedMs:1000},9000),5));
test('validates split totals and positive integer parts',()=>{assert.equal(validateSplit(60,[36,24]),true);assert.equal(validateSplit(60,[36,18]),false);assert.equal(validateSplit(60,[60,0]),false);});
