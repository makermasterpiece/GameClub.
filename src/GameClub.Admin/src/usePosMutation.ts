import { useRef, useState } from 'react';
import { api, ApiError, errorMessage } from './api';
import type { PendingPos } from './posModels';

// Device-local recovery journal, not financial state or authentication storage.
export function usePosMutation(token: string, employeeId: string, refresh: () => void) {
  const key = `gameclub.pos.pending.${employeeId}`;
  const [pending, setPending] = useState<PendingPos | null>(() => {
    try { return JSON.parse(sessionStorage.getItem(key) ?? 'null') as PendingPos | null; } catch { return null; }
  });
  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);
  const [message, setMessage] = useState('');
  async function execute(operation: PendingPos) {
    if (inFlight.current) return false;
    inFlight.current = true;
    setBusy(true); setMessage('');
    try {
      if (!operation.path.startsWith('/api/admin/pos/')) throw new Error('Invalid recovery path');
      // Persist before sending, so reload/relogin cannot silently generate another operation ID.
      sessionStorage.setItem(key, JSON.stringify(operation)); setPending(operation);
      await api(operation.path, token, 'POST', operation.body);
      sessionStorage.removeItem(key); setPending(null); setMessage(operation.success); refresh(); return true;
    } catch (failure) {
      // Validation/conflict/permission responses reject this operation; timeouts/5xx remain uncertain.
      if (failure instanceof ApiError && [400, 403, 404, 409, 413, 429].includes(failure.status)) {
        sessionStorage.removeItem(key); setPending(null); refresh();
      }
      setMessage(errorMessage(failure)); return false;
    } finally { inFlight.current = false; setBusy(false); }
  }
  return { pending, busy, message, execute, locked: busy || pending !== null };
}
