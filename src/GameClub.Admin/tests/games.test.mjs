import test from 'node:test';
import assert from 'node:assert/strict';
import { installationState, gameLaunchable } from '../src/gameModels.ts';
const now = Date.parse('2026-09-08T10:00:00Z');
const game = { gameId: 'a', name: 'Test', playniteGameId: 'b', isActive: true, installed: true, lastDetectedAtUtc: new Date(now).toISOString() };
test('fresh installed active game can launch', () => assert.equal(gameLaunchable(game, now), true));
test('missing and stale reports fail closed', () => {
  assert.equal(installationState({ ...game, lastDetectedAtUtc: null }, now), 'Нет отчёта');
  assert.equal(gameLaunchable(game, now + 90001), false);
  assert.equal(gameLaunchable(game, now - 1), false);
});
test('inactive, unmapped, uninstalled games cannot launch', () => {
  for (const change of [{ isActive: false }, { playniteGameId: null }, { installed: false }])
    assert.equal(gameLaunchable({ ...game, ...change }, now), false);
});
