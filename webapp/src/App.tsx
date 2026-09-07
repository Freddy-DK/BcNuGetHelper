import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  createAccessKey,
  fetchConfig,
  fetchMe,
  listAccessKeys,
  removeAccessKey,
  renewAccessKey,
  resolveBackendUrl,
  revokeAccessKey,
} from './api';
import {
  clearToken,
  getStoredToken,
  pollDeviceFlow,
  startDeviceFlow,
  storeToken,
  validateToken,
  type DeviceFlowCodes,
} from './auth';
import type { AccessKey, AccessKeyType, MeResponse } from './types';
import { FEEDS } from './types';

const BACKEND_URL = resolveBackendUrl();

export function App() {
  const [clientId, setClientId] = useState<string | null>(null);
  const [token, setToken] = useState<string | null>(getStoredToken());
  const [me, setMe] = useState<MeResponse | null>(null);
  const [checkingAuth, setCheckingAuth] = useState(true);

  useEffect(() => {
    fetchConfig(BACKEND_URL)
      .then((c) => setClientId(c.clientId || null))
      .catch(() => setClientId(null));
  }, []);

  const signIn = useCallback(async (newToken: string) => {
    const me = await fetchMe(BACKEND_URL, newToken);
    storeToken(newToken);
    setToken(newToken);
    setMe(me);
  }, []);

  const signOut = useCallback(() => {
    clearToken();
    setToken(null);
    setMe(null);
  }, []);

  // Validate any stored token on load.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      const stored = getStoredToken();
      if (!stored) {
        setCheckingAuth(false);
        return;
      }
      try {
        const meResponse = await fetchMe(BACKEND_URL, stored);
        if (!cancelled) setMe(meResponse);
      } catch {
        if (!cancelled) {
          clearToken();
          setToken(null);
        }
      } finally {
        if (!cancelled) setCheckingAuth(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  if (checkingAuth) {
    return (
      <div className="center">
        <p className="muted">Signing in…</p>
      </div>
    );
  }

  if (!token || !me) {
    return <SignIn clientId={clientId} onSignedIn={signIn} />;
  }

  if (!me.hasAccess) {
    return <NoAccess me={me} onSignOut={signOut} />;
  }

  return <Dashboard token={token} me={me} onSignOut={signOut} />;
}

function SignIn({
  clientId,
  onSignedIn,
}: {
  clientId: string | null;
  onSignedIn: (token: string) => Promise<void>;
}) {
  const [pat, setPat] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [device, setDevice] = useState<DeviceFlowCodes | null>(null);

  const useToken = useCallback(
    async (token: string) => {
      setBusy(true);
      setError(null);
      try {
        const user = await validateToken(token);
        if (!user) {
          setError('That GitHub token is invalid or expired.');
          return;
        }
        await onSignedIn(token);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Sign-in failed.');
      } finally {
        setBusy(false);
      }
    },
    [onSignedIn],
  );

  const startGitHub = useCallback(async () => {
    if (!clientId) return;
    setBusy(true);
    setError(null);
    try {
      const codes = await startDeviceFlow(BACKEND_URL, clientId);
      setDevice(codes);

      const deadline = Date.now() + codes.expires_in * 1000;
      let interval = codes.interval;
      while (Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, (interval + 1) * 1000));
        const result = await pollDeviceFlow(BACKEND_URL, clientId, codes.device_code);
        if (result.access_token) {
          await useToken(result.access_token);
          setDevice(null);
          return;
        }
        if (result.error === 'authorization_pending') continue;
        if (result.error === 'slow_down') {
          interval = result.interval ?? interval + 5;
          continue;
        }
        throw new Error(result.error_description ?? result.error ?? 'Authorization failed.');
      }
      throw new Error('The device code expired. Please try again.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'GitHub sign-in failed.');
      setDevice(null);
    } finally {
      setBusy(false);
    }
  }, [clientId, useToken]);

  return (
    <div className="center">
      <div className="card signin">
        <h1>NuGet Access Keys</h1>
        <p className="muted">Sign in with GitHub to manage feed access keys.</p>

        {device ? (
          <div className="device">
            <p>
              Open{' '}
              <a href={device.verification_uri} target="_blank" rel="noreferrer">
                {device.verification_uri}
              </a>{' '}
              and enter this code:
            </p>
            <div className="code">{device.user_code}</div>
            <p className="muted">Waiting for authorization…</p>
          </div>
        ) : (
          <>
            {clientId && (
              <button className="btn primary" disabled={busy} onClick={startGitHub}>
                Sign in with GitHub
              </button>
            )}

            <div className="pat">
              <label htmlFor="pat">{clientId ? 'Or use a personal access token' : 'Personal access token'}</label>
              <input
                id="pat"
                type="password"
                placeholder="ghp_…"
                value={pat}
                autoComplete="off"
                onChange={(e) => setPat(e.target.value)}
              />
              <button className="btn" disabled={busy || !pat} onClick={() => useToken(pat.trim())}>
                Sign in with token
              </button>
              <p className="hint">A token with the <code>read:user</code> scope is sufficient.</p>
            </div>
          </>
        )}

        {error && <p className="error">{error}</p>}
      </div>
    </div>
  );
}

function NoAccess({ me, onSignOut }: { me: MeResponse; onSignOut: () => void }) {
  return (
    <div className="center">
      <div className="card signin">
        <h1>Access denied</h1>
        <p>
          <strong>{me.login}</strong> is not on the access list for this NuGet server.
        </p>
        <p className="muted">Ask an administrator to add you to the WEBAPPUSERS variable.</p>
        <button className="btn" onClick={onSignOut}>
          Sign out
        </button>
      </div>
    </div>
  );
}

function Dashboard({
  token,
  me,
  onSignOut,
}: {
  token: string;
  me: MeResponse;
  onSignOut: () => void;
}) {
  const [keys, setKeys] = useState<AccessKey[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setKeys(await listAccessKeys(BACKEND_URL, token));
    } catch (err) {
      handleError(err, setError, onSignOut);
    } finally {
      setLoading(false);
    }
  }, [token, onSignOut]);

  useEffect(() => {
    load();
  }, [load]);

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">NuGet Access Keys</div>
        <div className="user">
          {me.avatarUrl && <img src={me.avatarUrl} alt="" className="avatar" />}
          <span>{me.name ?? me.login}</span>
          <button className="btn small" onClick={onSignOut}>
            Sign out
          </button>
        </div>
      </header>

      <main className="content">
        <CreateKeyForm
          token={token}
          existingNames={keys.map((k) => k.name.toLowerCase())}
          onCreated={(key) => setKeys((prev) => [key, ...prev.filter((k) => k.name !== key.name)])}
          onError={(err) => handleError(err, setError, onSignOut)}
        />

        {error && <p className="error banner">{error}</p>}

        <section className="keys">
          <div className="keys-head">
            <h2>Access keys</h2>
            <button className="btn small" onClick={load} disabled={loading}>
              {loading ? 'Refreshing…' : 'Refresh'}
            </button>
          </div>

          {loading && keys.length === 0 ? (
            <p className="muted">Loading…</p>
          ) : keys.length === 0 ? (
            <p className="muted">No access keys yet.</p>
          ) : (
            <KeyList
              keys={keys}
              token={token}
              onChanged={(key) =>
                setKeys((prev) => prev.map((k) => (k.name === key.name ? key : k)))
              }
              onRemoved={(name) => setKeys((prev) => prev.filter((k) => k.name !== name))}
              onError={(err) => handleError(err, setError, onSignOut)}
            />
          )}
        </section>
      </main>
    </div>
  );
}

