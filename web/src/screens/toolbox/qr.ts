// A small QR Code encoder (ISO/IEC 18004) for the Wi-Fi pairing code: byte mode, versions 1–10, error correction
// L or M, mask chosen by penalty. Enough for "WIFI:T:ADB;S:…;P:…;;" and no dependency.
// Follows the structure of Project Nayuki's reference implementation (MIT), written from the specification.

export type Ecc = 'L' | 'M';

const ECC_FORMAT_BITS: Record<Ecc, number> = { L: 1, M: 0 };

// Index = version (0 unused).
const ECC_PER_BLOCK: Record<Ecc, number[]> = {
  L: [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18],
  M: [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26],
};
const BLOCKS: Record<Ecc, number[]> = {
  L: [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4],
  M: [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5],
};

export const MAX_VERSION = 10;

export interface QrCode {
  version: number;
  size: number;
  ecc: Ecc;
  mask: number;
  /** modules[y][x], true = dark. */
  modules: boolean[][];
}

function rawDataModules(version: number): number {
  let result = (16 * version + 128) * version + 64;
  if (version >= 2) {
    const align = Math.floor(version / 7) + 2;
    result -= (25 * align - 10) * align - 55;
    if (version >= 7) result -= 36;
  }
  return result;
}

export function dataCodewords(version: number, ecc: Ecc): number {
  return Math.floor(rawDataModules(version) / 8) - ECC_PER_BLOCK[ecc][version]! * BLOCKS[ecc][version]!;
}

/** Bytes that fit in byte mode at this version and level. */
export function byteCapacity(version: number, ecc: Ecc): number {
  const countBits = version <= 9 ? 8 : 16;
  return Math.floor((dataCodewords(version, ecc) * 8 - 4 - countBits) / 8);
}

// ---- Reed–Solomon over GF(2^8), polynomial 0x11D ----

export function gfMultiply(x: number, y: number): number {
  let z = 0;
  for (let i = 7; i >= 0; i--) {
    z = (z << 1) ^ ((z >>> 7) * 0x11d);
    z ^= ((y >>> i) & 1) * x;
  }
  return z & 0xff;
}

export function rsDivisor(degree: number): number[] {
  const result = new Array<number>(degree).fill(0);
  result[degree - 1] = 1;
  let root = 1;
  for (let i = 0; i < degree; i++) {
    for (let j = 0; j < result.length; j++) {
      result[j] = gfMultiply(result[j]!, root);
      if (j + 1 < result.length) result[j]! ^= result[j + 1]!;
    }
    root = gfMultiply(root, 0x02);
  }
  return result;
}

export function rsRemainder(data: readonly number[], divisor: readonly number[]): number[] {
  const result = divisor.map(() => 0);
  for (const b of data) {
    const factor = b ^ result.shift()!;
    result.push(0);
    divisor.forEach((coef, i) => (result[i]! ^= gfMultiply(coef, factor)));
  }
  return result;
}

// ---- Encoding ----

export function encodeQr(text: string, ecc: Ecc = 'M'): QrCode {
  const bytes = Array.from(new TextEncoder().encode(text));
  let version = 1;
  while (byteCapacity(version, ecc) < bytes.length) {
    if (++version > MAX_VERSION) throw new Error('Text too long for this QR encoder.');
  }

  // Bit stream: mode 0100, count, bytes, terminator, pad to bytes, pad codewords.
  const bits: number[] = [];
  const put = (value: number, length: number) => {
    for (let i = length - 1; i >= 0; i--) bits.push((value >>> i) & 1);
  };
  put(0b0100, 4);
  put(bytes.length, version <= 9 ? 8 : 16);
  for (const b of bytes) put(b, 8);
  const capacityBits = dataCodewords(version, ecc) * 8;
  put(0, Math.min(4, capacityBits - bits.length));
  put(0, (8 - (bits.length % 8)) % 8);
  for (let pad = 0xec; bits.length < capacityBits; pad ^= 0xec ^ 0x11) put(pad, 8);
  const data: number[] = [];
  for (let i = 0; i < bits.length; i += 8) data.push(bits.slice(i, i + 8).reduce((acc, bit) => (acc << 1) | bit, 0));

  const codewords = addEccAndInterleave(data, version, ecc);
  const size = version * 4 + 17;
  const modules = Array.from({ length: size }, () => new Array<boolean>(size).fill(false));
  const isFunction = Array.from({ length: size }, () => new Array<boolean>(size).fill(false));
  const set = (x: number, y: number, dark: boolean) => {
    modules[y]![x] = dark;
    isFunction[y]![x] = true;
  };

  drawFunctionPatterns(version, size, set);
  drawCodewords(codewords, size, modules, isFunction);

  let best = { mask: 0, penalty: Infinity, modules: modules };
  for (let mask = 0; mask < 8; mask++) {
    const candidate = modules.map((row) => row.slice());
    applyMask(candidate, isFunction, mask);
    drawFormatBits(ecc, mask, size, (x, y, dark) => (candidate[y]![x] = dark));
    const penalty = penaltyScore(candidate);
    if (penalty < best.penalty) best = { mask, penalty, modules: candidate };
  }
  return { version, size, ecc, mask: best.mask, modules: best.modules };
}

