import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { api, errorMessage } from './api';
import { Modal } from './components';
import type { RunAction } from './components';
import { dateTime, eligibleTransferStations, money } from './models';
import type { Station } from './models';

type Quote = { sessionId: string; minutes: number; billingMode: string; charge: number; additionalReservation: number; expectedEndAtUtc: string | null };
type Segment = { id: string; stationId: string; stationName: string; startedAtUtc: string; endedAtUtc: string | null };
type Props = { station: Station; token: string; run: RunAction; onClose: () => void; onSuccess: () => void };

export function ExtendSessionModal({ station, token, run, onClose, onSuccess }: Props) {
  const sessionId = station.gamingSession!.id;
  const [minutes, setMinutes] = useState('15');
  const [quote, setQuote] = useState<Quote | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [attempted, setAttempted] = useState(false);
  const [revision, setRevision] = useState(0);
  const operation = useRef(crypto.randomUUID());
  const valid = /^\d+$/.test(minutes) && Number(minutes) >= 1 && Number(minutes) <= 10080;
  useEffect(() => {
    let cancelled = false;
    setQuote(null); setError('');
    if (!valid) return;
    const timer = setTimeout(() => {
      void api<Quote>(`/api/admin/gaming-sessions/${sessionId}/extension-quote?minutes=${Number(minutes)}`, token)
        .then(value => { if (!cancelled) setQuote(value); })
        .catch(failure => { if (!cancelled) setError(errorMessage(failure)); });
    }, 200);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [sessionId, minutes, valid, token, revision]);
  function changeMinutes(value: string) { setMinutes(value); setQuote(null); operation.current = crypto.randomUUID(); }
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!quote || busy) return;
    setBusy(true); setAttempted(true);
    const success = await run(() => api(`/api/admin/gaming-sessions/${sessionId}/extend`, token, 'POST', {
      operationId: operation.current, minutes: quote.minutes,
    }), 'Продление и расчёт подтверждены сервером.');
    setBusy(false); if (success) onSuccess();
  }
  return <Modal title={`Продлить · ${station.name}`} onClose={() => { if (!busy) onClose(); }}>
    <form className="stack" onSubmit={event => void submit(event)}>
      <fieldset disabled={busy || attempted} className="stack operation-fields"><legend className="sr-only">Длительность продления</legend>
        <div className="quick-minutes">{[15, 30, 60].map(value => <button type="button" key={value} aria-pressed={Number(minutes) === value} onClick={() => changeMinutes(String(value))}>+{value} мин</button>)}</div>
        <label>Добавить минут<input type="number" min={1} max={10080} step={1} required value={minutes} onChange={e => changeMinutes(e.target.value)} /></label>
      </fieldset>
      <div aria-live="polite">
        {error ? <p className="notice error">{error} <button type="button" onClick={() => setRevision(value => value + 1)}>Повторить расчёт</button></p> : !valid ? <p>Введите от 1 до 10080 минут.</p> : quote ? <div className="balance-card"><span>{quote.billingMode === 'Postpaid' ? 'Дополнительный резерв' : 'Доплата с баланса'}</span><strong>{money(quote.billingMode === 'Postpaid' ? quote.additionalReservation : quote.charge)}</strong><span>{quote.expectedEndAtUtc ? `Новое окончание: ${dateTime(quote.expectedEndAtUtc)}` : 'Сессия на паузе. Время добавится к оставшемуся бюджету.'}</span></div> : <p>Рассчитываем на сервере…</p>}
      </div>
      <p className="muted small">Цена берётся из исходной покупки. Ночное окно и срок входа не сдвигаются. Сервер повторно проверит баланс и доступное время при подтверждении.</p>
      {attempted && <p className="notice">Если ответ потерялся, повторите подтверждение здесь: идентификатор операции сохранён, повторного списания не будет.</p>}
      <div className="form-actions"><button type="button" disabled={busy} onClick={onClose}>Закрыть</button><button className="primary" disabled={busy || !quote || !valid}>{busy ? 'Продлеваем…' : `Подтвердить +${Number(minutes)} мин`}</button></div>
    </form>
  </Modal>;
}

export function TransferSessionModal({ station, stations, token, run, onClose, onSuccess }: Props & { stations: Station[] }) {
  const [target, setTarget] = useState('');
  const [busy, setBusy] = useState(false);
  const [attempted, setAttempted] = useState(false);
  const operation = useRef(crypto.randomUUID());
  const destinations = eligibleTransferStations(station, stations);
  async function submit(event: FormEvent) {
    event.preventDefault(); if (!target || busy) return;
    setBusy(true); setAttempted(true);
    const success = await run(() => api(`/api/admin/gaming-sessions/${station.gamingSession!.id}/transfer`, token, 'POST', {
      operationId: operation.current, destinationStationId: target,
    }), 'Игровая сессия и вход игрока перенесены.');
    setBusy(false); if (success) onSuccess();
  }
  return <Modal title={`Перенести · ${station.name}`} onClose={() => { if (!busy) onClose(); }}><form className="stack" onSubmit={event => void submit(event)}>
    <p>Игрок: <strong>{station.currentUser?.displayName || station.currentUser?.username}</strong></p>
    <label>На свободный ПК той же группы<select required disabled={busy || attempted} value={target} onChange={e => { setTarget(e.target.value); operation.current = crypto.randomUUID(); }}><option value="">Выберите станцию…</option>{attempted && target && !destinations.some(s => s.id === target) && <option value={target}>{stations.find(s => s.id === target)?.name ?? target} · выбранный ПК</option>}{destinations.map(item => <option key={item.id} value={item.id}>{item.name}{!item.clientConnected ? ' · нет Client' : item.clientState === 'Locked' ? ' · Client заблокирован' : ''}</option>)}</select></label>
    {destinations.length === 0 && !attempted && <p className="notice">Нет свободных Online-станций подходящей группы.</p>}
    <p className="muted small">Сохранятся тариф, остаток времени, баланс и состояние паузы. Вход игрока переместится на выбранный ПК. Блокировка Client не снимается автоматически; история прежней станции сохранится.</p>
    {attempted && <p className="notice">При потере ответа повторите этот запрос с тем же выбранным ПК: перенос не выполнится дважды.</p>}
    <div className="form-actions"><button type="button" disabled={busy} onClick={onClose}>Закрыть</button><button className="primary" disabled={busy || !target || (!attempted && !destinations.some(s => s.id === target))}>{busy ? 'Переносим…' : 'Подтвердить перенос'}</button></div>
  </form></Modal>;
}

export function SessionHistoryModal({ sessionId, token, onClose }: { sessionId: string; token: string; onClose: () => void }) {
  const [segments, setSegments] = useState<Segment[] | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    let cancelled = false;
    void api<Segment[]>(`/api/admin/gaming-sessions/${sessionId}/segments`, token)
      .then(value => { if (!cancelled) setSegments(value); }).catch(failure => { if (!cancelled) setError(errorMessage(failure)); });
    return () => { cancelled = true; };
  }, [sessionId, token]);
  return <Modal title="История станций сессии" onClose={onClose}>
    <p className="muted small">Периоды пребывания включают паузы; оплачиваемое время сервер считает отдельно.</p>
    {error ? <p className="notice error" role="alert">{error}</p> : !segments ? <p>Загружаем историю…</p> : <ol className="session-history">{segments.map(segment => <li key={segment.id}><strong>{segment.stationName}</strong><span>{dateTime(segment.startedAtUtc)} — {segment.endedAtUtc ? dateTime(segment.endedAtUtc) : 'текущая станция'}</span></li>)}</ol>}
  </Modal>;
}
