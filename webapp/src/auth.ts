import type { GitHubUser } from './types';

const TOKEN_KEY = 'bcnuget_github_token';

export function getStoredToken(): string | null {
  return localStorage.getItem(TOKEN_KEY);
}

export function storeToken(token: string): void {
  localStorage.setItem(TOKEN_KEY, token);
}

export function clearToken(): void {
  localStorage.removeItem(TOKEN_KEY);
}

/** Validate a GitHub token by fetching the authenticated user. Returns null if invalid. */
export async function validateToken(token: string): Promise<GitHubUser | null> {
  try {
    const res = await fetch('https://api.github.com/user', {
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json' },
    });
    if (!res.ok) return null;
    return (await res.json()) as GitHubUser;
  } catch {
    return null;
  }
}

export interface DeviceFlowCodes {
  device_code: string;
  user_code: string;
  verification_uri: string;
  expires_in: number;
  interval: number;
}

/**
 * Start the GitHub OAuth device flow. Proxied through the backend to avoid browser CORS
 * restrictions (GitHub's OAuth endpoints do not return CORS headers).
 */
export async function startDeviceFlow(backendUrl: string, clientId: string): Promise<DeviceFlowCodes> {
  let res: Response;
  try {
    res = await fetch(`${backendUrl}/auth/device/code`, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify({ client_id: clientId, scope: 'read:user' }),
    });
  } catch {
    throw new Error(`Cannot reach the backend at ${backendUrl}.`);
  }
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`Device flow failed (${res.status}): ${body || res.statusText}`);
  }
  return (await res.json()) as DeviceFlowCodes;
}

export interface DevicePollResult {
  access_token?: string;
  error?: string;
  error_description?: string;
  interval?: number;
}

export async function pollDeviceFlow(
  backendUrl: string,
  clientId: string,
  deviceCode: string,
): Promise<DevicePollResult> {
  const res = await fetch(`${backendUrl}/auth/device/token`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({
      client_id: clientId,
      device_code: deviceCode,
      grant_type: 'urn:ietf:params:oauth:grant-type:device_code',
    }),
  });
  if (!res.ok) throw new Error(`Token poll failed: ${res.status}`);
  return (await res.json()) as DevicePollResult;
}
