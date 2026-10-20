import { describe, expect, it } from 'vitest';
import {
  alignmentPositions,
  byteCapacity,
  encodeQr,
  formatBits,
  maskBit,
  rsDivisor,
  rsRemainder,
  type Ecc,
  type QrCode,
} from '../../src/screens/toolbox/qr';

/**
 * Reads a QR matrix back with layout rules written here independently of the encoder (function areas, zigzag,
 * block interleaving), checks every block's Reed–Solomon parity and returns the byte-mode text.
 */
function readQr(m: boolean[][]): { ecc: Ecc; mask: number; text: string } {
  const size = m.length;
  const version = (size - 17) / 4;
  let raw = 0;
  const positions: [number, number][] = [
    [8, 0], [8, 1], [8, 2], [8, 3], [8, 4], [8, 5], [8, 7], [8, 8], [7, 8], [5, 8], [4, 8], [3, 8], [2, 8], [1, 8], [0, 8],
  ];
  positions.forEach(([x, y], i) => {
    if (m[y]![x]) raw |= 1 << i;
  });
  const data = (raw ^ 0x5412) >>> 10;
  const ecc: Ecc = (data >>> 3) === 1 ? 'L' : 'M';
  const mask = data & 7;
  expect(formatBits(ecc, mask)).toBe(raw);

  const align = alignmentPositions(version);
  const last = align.length - 1;
  const reserved = (x: number, y: number) => {
    if ((x < 9 && y < 9) || (x >= size - 8 && y < 9) || (x < 9 && y >= size - 8)) return true;
    if (x === 6 || y === 6) return true;
    if (version >= 7 && ((x >= size - 11 && y < 6) || (y >= size - 11 && x < 6))) return true;
    return align.some((px, i) =>
      align.some((py, j) => !((i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0)) && Math.abs(x - px) <= 2 && Math.abs(y - py) <= 2),
    );
  };

  const bits: number[] = [];
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    for (let vert = 0; vert < size; vert++) {
      for (let j = 0; j < 2; j++) {
        const x = right - j;
        const y = ((right + 1) & 2) === 0 ? size - 1 - vert : vert;
        if (!reserved(x, y)) bits.push(m[y]![x]! !== maskBit(mask, x, y) ? 1 : 0);
      }
    }
  }
  const codewords: number[] = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) codewords.push(bits.slice(i, i + 8).reduce((a, b) => (a << 1) | b, 0));

  // Block sizes from the standard's table for versions 1–10.
  const table: Record<Ecc, [number, number][]> = {
    L: [[0, 0], [1, 7], [1, 10], [1, 15], [1, 20], [1, 26], [2, 18], [2, 20], [2, 24], [2, 30], [4, 18]],
    M: [[0, 0], [1, 10], [1, 16], [1, 26], [2, 18], [2, 24], [4, 16], [4, 18], [4, 22], [5, 22], [5, 26]],
  };
  const [blocks, eccLen] = table[ecc][version]!;
  const total = codewords.length;
  const shortLen = Math.floor(total / blocks);
  const numShort = blocks - (total % blocks);
  const lists: number[][] = Array.from({ length: blocks }, () => []);
  let k = 0;
  for (let i = 0; i < shortLen + 1; i++) {
    for (let b = 0; b < blocks; b++) {
      if (i === shortLen - eccLen && b < numShort) continue;
      lists[b]!.push(codewords[k++]!);
    }
  }
  const dataBytes: number[] = [];
  for (const block of lists) {
    const dataPart = block.slice(0, block.length - eccLen);
    expect(rsRemainder(dataPart, rsDivisor(eccLen))).toEqual(block.slice(block.length - eccLen));
    dataBytes.push(...dataPart);
  }

  const stream = dataBytes.flatMap((b) => Array.from({ length: 8 }, (_, i) => (b >>> (7 - i)) & 1));
  const take = (n: number) => stream.splice(0, n).reduce((a, b) => (a << 1) | b, 0);
  expect(take(4)).toBe(0b0100);
  const count = take(version <= 9 ? 8 : 16);
  const bytes = Array.from({ length: count }, () => take(8));
  return { ecc, mask, text: new TextDecoder().decode(new Uint8Array(bytes)) };
}

function finderAt(code: QrCode, cx: number, cy: number): boolean {
  for (let dy = -3; dy <= 3; dy++) {
    for (let dx = -3; dx <= 3; dx++) {
      const d = Math.max(Math.abs(dx), Math.abs(dy));
      if (code.modules[cy + dy]![cx + dx] !== (d !== 2)) return false;
    }
  }
  return true;
}

describe('QR encoder', () => {
  it('computes Reed–Solomon parity like the standard’s worked example (HELLO WORLD, 1-M)', () => {
    const data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
    expect(rsRemainder(data, rsDivisor(10))).toEqual([196, 35, 39, 119, 235, 215, 231, 226, 93, 23]);
  });

  it('writes the format bits of the published table', () => {
    expect(formatBits('M', 0).toString(2).padStart(15, '0')).toBe('101010000010010');
    expect(formatBits('L', 0).toString(2).padStart(15, '0')).toBe('111011111000100');
    expect(formatBits('M', 5).toString(2).padStart(15, '0')).toBe('100000011001110');
  });

  it('places alignment patterns where the standard puts them', () => {
    expect(alignmentPositions(1)).toEqual([]);
    expect(alignmentPositions(2)).toEqual([6, 18]);
    expect(alignmentPositions(7)).toEqual([6, 22, 38]);
    expect(alignmentPositions(10)).toEqual([6, 28, 50]);
  });

  it('knows the byte capacity of each version', () => {
    expect(byteCapacity(1, 'M')).toBe(14);
    expect(byteCapacity(3, 'M')).toBe(42);
    expect(byteCapacity(4, 'M')).toBe(62);
    expect(byteCapacity(10, 'M')).toBe(213);
  });

  it.each([
    ['WIFI:T:ADB;S:devicedeck-a1b2c3;P:Xy7Kp2Qm9Z;;', 'M' as Ecc],
    ['WIFI:T:ADB;S:studio-0123456789;P:abcdefghijklmnopqrstuvwxyz0123;;', 'M' as Ecc],
    ['short', 'L' as Ecc],
    ['x'.repeat(150), 'M' as Ecc],
    ['نام-دستگاه', 'M' as Ecc],
  ])('encodes %s so it reads back, with finder patterns and checked parity', (text, ecc) => {
    const code = encodeQr(text, ecc);
    expect(code.size).toBe(code.version * 4 + 17);
    expect(finderAt(code, 3, 3) && finderAt(code, code.size - 4, 3) && finderAt(code, 3, code.size - 4)).toBe(true);
    expect(code.modules[code.size - 8]![8]).toBe(true); // the dark module
    const read = readQr(code.modules);
    expect(read).toEqual({ ecc, mask: code.mask, text });
  });

  it('picks the smallest version that fits and refuses text past version 10', () => {
    expect(encodeQr('WIFI:T:ADB;S:devicedeck-a1b2c3;P:Xy7Kp2Qm9Z;;').version).toBe(4);
    expect(() => encodeQr('x'.repeat(400))).toThrow();
  });
});
