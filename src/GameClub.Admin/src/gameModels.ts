export type Game = { id: string; name: string; playniteGameId: string | null; executable: string | null; coverUrl: string | null; isActive: boolean };
export type StationGame = { gameId: string; name: string; playniteGameId: string | null; isActive: boolean; installed: boolean; lastDetectedAtUtc: string | null };

export function installationState(game: StationGame, now: number): string {
  const seen = game.lastDetectedAtUtc ? Date.parse(game.lastDetectedAtUtc) : NaN;
  if (!Number.isFinite(seen)) return 'Нет отчёта';
  if (seen > now || now - seen > 90000) return 'Отчёт устарел';
  return game.installed ? 'Установлена' : 'Не установлена';
}
export function gameLaunchable(game: StationGame, now: number): boolean {
  return game.isActive && !!game.playniteGameId && installationState(game, now) === 'Установлена';
}
