import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api, errorMessage } from './api';
import { money } from './models';
import type { Catalog, Tariff } from './models';
import { Empty, Modal } from './components';
import type { RunAction } from './components';

export function CatalogPage({ token, permissions, run, revision }: { token: string; permissions: string[]; run: RunAction; revision: number }) {
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [error, setError] = useState('');
  const [kind, setKind] = useState<'groups' | 'tariffs' | 'packages' | null>(null);
  const [editPrice, setEditPrice] = useState<Tariff | null>(null);
  const [busy, setBusy] = useState(false);
  const [localRevision, setLocalRevision] = useState(0);
  const canEdit = permissions.includes('ClubManageCatalog');
  useEffect(() => { let active = true; void api<Catalog>('/api/admin/catalog', token).then(result => { if (active) { setCatalog(result); setError(''); } }).catch(failure => { if (active) setError(errorMessage(failure)); }); return () => { active = false; }; }, [token, revision, localRevision]);
  const groupName = (id: string) => catalog?.groups.find(g => g.id === id)?.name ?? '—';
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const values = new FormData(event.currentTarget); setBusy(true);
    const body = editPrice ? { price: Number(values.get('price')) } : {
      name: String(values.get('name')), ...(kind !== 'groups' ? { stationGroupId: String(values.get('group')) } : {}),
      ...(kind === 'tariffs' ? { hourlyPrice: Number(values.get('price')) } : {}),
      ...(kind === 'packages' ? { price: Number(values.get('price')), durationMinutes: Number(values.get('minutes')), availableFrom: values.get('from') ? `${values.get('from')}:00` : null, availableUntil: values.get('until') ? `${values.get('until')}:00` : null } : {}),
    };
    const ok = await run(() => api(editPrice ? `/api/admin/catalog/tariffs/${editPrice.id}/price` : `/api/admin/catalog/${kind}`, token, editPrice ? 'PUT' : 'POST', body), 'Каталог обновлён. Изменения сохранены в аудите.');
    setBusy(false); if (ok) { setKind(null); setEditPrice(null); setLocalRevision(v => v + 1); }
  }
  async function active(type: 'tariffs' | 'packages', id: string, value: boolean) {
    setBusy(true); const ok = await run(() => api(`/api/admin/catalog/${type}/${id}/active`, token, 'PUT', { isActive: value }), value ? 'Предложение включено.' : 'Предложение отключено.'); setBusy(false); if (ok) setLocalRevision(v => v + 1);
  }
  return <><header className="page-heading"><div><span className="eyebrow">КАТАЛОГ КЛУБА</span><h1>Тарифы и пакеты</h1></div>{canEdit && <button onClick={() => setKind('groups')}>+ Группа станций</button>}</header>
    {error && <p className="notice error" role="alert">{error}</p>}
    <div className="group-tags">{catalog?.groups.map(group => <span key={group.id}>{group.name}</span>)}</div>
    <section className="panel table-panel"><header className="panel-header"><div><h2>Почасовые тарифы</h2><p className="muted small">Цена фиксируется при запуске сессии.</p></div>{canEdit && <button className="primary" onClick={() => setKind('tariffs')}>+ Тариф</button>}</header>
      <table><thead><tr><th>Тариф</th><th>Группа</th><th>Цена / час</th><th>Статус</th>{canEdit && <th>Действия</th>}</tr></thead><tbody>{catalog?.tariffs.map(tariff => <tr key={tariff.id}><td><strong>{tariff.name}</strong></td><td>{groupName(tariff.stationGroupId)}</td><td className="tabular">{money(tariff.hourlyPrice)}</td><td>{tariff.isActive ? 'Активен' : 'Отключён'}</td>{canEdit && <td><div className="row-actions"><button onClick={() => setEditPrice(tariff)}>Цена</button><button disabled={busy} onClick={() => void active('tariffs', tariff.id, !tariff.isActive)}>{tariff.isActive ? 'Отключить' : 'Включить'}</button></div></td>}</tr>)}</tbody></table>{catalog?.tariffs.length === 0 && <Empty>Создайте почасовой тариф для группы станций.</Empty>}
    </section>
    <section className="panel table-panel"><header className="panel-header"><div><h2>Пакеты времени</h2><p className="muted small">Prepaid. Временные окна ограничивают окончание игры, в том числе после паузы.</p></div>{canEdit && <button className="primary" onClick={() => setKind('packages')}>+ Пакет</button>}</header>
      <table><thead><tr><th>Пакет</th><th>Группа</th><th>Минуты</th><th>Цена</th><th>Окно</th>{canEdit && <th>Статус</th>}</tr></thead><tbody>{catalog?.packages.map(pack => <tr key={pack.id}><td><strong>{pack.name}</strong>{!pack.isActive && <span className="cell-secondary">Отключён</span>}</td><td>{groupName(pack.stationGroupId)}</td><td>{pack.durationMinutes}</td><td className="tabular">{money(pack.price)}</td><td>{pack.availableFrom && pack.availableUntil ? `${pack.availableFrom.slice(0, 5)}–${pack.availableUntil.slice(0, 5)}` : 'Любое время'}</td>{canEdit && <td><button disabled={busy} onClick={() => void active('packages', pack.id, !pack.isActive)}>{pack.isActive ? 'Отключить' : 'Включить'}</button></td>}</tr>)}</tbody></table>{catalog?.packages.length === 0 && <Empty>Пакеты ещё не созданы.</Empty>}
    </section>
    {(kind || editPrice) && <Modal title={editPrice ? `Цена · ${editPrice.name}` : ({ groups: 'Новая группа', tariffs: 'Новый тариф', packages: 'Новый пакет' }[kind!])} onClose={() => { setKind(null); setEditPrice(null); }}><form className="stack" onSubmit={submit}>
      {!editPrice && <label>Название<input name="name" required maxLength={100} autoFocus /></label>}
      {!editPrice && kind !== 'groups' && <label>Группа<select name="group" required><option value="">Выберите…</option>{catalog?.groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label>}
      {(editPrice || kind !== 'groups') && <label>{kind === 'tariffs' || editPrice ? 'Цена за час' : 'Цена пакета'}<input name="price" type="number" required min="0.01" step="0.01" defaultValue={editPrice?.hourlyPrice} /></label>}
      {kind === 'packages' && <><label>Длительность, минуты<input name="minutes" type="number" required min={1} max={10080} step={1} defaultValue={180} /></label><div className="field-row"><label>Доступен с<input name="from" type="time" /></label><label>До<input name="until" type="time" /></label></div><p className="muted small">Оба поля окна заполняются вместе. Время — в часовом поясе клуба. При поздней покупке окно сокращает время, но не цену.</p></>}
      <button className="primary" disabled={busy}>Сохранить</button>
    </form></Modal>}
  </>;
}
