import { makeRequestId } from '../domain.js';

export class ApiError extends Error {
  constructor(message, { status = 0, code = 'unknown', requestId } = {}) { super(message); this.name = 'ApiError'; this.status = status; this.code = code; this.requestId = requestId; }
}

const required = (value, field) => { if (value == null) throw new ApiError(`The service response is missing ${field}.`, { code: 'invalid_response' }); return value; };

const validateEnvelope = data => {
  required(data?.meta?.request_id, 'meta.request_id');
  required(data?.meta?.server_time, 'meta.server_time');
  if (data.meta.api_version !== 'v1') throw new ApiError('The service returned an unsupported API version.', { code: 'invalid_response' });
  return required(data.data, 'data');
};

/** Cloudflare-only browser client. Authentication is supplied by a secure session cookie. */
export class LedgerClient {
  constructor({ baseUrl = '/api/v1', fetchImpl = fetch, timeoutMs = 8000 } = {}) { this.baseUrl = baseUrl; this.fetchImpl = fetchImpl; this.timeoutMs = timeoutMs; }

  async request(path, { method = 'GET', body, requestId = body ? makeRequestId() : undefined, signal } = {}) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort('timeout'), this.timeoutMs);
    if (signal) signal.addEventListener('abort', () => controller.abort(signal.reason), { once: true });
    try {
      const response = await this.fetchImpl(`${this.baseUrl}${path}`, { method, credentials: 'include', signal: controller.signal, headers: { 'Accept': 'application/json', 'Content-Type': 'application/json', ...(requestId ? { 'Idempotency-Key': requestId, 'X-Request-ID': requestId } : {}) }, body: body ? JSON.stringify({ request_id: requestId, client_timestamp: new Date().toISOString(), payload: body }) : undefined });
      if (response.status === 409) throw new ApiError('This record changed after you opened it.', { status: 409, code: 'conflict', requestId });
      if (response.status === 429) throw new ApiError('The service is busy. Your command is safe to retry.', { status: 429, code: 'rate_limited', requestId });
      if (!response.ok) throw new ApiError('The ledger service could not complete this request.', { status: response.status, code: 'service_error', requestId });
      const data = await response.json(); required(data, 'response body');
      return data;
    } catch (error) {
      if (error instanceof ApiError) throw error;
      if (controller.signal.aborted) throw new ApiError('The ledger service took too long to respond.', { code: 'timeout', requestId });
      throw new ApiError('You appear to be offline. The command is waiting to send.', { code: 'offline', requestId });
    } finally { clearTimeout(timeout); }
  }

  async health(options) { return validateEnvelope(await this.request('/health', options)); }
  async config(options) { return validateEnvelope(await this.request('/config', options)); }
  async projects(options) { return validateEnvelope(await this.request('/projects', options)); }
  async activityTypes(options) { return validateEnvelope(await this.request('/activity-types', options)); }
}
