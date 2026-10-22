import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { BridgeError } from '../../src/bridge/errors';
import { METHOD_NAMES } from '../../src/bridge/methods';
import type { MockBackend } from '../../src/bridge/mock/backend';
import type { MockDeleteSpeed, MockPhotoDeleteScenario, MockReachScenario } from '../../src/bridge/mock/photoDelete';
import { ANDROID_ID, IPHONE_ID, IPHONE_LIMITED_CAPABILITIES } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import type { TransferJob } from '../../src/bridge/types';
import { photoDeleteEn, photoDeleteFa } from '../../src/screens/photos/deleteStrings';
import { reachEn, reachFa } from '../../src/screens/photos/reachStrings';
import { deleteJobEn, deleteJobFa } from '../../src/shell/deleteJobStrings';
import { jobFraction, overallProgress } from '../../src/state/transfers';

// Deleting photos (round 4, photodelete; a transfer job since v0.4.1) in the mock: the same rules as the host.

let handle: MockHandle | null = null;

function setup(photoDelete: MockPhotoDeleteScenario = 'ok', deleteSpeed: MockDeleteSpeed = 'normal', reach: MockReachScenario = 'all') {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, indexing: false, seedQueue: false, photoDelete, deleteSpeed, reach });
  const client = new BridgeClient(handle.transport, { timeoutMs: 0 });
  const updates: TransferJob[] = [];
  client.on('transfer.updated', (job) => updates.push(job));
  return { client, backend: handle.backend, updates };
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

async function rejection(promise: Promise<unknown>): Promise<BridgeError> {
  try {
    await promise;
  } catch (error) {
    return error as BridgeError;
  }
  throw new Error('expected the call to fail');
}

async function firstPhotos(client: BridgeClient, deviceId: string, n: number) {
  const page = await client.call('photos.list', { deviceId, offset: 0, limit: 60 });
  return page.items.filter((a) => !a.inCloudOnly).slice(0, n);
}

const finished = (job: TransferJob | undefined) => !!job && ['done', 'failed', 'cancelled'].includes(job.state);

/** Advances the mock's clock until the job has finished (at most a simulated minute). */
async function finish(client: BridgeClient, backend: MockBackend, jobId: string) {
  for (let t = 0; t < 60_000; t += 250) {
    if (finished((await client.call('transfers.list')).find((j) => j.id === jobId))) break;
    backend.tick(250);
  }
  return client.call('photos.deleteJob', { jobId });
}

async function deleteItems(client: BridgeClient, deviceId: string, items: { id: string; size: number }[], copyFirstTo?: string) {
  const started = await client.call('photos.deleteItems', { deviceId, items, expectedBytes: items.reduce((n, i) => n + i.size, 0), copyFirstTo });
  expect(started.skipped).toBe(0);
  return started.jobId!;
}

