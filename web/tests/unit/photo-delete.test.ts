import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { BridgeError } from '../../src/bridge/errors';
import { METHOD_NAMES } from '../../src/bridge/methods';
import type { MockPhotoDeleteScenario } from '../../src/bridge/mock/photoDelete';
import { ANDROID_ID, IPHONE_ID, IPHONE_LIMITED_CAPABILITIES } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { photoDeleteEn, photoDeleteFa } from '../../src/screens/photos/deleteStrings';

// Deleting photos (round 4, photodelete) in the mock: the same rules as the host.

let handle: MockHandle | null = null;

function setup(photoDelete: MockPhotoDeleteScenario = 'ok') {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, indexing: false, seedQueue: false, photoDelete });
  return { client: new BridgeClient(handle.transport, { timeoutMs: 0 }), backend: handle.backend };
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

describe('mock photo delete', () => {
  it('lists both methods, and the limited iPhone (no Apple driver) no longer claims photosDelete', () => {
    expect(METHOD_NAMES).toContain('photos.deleteSupport');
    expect(METHOD_NAMES).toContain('photos.deleteItems');
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

  it('deletes exactly the confirmed photos and answers per item, with where they went', async () => {
    const { client } = setup();
    const [a, b] = await firstPhotos(client, IPHONE_ID, 2);
    const before = (await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 1 })).total;
    const support = await client.call('photos.deleteSupport', { deviceId: IPHONE_ID });
    expect(support).toMatchObject({ canDelete: true, reason: 'none', goesTo: 'permanent', observed: false });

    const report = await client.call('photos.deleteItems', {
      deviceId: IPHONE_ID,
      items: [{ id: a!.id, size: a!.size }, { id: b!.id, size: b!.size }],
      expectedBytes: a!.size + b!.size,
    });

    expect(report.deleted).toBe(2);
    expect(report.items.map((i) => [i.id, i.outcome])).toEqual([[a!.id, 'deleted'], [b!.id, 'deleted']]);
    expect((await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 1 })).total).toBe(before - 2);
    expect((await client.call('photos.deleteSupport', { deviceId: IPHONE_ID })).observed).toBe(true);
  });

  it('one item that changed means nothing is deleted', async () => {
    const { client } = setup();
    const [a, b] = await firstPhotos(client, ANDROID_ID, 2);
    const before = (await client.call('photos.list', { deviceId: ANDROID_ID, offset: 0, limit: 1 })).total;
    const report = await client.call('photos.deleteItems', {
      deviceId: ANDROID_ID,
      items: [{ id: a!.id, size: a!.size }, { id: b!.id, size: b!.size + 1 }],
      expectedBytes: a!.size + b!.size + 1,
    });
    expect(report.items.map((i) => i.outcome)).toEqual(['notTried', 'mismatch']);
    expect((await client.call('photos.list', { deviceId: ANDROID_ID, offset: 0, limit: 1 })).total).toBe(before);
  });

  it('stops after the batch where the phone refused one, and the rest are not tried', async () => {
    const { client } = setup('fails');
    const photos = await firstPhotos(client, IPHONE_ID, 7);
    const report = await client.call('photos.deleteItems', {
      deviceId: IPHONE_ID,
      items: photos.map((p) => ({ id: p.id, size: p.size })),
      expectedBytes: photos.reduce((n, p) => n + p.size, 0),
    });
    expect(report.items.map((i) => i.outcome)).toEqual(['deleted', 'deleted', 'failed', 'deleted', 'deleted', 'notTried', 'notTried']);
    expect(report.items[2]!.code).toBe('permission_denied');
  });

  it('copies first when asked, and checks its params', async () => {
    const { client } = setup('recentlyDeleted');
    const [a] = await firstPhotos(client, IPHONE_ID, 1);
    const report = await client.call('photos.deleteItems', { deviceId: IPHONE_ID, items: [{ id: a!.id, size: a!.size }], expectedBytes: a!.size, copyFirstTo: 'D:\\Kept' });
    expect(report.copyJobIds).toHaveLength(1);
    expect(report.goesTo).toBe('recentlyDeleted');

    const total = await rejection(client.call('photos.deleteItems', { deviceId: IPHONE_ID, items: [{ id: 'x', size: 1 }], expectedBytes: 2 }));
    expect(total.code).toBe('invalid_params');
    const relative = await rejection(client.call('photos.deleteItems', { deviceId: IPHONE_ID, items: [{ id: 'x', size: 1 }], expectedBytes: 1, copyFirstTo: 'Kept' }));
    expect(relative.code).toBe('invalid_params');
  });

  it('refuses an iPhone that can’t delete', async () => {
    const { client } = setup('iCloudPhotos');
    const [a] = await firstPhotos(client, IPHONE_ID, 1);
    const error = await rejection(client.call('photos.deleteItems', { deviceId: IPHONE_ID, items: [{ id: a!.id, size: a!.size }], expectedBytes: a!.size }));
    expect(error.code).toBe('not_supported');
  });

  it('has every string in English and Persian, none empty', () => {
    expect(Object.keys(photoDeleteFa).sort()).toEqual(Object.keys(photoDeleteEn).sort());
    for (const [key, text] of Object.entries(photoDeleteFa)) {
      expect(text.trim(), key).not.toBe('');
      expect(text, key).toMatch(/[\u0600-\u06FF]/); // actually Persian, not English left behind
    }
  });
});
