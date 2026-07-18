/**
 * Generates the Edgewise placeholder brand assets into public/:
 *   favicon.svg, pwa-192x192.png, pwa-512x512.png, pwa-maskable-512x512.png
 *
 * The mark: a minimal geometric "E" (three signal bars) in signal green on a
 * deep-slate tile — matching src/components/brand/LogoMark.tsx.
 *
 * PNGs are written with a tiny hand-rolled encoder (node:zlib only) so the
 * repo needs no image tooling. Re-run with: node scripts/generate-icons.mjs
 */
import { deflateSync } from 'node:zlib'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const outDir = join(dirname(fileURLToPath(import.meta.url)), '..', 'public')
mkdirSync(outDir, { recursive: true })

/* Brand colors (see src/styles/tokens.css / LogoMark.tsx). */
const BG = [0x0e, 0x14, 0x20, 0xff] // deep slate
const GREEN = [0x2f, 0xd9, 0xa2, 0xff] // signal green
const GREEN_DIM = [0x2f, 0xd9, 0xa2, 0xd9]

/* ------------------------------ PNG encoding ----------------------------- */

const CRC_TABLE = new Int32Array(256).map((_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c
})

function crc32(bytes) {
  let c = 0xffffffff
  for (const b of bytes) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}

function chunk(type, data) {
  const typeBytes = Buffer.from(type, 'ascii')
  const len = Buffer.alloc(4)
  len.writeUInt32BE(data.length)
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(Buffer.concat([typeBytes, data])))
  return Buffer.concat([len, typeBytes, data, crc])
}

function encodePng(width, height, rgba) {
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(width, 0)
  ihdr.writeUInt32BE(height, 4)
  ihdr[8] = 8 // bit depth
  ihdr[9] = 6 // color type: RGBA
  // scanlines, each prefixed with filter byte 0
  const raw = Buffer.alloc(height * (1 + width * 4))
  for (let y = 0; y < height; y++) {
    const rowStart = y * (1 + width * 4)
    raw[rowStart] = 0
    rgba.copy(raw, rowStart + 1, y * width * 4, (y + 1) * width * 4)
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

/* ------------------------------- drawing --------------------------------- */

/** The mark in a 64×64 design space: [x, y, w, h, color]. */
const MARK_RECTS = [
  [16, 16, 6, 32, GREEN], // spine
  [26, 16, 22, 6, GREEN], // top bar
  [26, 29, 16, 6, GREEN_DIM], // middle bar (shorter — the "signal" cue)
  [26, 42, 22, 6, GREEN], // bottom bar
]

/**
 * Render the icon.
 * contentScale: how much of the tile the 64-space mark occupies (maskable
 * icons need the mark inside the central safe zone).
 * cornerRadius: rounded tile corners (0 = full-bleed square for maskable).
 */
function drawIcon(size, { contentScale, cornerRadius }) {
  const rgba = Buffer.alloc(size * size * 4)
  const r = cornerRadius

  const insideTile = (x, y) => {
    if (r <= 0) return true
    const cx = x < r ? r : x > size - r ? size - r : x
    const cy = y < r ? r : y > size - r ? size - r : y
    if (cx === x || cy === y) return true
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r
  }

  const scale = (size / 64) * contentScale
  const offset = (size - 64 * scale) / 2

  const markColorAt = (x, y) => {
    const dx = (x - offset) / scale
    const dy = (y - offset) / scale
    for (const [rx, ry, rw, rh, color] of MARK_RECTS) {
      if (dx >= rx && dx < rx + rw && dy >= ry && dy < ry + rh) return color
    }
    return null
  }

  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const i = (y * size + x) * 4
      const px = x + 0.5
      const py = y + 0.5
      const color = insideTile(px, py) ? (markColorAt(px, py) ?? BG) : [0, 0, 0, 0]
      rgba[i] = color[0]
      rgba[i + 1] = color[1]
      rgba[i + 2] = color[2]
      rgba[i + 3] = color[3]
    }
  }
  return encodePng(size, size, rgba)
}

/* -------------------------------- outputs -------------------------------- */

const faviconSvg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="14" fill="#0e1420"/>
  <rect x="16" y="16" width="6" height="32" rx="2" fill="#2fd9a2"/>
  <rect x="26" y="16" width="22" height="6" rx="2" fill="#2fd9a2"/>
  <rect x="26" y="29" width="16" height="6" rx="2" fill="#2fd9a2" opacity="0.85"/>
  <rect x="26" y="42" width="22" height="6" rx="2" fill="#2fd9a2"/>
</svg>
`

writeFileSync(join(outDir, 'favicon.svg'), faviconSvg)
writeFileSync(join(outDir, 'pwa-192x192.png'), drawIcon(192, { contentScale: 1, cornerRadius: 0 }))
writeFileSync(join(outDir, 'pwa-512x512.png'), drawIcon(512, { contentScale: 1, cornerRadius: 0 }))
// Maskable: full-bleed tile, mark shrunk into the central ~64% safe zone.
writeFileSync(
  join(outDir, 'pwa-maskable-512x512.png'),
  drawIcon(512, { contentScale: 0.64, cornerRadius: 0 }),
)

console.log(
  'Wrote favicon.svg, pwa-192x192.png, pwa-512x512.png, pwa-maskable-512x512.png →',
  outDir,
)
