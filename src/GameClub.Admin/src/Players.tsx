import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { api, errorMessage } from './api';
import { dateTime, money } from './models';
import type { LedgerEntry, Player, Wallet } from './models';
import { Empty, Modal } from './components';
import type { RunAction } from './components';

export function Players({ token, permissions, run, revision }: { token: string; permissions: string[]; run: RunAction; revision: number }) {
  const [players, setPlayers] = useState<Player[]>([]);
  const [search, setSearch] = useState('');
  const [error, setError] = useState('');
  const [selected, setSelected] = useState<Player | null>(null);
  const [wallet, setWallet] = useState<Wallet | null>(null);
  const [ledger, setLedger] = useState<LedgerEntry[]>([]);
  const [create, setCreate] = useState(false);
  const [change, setChange] = useState<'deposit' | 'adjustment' | null>(null);
  const [localRevision, setLocalRevision] = useState(0);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    let active = true;
    const timer = setTimeout(() => {
      void api<Player[]>(`/api/admin/users?search=${encodeURIComponent(search)}`, token).then(result => { if (active) { setPlayers(result); setError(''); } }).catch(failure => { if (active) setError(errorMessage(failure)); });
    }, 250);
    return () => { active = false; clearTimeout(timer); };
  }, [search, token, revision, localRevision]);
  useEffect(() => {
    let active = true; setWallet(null); setLedger([]);
    if (selected) void Promise.all([api<Wallet>(`/api/admin/users/${selected.id}/wallet`, token), api<LedgerEntry[]>(`/api/admin/users/${selected.id}/wallet/transactions`, token)])
      .then(([balance, entries]) => { if (active) { setWallet(balance); setLedger(entries); } }).catch(failure => { if (active) setError(errorMessage(failure)); });
    return () => { active = false; };
  }, [selected, token, localRevision, revision]);
  async function createPlayer(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const form = event.currentTarget; const values = new FormData(form); setBusy(true);
    const body = { username: String(values.get('username')), password: String(values.get('password')), displayName: String(values.get('displayName')) || null, email: null, phone: null };
    (form.elements.namedItem('password') as HTMLInputElement).value = '';
    const ok = await run(() => api('/api/admin/users', token, 'POST', body), 'Игрок создан.'); body.password = ''; setBusy(false);
    if (ok) { setCreate(false); setLocalRevision(v => v + 1); }
  }
  async function setStatus(status: string) {
    if (!selected) return;
    setBusy(true); const ok = await run(() => api(`/api/admin/users/${selected.id}/status`, token, 'PUT', { status }), 'Статус игрока обновлён.'); setBusy(false);
    if (ok) { setSelected({ ...selected, status }); setLocalRevision(v => v + 1); }
  }
  return <><header className="page-heading"><div><span className="eyebrow">ИГРОКИ И БАЛАНС</span><h1>Игроки клуба</h1></div>{permissions.includes('ClubOperate') && <button className="primary" onClick={() => setCreate(true)}>+ Новый игрок</button>}</header>
    {error && <p className="notice error" role="alert">{error}</p>}
    <div className="map-toolbar"><label className="search"><span className="sr-only">Поиск игрока</span><input placeholder="Логин или имя игрока…" value={search} onChange={e => setSearch(e.target.value)} /></label><span className="muted">До 100 результатов</span></div>
    <div className="two-columns"><section className="panel table-panel"><table><thead><tr><th>Игрок</th><th>Статус</th><th><span className="sr-only">Выбрать</span></th></tr></thead><tbody>{players.map(player => <tr key={player.id} className={selected?.id === player.id ? 'selected-row' : ''}><td><strong>{player.displayName || player.username}</strong><span className="cell-secondary">{player.username}</span></td><td><span className={`badge ${player.status === 'Active' ? 'available' : 'warning'}`}>{player.status}</span></td><td><button onClick={() => setSelected(player)}>Открыть</button></td></tr>)}</tbody></table>{players.length === 0 && <Empty>Игроки не найдены.</Empty>}</section>
    {selected && <section className="panel"><header className="panel-header"><h2>{selected.displayName || selected.username}</h2><button className="icon-button" aria-label="Закрыть игрока" onClick={() => setSelected(null)}>×</button></header>
      <div className="balance-card"><span>Доступный баланс</span><strong>{wallet ? money(wallet.available) : '—'}</strong><div><span>Баланс {wallet ? money(wallet.balance) : '—'}</span><span>Резерв {wallet ? money(wallet.reserved) : '—'}</span></div></div>
      <div className="form-actions">{permissions.includes('ClubOperate') && <button className="primary" onClick={() => setChange('deposit')}>Пополнить</button>}{permissions.includes('ClubManageMoney') && <button onClick={() => setChange('adjustment')}>Корректировка</button>}</div>
      {permissions.includes('ClubManageMoney') && <label className="group-assignment">Статус аккаунта<select value={selected.status ?? 'Active'} disabled={busy} onChange={e => void setStatus(e.target.value)}><option value="Active">Активен</option><option value="Disabled">Отключён</option><option value="Banned">Заблокирован</option></select></label>}
      <h3>История операций</h3><div className="ledger">{ledger.map(entry => <div key={entry.id}><div><strong>{({ Deposit: 'Пополнение', GamingCharge: 'Игровая сессия', Adjustment: 'Корректировка', Refund: 'Возврат', Bonus: 'Бонус' } as Record<string, string>)[entry.type] ?? entry.type}</strong><small>{dateTime(entry.createdAtUtc)}</small></div><div className={entry.amount < 0 ? 'negative' : 'positive'}>{entry.amount > 0 ? '+' : ''}{money(entry.amount)}<small>Баланс {money(entry.balanceAfter)}</small></div></div>)}{ledger.length === 0 && <p className="muted">Операций пока нет.</p>}</div>
    </section>}</div>
    {create && <Modal title="Новый игрок" onClose={() => setCreate(false)}><form className="stack" onSubmit={createPlayer}><label>Логин<input name="username" required minLength={3} maxLength={32} autoComplete="off" /></label><label>Имя для отображения<input name="displayName" maxLength={100} /></label><label>Пароль (12–128 символов)<input name="password" type="password" required minLength={12} maxLength={128} autoComplete="new-password" /></label><button className="primary" disabled={busy}>Создать игрока</button></form></Modal>}
    {change && selected && <WalletModal token={token} player={selected} kind={change} run={run} onClose={() => setChange(null)} onSuccess={() => { setChange(null); setLocalRevision(v => v + 1); }} />}
  </>;
}

function WalletModal({ token, player, kind, run, onClose, onSuccess }: { token: string; player: Player; kind: 'deposit' | 'adjustment'; run: RunAction; onClose: () => void; onSuccess: () => void }) {
  const [busy, setBusy] = useState(false);
  const operation = useRef(crypto.randomUUID());
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const values = new FormData(event.currentTarget); setBusy(true);
    const ok = await run(() => api(`/api/admin/users/${player.id}/wallet/${kind}`, token, 'POST', { amount: Number(values.get('amount')), operationId: operation.current }), 'Операция сохранена в журнале кошелька.');
    setBusy(false); if (ok) onSuccess();
  }
  return <Modal title={`${kind === 'deposit' ? 'Пополнение' : 'Корректировка'} · ${player.username}`} onClose={onClose}><form className="stack" onSubmit={submit} onChange={() => { operation.current = crypto.randomUUID(); }}><label>Сумма{kind === 'adjustment' ? ' (со знаком)' : ''}<input name="amount" type="number" min={kind === 'deposit' ? '0.01' : undefined} step="0.01" required autoFocus /></label><p className="muted">Изменение баланса будет записано отдельной операцией с указанием сотрудника.</p><button className="primary" disabled={busy}>Подтвердить</button></form></Modal>;
}
