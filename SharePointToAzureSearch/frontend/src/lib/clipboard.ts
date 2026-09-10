/**
 * Copies text to the clipboard, reporting whether it worked.
 *
 * The async clipboard API needs a secure context and a focused document, and this tool is quite
 * likely to be served over plain HTTP on a LAN address, so a rejection is a real case rather than a
 * theoretical one. The legacy selection-based path covers it; a caller that shows a "Copied"
 * confirmation should only show it when this returns true.
 */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text)
    return true
  } catch {
    // Falls through to the legacy path below.
  }

  try {
    const area = document.createElement('textarea')
    area.value = text
    area.setAttribute('readonly', '')
    area.style.position = 'fixed'
    area.style.top = '0'
    area.style.opacity = '0'
    document.body.appendChild(area)
    area.select()
    const copied = document.execCommand('copy')
    document.body.removeChild(area)
    return copied
  } catch {
    return false
  }
}
