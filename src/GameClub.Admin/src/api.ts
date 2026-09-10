export type Employee = { id: string; username: string; role: string; permissions: string[] };
export type Authentication = { accessToken: string; expiresAtUtc: string; employee: Employee };
export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code: string) { super(code); }
}

export async function api<T>(path: string, token: string | null, method = 'GET', body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
    cache: 'no-store',
    signal: AbortSignal.timeout(15000),
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { code?: string; title?: string };
    throw new ApiError(response.status, error.code ?? `HTTP_${response.status}`);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

const errors: Record<string, string> = {
  PRICE_CHANGED: 'Цена изменилась. Проверьте обновлённую корзину перед новой продажей.',
  INSUFFICIENT_STOCK: 'Недостаточно товара на складе.',
  PRODUCT_NOT_AVAILABLE: 'Товар или его категория отключены.',
  PRODUCT_NOT_FOUND: 'Товар не найден.', PRODUCT_CATEGORY_NOT_FOUND: 'Категория не найдена.',
  SHIFT_NOT_OPEN: 'Нужна ваша открытая смена.', SHIFT_ALREADY_OPEN: 'У вас уже есть открытая смена.',
  SHIFT_CLOSED: 'Смена уже закрыта.', SHIFT_NOT_OWNED: 'Это смена другого сотрудника.',
  SHIFT_NOT_FOUND: 'Смена не найдена.', SALE_NOT_FOUND: 'Продажа не найдена.',
  SALE_ALREADY_REFUNDED: 'По этой продаже уже оформлен полный возврат.',
  USER_REQUIRED: 'Для оплаты кошельком выберите покупателя.',
  INVALID_CART: 'Проверьте товары и количество в корзине.',
  INVALID_STOCK_QUANTITY: 'Изменение выводит остаток за допустимые пределы.',
  STOCK_LIMIT_EXCEEDED: 'Возврат превысит максимальный остаток товара.',
  POS_CATALOG_LIMIT: 'Достигнут лимит каталога POS.',
  ACTIVE_PAID_SESSION_REQUIRED: 'Для запуска нужна активная оплаченная сессия.',
  GAME_UNAVAILABLE: 'Игра отключена или нет свежего подтверждения установки.',
  CLIENT_NOT_READY: 'Client станции не готов к запуску игры.',
  GAME_MAPPING_CHANGED: 'Сопоставление игры изменилось. Проверьте локальный список станции.',
  PLAYNITE_GAME_ALREADY_MAPPED: 'Этот Playnite ID уже используется другой игрой.',
  INVALID_CREDENTIALS: 'Неверный логин или пароль.', RATE_LIMITED: 'Слишком много попыток. Повторите через минуту.',
  HTTP_401: 'Войдите снова: авторизация истекла.', HTTP_403: 'Недостаточно прав для этого действия.',
  HTTP_400: 'Проверьте введённые данные.', INSUFFICIENT_FUNDS: 'Недостаточно доступных средств.',
  STATION_UNAVAILABLE: 'Станция недоступна.', PLAYER_NOT_AUTHENTICATED: 'Игрок должен войти на этой станции.',
  SESSION_ALREADY_ACTIVE: 'У игрока или станции уже есть игровая сессия.', IDEMPOTENCY_CONFLICT: 'Идентификатор запроса уже использован с другими данными.',
  CONCURRENT_CONFLICT: 'Данные изменились параллельно. Повторите запрос.',
  SESSION_EXCEEDS_AUTH_LIFETIME: 'Купленное время выходит за срок авторизации игрока. Требуется повторный вход или меньшая длительность.',
  PACKAGE_UNAVAILABLE: 'Пакет недоступен в текущее время.', STATION_GROUP_MISMATCH: 'Тариф не подходит для этой группы станций.',
  INVALID_SESSION_TRANSITION: 'Состояние сессии уже изменилось. Обновите данные.',
  USERNAME_ALREADY_EXISTS: 'Этот логин уже занят.', INVALID_AMOUNT: 'Введите допустимую сумму с точностью до двух знаков.',
  LAST_ADMINISTRATOR: 'Нельзя отключить последнего администратора.',
  SESSION_EXCEEDS_PACKAGE_WINDOW: 'Продление выходит за окончание ночного окна пакета.',
  EXTENSION_PRICING_UNAVAILABLE: 'Для этой старой сессии нет данных исходной цены продления.',
  SESSION_EXPIRED: 'Время сессии уже истекло.', INVALID_DURATION: 'Недопустимая длительность сессии.',
  STATION_OCCUPIED: 'На выбранной станции уже есть игрок или игровая сессия.',
  STATION_OFFLINE: 'Нет свежего heartbeat станции.', STATION_BUSY: 'Сначала завершите игру и вход игрока.',
  STATION_POWER_PENDING: 'Станция выполняет команду питания. Повторите позже.', SAME_STATION: 'Выберите другой ПК.',
};
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return errors[error.code] ?? `Запрос отклонён: ${error.code}.`;
  return 'Нет ответа от сервера. Проверьте соединение и повторите запрос.';
}