describe('mock photo delete', () => {
  it('lists the methods, and the limited iPhone (no Apple driver) no longer claims photosDelete', () => {
    expect(METHOD_NAMES).toContain('photos.deleteSupport');
    expect(METHOD_NAMES).toContain('photos.deleteItems');
    expect(METHOD_NAMES).toContain('photos.deleteJob');
    expect(METHOD_NAMES).toContain('photos.deletePrepare');
    expect(IPHONE_LIMITED_CAPABILITIES).not.toContain('photosDelete');
  });

  it('says why an iPhone can’t delete, and takes away photosDelete', async () => {
    for (const reason of ['iCloudPhotos', 'readOnly', 'locked', 'notCamera', 'converted'] as const) {
      const { client } = setup(reason);
      const support = await client.call('photos.deleteSupport', { deviceId: IPHONE_ID });
      expect(support.canDelete).toBe(false);
      expect(support.reason).toBe(reason);
      const device = (await client.call('device.list')).find((d) => d.id === IPHONE_ID)!;
      expect(device.capabilities).not.toContain('photosDelete');
      handle!.dispose();
      handle = null;
    }
  });

  it('“Check again” after unlocking gives the iPhone photosDelete back', async () => {
    const { client } = setup('locked');
    const changed: string[][] = [];
    client.on('device.changed', (d) => changed.push(d.capabilities));
    const support = await client.call('photos.deleteSupport', { deviceId: IPHONE_ID, recheck: true });
    expect(support.canDelete).toBe(true);
    expect(changed.at(-1)).toContain('photosDelete');
  });

  it('starts a job at once that reads, deletes and checks, then answers per item with where they went', async () => {
    const { client, backend, updates } = setup();
    const [a, b] = await firstPhotos(client, IPHONE_ID, 2);
    const before = (await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 1 })).total;
    const support = await client.call('photos.deleteSupport', { deviceId: IPHONE_ID });
    expect(support).toMatchObject({ canDelete: true, reason: 'none', goesTo: 'permanent', observed: false });

    const jobId = await deleteItems(client, IPHONE_ID, [{ id: a!.id, size: a!.size }, { id: b!.id, size: b!.size }]);
    const queued = (await client.call('transfers.list')).find((j) => j.id === jobId)!;
    expect(queued).toMatchObject({ kind: 'photosDelete', totalItems: 2, doneItems: 0 });
    expect((await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 1 })).total).toBe(before); // nothing yet

    const report = await finish(client, backend, jobId);
    expect(report).toMatchObject({ state: 'done', deleted: 2, source: 'photos', goesTo: 'permanent', stage: null });
    expect(report.items.map((i) => [i.id, i.outcome])).toEqual([[a!.id, 'deleted'], [b!.id, 'deleted']]);
    expect((await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 1 })).total).toBe(before - 2);
    expect((await client.call('photos.deleteSupport', { deviceId: IPHONE_ID })).observed).toBe(true);

    const stages = updates.filter((u) => u.id === jobId && u.stage).map((u) => u.stage!.name);
    expect([...new Set(stages)]).toEqual(['reading', 'deleting', 'checking']);
    const reading = updates.filter((u) => u.id === jobId && u.stage?.name === 'reading').map((u) => u.stage!);
    expect(reading.at(-1)!.total).toBe(3258); // the camera roll Windows lists, read N of M
    expect(reading.map((s) => s.done)).toEqual([...reading.map((s) => s.done)].sort((x, y) => x - y));
  });

  it('one item that changed means nothing is deleted and the job fails saying so', async () => {
    const { client, backend } = setup();
    const [a, b] = await firstPhotos(client, ANDROID_ID, 2);
    const before = (await client.call('photos.list', { deviceId: ANDROID_ID, offset: 0, limit: 1 })).total;
    const jobId = await deleteItems(client, ANDROID_ID, [{ id: a!.id, size: a!.size }, { id: b!.id, size: b!.size + 1 }]);
    const report = await finish(client, backend, jobId);
    expect(report.state).toBe('failed');
    expect(report.error!.message).toContain('Nothing was deleted');
    expect(report.items.map((i) => i.outcome)).toEqual(['notTried', 'mismatch']);
    expect((await client.call('photos.list', { deviceId: ANDROID_ID, offset: 0, limit: 1 })).total).toBe(before);
  });

  it('stops after the batch where the phone refused one, and the rest are not tried', async () => {
    const { client, backend } = setup('fails');
    const photos = await firstPhotos(client, IPHONE_ID, 7);
    const jobId = await deleteItems(client, IPHONE_ID, photos.map((p) => ({ id: p.id, size: p.size })));
    const report = await finish(client, backend, jobId);
    expect(report.items.map((i) => i.outcome)).toEqual(['deleted', 'deleted', 'failed', 'deleted', 'deleted', 'notTried', 'notTried']);
    expect(report.items[2]!.code).toBe('permission_denied');
    expect(report.state).toBe('done'); // some went: done, with failures
  });

  it('copies first when asked, then deletes', async () => {
    const { client, backend, updates } = setup('recentlyDeleted');
    const [a] = await firstPhotos(client, IPHONE_ID, 1);
    const jobId = await deleteItems(client, IPHONE_ID, [{ id: a!.id, size: a!.size }], 'D:\\Kept');
    const report = await finish(client, backend, jobId);
    expect(report.copyJobIds).toHaveLength(1);
    expect(report.goesTo).toBe('recentlyDeleted');
    expect([...new Set(updates.filter((u) => u.id === jobId && u.stage).map((u) => u.stage!.name))]).toEqual(['copying', 'reading', 'deleting', 'checking']);
  });

  it('checks its params and refuses an iPhone that can’t delete, without a job', async () => {
    const { client } = setup();
    const total = await rejection(client.call('photos.deleteItems', { deviceId: IPHONE_ID, items: [{ id: 'x', size: 1 }], expectedBytes: 2 }));
    expect(total.code).toBe('invalid_params');
    const relative = await rejection(client.call('photos.deleteItems', { deviceId: IPHONE_ID, items: [{ id: 'x', size: 1 }], expectedBytes: 1, copyFirstTo: 'Kept' }));
    expect(relative.code).toBe('invalid_params');
    handle!.dispose();

    const blocked = setup('iCloudPhotos');
    const [a] = await firstPhotos(blocked.client, IPHONE_ID, 1);
    const error = await rejection(blocked.client.call('photos.deleteItems', { deviceId: IPHONE_ID, items: [{ id: a!.id, size: a!.size }], expectedBytes: a!.size }));
    expect(error.code).toBe('not_supported');
    expect(await blocked.client.call('transfers.list')).toEqual([]);
  });

  it('cancel while it reads deletes nothing; cancel while it deletes stops after the batch', async () => {
    const { client, backend } = setup('ok', 'slow');
    const photos = await firstPhotos(client, IPHONE_ID, 12);
    const before = (await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 1 })).total;
    const first = await deleteItems(client, IPHONE_ID, photos.map((p) => ({ id: p.id, size: p.size })));
    backend.tick(1000);
    expect((await client.call('transfers.list')).find((j) => j.id === first)!.stage!.name).toBe('reading');
    await client.call('transfers.cancel', { jobId: first });
    const stopped = await client.call('photos.deleteJob', { jobId: first });
    expect(stopped).toMatchObject({ state: 'cancelled', deleted: 0 });
    expect(stopped.items.every((i) => i.outcome === 'notTried')).toBe(true);
    expect((await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 1 })).total).toBe(before);

    const second = await deleteItems(client, IPHONE_ID, photos.map((p) => ({ id: p.id, size: p.size })));
    for (let t = 0; t < 60_000; t += 250) {
      const job = (await client.call('transfers.list')).find((j) => j.id === second)!;
      if (job.stage?.name === 'deleting' && job.stage.done >= 5) break;
      backend.tick(250);
    }
    await client.call('transfers.cancel', { jobId: second });
    const partly = await client.call('photos.deleteJob', { jobId: second });
    expect(partly.state).toBe('cancelled');
    expect(partly.deleted).toBe(10); // the batch on its way when Stop was pressed went too
    expect(partly.items.slice(10).every((i) => i.outcome === 'notTried')).toBe(true);
  });

  it('can’t be paused, resumed or retried, and fails when its phone goes away', async () => {
    const { client, backend } = setup('ok', 'slow');
    const photos = await firstPhotos(client, IPHONE_ID, 3);
    const jobId = await deleteItems(client, IPHONE_ID, photos.map((p) => ({ id: p.id, size: p.size })));
    backend.tick(500);
    expect((await rejection(client.call('transfers.pause', { jobId }))).message).toContain('can’t be paused');
    backend.unplug(IPHONE_ID);
    const failed = (await client.call('transfers.list')).find((j) => j.id === jobId)!;
    expect(failed).toMatchObject({ state: 'failed', error: { code: 'device_disconnected' } });
    expect((await rejection(client.call('transfers.retry', { jobId }))).code).toBe('internal');
    expect((await rejection(client.call('transfers.resume', { jobId }))).code).toBe('internal');
  });

  it('reads an iPhone’s camera roll ahead when asked (Photos, or a Clean-up analysis), and a delete carries on from it', async () => {
    const { client, backend, updates } = setup('ok', 'slow');
    const [a, b] = await firstPhotos(client, IPHONE_ID, 2);
    expect(await client.call('photos.deletePrepare', { deviceId: IPHONE_ID, reason: 'Photos: selection' })).toEqual({});
    for (let t = 0; t < 6_000; t += 250) backend.tick(250); // part of it
    const first = await deleteItems(client, IPHONE_ID, [{ id: a!.id, size: a!.size }]);
    const reading = updates.find((u) => u.id === first && u.stage?.name === 'reading')!;
    expect(reading.stage!.done).toBeGreaterThanOrEqual(1300); // carried on from about 6 s of reading ahead
    await finish(client, backend, first);

    // A Clean-up analysis starts it too; read to the end, the next delete reads nothing.
    await client.call('cleanup.analyze', { deviceId: IPHONE_ID });
    for (let t = 0; t < 20_000; t += 250) backend.tick(250);
    const second = await deleteItems(client, IPHONE_ID, [{ id: b!.id, size: b!.size }]);
    expect(updates.find((u) => u.id === second && u.stage?.name === 'reading')!.stage).toEqual({ name: 'reading', done: 3258, total: 3258 });
  });

  it('says which items only the iPhone can delete, and only once its camera roll’s view is read completely (v0.4.2)', async () => {
    const { client, backend } = setup('ok', 'slow', 'some');
    expect(METHOD_NAMES).toContain('photos.deleteReach');

    // Asking starts the read ahead; until it is complete nothing is marked.
    const first = await client.call('photos.deleteReach', { deviceId: IPHONE_ID });
    expect(first).toMatchObject({ deviceId: IPHONE_ID, state: 'checking', done: 0, total: 3258, notListed: [], checkedAt: null });
    for (let t = 0; t < 4_000; t += 250) backend.tick(250);
    const midway = await client.call('photos.deleteReach', { deviceId: IPHONE_ID });
    expect(midway.state).toBe('checking');
    expect(midway.done).toBeGreaterThan(0);
    expect(midway.done).toBeLessThan(3258);
    expect(midway.notListed).toEqual([]);

    for (let t = 0; t < 20_000; t += 250) backend.tick(250);
    const ready = await client.call('photos.deleteReach', { deviceId: IPHONE_ID });
    expect(ready).toMatchObject({ state: 'ready', done: 3258, total: 3258 });
    expect(ready.checkedAt).not.toBeNull();
    expect(ready.notListed.length).toBeGreaterThan(0);

    // A delete of one of them is refused, saying where to delete it; the others go.
    const page = await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 200 });
    const onPhone = page.items.find((a) => ready.notListed.includes(a.id))!;
    const listed = page.items.find((a) => !a.inCloudOnly && !ready.notListed.includes(a.id))!;
    expect(onPhone.inCloudOnly).toBeFalsy();
    const refused = await finish(client, backend, await deleteItems(client, IPHONE_ID, [{ id: onPhone.id, size: onPhone.size }]));
    expect(refused.items[0]).toMatchObject({ outcome: 'mismatch' });
    expect(refused.items[0]!.message).toContain('Delete it in the Photos app on the iPhone');
    const went = await finish(client, backend, await deleteItems(client, IPHONE_ID, [{ id: listed.id, size: listed.size }]));
    expect(went.items[0]).toMatchObject({ outcome: 'deleted' });

    // What was known stands after a delete (it read the whole view too).
    expect((await client.call('photos.deleteReach', { deviceId: IPHONE_ID })).notListed).toEqual(ready.notListed);
  });

  it('marks nothing on an iPhone that lists everything, and answers Android and an iPhone that can’t delete', async () => {
    const all = setup('ok', 'normal', 'all');
    await all.client.call('photos.deleteReach', { deviceId: IPHONE_ID });
    for (let t = 0; t < 5_000; t += 250) all.backend.tick(250);
    expect(await all.client.call('photos.deleteReach', { deviceId: IPHONE_ID })).toMatchObject({ state: 'ready', notListed: [] });
    expect(await all.client.call('photos.deleteReach', { deviceId: ANDROID_ID })).toMatchObject({ state: 'ready', notListed: [] });
    handle!.dispose();

    const cant = setup('iCloudPhotos', 'normal', 'some');
    expect(await cant.client.call('photos.deleteReach', { deviceId: IPHONE_ID })).toMatchObject({ state: 'unsupported', notListed: [] });
  });

  it('a phone without photosDelete, or an Android phone, reads nothing ahead', async () => {
    const { client } = setup('iCloudPhotos');
    expect(await client.call('photos.deletePrepare', { deviceId: IPHONE_ID })).toEqual({});
    expect(await client.call('photos.deletePrepare', { deviceId: ANDROID_ID })).toEqual({});
  });

  it('shows a stage’s progress, and keeps a delete out of the title bar’s bytes', () => {
    const base: TransferJob = {
      id: 'd', deviceId: 'p', kind: 'photosDelete', title: 'Deleting', state: 'running', totalItems: 39, doneItems: 0, failedItems: 0,
      totalBytes: 4_300_000_000, doneBytes: 0, bytesPerSecond: null, etaSeconds: null, conflict: null, error: null, createdAt: '2026-10-22T09:38:58Z', finishedAt: null,
      stage: { name: 'reading', done: 1629, total: 3258 },
    };
    expect(jobFraction(base)).toBe(0.5);
    expect(jobFraction({ ...base, stage: { name: 'reading', done: 0, total: 0 } })).toBe(0);
    expect(overallProgress([base])).toEqual({ active: 1, fraction: null, failed: 0 });
    const copy: TransferJob = { ...base, id: 'c', kind: 'photosExport', stage: null, totalBytes: 100, doneBytes: 25 };
    expect(overallProgress([base, copy]).fraction).toBe(0.25);
  });

  it('has every string in English and Persian, none empty', () => {
    for (const [en, fa] of [
      [photoDeleteEn, photoDeleteFa],
      [deleteJobEn, deleteJobFa],
      [reachEn, reachFa],
    ] as const) {
      expect(Object.keys(fa).sort()).toEqual(Object.keys(en).sort());
      for (const [key, text] of Object.entries(fa)) {
        expect(text.trim(), key).not.toBe('');
        expect(text, key).toMatch(/[\u0600-\u06FF]/); // actually Persian, not English left behind
      }
    }
  });
});
