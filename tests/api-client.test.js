import test from 'node:test';
import assert from 'node:assert/strict';
import { LedgerClient, ApiError } from '../src/api/client.js';
import { handleRequest } from '../worker/src/handler.js';

const fetchImpl = (url, options = {}) => handleRequest(new Request(url, options));

test('health and config satisfy the v1 envelope', async () => {
  const client = new LedgerClient({ baseUrl: 'https://ledger.test/api/v1', fetchImpl });
  assert.deepEqual(await client.health(), { status: 'ok' });
  const config = await client.config();
  assert.equal(config.schema_version, '1.0.0');
  assert.equal(config.display.six_minute_controls, true);
});

test('project and activity-type projections are versioned', async () => {
  const client = new LedgerClient({ baseUrl: 'https://ledger.test/api/v1', fetchImpl });
  assert.ok((await client.projects()).every(item => item.id && item.version));
  assert.ok((await client.activityTypes()).every(item => item.id && item.version));
});

test('unknown resources use structured service errors', async () => {
  const client = new LedgerClient({ baseUrl: 'https://ledger.test/api/v1', fetchImpl });
  await assert.rejects(() => client.request('/missing'), error => error instanceof ApiError && error.status === 404);
});