function addEccAndInterleave(data: number[], version: number, ecc: Ecc): number[] {
  const numBlocks = BLOCKS[ecc][version]!;
  const eccLen = ECC_PER_BLOCK[ecc][version]!;
  const rawCodewords = Math.floor(rawDataModules(version) / 8);
  const numShort = numBlocks - (rawCodewords % numBlocks);
  const shortLen = Math.floor(rawCodewords / numBlocks);
  const divisor = rsDivisor(eccLen);
  const blocks: number[][] = [];
  for (let i = 0, k = 0; i < numBlocks; i++) {
    const dat = data.slice(k, k + shortLen - eccLen + (i < numShort ? 0 : 1));
    k += dat.length;
    const ecc_ = rsRemainder(dat, divisor);
    if (i < numShort) dat.push(0);
    blocks.push(dat.concat(ecc_));
  }
  const result: number[] = [];
  for (let i = 0; i < blocks[0]!.length; i++) {
    blocks.forEach((block, j) => {
      if (i !== shortLen - eccLen || j >= numShort) result.push(block[i]!);
    });
  }
  return result;
}

export function alignmentPositions(version: number): number[] {
  if (version === 1) return [];
  const count = Math.floor(version / 7) + 2;
  const size = version * 4 + 17;
  const step = Math.ceil((version * 4 + 4) / (count * 2 - 2)) * 2;
  const result = [6];
  for (let pos = size - 7; result.length < count; pos -= step) result.splice(1, 0, pos);
  return result;
}

function drawFunctionPatterns(version: number, size: number, set: (x: number, y: number, dark: boolean) => void) {
  for (let i = 0; i < size; i++) {
    set(6, i, i % 2 === 0);
    set(i, 6, i % 2 === 0);
  }
  const finder = (cx: number, cy: number) => {
    for (let dy = -4; dy <= 4; dy++) {
      for (let dx = -4; dx <= 4; dx++) {
        const x = cx + dx;
        const y = cy + dy;
        const dist = Math.max(Math.abs(dx), Math.abs(dy));
        if (x >= 0 && x < size && y >= 0 && y < size) set(x, y, dist !== 2 && dist !== 4);
      }
    }
  };
  finder(3, 3);
  finder(size - 4, 3);
  finder(3, size - 4);

  const positions = alignmentPositions(version);
  const last = positions.length - 1;
  positions.forEach((px, i) =>
    positions.forEach((py, j) => {
      if ((i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0)) return;
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) set(px + dx, py + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }),
  );

  // Reserve the format areas now (real bits come per mask); the dark module is fixed.
  drawFormatBits('M', 0, size, set);

  if (version >= 7) {
    let rem = version;
    for (let i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >>> 11) * 0x1f25);
    const bits = (version << 12) | rem;
    for (let i = 0; i < 18; i++) {
      const dark = ((bits >>> i) & 1) !== 0;
      const a = size - 11 + (i % 3);
      const b = Math.floor(i / 3);
      set(a, b, dark);
      set(b, a, dark);
    }
  }
}

