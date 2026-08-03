export class ServiceError extends Error {
  constructor(code, message, { status = 400, retryable = false, details = {} } = {}) {
    super(message); this.name = 'ServiceError'; this.code = code; this.status = status; this.retryable = retryable; this.details = details;
  }
}

export const requireField = (value, field) => {
  if (value === undefined || value === null || value === '') throw new ServiceError('validation_failed', `${field} is required.`, { details: { field } });
  return value;
};
