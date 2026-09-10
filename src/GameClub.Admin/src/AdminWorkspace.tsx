import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, errorMessage } from './api';
import type { Authentication } from './api';
import { Dashboard } from './Dashboard';
import { Players } from './Players';
import { CatalogPage } from './CatalogPage';
import { GamesPage } from './GamesPage';
import { PosPage } from './PosPage';
import { Employees } from './Employees';
import { FeedbackContext } from './components';
import { roleName } from './models';

export function AdminWorkspace({ auth, onLogout }: { auth: Authentication; onLogout: () => void }) {
  const [view, setView] = useState('dashboard');
  const [feedback, setFeedback] = useState<{ text: string; error: boolean } | null>(null);
  const [revision, setRevision] = useState(0);
  const [logoutBusy, setLogoutBusy] = useState(false);
  const token = auth.accessToken;
  const permissions = auth.employee.permissions;
  const run = useCallback(async (operation: () => Promise<unknown>, success: string): Promise<boolean> => {
    setFeedback(null);
    try { await operation(); setFeedback({ text: success, error: false }); setRevision(value => value + 1); return true; }
    catch (failure) {
      setFeedback({ text: errorMessage(failure), error: true });
      if (failure instanceof ApiError && failure.status === 401) onLogout();
      return false;
    }
  }, [onLogout]);
  useEffect(() => {
    const check = async () => {
      if (Date.now() >= Date.parse(auth.expiresAtUtc)) { onLogout(); return; }
      try { await api('/api/admin/auth/me', token); }
      catch (failure) { if (failure instanceof ApiError && failure.status === 401) onLogout(); }
    };
    const timer = setInterval(() => void check(), 15000);
    return () => clearInterval(timer);
  }, [token, auth.expiresAtUtc, onLogout]);
  async function logout() {
    setLogoutBusy(true);
    try { await api('/api/admin/auth/logout', token, 'POST'); }
    catch { /* Removing the in-memory credential is always allowed; no persistent browser token exists. */ }
    finally { onLogout(); }
  }
  const items = [{ id: 'dashboard', title: 'Карта клуба', icon: '▦' }, { id: 'players', title: 'Игроки', icon: '♙' }, { id: 'catalog', title: 'Тарифы и пакеты', icon: '◷' }, { id: 'games', title: 'Игры', icon: '▤' }, { id: 'pos', title: 'POS и смены', icon: '▣' }, ...(permissions.includes('ClubManageEmployees') ? [{ id: 'employees', title: 'Сотрудники', icon: '◇' }] : [])];
  return <FeedbackContext.Provider value={feedback}><div className="admin-shell">
    <aside className="sidebar"><div className="brand"><span className="brand-mark">G</span> GameClub</div><div className="sidebar-caption">УПРАВЛЕНИЕ КЛУБОМ</div><nav aria-label="Разделы панели">{items.map(item => <button key={item.id} className={view === item.id ? 'active' : ''} aria-current={view === item.id ? 'page' : undefined} onClick={() => { setView(item.id); setFeedback(null); }}><span aria-hidden="true">{item.icon}</span>{item.title}</button>)}</nav>
      <div className="sidebar-bottom"><div className="employee-avatar">{auth.employee.username.slice(0, 1).toLocaleUpperCase()}</div><div><strong>{auth.employee.username}</strong><span>{roleName(auth.employee.role)}</span></div><button disabled={logoutBusy} onClick={() => void logout()}>Выйти</button></div>
    </aside>
    <main className="workspace"><div className="workspace-topline"><span>GAMECLUB / {items.find(i => i.id === view)?.title}</span><span>Локальная система</span></div>
      {feedback && <div className={`notice feedback ${feedback.error ? 'error' : ''}`} role={feedback.error ? 'alert' : 'status'}><span>{feedback.text}</span><button className="icon-button" aria-label="Скрыть уведомление" onClick={() => setFeedback(null)}>×</button></div>}
      {view === 'dashboard' && <Dashboard token={token} permissions={permissions} run={run} revision={revision} />}
      {view === 'players' && <Players token={token} permissions={permissions} run={run} revision={revision} />}
      {view === 'catalog' && <CatalogPage token={token} permissions={permissions} run={run} revision={revision} />}
      {view === 'games' && <GamesPage token={token} permissions={permissions} run={run} revision={revision} />}
      {view === 'pos' && <PosPage token={token} employeeId={auth.employee.id} permissions={permissions} run={run} />}
      {view === 'employees' && permissions.includes('ClubManageEmployees') && <Employees token={token} run={run} revision={revision} />}
    </main>
  </div></FeedbackContext.Provider>;
}