function CreateKeyForm({
  token,
  existingNames,
  onCreated,
  onError,
}: {
  token: string;
  existingNames: string[];
  onCreated: (key: AccessKey) => void;
  onError: (err: unknown) => void;
}) {
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [feeds, setFeeds] = useState<string[]>([...FEEDS]);
  const [type, setType] = useState<AccessKeyType>('read');
  const [expiresInDays, setExpiresInDays] = useState('');
  const [busy, setBusy] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);

  const toggleFeed = (feed: string) =>
    setFeeds((prev) => (prev.includes(feed) ? prev.filter((f) => f !== feed) : [...prev, feed]));

  const allSelected = feeds.length === FEEDS.length;

  const submit = async () => {
    setLocalError(null);
    const trimmed = name.trim();
    if (!trimmed) {
      setLocalError('Name is required.');
      return;
    }
    if (existingNames.includes(trimmed.toLowerCase())) {
      setLocalError('An access key with that name already exists.');
      return;
    }
    if (feeds.length === 0) {
      setLocalError('Select at least one feed.');
      return;
    }
    const days = expiresInDays.trim() === '' ? null : Number(expiresInDays);
    if (days !== null && (!Number.isInteger(days) || days <= 0)) {
      setLocalError('Expiry must be a positive whole number of days, or blank for no expiry.');
      return;
    }

    setBusy(true);
    try {
      const key = await createAccessKey(BACKEND_URL, token, {
        name: trimmed,
        feeds,
        type,
        description: description.trim(),
        expiresInDays: days,
      });
      onCreated(key);
      setName('');
      setDescription('');
      setFeeds([...FEEDS]);
      setType('read');
      setExpiresInDays('');
    } catch (err) {
      onError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="card create">
      <h2>New access key</h2>
      <div className="form-grid">
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="acme-ci" />
        </label>
        <label>
          Description
          <input
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Who or what this key is for"
          />
        </label>
        <fieldset className="feeds">
          <legend>Feeds</legend>
          <label className="check">
            <input
              type="checkbox"
              checked={allSelected}
              onChange={() => setFeeds(allSelected ? [] : [...FEEDS])}
            />
            All feeds
          </label>
          {FEEDS.map((feed) => (
            <label className="check" key={feed}>
              <input type="checkbox" checked={feeds.includes(feed)} onChange={() => toggleFeed(feed)} />
              {feed}
            </label>
          ))}
        </fieldset>
        <fieldset className="type">
          <legend>Access</legend>
          {(['read', 'write', 'readwrite'] as const).map((t) => (
            <label className="check" key={t}>
              <input type="radio" name="type" checked={type === t} onChange={() => setType(t)} />
              {t}
            </label>
          ))}
        </fieldset>
        <label>
          Expires in (days)
          <input
            value={expiresInDays}
            onChange={(e) => setExpiresInDays(e.target.value)}
            placeholder="never"
            inputMode="numeric"
          />
        </label>
      </div>
      {localError && <p className="error">{localError}</p>}
      <button className="btn primary" disabled={busy} onClick={submit}>
        {busy ? 'Creating…' : 'Create access key'}
      </button>
    </section>
  );
}

