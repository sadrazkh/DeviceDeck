// H.264 Annex B helpers for the mirror window: the codec string WebCodecs wants, and the order packets must reach
// the decoder in (configuration with the next key frame, nothing until a key frame after a reset).

import type { MirrorPacketKind } from '../../../bridge/toolbox';

/** NAL units of an Annex B buffer (start codes 00 00 01 or 00 00 00 01), without the start codes. */
export function splitNalUnits(data: Uint8Array): Uint8Array[] {
  const units: Uint8Array[] = [];
  let start = -1;
  let i = 0;
  while (i + 2 < data.length) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1) {
      if (start >= 0) units.push(trimZeros(data.subarray(start, i)));
      start = i + 3;
      i += 3;
    } else {
      i++;
    }
  }
  if (start >= 0 && start < data.length) units.push(data.subarray(start));
  return units.filter((u) => u.length > 0);
}

/** Drops the trailing zero of a four-byte start code that belongs to the next unit. */
function trimZeros(unit: Uint8Array): Uint8Array {
  let end = unit.length;
  while (end > 0 && unit[end - 1] === 0) end--;
  return unit.subarray(0, end);
}

const hex = (n: number) => n.toString(16).padStart(2, '0').toUpperCase();

/** "avc1.640028" from the SPS in a configuration packet (profile, constraint flags, level); null without an SPS. */
export function codecFromConfig(config: Uint8Array): string | null {
  for (const unit of splitNalUnits(config)) {
    if ((unit[0]! & 0x1f) === 7 && unit.length >= 4) return `avc1.${hex(unit[1]!)}${hex(unit[2]!)}${hex(unit[3]!)}`;
  }
  return null;
}

export interface PacketIn {
  kind: MirrorPacketKind;
  pts: number;
  key: boolean;
  width: number;
  height: number;
  data: Uint8Array;
}

export type AssemblerOutput =
  | { type: 'session'; width: number; height: number }
  | { type: 'configure'; codec: string; chunk: { key: true; timestamp: number; data: Uint8Array } }
  | { type: 'chunk'; chunk: { key: boolean; timestamp: number; data: Uint8Array } };

/**
 * Turns the stream into decoder calls. A configuration packet is held and sent in front of the next key frame
 * together with a (re)configure; delta frames are dropped while waiting for a key frame (start, rotation, or
 * after <see cref="skipToKeyFrame"/> when the decoder fell behind).
 */
export class FrameAssembler {
  private config: Uint8Array | null = null;
  private codec: string | null = null;
  private needConfigure = true;
  private waitForKey = true;

  /** Drop frames until the next key frame (the page couldn't keep up). */
  skipToKeyFrame(): void {
    this.waitForKey = true;
  }

  /** The decoder broke: configure again on the next key frame. */
  reset(): void {
    this.needConfigure = true;
    this.waitForKey = true;
  }

  push(packet: PacketIn): AssemblerOutput | null {
    switch (packet.kind) {
      case 'session':
        this.reset();
        return { type: 'session', width: packet.width, height: packet.height };
      case 'config':
        this.config = packet.data;
        this.codec = codecFromConfig(packet.data) ?? this.codec;
        this.needConfigure = true;
        this.waitForKey = true;
        return null;
      case 'frame': {
        if (this.waitForKey && !packet.key) return null;
        if (packet.key && (this.needConfigure || this.waitForKey)) {
          if (!this.codec) return null;
          this.waitForKey = false;
          const data = this.config ? concat(this.config, packet.data) : packet.data;
          if (this.needConfigure) {
            this.needConfigure = false;
            return { type: 'configure', codec: this.codec, chunk: { key: true, timestamp: packet.pts, data } };
          }
          return { type: 'chunk', chunk: { key: true, timestamp: packet.pts, data } };
        }
        return { type: 'chunk', chunk: { key: packet.key, timestamp: packet.pts, data: packet.data } };
      }
    }
  }
}

function concat(a: Uint8Array, b: Uint8Array): Uint8Array {
  const out = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}

const BASE64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
const LOOKUP = (() => {
  const table = new Uint8Array(256);
  for (let i = 0; i < BASE64.length; i++) table[BASE64.charCodeAt(i)] = i;
  return table;
})();

/** Base64 to bytes without building an intermediate string (video packets can be a few hundred KB). */
export function fromBase64(text: string): Uint8Array {
  let length = text.length;
  while (length > 0 && text.charCodeAt(length - 1) === 61 /* = */) length--;
  const out = new Uint8Array(Math.floor((length * 3) / 4));
  let o = 0;
  let i = 0;
  for (; i + 3 < length; i += 4) {
    const n =
      (LOOKUP[text.charCodeAt(i)]! << 18) |
      (LOOKUP[text.charCodeAt(i + 1)]! << 12) |
      (LOOKUP[text.charCodeAt(i + 2)]! << 6) |
      LOOKUP[text.charCodeAt(i + 3)]!;
    out[o++] = n >> 16;
    out[o++] = (n >> 8) & 0xff;
    out[o++] = n & 0xff;
  }
  const rest = length - i;
  if (rest >= 2) {
    const n = (LOOKUP[text.charCodeAt(i)]! << 18) | (LOOKUP[text.charCodeAt(i + 1)]! << 12) | (rest === 3 ? LOOKUP[text.charCodeAt(i + 2)]! << 6 : 0);
    out[o++] = n >> 16;
    if (rest === 3) out[o++] = (n >> 8) & 0xff;
  }
  return out;
}
