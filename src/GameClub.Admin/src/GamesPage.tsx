import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api, errorMessage } from './api';
import { Empty, Modal, useTick } from './components';
import type { RunAction } from './components';
import type { DashboardData, Station } from './models';
import { dateTime } from './models';
import type { Game, StationGame } from './gameModels';
import { gameLaunchable, installationState } from './gameModels';

export function GamesPage({ token, permissions, run, revision }: { token: string; permissions: string[]; run: RunAction; revision: number }) {
  const [games, setGames] = useState<Game[] | null>(null);
  const [stations, setStations] = useState<Station[]>([]);
  const [stationId, setStationId] = useState('');
  const [inventory, setInventory] = useState<StationGame[] | null>(null);
  const [editing, setEditing] = useState<Game | 'new' | null>(null);
  const [launch, setLaunch] = useState<StationGame | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [localRevision, setLocalRevision] = useState(0);
  useTick();
  const canEdit = permissions.includes('ClubManageCatalog');
  const canOperate = permissions.includes('ClubOperate');
  const station = stations.find(s => s.id === stationId);
  const ready = !!station?.agentOnline && station.clientConnected && station.clientState === 'SessionActive' &&
    station.gamingSession?.status === 'Active';
  useEffect(() => {
    let active = true;
    const load = async () => {
      try {
        const [catalog, dashboard, report] = await Promise.all([
          api<Game[]>('/api/admin/games', token), api<DashboardData>('/api/admin/dashboard', token),
          stationId ? api<StationGame[]>(`/api/admin/stations/${stationId}/games`, token) : Promise.resolve(null),
        ]);
        if (active) { setGames(catalog); setStations(dashboard.stations); setInventory(report); setError(''); }
      } catch (failure) { if (active) { setError(errorMessage(failure)); setInventory(null); } }
    };
    setInventory(null); void load(); const timer = setInterval(() => void load(), 15000);
    return () => { active = false; clearInterval(timer); };
  }, [token, revision, localRevision, stationId]);
  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (busy) return;
    const form = new FormData(event.currentTarget);
    const value = (key: string) => String(form.get(key) ?? '').trim();
    setBusy(true);
    const ok = await run(() => api(editing === 'new' ? '/api/admin/games' : `/api/admin/games/${(editing as Game).id}`,
      token, editing === 'new' ? 'POST' : 'PUT', { name: value('name'), playniteGameId: value('playniteGameId') || null,
        executable: value('executable') || null, coverUrl: value('coverUrl') || null, isActive: form.has('isActive') }), 'Игра сохранена.');
    setBusy(false); if (ok) { setEditing(null); setLocalRevision(v => v + 1); }
  }
  async function launchGame() {
    if (!launch || busy) return;
    setBusy(true);
    const ok = await run(() => api(`/api/stations/${stationId}/commands`, token, 'POST', {
      type: 'LaunchGame', payload: { gameId: launch.gameId },
    }), 'Команда отправлена. Результат выполнения — в истории команд станции.');
    setBusy(false); if (ok) setLaunch(null);
  }
  const edited = editing && editing !== 'new' ? editing : null;
  return <><header className="page-heading"><div><span className="eyebrow">КАТАЛОГ КЛУБА</span><h1>Игры</h1></div>
    {canEdit && <button className="primary" onClick={() => setEditing('new')}>+ Игра</button>}</header>
    {error && <p className="notice error" role="alert">{error}</p>}
    <section className="panel table-panel"><header className="panel-header"><div><h2>Каталог Playnite</h2><p className="muted small">Для запуска настройте разрешённые GameClub ID и Playnite ID на каждом ПК.</p></div></header>
      {games === null ? <Empty>Загрузка каталога…</Empty> : games.length === 0 ? <Empty>Игры ещё не добавлены.</Empty> :
        <table><thead><tr><th>Игра / GameClub ID</th><th>Playnite ID</th><th>Статус</th>{canEdit && <th>Действия</th>}</tr></thead><tbody>{games.map(game =>
          <tr key={game.id}><td><strong>{game.name}</strong><span className="cell-secondary">{game.id}</span></td><td>{game.playniteGameId ?? 'Не привязана'}</td><td>{game.isActive ? 'Активна' : 'Отключена'}</td>
            {canEdit && <td><button onClick={() => setEditing(game)}>Изменить</button></td>}</tr>)}</tbody></table>}
    </section>
    <section className="panel table-panel"><header className="panel-header"><div><h2>Игры на станции</h2><p className="muted small">Обновление каждые 15 секунд. Перед запуском игрок открывает «ИГРЫ» в Client.</p></div>
      <label>Станция<select value={stationId} onChange={e => { setStationId(e.target.value); setLaunch(null); }}><option value="">Выберите ПК</option>{stations.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label></header>
      {!stationId ? <Empty>Выберите станцию для просмотра установки.</Empty> : !inventory ? <Empty>Ожидание данных…</Empty> : inventory.length === 0 ? <Empty>Каталог пуст.</Empty> :
        <table><thead><tr><th>Игра</th><th>Установка</th><th>Последний отчёт</th>{canOperate && <th>Действие</th>}</tr></thead><tbody>{inventory.map(game =>
          <tr key={game.gameId}><td>{game.name}{!game.isActive && <span className="cell-secondary">Отключена</span>}</td><td>{installationState(game, Date.now())}</td><td>{dateTime(game.lastDetectedAtUtc)}</td>
            {canOperate && <td><button disabled={!ready || !gameLaunchable(game, Date.now()) || !!error || busy} onClick={() => setLaunch(game)}>Запустить</button></td>}</tr>)}</tbody></table>}
    </section>
    {editing && <Modal title={editing === 'new' ? 'Новая игра' : `Игра · ${edited?.name}`} onClose={() => { if (!busy) setEditing(null); }}><form className="stack" onSubmit={save}>
      <label>Название<input name="name" required maxLength={200} defaultValue={edited?.name} autoFocus /></label>
      <label>Playnite ID (GUID библиотеки)<input name="playniteGameId" maxLength={36} defaultValue={edited?.playniteGameId ?? ''} /></label>
      <label>URL обложки<input name="coverUrl" type="url" maxLength={2048} defaultValue={edited?.coverUrl ?? ''} /></label>
      <label>Executable — справочная информация<input name="executable" maxLength={1024} defaultValue={edited?.executable ?? ''} /></label>
      <p className="muted small">Путь из каталога не используется для запуска и не передаётся Agent.</p>
      <label><input name="isActive" type="checkbox" defaultChecked={edited?.isActive ?? true} /> Активна</label>
      <button className="primary" disabled={busy}>Сохранить</button></form></Modal>}
    {launch && <Modal title={`Запустить · ${launch.name}`} onClose={() => { if (!busy) setLaunch(null); }}><div className="stack"><p>Станция: {station?.name}. Игра будет запрошена у Playnite в текущей оплаченной сессии.</p>
      <button className="primary" disabled={busy || !ready || !gameLaunchable(launch, Date.now())} onClick={() => void launchGame()}>Отправить команду</button></div></Modal>}
  </>;
}
