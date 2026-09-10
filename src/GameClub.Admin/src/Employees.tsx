import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api, errorMessage } from './api';
import { roleName, dateTime } from './models';
import { Empty, Modal } from './components';
import type { RunAction } from './components';

type EmployeeRow = { id: string; username: string; role: string; isActive: boolean; createdAtUtc: string };
export function Employees({ token, run, revision }: { token: string; run: RunAction; revision: number }) {
  const [rows, setRows] = useState<EmployeeRow[]>([]);
  const [error, setError] = useState('');
  const [create, setCreate] = useState(false);
  const [edit, setEdit] = useState<EmployeeRow | null>(null);
  const [password, setPassword] = useState<EmployeeRow | null>(null);
  const [busy, setBusy] = useState(false);
  const [localRevision, setLocalRevision] = useState(0);
  useEffect(() => { let active = true; void api<EmployeeRow[]>('/api/admin/employees', token).then(result => { if (active) { setRows(result); setError(''); } }).catch(failure => { if (active) setError(errorMessage(failure)); }); return () => { active = false; }; }, [token, revision, localRevision]);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const form = event.currentTarget; const values = new FormData(form); setBusy(true);
    const body = password ? { password: String(values.get('password')) } : edit ? { role: String(values.get('role')), isActive: values.get('active') === 'true' } : { username: String(values.get('username')), password: String(values.get('password')), role: String(values.get('role')) };
    const field = form.elements.namedItem('password') as HTMLInputElement | null; if (field) field.value = '';
    const ok = await run(() => api(password ? `/api/admin/employees/${password.id}/password` : edit ? `/api/admin/employees/${edit.id}/access` : '/api/admin/employees', token, password || edit ? 'PUT' : 'POST', body), 'Учётная запись сотрудника обновлена.');
    if ('password' in body) body.password = ''; setBusy(false);
    if (ok) { setCreate(false); setEdit(null); setPassword(null); setLocalRevision(v => v + 1); }
  }
  return <><header className="page-heading"><div><span className="eyebrow">ДОСТУП К ПАНЕЛИ</span><h1>Сотрудники</h1></div><button className="primary" onClick={() => setCreate(true)}>+ Сотрудник</button></header>
    {error && <p className="notice error" role="alert">{error}</p>}
    <div className="permission-summary"><div><strong>Оператор</strong><p>Станции, игровые сессии, игроки и пополнение кошельков.</p></div><div><strong>Менеджер</strong><p>Дополнительно: тарифы, пакеты и корректировка баланса.</p></div><div><strong>Администратор</strong><p>Полный доступ, сотрудники и безопасность станций.</p></div></div>
    <section className="panel table-panel"><table><thead><tr><th>Логин</th><th>Роль</th><th>Доступ</th><th>Создан</th><th>Действия</th></tr></thead><tbody>{rows.map(row => <tr key={row.id}><td><strong>{row.username}</strong></td><td>{roleName(row.role)}</td><td><span className={`badge ${row.isActive ? 'available' : 'warning'}`}>{row.isActive ? 'Активен' : 'Отключён'}</span></td><td>{dateTime(row.createdAtUtc)}</td><td><div className="row-actions"><button onClick={() => setEdit(row)}>Доступ</button><button onClick={() => setPassword(row)}>Пароль</button></div></td></tr>)}</tbody></table>{rows.length === 0 && <Empty>Загружаем сотрудников…</Empty>}</section>
    {(create || edit || password) && <Modal title={password ? `Новый пароль · ${password.username}` : edit ? `Доступ · ${edit.username}` : 'Новый сотрудник'} onClose={() => { setCreate(false); setEdit(null); setPassword(null); }}><form className="stack" onSubmit={submit}>
      {create && <label>Логин<input name="username" required minLength={3} maxLength={100} autoComplete="off" /></label>}
      {(create || password) && <label>Пароль (12–128 символов)<input name="password" type="password" required minLength={12} maxLength={128} autoComplete="new-password" /></label>}
      {!password && <label>Роль<select name="role" defaultValue={edit?.role ?? 'Operator'} required><option value="Operator">Оператор</option><option value="Manager">Менеджер</option><option value="Administrator">Администратор</option></select></label>}
      {edit && <label>Доступ<select name="active" defaultValue={String(edit.isActive)}><option value="true">Активен</option><option value="false">Отключён</option></select></label>}
      {(edit || password) && <p className="muted small">Сохранение отзовёт текущие токены сотрудника. Последнего активного администратора отключить нельзя.</p>}
      <button className="primary" disabled={busy}>Сохранить</button>
    </form></Modal>}
  </>;
}
