import type { AccessKey, ConfigResponse, CreateKeyInput, MeResponse } from './types';

/** Resolve the backend base URL (…/api). */
export function resolveBackendUrl(): string {
  const params = new URLSearchParams(window.location.search);
  const explicit = params.get('backendUrl');
  if (explicit) return explicit.replace(/\/+$/, '');

  const host = window.location.hostname;
  if (host === 'localhost' || host === '127.0.0.1') {
    // Vite dev server; talk to the local Functions host.
    return 'http://localhost:7071/api';
  }

  // Served from the Function App at /api/app, so the API lives at the same origin under /api.
  return `${window.location.origin}/api`;
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function request<T>(
  backendUrl: string,
  token: string,
  path: string,
  init?: RequestInit,
): Promise<T> {
  const res = await fetch(`${backendUrl}/${path}`, {
    ...init,
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(init?.headers ?? {}),
    },
  });
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new ApiError(res.status, text || res.statusText);
  }
  if (res.status === 204) {
    return undefined as T;
  }
  return (await res.json()) as T;
}

export async function fetchConfig(backendUrl: string): Promise<ConfigResponse> {
  const res = await fetch(`${backendUrl}/config`);
  if (!res.ok) throw new Error(`Failed to load config: ${res.status}`);
  return (await res.json()) as ConfigResponse;
}

export function fetchMe(backendUrl: string, token: string): Promise<MeResponse> {
  return request<MeResponse>(backendUrl, token, 'me');
}

export function listAccessKeys(backendUrl: string, token: string): Promise<AccessKey[]> {
  return request<AccessKey[]>(backendUrl, token, 'accesskeys');
}

export function createAccessKey(
  backendUrl: string,
  token: string,
  input: CreateKeyInput,
): Promise<AccessKey> {
  return request<AccessKey>(backendUrl, token, `accesskeys/${encodeURIComponent(input.name)}`, {
    method: 'POST',
    body: JSON.stringify({
      feeds: input.feeds,
      type: input.type,
      description: input.description,
      email: input.email,
      expiresInDays: input.expiresInDays,
    }),
  });
}

export function revokeAccessKey(backendUrl: string, token: string, name: string): Promise<AccessKey> {
  return request<AccessKey>(backendUrl, token, `accesskeys/${encodeURIComponent(name)}/revoke`, {
    method: 'POST',
  });
}

export function renewAccessKey(
  backendUrl: string,
  token: string,
  name: string,
  expiresInDays: number | null,
): Promise<AccessKey> {
  return request<AccessKey>(backendUrl, token, `accesskeys/${encodeURIComponent(name)}/renew`, {
    method: 'POST',
    body: JSON.stringify({ expiresInDays }),
  });
}

export function rotateAccessKey(
  backendUrl: string,
  token: string,
  name: string,
  oldKeyValidHours: number,
): Promise<AccessKey> {
  return request<AccessKey>(backendUrl, token, `accesskeys/${encodeURIComponent(name)}/rotate`, {
    method: 'POST',
    body: JSON.stringify({ oldKeyValidHours }),
  });
}

export function removeAccessKey(backendUrl: string, token: string, name: string): Promise<void> {
  return request<void>(backendUrl, token, `accesskeys/${encodeURIComponent(name)}`, {
    method: 'DELETE',
  });
}
