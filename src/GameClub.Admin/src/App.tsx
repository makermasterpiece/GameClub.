import { useCallback, useState } from 'react';
import type { FormEvent } from 'react';
import { api, errorMessage } from './api';
import type { Authentication } from './api';
import { AdminWorkspace } from './AdminWorkspace';

export function App() {
  const [auth, setAuth] = useState<Authentication | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const logout = useCallback(() => { setAuth(null); setError(''); }, []);
  async function login(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const values = new FormData(form);
    setBusy(true); setError('');
    const credentials = { username: String(values.get('username')), password: String(values.get('password')) };
    (form.elements.namedItem('password') as HTMLInputElement).value = '';
    try { setAuth(await api<Authentication>('/api/admin/auth/login', null, 'POST', credentials)); }
    catch (failure) { setError(errorMessage(failure)); }
    finally { credentials.password = ''; setBusy(false); }
  }
  if (auth) return <AdminWorkspace auth={auth} onLogout={logout} />;
  return <main className="login-screen">
    <div className="login-layout">
      <section className="login-intro"><div className="brand"><span className="brand-mark">G</span> GameClub</div>
        <span className="eyebrow">ПАНЕЛЬ СОТРУДНИКА</span><h1>Ваш клуб.<br />Всё под контролем.</h1>
        <p>Станции, игроки и игровое время — в одном рабочем пространстве.</p>
        <div className="local-note"><span className="status-dot" /> Self-hosted · Данные остаются в вашем клубе</div>
      </section>
      <form className="login-card" onSubmit={login}><span className="eyebrow">GAMECLUB ADMIN</span><h2>Вход в панель</h2>
        <p className="muted">Используйте учётную запись сотрудника.</p>
        <label>Логин<input name="username" autoComplete="username" required maxLength={100} autoFocus /></label>
        <label>Пароль<input name="password" type="password" autoComplete="current-password" required maxLength={128} /></label>
        {error && <p className="notice error" role="alert">{error}</p>}
        <button className="primary full" disabled={busy}>{busy ? 'Входим…' : 'Войти'}</button>
        <p className="login-help">Нет доступа? Обратитесь к администратору клуба.</p>
      </form>
    </div>
  </main>;
}
