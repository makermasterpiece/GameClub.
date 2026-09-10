import { useState } from 'react';
import type { FormEvent } from 'react';
import { api } from './api';
import { Empty, Modal } from './components';
import type { RunAction } from './components';
import type { PendingPos, PosCatalog, Product, ProductCategory } from './posModels';
import { money } from './models';

export function PosCatalogPanel({ catalog, token, canEdit, run, refresh, locked, mutate }: { catalog: PosCatalog; token: string; canEdit: boolean; run: RunAction; refresh: () => void; locked: boolean; mutate: (op: PendingPos) => Promise<boolean> }) {
  const [product, setProduct] = useState<Product | 'new' | null>(null);
  const [category, setCategory] = useState<ProductCategory | 'new' | null>(null);
  const [stock, setStock] = useState<Product | null>(null);
  const [busy, setBusy] = useState(false);
  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (busy || locked) return;
    const form = new FormData(event.currentTarget); setBusy(true);
    const editing = product ?? category;
    const path = `/api/admin/pos/${product ? 'products' : 'categories'}${editing === 'new' ? '' : `/${editing!.id}`}`;
    const body = { name: String(form.get('name')), isActive: form.has('active'), ...(product ? {
      categoryId: String(form.get('category')) || null, price: Number(form.get('price')), initialStock: product === 'new' ? Number(form.get('stock')) : 0,
    } : {}) };
    const ok = await run(() => api(path, token, editing === 'new' ? 'POST' : 'PUT', body), 'Каталог товаров сохранён.');
    setBusy(false); if (ok) { setProduct(null); setCategory(null); refresh(); }
  }
  async function adjust(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (!stock || locked) return;
    const values = new FormData(event.currentTarget);
    await mutate({ path: `/api/admin/pos/products/${stock.id}/stock`, body: { operationId: crypto.randomUUID(), delta: Number(values.get('delta')), reason: String(values.get('reason')) }, success: 'Изменение остатка записано в журнал.' });
    setStock(null);
  }
  const item = product && product !== 'new' ? product : null;
  const group = category && category !== 'new' ? category : null;
  return <section className="panel table-panel"><header className="panel-header"><h2>Товары и остатки</h2>{canEdit && <div className="row-actions"><button disabled={locked} onClick={() => setCategory('new')}>+ Категория</button><button className="primary" disabled={locked} onClick={() => setProduct('new')}>+ Товар</button></div>}</header>
    <div className="group-tags">{catalog.categories.map(c => canEdit ? <button disabled={locked} key={c.id} onClick={() => setCategory(c)}>{c.name}{!c.isActive ? ' · отключена' : ''}</button> : <span key={c.id}>{c.name}</span>)}</div>
    {catalog.products.length === 0 ? <Empty>Добавьте первый товар.</Empty> : <table><thead><tr><th>Товар</th><th>Цена</th><th>Остаток</th>{canEdit && <th>Действия</th>}</tr></thead><tbody>{catalog.products.map(p => <tr key={p.id}><td><strong>{p.name}</strong><span className="cell-secondary">{catalog.categories.find(c => c.id === p.categoryId)?.name ?? 'Без категории'}{!p.isActive ? ' · отключён' : ''}</span></td><td>{money(p.price)}</td><td>{p.stockQuantity}</td>{canEdit && <td><div className="row-actions"><button disabled={locked} onClick={() => setProduct(p)}>Изменить</button><button disabled={locked} onClick={() => setStock(p)}>Остаток</button></div></td>}</tr>)}</tbody></table>}
    {(product || category) && <Modal title={product ? 'Товар' : 'Категория'} onClose={() => { if (!busy) { setProduct(null); setCategory(null); } }}><form className="stack" onSubmit={save}>
      <label>Название<input name="name" required maxLength={product ? 200 : 100} defaultValue={item?.name ?? group?.name} /></label>
      {product && <><label>Категория<select name="category" defaultValue={item?.categoryId ?? ''}><option value="">Без категории</option>{catalog.categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label><label>Цена<input name="price" type="number" min="0.01" max="1000000000" step="0.01" required defaultValue={item?.price} /></label>{product === 'new' && <label>Начальный остаток<input name="stock" type="number" min="0" max="1000000" step="1" required defaultValue={0} /></label>}</>}
      <label><input name="active" type="checkbox" defaultChecked={item?.isActive ?? group?.isActive ?? true} /> Активен</label><button className="primary" disabled={busy || locked}>Сохранить</button>
    </form></Modal>}
    {stock && <Modal title={`Остаток · ${stock.name}`} onClose={() => { if (!locked) setStock(null); }}><form className="stack" onSubmit={adjust}><p>Сейчас: {stock.stockQuantity}. Положительное число — приход, отрицательное — списание.</p><label>Изменение количества<input name="delta" type="number" min="-1000000" max="1000000" step="1" required /></label><label>Причина<textarea name="reason" required maxLength={500} /></label><button className="primary" disabled={locked}>Записать корректировку</button></form></Modal>}
  </section>;
}
