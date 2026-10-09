import { writeFileSync, mkdirSync } from 'node:fs';
import { deflateSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';

const directory = fileURLToPath(new URL('../StevanPlayer.App/Assets/', import.meta.url));
mkdirSync(directory, { recursive: true });
const crcTable = Array.from({ length: 256 }, (_, n) => {
  let value = n;
  for (let k = 0; k < 8; k++) value = value & 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1;
  return value >>> 0;
});
function crc32(data) {
  let crc = 0xffffffff;
  for (const b of data) crc = crcTable[(crc ^ b) & 255] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const name = Buffer.from(type), result = Buffer.alloc(data.length + 12);
  result.writeUInt32BE(data.length); name.copy(result, 4); data.copy(result, 8);
  result.writeUInt32BE(crc32(Buffer.concat([name, data])), data.length + 8);
  return result;
}
function image(size) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const u = (x + .5) / size, v = (y + .5) / size;
    const rx = Math.max(.18 - u, 0, u - .82), ry = Math.max(.18 - v, 0, v - .82);
    const inside = rx * rx + ry * ry < .18 * .18;
    let rgba = [Math.round(19 + 16 * v), Math.round(71 + 33 * u), Math.round(116 + 56 * (1 - v)), inside ? 255 : 0];
    if (inside && u >= .34 && u <= .72 && Math.abs(v - .43) <= (.72 - u) * .62) rgba = [248, 251, 255, 255];
    if (inside && u > .24 && u < .76 && ((v > .72 && v < .765) || (v > .815 && v < .86))) rgba = [255, 183, 67, 255];
    for (let c = 0; c < 4; c++) raw[y * (size * 4 + 1) + 1 + x * 4 + c] = rgba[c];
  }
  const header = Buffer.alloc(13); header.writeUInt32BE(size); header.writeUInt32BE(size, 4); header[8] = 8; header[9] = 6;
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]), chunk('IHDR', header), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}
const sizes = [16, 32, 48, 256], images = sizes.map(image);
const header = Buffer.alloc(6 + sizes.length * 16); header.writeUInt16LE(1, 2); header.writeUInt16LE(sizes.length, 4);
let offset = header.length;
for (let i = 0; i < sizes.length; i++) {
  const pos = 6 + i * 16; header[pos] = sizes[i] % 256; header[pos + 1] = sizes[i] % 256;
  header.writeUInt16LE(1, pos + 4); header.writeUInt16LE(32, pos + 6);
  header.writeUInt32LE(images[i].length, pos + 8); header.writeUInt32LE(offset, pos + 12); offset += images[i].length;
}
writeFileSync(directory + 'stevan.ico', Buffer.concat([header, ...images]));
writeFileSync(directory + 'stevan.png', images.at(-1));
writeFileSync(directory + 'stevan.svg', '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><defs><linearGradient id="b" x2="1" y2="1"><stop stop-color="#1347ac"/><stop offset="1" stop-color="#236874"/></linearGradient></defs><rect width="256" height="256" rx="46" fill="url(#b)"/><path d="M87 52L184 110L87 168Z" fill="#f8fbff"/><path d="M62 190H194M62 215H194" stroke="#ffb743" stroke-width="12"/></svg>\n');
console.log('Generated original STEVANplejer multi-resolution Windows icon.');
