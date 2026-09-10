import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { api, errorMessage } from './api';
import { countdown, duration, stationState, dateTime, money, canRequestPower } from './models';
import { ExtendSessionModal, TransferSessionModal, SessionHistoryModal } from './SessionOperations';
import type { Catalog, DashboardData, Station } from './models';
import { Empty, Modal, MonitorIcon, useTick } from './components';
import type { RunAction } from './components';

export function Dashboard({ token, permissions, run, revision }: { token: string; permissions: string[]; run: RunAction; revision: number }) {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState('');
  const [connected, setConnected] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [purchase, setPurchase] = useState(false);
  const [confirm, setConfirm] = useState<{ title: string; description?: string; action: () => Promise<unknown> } | null>(null);
  const [sessionOperation, setSessionOperation] = useState<{ type: 'extend' | 'transfer'; station: Station } | null>(null);
  const [history, setHistory] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [command, setCommand] = useState<{ id: string; status: string } | null>(null);
  const receivedAt = useRef(performance.now());
  const activeRequest = useRef(false);
  const lifetime = useRef(true);
  const toolData = useRef<DashboardData | null>(null);
  toolData.current = data;
  useTick();
  const refresh = useCallback(async () => {
    if (activeRequest.current) return;
    activeRequest.current = true;
    try {
      const snapshot = await api<DashboardData>('/api/admin/dashboard', token);
      if (lifetime.current) { receivedAt.current = performance.now(); setData(snapshot); setError(''); }
    } catch (failure) { if (lifetime.current) setError(errorMessage(failure)); }
    finally { activeRequest.current = false; }
  }, [token]);
  useEffect(() => {
    lifetime.current = true;
    void refresh();
    void api<Catalog>('/api/admin/catalog', token).then(value => { if (lifetime.current) setCatalog(value); }).catch(() => {});
    const timer = setInterval(() => void refresh(), 15000);
    const hub = new HubConnectionBuilder().withUrl('/hubs/admin', { accessTokenFactory: () => token })
      .withAutomaticReconnect([0, 2000, 5000, 15000]).configureLogging(LogLevel.None).build();
    hub.on('DashboardChanged', () => void refresh());
    hub.onreconnecting(() => setConnected(false));
    hub.onreconnected(() => { setConnected(true); void refresh(); });
    let stopped = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    const connect = async () => { try { await hub.start(); if (!stopped) setConnected(true); } catch { if (!stopped) retry = setTimeout(() => void connect(), 5000); } };
    hub.onclose(() => { setConnected(false); if (!stopped) retry = setTimeout(() => void connect(), 5000); });
    void connect();
    return () => { stopped = true; lifetime.current = false; clearInterval(timer); clearTimeout(retry); void hub.stop(); };
  }, [refresh, token]);
  useEffect(() => { void refresh(); }, [revision, refresh]);
  useEffect(() => {
    if (!command || ['Completed', 'Failed', 'Expired'].includes(command.status)) return;
    let cancelled = false;
    const timer = setInterval(() => {
      void api<{ status: string }>(`/api/commands/${command.id}`, token).then(result => {
        if (!cancelled) { setCommand({ id: command.id, status: result.status }); void refresh(); }
      }).catch(() => {});
    }, 2000);
    return () => { cancelled = true; clearInterval(timer); };
  }, [command, token, refresh]);
  useEffect(() => {
    const context = (document as Document & { modelContext?: { registerTool: (tool: unknown, options: { signal: AbortSignal }) => void | Promise<void> } }).modelContext;
    if (!context?.registerTool) return;
    const controller = new AbortController();
    try {
      void Promise.resolve(context.registerTool({ name: 'get_gameclub_station_status', title: 'Состояние станций GameClub',
        description: 'Read the station status currently visible to the signed-in employee. Does not send commands or change money.',
        inputSchema: { type: 'object', properties: {}, additionalProperties: false },
        annotations: { readOnlyHint: true, untrustedContentHint: true },
        execute: (input: unknown) => {
          if (!input || typeof input !== 'object' || Array.isArray(input) || Object.keys(input).length) throw new Error('Expected an empty object.');
          const current = toolData.current;
          if (!current) throw new Error('Station data is still loading.');
          return { serverTimeUtc: current.serverTimeUtc, stations: current.stations.map(s => ({ id: s.id, name: s.name, status: stationState(s).label })) };
        },
      }, { signal: controller.signal })).catch(() => {});
    } catch { /* Optional browser API; the ordinary employee UI remains fully functional. */ }
    return () => controller.abort();
  }, []);

  const canOperate = permissions.includes('ClubOperate');
  const canCatalog = permissions.includes('ClubManageCatalog');
  const canPower = permissions.includes('ClubPower');
  const station = data?.stations.find(item => item.id === selected);
  const elapsed = performance.now() - receivedAt.current;
  const stations = data?.stations.filter(s => `${s.name} ${s.currentUser?.username ?? ''}`.toLocaleLowerCase().includes(filter.toLocaleLowerCase())) ?? [];
  const groups = [...new Set(stations.map(s => s.groupName ?? 'Без группы'))];
  async function execute(action: () => Promise<unknown>, success: string) {
    setBusy(true); const result = await run(action, success); setBusy(false); if (result) { setConfirm(null); await refresh(); } return result;
  }
  async function sendCommand(type: string) {
    if (!station) return;
    await execute(() => createCommand(station.id, type), 'Команда отправлена. Ожидаем подтверждения Agent.');
  }
  async function createCommand(stationId: string, type: string) {
    const result = await api<{ commandId: string; status: string }>(`/api/stations/${stationId}/commands`, token, 'POST', { type });
    setCommand({ id: result.commandId, status: result.status });
  }
  function confirmPower(type: 'RestartStation' | 'ShutdownStation') {
    if (!station) return;
    setConfirm({ title: `${type === 'RestartStation' ? 'Перезагрузить' : 'Выключить'} ${station.name}?`,
      description: 'Команда допустима только для свободной Online-станции. Windows получит запрос с задержкой 30 секунд, без принудительного закрытия приложений. Completed означает, что Windows приняла запрос; фактическое выключение проверяйте по состоянию ПК. Повторная команда не служит отменой.',
      action: () => createCommand(station.id, type) });
  }
  return <>
    <header className="page-heading"><div><span className="eyebrow">ОПЕРАЦИОННЫЙ ЗАЛ</span><h1>Карта клуба</h1></div><div className={`live-label ${connected ? 'connected' : ''}`}><span className="status-dot" />{connected ? 'Realtime подключён' : 'Проверяем соединение'}</div></header>
    {error && <div className="notice error" role="alert">{error} <button onClick={() => void refresh()}>Повторить</button></div>}
    <section className="stats" aria-label="Состояние клуба">
      <div><span>Всего станций</span><strong>{data?.stations.length ?? '—'}</strong></div>
      <div><span>Online</span><strong>{data?.stations.filter(s => s.agentOnline).length ?? '—'}</strong></div>
      <div><span>В игре</span><strong>{data?.stations.filter(s => s.gamingSession).length ?? '—'}</strong></div>
      <div><span>Offline</span><strong>{data?.stations.filter(s => !s.agentOnline).length ?? '—'}</strong></div>
    </section>
    <div className="map-toolbar"><label className="search"><span className="sr-only">Поиск станции или игрока</span><input value={filter} onChange={e => setFilter(e.target.value)} placeholder="Найти станцию или игрока…" /></label><span className="muted">Обновлено {data ? new Date(data.serverTimeUtc).toLocaleTimeString('ru-RU') : '—'}</span></div>
    <div className={`map-layout ${station ? 'with-detail' : ''}`}><div>
      {!data && !error && <Empty>Загружаем станции…</Empty>}
      {data && stations.length === 0 && <Empty>{filter ? 'Совпадений не найдено.' : 'Пока нет станций. Зарегистрируйте Agent по инструкции в README.'}</Empty>}
      {groups.map(group => <section className="station-group" key={group}><h2>{group} <span>{stations.filter(s => (s.groupName ?? 'Без группы') === group).length}</span></h2><div className="station-grid">
        {stations.filter(s => (s.groupName ?? 'Без группы') === group).map(s => { const state = stationState(s); const game = s.gamingSession; return <button className={`station-card ${state.tone} ${selected === s.id ? 'selected' : ''}`} key={s.id} onClick={() => setSelected(s.id)} aria-pressed={selected === s.id}>
          <div className="station-top"><strong>{s.name}</strong><span className={`badge ${state.tone}`}>{state.label}</span></div>
          <div className="station-middle"><MonitorIcon /><div><span className="player-name">{s.currentUser?.displayName || s.currentUser?.username || 'Свободная станция'}</span><span className="timer">{game ? duration(countdown(game.remainingSeconds, game.status, elapsed)) : '—'}</span></div></div>
          <div className="station-bottom"><span>{s.tariffName || (s.agentOnline ? `Agent ${s.agentVersion}` : 'Нет связи с Agent')}</span><span>↗</span></div>
        </button>; })}
      </div></section>)}
    </div>
    {station && <aside className="station-detail"><header><div><span className="eyebrow">СТАНЦИЯ</span><h2>{station.name}</h2></div><button className="icon-button" aria-label="Закрыть станцию" onClick={() => setSelected(null)}>×</button></header>
      <span className={`badge ${stationState(station).tone}`}>{stationState(station).label}</span>
      <dl><dt>Agent</dt><dd>{station.agentOnline ? 'Online' : 'Offline'}</dd><dt>Client</dt><dd>{station.clientConnected ? 'Подключён' : 'Нет подключения'}</dd><dt>Игрок</dt><dd>{station.currentUser?.displayName || station.currentUser?.username || '—'}</dd><dt>Тариф / пакет</dt><dd>{station.tariffName || '—'}</dd><dt>Осталось</dt><dd className="tabular">{station.gamingSession ? duration(countdown(station.gamingSession.remainingSeconds, station.gamingSession.status, elapsed)) : '—'}</dd><dt>Начало</dt><dd>{dateTime(station.gamingSession?.startedAtUtc ?? null)}</dd><dt>Последняя связь</dt><dd>{dateTime(station.lastSeenAtUtc)}</dd></dl>
      {canOperate && <div className="station-actions"><button disabled={busy} onClick={() => void sendCommand('LockStation')}>Заблокировать</button><button disabled={busy} onClick={() => void sendCommand('UnlockStation')}>Разблокировать</button>
        {station.gamingSession ? <><button disabled={busy} onClick={() => void execute(() => api(`/api/admin/gaming-sessions/${station.gamingSession!.id}/${station.gamingSession!.status === 'Paused' ? 'resume' : 'pause'}`, token, 'POST'), 'Состояние игровой сессии обновлено.')}>{station.gamingSession.status === 'Paused' ? 'Продолжить сессию' : 'Пауза'}</button><button className="danger" onClick={() => setConfirm({ title: 'Завершить игровую сессию?', action: () => api(`/api/admin/gaming-sessions/${station.gamingSession!.id}/end`, token, 'POST') })}>Завершить сессию</button></> : <button className="primary" disabled={!station.currentUser || !station.agentOnline || !catalog} onClick={() => setPurchase(true)}>Начать сессию</button>}
        <button disabled={busy || !station.currentUser} onClick={() => setConfirm({ title: 'Завершить вход игрока и его игру?', action: async () => { const result = await api<{ commandId: string; status: string }>(`/api/stations/${station.id}/commands`, token, 'POST', { type: 'LogoutPlayer' }); setCommand({ id: result.commandId, status: result.status }); } })}>Выйти за игрока</button>
      </div>}
      {station.gamingSession && <div className="station-actions">
        {canOperate && <><button disabled={busy} onClick={() => setSessionOperation({ type: 'extend', station })}>Продлить время</button><button disabled={busy} onClick={() => setSessionOperation({ type: 'transfer', station })}>Перенести на ПК</button></>}
        <button onClick={() => setHistory(station.gamingSession!.id)}>История станций</button>
      </div>}
      {canPower && <div className="station-actions"><button disabled={busy || !canRequestPower(station)} onClick={() => confirmPower('RestartStation')}>Перезагрузить ПК</button><button disabled={busy || !canRequestPower(station)} onClick={() => confirmPower('ShutdownStation')}>Выключить ПК</button></div>}
      {canPower && !canRequestPower(station) && <p className="muted small">Команды питания доступны только свободному Online-ПК без входа игрока и игровой сессии.</p>}
      {command && <p className="notice" aria-live="polite">Команда: {command.status}</p>}
      {!station.currentUser && <p className="muted small">Для запуска игры пользователь должен войти через Client на этой станции.</p>}
      {canCatalog && catalog && <label className="group-assignment">Группа<select value={station.stationGroupId ?? ''} disabled={busy || !!station.gamingSession} onChange={e => void execute(() => api(`/api/admin/catalog/stations/${station.id}/group`, token, 'PUT', { stationGroupId: e.target.value }), 'Группа станции изменена.')}><option value="" disabled>Выберите группу</option>{catalog.groups.map(g => <option key={g.id} value={g.id}>{g.name}</option>)}</select></label>}
      <p className="muted small">Блокировка меняет оболочку Client, а не средства безопасности Windows.</p>
    </aside>}
    </div>
    {purchase && station && catalog && <PurchaseModal station={station} catalog={catalog} token={token} run={run} onClose={() => setPurchase(false)} onSuccess={() => { setPurchase(false); void refresh(); }} />}
    {sessionOperation?.type === 'extend' && <ExtendSessionModal station={sessionOperation.station} token={token} run={run} onClose={() => setSessionOperation(null)} onSuccess={() => { setSessionOperation(null); void refresh(); }} />}
    {sessionOperation?.type === 'transfer' && <TransferSessionModal station={sessionOperation.station} stations={data?.stations ?? []} token={token} run={run} onClose={() => setSessionOperation(null)} onSuccess={() => { setSessionOperation(null); void refresh(); }} />}
    {history && <SessionHistoryModal sessionId={history} token={token} onClose={() => setHistory(null)} />}
    {confirm && <Modal title={confirm.title} onClose={() => { if (!busy) setConfirm(null); }}><p>{confirm.description ?? 'Игра будет остановлена сервером. Неиспользованное prepaid-время автоматически не возвращается; postpaid рассчитывается по фактическому времени.'}</p><div className="form-actions"><button disabled={busy} onClick={() => setConfirm(null)}>Отмена</button><button className="danger" disabled={busy} onClick={() => void execute(confirm.action, 'Запрос принят сервером.')}>Подтвердить</button></div></Modal>}
  </>;
}

