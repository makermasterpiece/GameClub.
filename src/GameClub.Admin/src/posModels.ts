export type ProductCategory = { id: string; name: string; isActive: boolean };
export type Product = { id: string; categoryId: string | null; name: string; price: number; stockQuantity: number; isActive: boolean };
export type PosCatalog = { categories: ProductCategory[]; products: Product[] };
export type Shift = { id: string; employeeId: string; openedAtUtc: string; closedAtUtc: string | null; openingCash: number; closingCash: number | null };
export type ShiftSummary = { shift: Shift; gamingSales: number; productSales: number; refunds: number; cash: number; card: number; wallet: number; expectedCash: number; cashDifference: number | null };
export type Sale = { id: string; employeeShiftId: string; employeeId: string; userId: string | null; stationId: string | null; paymentMethod: 'Cash' | 'Card' | 'Wallet'; capturedTotal: number; createdAtUtc: string };
export type SaleDetails = { sale: Sale; items: { id: string; nameSnapshot: string; unitPrice: number; quantity: number; lineTotal: number }[]; refund: { id: string; amount: number; reason: string } | null };
export type Cart = { productId: string; quantity: number }[];
export type PendingPos = { path: string; body: Record<string, unknown>; success: string };
export function cartTotal(cart: Cart, products: Product[]): number {
  const cents = cart.reduce((total, item) => {
    const product = products.find(p => p.id === item.productId);
    if (!product || !Number.isInteger(item.quantity) || item.quantity < 1 || item.quantity > 1000) return NaN;
    return total + Math.round(product.price * 100) * item.quantity;
  }, 0);
  return Number.isSafeInteger(cents) ? cents / 100 : NaN;
}
export function canAddProduct(product: Product, categories: ProductCategory[], quantity: number): boolean {
  return product.isActive && (!product.categoryId || categories.some(c => c.id === product.categoryId && c.isActive)) && quantity < Math.min(product.stockQuantity, 1000);
}
export const paymentName = (method: string) => ({ Cash: 'Наличные', Card: 'Карта', Wallet: 'Кошелёк' }[method] ?? method);
