export type Player = { id: string; username: string; displayName: string | null; status?: string };
export type Gaming = { id: string; userId: string; stationId: string; status: string; startedAtUtc: string | null; expectedEndAtUtc: string | null; remainingSeconds: number | null; serverTimeUtc: string; elapsedSeconds: number; tariffId: string | null; packageId: string | null };
export type Station = { id: string; name: string; stationGroupId: string | null; groupName: string | null; status: string; agentOnline: boolean; clientConnected: boolean; clientState: string; agentVersion: string; lastSeenAtUtc: string; currentUser: Player | null; gamingSession: Gaming | null; tariffName: string | null };
export type DashboardData = { serverTimeUtc: string; stations: Station[] };
export type Group = { id: string; name: string };
export type Tariff = { id: string; name: string; stationGroupId: string; hourlyPrice: number; isActive: boolean };
export type Package = { id: string; name: string; stationGroupId: string; durationMinutes: number; price: number; availableFrom: string | null; availableUntil: string | null; isActive: boolean };
export type Catalog = { groups: Group[]; tariffs: Tariff[]; packages: Package[] };
export type Wallet = { balance: number; reserved: number; available: number };
export type LedgerEntry = { id: string; type: string; amount: number; balanceAfter: number; createdAtUtc: string; referenceType: string | null };

export function countdown(seconds: number | null, status: string, elapsedMilliseconds: number): number | null {
  if (seconds === null) return null;
  return Math.max(0, seconds - (status === 'Active' ? Math.floor(Math.max(0, elapsedMilliseconds) / 1000) : 0));
}
export function duration(seconds: number | null): string {
  if (seconds === null) return 'Без лимита';
  const value = Math.max(0, Math.floor(seconds));
  return `${Math.floor(value / 3600).toString().padStart(2, '0')}:${Math.floor(value / 60 % 60).toString().padStart(2, '0')}:${(value % 60).toString().padStart(2, '0')}`;
}
export function stationState(station: Station): { label: string; tone: string } {
  if (!station.agentOnline) return { label: 'OFFLINE', tone: 'offline' };
  if (!station.clientConnected) return { label: 'НЕТ CLIENT', tone: 'warning' };
  if (station.clientState === 'Offline') return { label: 'CLIENT OFFLINE', tone: 'warning' };
  if (station.clientState === 'Maintenance') return { label: 'ОБСЛУЖИВАНИЕ', tone: 'warning' };
  if (station.clientState === 'Locked') return { label: 'LOCKED', tone: 'locked' };
  if (station.gamingSession) return station.gamingSession.status === 'Paused'
    ? { label: 'ПАУЗА', tone: 'warning' } : { label: 'В ИГРЕ', tone: 'playing' };
  if (station.currentUser) return { label: 'ОЖИДАЕТ СТАРТА', tone: 'waiting' };
  return { label: 'AVAILABLE', tone: 'available' };
}
export const money = (value: number) => new Intl.NumberFormat('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
export const dateTime = (value: string | null) => value ? new Date(value).toLocaleString('ru-RU') : '—';
export const roleName = (role: string) => ({ Administrator: 'Администратор', Manager: 'Менеджер', Operator: 'Оператор' }[role] ?? role);

export function eligibleTransferStations(source: Station, stations: Station[]): Station[] {
  return stations.filter(target => target.id !== source.id && !!source.stationGroupId &&
    target.stationGroupId === source.stationGroupId && target.agentOnline && target.status === 'Online' &&
    target.clientState !== 'Maintenance' && !target.currentUser && !target.gamingSession);
}

export function canRequestPower(station: Station): boolean {
  return station.agentOnline && station.status === 'Online' && !station.currentUser && !station.gamingSession;
}
