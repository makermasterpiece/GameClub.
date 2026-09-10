import test from 'node:test';
import assert from 'node:assert/strict';
import { cartTotal, canAddProduct, paymentName } from '../src/posModels.ts';
const product = { id: 'p', categoryId: 'c', name: 'Water', price: 0.1, stockQuantity: 2, isActive: true };
const categories = [{ id: 'c', name: 'Drinks', isActive: true }];
test('POS display totals use integer cents', () => assert.equal(cartTotal([{ productId: 'p', quantity: 3 }], [product]), 0.3));
test('POS invalid or missing cart lines fail closed', () => {
  for (const quantity of [0, -1, 1.5, 1001, NaN]) assert.ok(Number.isNaN(cartTotal([{ productId: 'p', quantity }], [product])));
  assert.ok(Number.isNaN(cartTotal([{ productId: 'missing', quantity: 1 }], [product])));
});
test('POS exhausted stock cannot be added', () => {
  assert.equal(canAddProduct(product, categories, 1), true);
  assert.equal(canAddProduct(product, categories, 2), false);
});
test('POS inactive products and categories cannot be added', () => {
  assert.equal(canAddProduct({ ...product, isActive: false }, categories, 0), false);
  assert.equal(canAddProduct(product, [], 0), false);
  assert.equal(canAddProduct(product, [{ ...categories[0], isActive: false }], 0), false);
});
test('POS uncategorized goods and payment labels', () => {
  assert.equal(canAddProduct({ ...product, categoryId: null }, [], 0), true);
  assert.equal(paymentName('Wallet'), 'Кошелёк');
});
