const MAX_ENTRIES = 4000
const MAX_ENTRY_BYTES = 32 * 1024 * 1024
const MAX_TOTAL_BYTES = 256 * 1024 * 1024

/**
 * Office CLI can write a UTF-8 BOM before OOXML relationship files. The PPTX renderer treats those
 * entries as malformed XML, so remove the marker from a preview copy of the archive. The original
 * downloaded Blob is kept for the user's Download button.
 */
export async function normalizePptxXml(bytes: ArrayBuffer, signal: AbortSignal): Promise<ArrayBuffer> {
  const { default: JSZip } = await import('jszip')
  const zip = await JSZip.loadAsync(bytes)
  const entries = Object.values(zip.files).filter((entry) => !entry.dir)
  if (entries.length > MAX_ENTRIES) throw new Error('Presentation contains too many archive entries.')

  let declaredTotal = 0
  for (const entry of entries) {
    // JSZip keeps the ZIP directory's uncompressed sizes here. Check them before inflating any XML;
    // the renderer applies the same limits when it parses the normalized archive.
    const declaredSize = (entry as unknown as { _data?: { uncompressedSize?: number } })._data?.uncompressedSize
    if (declaredSize !== undefined) {
      if (declaredSize > MAX_ENTRY_BYTES) throw new Error('Presentation contains an oversized archive entry.')
      declaredTotal += declaredSize
      if (declaredTotal > MAX_TOTAL_BYTES) throw new Error('Presentation expands beyond the preview limit.')
    }
  }

  let changed = false
  let decodedTotal = 0
  for (const entry of entries) {
    if (signal.aborted) throw new DOMException('Preview cancelled.', 'AbortError')
    if (entry.name !== '[Content_Types].xml' && !/\.(xml|rels)$/i.test(entry.name)) continue

    const data = await entry.async('uint8array')
    if (data.length > MAX_ENTRY_BYTES) throw new Error('Presentation contains an oversized XML entry.')
    decodedTotal += data.length
    if (decodedTotal > MAX_TOTAL_BYTES) throw new Error('Presentation XML expands beyond the preview limit.')

    if (data[0] === 0xef && data[1] === 0xbb && data[2] === 0xbf) {
      zip.file(entry.name, data.subarray(3))
      changed = true
    }
  }

  if (!changed) return bytes
  if (signal.aborted) throw new DOMException('Preview cancelled.', 'AbortError')
  return zip.generateAsync({ type: 'arraybuffer', compression: 'DEFLATE', compressionOptions: { level: 6 } })
}