function PurchaseModal({ station, catalog, token, run, onClose, onSuccess }: { station: Station; catalog: Catalog; token: string; run: RunAction; onClose: () => void; onSuccess: () => void }) {
  const [mode, setMode] = useState('Prepaid');
  const [selection, setSelection] = useState('');
  const [busy, setBusy] = useState(false);
  const operation = useRef(crypto.randomUUID());
  const prices = [...catalog.tariffs.filter(t => t.stationGroupId === station.stationGroupId && t.isActive).map(t => ({ id: `t:${t.id}`, text: `${t.name} · ${money(t.hourlyPrice)}/ч` })), ...(mode === 'Prepaid' ? catalog.packages.filter(p => p.stationGroupId === station.stationGroupId && p.isActive).map(p => ({ id: `p:${p.id}`, text: `${p.name} · ${money(p.price)}` })) : [])];
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const values = new FormData(event.currentTarget); setBusy(true);
    const [kind, id] = selection.split(':');
    const success = await run(() => api('/api/admin/gaming-sessions', token, 'POST', { operationId: operation.current, userId: station.currentUser!.id, stationId: station.id, mode,
      tariffId: kind === 't' ? id : null, packageId: kind === 'p' ? id : null,
      purchasedMinutes: mode === 'Prepaid' && kind === 't' ? Number(values.get('minutes')) : null,
      postpaidLimit: mode === 'Postpaid' && values.get('limit') ? Number(values.get('limit')) : null }), 'Оплата и запуск игровой сессии подтверждены сервером.');
    setBusy(false); if (success) onSuccess();
  }
  return <Modal title={`Запуск · ${station.name}`} onClose={onClose}><form className="stack" onSubmit={submit} onChange={() => { operation.current = crypto.randomUUID(); }}><p>Игрок: <strong>{station.currentUser?.displayName || station.currentUser?.username}</strong></p>
    <label>Оплата<select value={mode} onChange={e => { setMode(e.target.value); setSelection(''); }}><option value="Prepaid">Prepaid — фиксированное время</option><option value="Postpaid">Postpaid — по фактическому времени</option></select></label>
    <label>Тариф или пакет<select required value={selection} onChange={e => setSelection(e.target.value)}><option value="">Выберите…</option>{prices.map(p => <option key={p.id} value={p.id}>{p.text}</option>)}</select></label>
    {mode === 'Prepaid' && !selection.startsWith('p:') && <label>Количество минут<input name="minutes" type="number" min={1} max={10080} step={1} required defaultValue={60} /></label>}
    {mode === 'Postpaid' && <label>Лимит резерва (пусто — весь доступный баланс)<input name="limit" type="number" min="0.01" step="0.01" /></label>}
    <p className="muted small">Prepaid списывается сразу. Для postpaid средства резервируются, а остаток освобождается при завершении. Окончательную доступность и время проверяет сервер.</p>
    <button className="primary" disabled={busy || !selection}>{busy ? 'Запускаем…' : 'Оплатить и начать'}</button>
  </form></Modal>;
}
