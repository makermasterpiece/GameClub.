import test from 'node:test';
import assert from 'node:assert/strict';
import { countdown, duration, stationState, eligibleTransferStations, canRequestPower } from '../src/models.ts';

test('active countdown interpolates whole seconds', () => assert.equal(countdown(100, 'Active', 2300), 98));
test('paused countdown is frozen', () => assert.equal(countdown(100, 'Paused', 50000), 100));
test('countdown never becomes negative', () => assert.equal(countdown(10, 'Active', 50000), 0));
test('unlimited remains unbounded', () => assert.equal(countdown(null, 'Active', 50000), null));
test('clock regression does not add time', () => assert.equal(countdown(10, 'Active', -100), 10));
test('zero is not an implicit completed status', () => assert.equal(countdown(0, 'Paused', 1000), 0));
test('duration formats hours greater than 24', () => assert.equal(duration(90061), '25:01:01'));
test('negative duration clamps to zero', () => assert.equal(duration(-1), '00:00:00'));
const available = { agentOnline: true, clientConnected: true, clientState: 'Available', gamingSession: null, currentUser: null };
test('available station is classified', () => assert.equal(stationState(available).tone, 'available'));
test('offline overrides stale active game', () => assert.equal(stationState({ ...available, agentOnline: false, gamingSession: { status: 'Active' } }).tone, 'offline'));
test('lock overrides active game shell', () => assert.equal(stationState({ ...available, clientState: 'Locked', gamingSession: { status: 'Active' } }).tone, 'locked'));
test('logged in player waits for paid game', () => assert.equal(stationState({ ...available, currentUser: { id: '1' } }).tone, 'waiting'));
test('missing Client is not shown available', () => assert.equal(stationState({ ...available, clientConnected: false }).tone, 'warning'));
test('connected but fail-closed Client is not available', () => assert.equal(stationState({ ...available, clientState: 'Offline' }).tone, 'warning'));
test('paused paid game remains visible', () => assert.equal(stationState({ ...available, gamingSession: { status: 'Paused' } }).label, 'ПАУЗА'));

const source = { ...available, id: 'source', stationGroupId: 'standard', status: 'Online' };
const target = { ...source, id: 'target' };
test('transfer accepts only another free online station in the same group', () => {
  assert.deepEqual(eligibleTransferStations(source, [source, target, { ...target, id: 'vip', stationGroupId: 'vip' }]), [target]);
});
for (const state of [{ agentOnline: false }, { status: 'Maintenance' }, { clientState: 'Maintenance' }, { currentUser: { id: 'user' } }, { gamingSession: { status: 'Paused' } }]) {
  test(`transfer rejects unavailable target ${JSON.stringify(state)}`, () => assert.deepEqual(eligibleTransferStations(source, [{ ...target, ...state }]), []));
}
test('missing group does not imply tariff compatibility', () => assert.deepEqual(eligibleTransferStations({ ...source, stationGroupId: null }, [{ ...target, stationGroupId: null }]), []));
test('power accepts free Online station', () => assert.equal(canRequestPower(target), true));
for (const state of [{ agentOnline: false }, { status: 'Maintenance' }, { currentUser: { id: 'user' } }, { gamingSession: { status: 'Paused' } }]) {
  test(`power rejects unavailable station ${JSON.stringify(state)}`, () => assert.equal(canRequestPower({ ...target, ...state }), false));
}
