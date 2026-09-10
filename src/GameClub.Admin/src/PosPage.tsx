import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api, errorMessage } from './api';
import { Empty, Modal } from './components';
import type { RunAction } from './components';
import { dateTime, money } from './models';
import type { Player, Station, DashboardData } from './models';
import type { Cart, PendingPos, PosCatalog, Sale, SaleDetails, Shift, ShiftSummary } from './posModels';
import { canAddProduct, cartTotal, paymentName } from './posModels';
import { PosCatalogPanel } from './PosCatalogPanel';
import { usePosMutation } from './usePosMutation';

export function PosPage({ token, employeeId, permissions, run }: { token: string; employeeId: string; permissions: string[]; run: RunAction }) {
  const [view, setView] = useState('sale');
  const [catalog, setCatalog] = useState<PosCatalog>({ categories: [], products: [] });
  const [shift, setShift] = useState<Shift | null>(null);
  const [history, setHistory] = useState<Shift[]>([]);
  const [sales, setSales] = useState<Sale[]>([]);
  const [players, setPlayers] = useState<Player[]>([]);
  const [stations, setStations] = useState<Station[]>([]);
  const [summary, setSummary] = useState<ShiftSummary | null>(null);
  const [details, setDetails] = useState<SaleDetails | null>(null);
  const [error, setError] = useState('');
  const [loaded, setLoaded] = useState(false);
  const [revision, setRevision] = useState(0);
  const refresh = useCallback(() => setRevision(v => v + 1), []);
  const mutation = usePosMutation(token, employeeId, refresh);
  const [cart, setCart] = useState<Cart>([]);
  const [userId, setUserId] = useState('');
  const [stationId, setStationId] = useState('');
  const [method, setMethod] = useState('Cash');
  const [confirm, setConfirm] = useState<PendingPos | null>(null);
  const [shiftAction, setShiftAction] = useState<'open' | 'close' | null>(null);
  const [refund, setRefund] = useState(false);
  const canEdit = permissions.includes('ClubManageCatalog');
  const canRefund = permissions.includes('ClubManageMoney');
  useEffect(() => {
    let active = true;
    const load = async () => {
      try {
        const [cat, current, allSales, allShifts, users, dashboard] = await Promise.all([
          api<PosCatalog>('/api/admin/pos/catalog', token), api<{ shift: Shift | null }>('/api/admin/pos/shifts/current', token),
          api<Sale[]>('/api/admin/pos/sales', token), api<Shift[]>('/api/admin/pos/shifts', token),
          api<Player[]>('/api/admin/users', token), api<DashboardData>('/api/admin/dashboard', token),
        ]);
        if (active) { setCatalog(cat); setShift(current.shift); setSales(allSales); setHistory(allShifts); setPlayers(users); setStations(dashboard.stations); setError(''); setLoaded(true); }
      } catch (failure) { if (active) setError(errorMessage(failure)); }
    };
    void load(); const timer = setInterval(() => void load(), 15000);
    return () => { active = false; clearInterval(timer); };
  }, [token, revision]);
  const total = cartTotal(cart, catalog.products);
  const locked = mutation.locked || !loaded || !!error;
  const canSell = !!shift && cart.length > 0 && Number.isFinite(total) && total > 0 && !locked && (method !== 'Wallet' || !!userId);
  async function inspectSale(id: string) { try { setDetails(await api<SaleDetails>(`/api/admin/pos/sales/${id}`, token)); } catch (failure) { setError(errorMessage(failure)); } }
  async function inspectShift(id: string) { try { setSummary(await api<ShiftSummary>(`/api/admin/pos/shifts/${id}`, token)); } catch (failure) { setError(errorMessage(failure)); } }
  function prepareSale() {
    if (!canSell) return;
    setConfirm({ path: '/api/admin/pos/sales', body: { operationId: crypto.randomUUID(), shiftId: shift!.id, userId: userId || null, stationId: stationId || null, paymentMethod: method, items: cart, expectedTotal: total }, success: 'Продажа сохранена. Остаток и оплата обновлены.' });
  }
  async function submitShift(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (locked) return;
    const amount = Number(new FormData(event.currentTarget).get('cash'));
    await mutation.execute({ path: shiftAction === 'open' ? '/api/admin/pos/shifts' : `/api/admin/pos/shifts/${shift!.id}/close`, body: { operationId: crypto.randomUUID(), ...(shiftAction === 'open' ? { openingCash: amount } : { closingCash: amount }) }, success: shiftAction === 'open' ? 'Смена открыта.' : 'Смена закрыта. Итоги доступны в истории.' });
    setShiftAction(null);
  }
  async function submitRefund(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (!details || !shift || locked) return;
    const reason = String(new FormData(event.currentTarget).get('reason'));
    await mutation.execute({ path: `/api/admin/pos/sales/${details.sale.id}/refund`, body: { operationId: crypto.randomUUID(), shiftId: shift.id, reason }, success: 'Полный возврат записан. Товары возвращены в остаток.' });
    setRefund(false); setDetails(null);
  }
  return <><header className="page-heading"><div><span className="eyebrow">ПРОДАЖИ И СМЕНЫ</span><h1>POS</h1></div><button disabled={locked} onClick={() => setShiftAction(shift ? 'close' : 'open')}>{shift ? 'Закрыть мою смену' : 'Открыть смену'}</button></header>
    {error && <p className="notice error" role="alert">{error}</p>}{mutation.message && <p className="notice" role="status">{mutation.message}</p>}
    {mutation.pending && <div className="notice error" role="alert"><p>Исход предыдущей операции ещё не подтверждён. Новые операции заблокированы; повтор использует тот же идентификатор.</p><button disabled={mutation.busy} onClick={async () => { const pending = mutation.pending!; if (await mutation.execute(pending) && pending.path === '/api/admin/pos/sales') setCart([]); }}>Проверить повтором исходного запроса</button></div>}
    <div className="quick-minutes pos-tabs">{[['sale', 'Продажа'], ['products', 'Товары'], ['history', 'Продажи и смены']].map(([id, label]) => <button key={id} aria-pressed={view === id} onClick={() => setView(id)}>{label}</button>)}</div>
    {view === 'sale' && <div className="pos-layout"><section className="panel"><header className="panel-header"><h2>Товары</h2><span className="muted">{shift ? `Смена с ${dateTime(shift.openedAtUtc)}` : 'Сначала откройте смену'}</span></header><div className="pos-products">{catalog.products.map(p => {
      const quantity = cart.find(c => c.productId === p.id)?.quantity ?? 0;
      return <button key={p.id} disabled={locked || !canAddProduct(p, catalog.categories, quantity)} onClick={() => setCart(current => quantity ? current.map(c => c.productId === p.id ? { ...c, quantity: c.quantity + 1 } : c) : [...current, { productId: p.id, quantity: 1 }])}><strong>{p.name}</strong><span>{money(p.price)}</span><small>Остаток: {p.stockQuantity}</small></button>;
    })}{loaded && catalog.products.length === 0 && <Empty>Товары ещё не созданы.</Empty>}</div></section>
      <section className="panel pos-cart"><h2>Корзина</h2><fieldset className="operation-fields stack" disabled={locked || !!confirm}>
        <label>Покупатель<select value={userId} onChange={e => { setUserId(e.target.value); setStationId(''); }}><option value="">Гость без аккаунта</option>{players.map(p => <option key={p.id} value={p.id}>{p.displayName || p.username}</option>)}</select></label>
        <label>ПК покупателя<select value={stationId} onChange={e => setStationId(e.target.value)}><option value="">Без привязки к ПК</option>{stations.filter(s => s.currentUser?.id === userId).map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
        {cart.map(item => <div className="pos-cart-line" key={item.productId}><span>{catalog.products.find(p => p.id === item.productId)?.name ?? 'Товар'} × {item.quantity}</span><button type="button" aria-label="Уменьшить количество" onClick={() => setCart(cart.flatMap(c => c.productId !== item.productId ? [c] : c.quantity > 1 ? [{ ...c, quantity: c.quantity - 1 }] : []))}>−</button></div>)}
        {!cart.length && <p className="muted">Выберите товары слева.</p>}<label>Оплата<select value={method} onChange={e => setMethod(e.target.value)}>{['Cash', 'Card', 'Wallet'].map(m => <option key={m} value={m}>{paymentName(m)}</option>)}</select></label>
        <div className="pos-total">Итого <strong>{Number.isFinite(total) ? money(total) : '—'}</strong></div><button className="primary" disabled={!canSell} onClick={prepareSale}>Проверить и оформить</button>
      </fieldset></section></div>}
    {view === 'products' && <PosCatalogPanel catalog={catalog} token={token} canEdit={canEdit} run={run} refresh={refresh} locked={locked} mutate={mutation.execute} />}
    {view === 'history' && <><section className="panel table-panel"><header className="panel-header"><h2>Последние продажи</h2></header><table><thead><tr><th>Время</th><th>Покупатель</th><th>Оплата</th><th>Сумма</th><th /></tr></thead><tbody>{sales.map(s => <tr key={s.id}><td>{dateTime(s.createdAtUtc)}</td><td>{players.find(p => p.id === s.userId)?.username ?? (s.userId ? s.userId : 'Гость')}</td><td>{paymentName(s.paymentMethod)}</td><td>{money(s.capturedTotal)}</td><td><button onClick={() => void inspectSale(s.id)}>Чек</button></td></tr>)}</tbody></table>{!sales.length && <Empty>Продаж пока нет.</Empty>}</section>
      <section className="panel table-panel"><header className="panel-header"><h2>Последние смены</h2></header><table><thead><tr><th>Открыта</th><th>Закрыта</th><th>Сотрудник</th><th /></tr></thead><tbody>{history.map(s => <tr key={s.id}><td>{dateTime(s.openedAtUtc)}</td><td>{dateTime(s.closedAtUtc)}</td><td>{s.employeeId === employeeId ? 'Моя смена' : s.employeeId}</td><td><button onClick={() => void inspectShift(s.id)}>Итоги</button></td></tr>)}</tbody></table></section></>}
    {confirm && <Modal title="Подтверждение продажи" onClose={() => { if (!mutation.busy) setConfirm(null); }}><div className="stack"><p>{players.find(p => p.id === userId)?.username ?? 'Гость'} / {stations.find(s => s.id === stationId)?.name ?? 'без ПК'}</p><p>Итого: <strong>{money(Number(confirm.body.expectedTotal))}</strong> · {paymentName(String(confirm.body.paymentMethod))}</p><p>{method === 'Wallet' ? 'Сумма будет списана с доступного баланса покупателя.' : 'Подтвердите, что оплата уже получена. Эта запись не выполняет операцию банковского терминала.'}</p><button className="primary" disabled={mutation.busy || mutation.pending !== null} onClick={async () => { const ok = await mutation.execute(confirm); setConfirm(null); if (ok) setCart([]); }}>Подтвердить оплату</button></div></Modal>}
    {shiftAction && <Modal title={shiftAction === 'open' ? 'Открыть смену' : 'Закрыть смену'} onClose={() => { if (!mutation.busy) setShiftAction(null); }}><form className="stack" onSubmit={submitShift}><label>{shiftAction === 'open' ? 'Наличные в начале смены' : 'Фактически пересчитанные наличные'}<input name="cash" type="number" min="0" max="1000000000" step="0.01" required /></label><p>Закрытую смену нельзя редактировать. Продажи и возвраты требуют открытой личной смены.</p><button className="primary" disabled={locked}>Подтвердить</button></form></Modal>}
    {details && <Modal title="Продажа" onClose={() => { if (!mutation.busy) { setDetails(null); setRefund(false); } }}><div className="stack"><p className="small">{details.sale.id}</p>{details.items.map(i => <p key={i.id}>{i.nameSnapshot} × {i.quantity} · {money(i.lineTotal)}</p>)}<p>Итого: <strong>{money(details.sale.capturedTotal)}</strong> · {paymentName(details.sale.paymentMethod)}</p>{details.refund ? <p>Возвращено: {money(details.refund.amount)}. {details.refund.reason}</p> : canRefund && shift && !refund && <button disabled={locked} onClick={() => setRefund(true)}>Полный возврат</button>}{refund && <form className="stack" onSubmit={submitRefund}><p>Весь товар возвращается в остаток. Возврат — исходным способом оплаты; Cash/Card подтвердите после внешней операции.</p><label>Причина<textarea name="reason" required maxLength={500} /></label><button className="primary" disabled={locked}>Подтвердить полный возврат</button></form>}</div></Modal>}
    {summary && <Modal title="Итоги смены" onClose={() => setSummary(null)}><div className="stack"><p>{dateTime(summary.shift.openedAtUtc)} — {dateTime(summary.shift.closedAtUtc)}</p>{[['Игры оператора', summary.gamingSales], ['Товары (до возвратов)', summary.productSales], ['Возвраты', summary.refunds], ['Наличные (нетто)', summary.cash], ['Карта (нетто)', summary.card], ['Кошелёк, включая игры (нетто)', summary.wallet], ['Ожидаемые наличные', summary.expectedCash], ['Фактические наличные', summary.shift.closingCash], ['Расхождение', summary.cashDifference]].map(([label, value]) => <div className="pos-cart-line" key={label as string}><span>{label}</span><strong>{value === null ? '—' : money(Number(value))}</strong></div>)}<p className="muted small">Игры — списания, проведённые этим сотрудником в интервале смены. Автоматические списания без сотрудника сюда не входят. Это не общий отчёт клуба.</p></div></Modal>}
  </>;
}
