// Locally generated placeholder images for the mock backend (SVG data URLs, never network images).

import type { MockAsset } from './photos';
import { createRandom, hashString } from './random';

const toDataUrl = (svg: string) => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;

const hsl = (h: number, s: number, l: number) => `hsl(${Math.round(h)} ${Math.round(s)}% ${Math.round(l)}%)`;

/** A soft photographic-looking scene: sky, sun or moon, two hills, sometimes a city or a figure. */
function sceneSvg(asset: Pick<MockAsset, 'seed' | 'kind' | 'width' | 'height'>, detail: boolean): string {
  const rnd = createRandom(asset.seed);
  const w = 100;
  const ratio = asset.width && asset.height ? asset.height / asset.width : 0.75;
  const h = Math.round(100 * ratio);
  const hue = rnd.between(0, 360);
  const night = asset.kind === 'video' ? rnd.chance(0.5) : rnd.chance(0.18);
  const skyTop = night ? hsl(hue + 200, 45, 14) : hsl(hue, 55, 78);
  const skyBottom = night ? hsl(hue + 230, 40, 30) : hsl(hue + 25, 65, 88);
  const hillA = night ? hsl(hue + 150, 25, 18) : hsl(hue + 110, 32, 52);
  const hillB = night ? hsl(hue + 170, 22, 12) : hsl(hue + 130, 36, 38);
  const sunX = rnd.between(15, 85);
  const sunY = rnd.between(h * 0.15, h * 0.45);
  const sunR = rnd.between(5, 11);
  const sun = night ? hsl(50, 30, 90) : hsl(hue + 40, 95, 92);
  const y1 = h * rnd.between(0.55, 0.72);
  const y2 = h * rnd.between(0.7, 0.86);
  const variant = rnd.int(0, 3);
  const parts: string[] = [
    `<defs><linearGradient id="s" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${skyTop}"/><stop offset="1" stop-color="${skyBottom}"/></linearGradient></defs>`,
    `<rect width="${w}" height="${h}" fill="url(#s)"/>`,
    `<circle cx="${sunX.toFixed(1)}" cy="${sunY.toFixed(1)}" r="${sunR.toFixed(1)}" fill="${sun}" opacity="0.92"/>`,
  ];
  if (variant === 0) {
    // city skyline
    let x = 0;
    while (x < w) {
      const bw = rnd.between(6, 14);
      const bh = rnd.between(h * 0.12, h * 0.42);
      parts.push(`<rect x="${x.toFixed(1)}" y="${(y2 - bh).toFixed(1)}" width="${bw.toFixed(1)}" height="${(h - y2 + bh).toFixed(1)}" fill="${hillB}" opacity="0.9"/>`);
      x += bw + rnd.between(0.5, 2);
    }
  } else {
    parts.push(
      `<path d="M0 ${y1.toFixed(1)} Q ${rnd.between(20, 45).toFixed(1)} ${(y1 - h * 0.18).toFixed(1)} ${rnd.between(55, 70).toFixed(1)} ${(y1 + 2).toFixed(1)} T ${w} ${(y1 - 4).toFixed(1)} V ${h} H 0 Z" fill="${hillA}"/>`,
    );
  }
  parts.push(
    `<path d="M0 ${y2.toFixed(1)} Q ${rnd.between(30, 60).toFixed(1)} ${(y2 - h * 0.12).toFixed(1)} ${w} ${(y2 + 3).toFixed(1)} V ${h} H 0 Z" fill="${hillB}"/>`,
  );
  if (variant === 2) {
    const fx = rnd.between(25, 75);
    parts.push(
      `<circle cx="${fx.toFixed(1)}" cy="${(y2 - 15).toFixed(1)}" r="3.4" fill="${hsl(hue + 200, 25, 20)}"/>`,
      `<rect x="${(fx - 3.5).toFixed(1)}" y="${(y2 - 11).toFixed(1)}" width="7" height="12" rx="3" fill="${hsl(hue + 200, 25, 20)}"/>`,
    );
  }
  if (detail) {
    for (let i = 0; i < 5; i++) {
      parts.push(
        `<ellipse cx="${rnd.between(0, w).toFixed(1)}" cy="${rnd.between(4, h * 0.35).toFixed(1)}" rx="${rnd.between(6, 16).toFixed(1)}" ry="${rnd.between(1.5, 3).toFixed(1)}" fill="#fff" opacity="${night ? 0.08 : 0.35}"/>`,
      );
    }
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" preserveAspectRatio="xMidYMid slice">${parts.join('')}</svg>`;
}

/** A phone screenshot: wallpaper, status bar and a few cards. */
function screenshotSvg(seed: number): string {
  const rnd = createRandom(seed);
  const hue = rnd.between(0, 360);
  const parts = [
    `<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${hsl(hue, 50, 92)}"/><stop offset="1" stop-color="${hsl(hue + 40, 45, 84)}"/></linearGradient></defs>`,
    `<rect width="46" height="100" fill="url(#g)"/>`,
    `<rect x="3" y="2.6" width="8" height="2.2" rx="1" fill="${hsl(hue, 20, 25)}"/>`,
    `<rect x="35" y="2.6" width="8" height="2.2" rx="1" fill="${hsl(hue, 20, 25)}"/>`,
  ];
  let y = 10;
  while (y < 92) {
    const ch = rnd.between(8, 22);
    parts.push(`<rect x="3" y="${y.toFixed(1)}" width="40" height="${ch.toFixed(1)}" rx="2.5" fill="#fff" opacity="0.88"/>`);
    parts.push(`<rect x="6" y="${(y + 3).toFixed(1)}" width="${rnd.between(12, 30).toFixed(1)}" height="2" rx="1" fill="${hsl(hue, 25, 40)}" opacity="0.7"/>`);
    y += ch + 3;
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 46 100" preserveAspectRatio="xMidYMid slice">${parts.join('')}</svg>`;
}

const thumbCache = new Map<string, string>();

export function thumbnailUrl(asset: MockAsset): string {
  let url = thumbCache.get(asset.id);
  if (!url) {
    url = toDataUrl(asset.kind === 'screenshot' ? screenshotSvg(asset.seed) : sceneSvg(asset, false));
    if (thumbCache.size > 4000) thumbCache.clear();
    thumbCache.set(asset.id, url);
  }
  return url;
}

export function photoUrl(asset: MockAsset): string {
  return toDataUrl(asset.kind === 'screenshot' ? screenshotSvg(asset.seed) : sceneSvg(asset, true));
}

/** A letter tile in two colours: a stand-in for the app's real icon, never a copy of a logo. */
export function appIconUrl(appId: string, name: string, colours?: [string, string]): string {
  const [a, b] = colours ?? [hsl(hashString(appId) % 360, 60, 60), hsl((hashString(appId) + 40) % 360, 60, 42)];
  const letter = (name.trim()[0] ?? '?').toUpperCase().replace(/[<&>]/g, '');
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">` +
    `<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${a}"/><stop offset="1" stop-color="${b}"/></linearGradient></defs>` +
    `<rect width="64" height="64" rx="15" fill="url(#g)"/>` +
    `<text x="32" y="42" text-anchor="middle" font-family="Segoe UI, sans-serif" font-size="28" font-weight="600" fill="#fff">${letter}</text>` +
    `</svg>`;
  return toDataUrl(svg);
}

/** What tools.screenshot returns in the mock: a home screen in a phone-shaped frame. */
export function phoneScreenshotUrl(seed: number, platform: 'ios' | 'android'): string {
  const rnd = createRandom(seed);
  const hue = rnd.between(180, 300);
  const icons: string[] = [];
  for (let row = 0; row < 6; row++) {
    for (let col = 0; col < 4; col++) {
      const x = 34 + col * 92;
      const y = 150 + row * 110;
      icons.push(`<rect x="${x}" y="${y}" width="64" height="64" rx="${platform === 'ios' ? 15 : 32}" fill="${hsl(hue + rnd.between(-60, 60), 60, rnd.between(45, 70))}"/>`);
    }
  }
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 402 874">` +
    `<defs><linearGradient id="w" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${hsl(hue, 55, 35)}"/><stop offset="1" stop-color="${hsl(hue + 50, 60, 18)}"/></linearGradient></defs>` +
    `<rect width="402" height="874" fill="url(#w)"/>` +
    `<text x="40" y="38" font-family="Segoe UI, sans-serif" font-size="17" font-weight="600" fill="#fff">9:41</text>` +
    icons.join('') +
    `<rect x="24" y="770" width="354" height="84" rx="30" fill="#fff" opacity="0.18"/>` +
    `</svg>`;
  return toDataUrl(svg);
}