export function formatBits(ecc: Ecc, mask: number): number {
  const data = (ECC_FORMAT_BITS[ecc] << 3) | mask;
  let rem = data;
  for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
  return ((data << 10) | rem) ^ 0x5412;
}

function drawFormatBits(ecc: Ecc, mask: number, size: number, set: (x: number, y: number, dark: boolean) => void) {
  const bits = formatBits(ecc, mask);
  const bit = (i: number) => ((bits >>> i) & 1) !== 0;
  for (let i = 0; i <= 5; i++) set(8, i, bit(i));
  set(8, 7, bit(6));
  set(8, 8, bit(7));
  set(7, 8, bit(8));
  for (let i = 9; i < 15; i++) set(14 - i, 8, bit(i));
  for (let i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
  for (let i = 8; i < 15; i++) set(8, size - 15 + i, bit(i));
  set(8, size - 8, true);
}

function drawCodewords(data: number[], size: number, modules: boolean[][], isFunction: boolean[][]) {
  let i = 0;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    for (let vert = 0; vert < size; vert++) {
      for (let j = 0; j < 2; j++) {
        const x = right - j;
        const upward = ((right + 1) & 2) === 0;
        const y = upward ? size - 1 - vert : vert;
        if (!isFunction[y]![x] && i < data.length * 8) {
          modules[y]![x] = ((data[i >>> 3]! >>> (7 - (i & 7))) & 1) !== 0;
          i++;
        }
      }
    }
  }
}

export function maskBit(mask: number, x: number, y: number): boolean {
  switch (mask) {
    case 0:
      return (x + y) % 2 === 0;
    case 1:
      return y % 2 === 0;
    case 2:
      return x % 3 === 0;
    case 3:
      return (x + y) % 3 === 0;
    case 4:
      return (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0;
    case 5:
      return ((x * y) % 2) + ((x * y) % 3) === 0;
    case 6:
      return (((x * y) % 2) + ((x * y) % 3)) % 2 === 0;
    default:
      return (((x + y) % 2) + ((x * y) % 3)) % 2 === 0;
  }
}

function applyMask(modules: boolean[][], isFunction: boolean[][], mask: number) {
  for (let y = 0; y < modules.length; y++) {
    for (let x = 0; x < modules.length; x++) {
      if (!isFunction[y]![x] && maskBit(mask, x, y)) modules[y]![x] = !modules[y]![x];
    }
  }
}

/** Runs of five or more, 2×2 blocks, finder-like 1:1:3:1:1 runs and dark/light balance (the four rules). */
export function penaltyScore(modules: boolean[][]): number {
  const size = modules.length;
  let score = 0;
  const lines: boolean[][] = [];
  for (let i = 0; i < size; i++) {
    lines.push(modules[i]!);
    lines.push(modules.map((row) => row[i]!));
  }
  for (const line of lines) {
    let run = 1;
    for (let i = 1; i <= size; i++) {
      if (i < size && line[i] === line[i - 1]) run++;
      else {
        if (run >= 5) score += 3 + (run - 5);
        run = 1;
      }
    }
    const text = line.map((d) => (d ? '1' : '0')).join('');
    for (const pattern of ['10111010000', '00001011101']) {
      for (let at = text.indexOf(pattern); at >= 0; at = text.indexOf(pattern, at + 1)) score += 40;
    }
  }
  for (let y = 0; y < size - 1; y++) {
    for (let x = 0; x < size - 1; x++) {
      const c = modules[y]![x];
      if (c === modules[y]![x + 1] && c === modules[y + 1]![x] && c === modules[y + 1]![x + 1]) score += 3;
    }
  }
  const dark = modules.reduce((sum, row) => sum + row.filter(Boolean).length, 0);
  const total = size * size;
  score += Math.floor(Math.abs(dark * 20 - total * 10) / total) * 10;
  return score;
}

/** An SVG path of the dark modules, with a four-module quiet zone; viewBox "0 0 size+8 size+8". */
export function qrPath(code: QrCode): string {
  let d = '';
  code.modules.forEach((row, y) =>
    row.forEach((dark, x) => {
      if (dark) d += `M${x + 4} ${y + 4}h1v1h-1z`;
    }),
  );
  return d;
}