function KeyList({
  keys,
  token,
  onChanged,
  onRemoved,
  onError,
}: {
  keys: AccessKey[];
  token: string;
  onChanged: (key: AccessKey) => void;
  onRemoved: (name: string) => void;
  onError: (err: unknown) => void;
}) {
  return (
    <div className="table-wrap">
      <table className="keys-table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Description</th>
            <th>Feeds</th>
            <th>Access</th>
            <th>Status</th>
            <th>Expires</th>
            <th>Key</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {keys.map((key) => (
            <KeyRow
              key={key.name}
              accessKey={key}
              token={token}
              onChanged={onChanged}
              onRemoved={onRemoved}
              onError={onError}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function KeyRow({
  accessKey,
  token,
  onChanged,
  onRemoved,
  onError,
}: {
  accessKey: AccessKey;
  token: string;
  onChanged: (key: AccessKey) => void;
  onRemoved: (name: string) => void;
  onError: (err: unknown) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [revealed, setRevealed] = useState(false);
  const [copied, setCopied] = useState(false);

  const inactive = isInactive(accessKey);

  const run = async (fn: () => Promise<void>) => {
    setBusy(true);
    try {
      await fn();
    } catch (err) {
      onError(err);
    } finally {
      setBusy(false);
    }
  };

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(accessKey.key);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      setRevealed(true);
    }
  };

  const revoke = () =>
    run(async () => onChanged(await revokeAccessKey(BACKEND_URL, token, accessKey.name)));

  const renew = () =>
    run(async () => {
      const input = window.prompt('Renew for how many days? Leave blank for no expiry.', '');
      if (input === null) return;
      const days = input.trim() === '' ? null : Number(input);
      if (days !== null && (!Number.isInteger(days) || days <= 0)) {
        onError(new Error('Enter a positive whole number of days, or leave blank.'));
        return;
      }
      onChanged(await renewAccessKey(BACKEND_URL, token, accessKey.name, days));
    });

  const remove = () =>
    run(async () => {
      if (!window.confirm(`Permanently remove access key "${accessKey.name}"?`)) return;
      await removeAccessKey(BACKEND_URL, token, accessKey.name);
      onRemoved(accessKey.name);
    });

  return (
    <tr className={inactive ? 'inactive' : ''}>
      <td className="mono">{accessKey.name}</td>
      <td>{accessKey.description ?? <span className="muted">—</span>}</td>
      <td>
        {accessKey.feeds.map((f) => (
          <span className="badge" key={f}>
            {f}
          </span>
        ))}
      </td>
      <td>{accessKey.type ?? 'read'}</td>
      <td>
        <span className={`status ${inactive ? 'off' : 'on'}`}>{inactive ? 'Revoked' : 'Active'}</span>
      </td>
      <td>{formatExpiry(accessKey.expires)}</td>
      <td className="mono keycell">
        <span title={revealed ? accessKey.key : undefined}>
          {revealed ? accessKey.key : mask(accessKey.key)}
        </span>
        <button className="btn tiny" onClick={copy} disabled={busy}>
          {copied ? 'Copied' : 'Copy'}
        </button>
      </td>
      <td className="actions">
        {inactive ? (
          <button className="btn tiny" onClick={renew} disabled={busy}>
            Renew
          </button>
        ) : (
          <button className="btn tiny warn" onClick={revoke} disabled={busy}>
            Revoke
          </button>
        )}
        <button className="btn tiny danger" onClick={remove} disabled={busy}>
          Remove
        </button>
      </td>
    </tr>
  );
}

function isInactive(key: AccessKey): boolean {
  return key.expires !== null && new Date(key.expires).getTime() <= Date.now();
}

function mask(key: string): string {
  return key.length <= 8 ? '••••' : `${key.slice(0, 4)}…${key.slice(-4)}`;
}

function formatExpiry(expires: string | null): string {
  if (!expires) return 'Never';
  return new Date(expires).toLocaleString();
}

function handleError(err: unknown, setError: (msg: string) => void, onSignOut: () => void) {
  if (err instanceof ApiError && (err.status === 401 || err.status === 403)) {
    onSignOut();
    return;
  }
  setError(err instanceof Error ? err.message : 'Something went wrong.');
}
