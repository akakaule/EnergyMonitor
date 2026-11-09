// Generates the PWA icons as PNG files without any image dependency: a rounded blue tile with a
// white lightning bolt. Run with `npm run icons`; the output is committed.
import { deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';

const BOLT = [[0.56, 0.14], [0.27, 0.56], [0.47, 0.56], [0.41, 0.86], [0.73, 0.42], [0.52, 0.42]];

function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function inPolygon(x, y, poly) {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [xi, yi] = poly[i], [xj, yj] = poly[j];
    if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

function png(size, { radius, scale }) {
  const raw = Buffer.alloc(size * (size * 4 + 1));
  const r = radius * size;
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0;
    for (let x = 0; x < size; x++) {
      const o = y * (size * 4 + 1) + 1 + x * 4;
      const cx = Math.min(x, size - 1 - x), cy = Math.min(y, size - 1 - y);
      const corner = cx < r && cy < r && Math.hypot(r - cx, r - cy) > r;
      // The bolt is scaled around the centre, so a maskable icon keeps it inside the safe zone.
      const u = (x / size - 0.5) / scale + 0.5, v = (y / size - 0.5) / scale + 0.5;
      const bolt = inPolygon(u, v, BOLT);
      const [cr, cg, cb] = bolt ? [255, 255, 255] : [24, 79, 149];
      raw[o] = cr; raw[o + 1] = cg; raw[o + 2] = cb; raw[o + 3] = corner ? 0 : 255;
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0)),
  ]);
}

mkdirSync('public/icons', { recursive: true });
writeFileSync('public/icons/icon-192.png', png(192, { radius: 0.2, scale: 1 }));
writeFileSync('public/icons/icon-512.png', png(512, { radius: 0.2, scale: 1 }));
writeFileSync('public/icons/icon-maskable-512.png', png(512, { radius: 0, scale: 0.7 }));
writeFileSync('public/icons/apple-touch-icon.png', png(180, { radius: 0, scale: 0.85 }));
writeFileSync(
  'public/icons/favicon.svg',
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect width="100" height="100" rx="20" fill="#184f95"/><polygon fill="#fff" points="${BOLT.map(([x, y]) => `${x * 100},${y * 100}`).join(' ')}"/></svg>\n`,
);
console.log('icons written');
