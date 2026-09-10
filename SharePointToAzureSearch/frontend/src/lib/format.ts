const NUMBER = new Intl.NumberFormat()

const DATE_TIME = new Intl.DateTimeFormat(undefined, {
  dateStyle: 'medium',
  timeStyle: 'short',
})

const RELATIVE = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })

const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600_000],
  ['month', 30 * 24 * 3600_000],
  ['day', 24 * 3600_000],
  ['hour', 3600_000],
  ['minute', 60_000],
  ['second', 1000],
]

export function formatNumber(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : NUMBER.format(value)
}

export function formatBytes(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—'
  if (value < 1024) return `${value} B`

  const units = ['KB', 'MB', 'GB', 'TB']
  let size = value / 1024
  let unit = 0
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024
    unit += 1
  }
  return `${size.toFixed(size >= 100 || unit === 0 ? 0 : 1)} ${units[unit]}`
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : DATE_TIME.format(date)
}

/** "3 hours ago" for a timestamp, so how stale a checkpoint is reads at a glance. */
export function formatRelative(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '—'

  const elapsed = date.getTime() - Date.now()
  for (const [unit, milliseconds] of UNITS) {
    if (Math.abs(elapsed) >= milliseconds) {
      return RELATIVE.format(Math.round(elapsed / milliseconds), unit)
    }
  }
  return 'just now'
}

const TIME_OF_DAY = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })

/**
 * The clock time a message was sent, which is what a reader scans a conversation for. Older than
 * today it carries the date as well, so a thread spanning days stays unambiguous.
 */
export function formatMessageTime(value: string | null | undefined): string {
  if (!value) return ''
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return ''

  const time = TIME_OF_DAY.format(date)
  return date.toDateString() === new Date().toDateString()
    ? time
    : `${date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}, ${time}`
}

export function formatDuration(milliseconds: number): string {
  return milliseconds < 1000
    ? `${Math.round(milliseconds)} ms`
    : `${(milliseconds / 1000).toFixed(2)} s`
}

export function formatScore(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : value.toFixed(4)
}

/** The short form of an identifier, for a column too narrow for the whole thing. */
export function shorten(value: string | null | undefined, head = 10, tail = 6): string {
  if (!value) return '—'
  return value.length <= head + tail + 1 ? value : `${value.slice(0, head)}…${value.slice(-tail)}`
}

export function fileExtension(name: string): string | null {
  const dot = name.lastIndexOf('.')
  return dot > 0 && dot < name.length - 1 ? name.slice(dot + 1).toLowerCase() : null
}

/**
 * Microsoft Graph reports a parent as `/drives/{driveId}/root:` plus the folder path, and the drive ID
 * alone is ~70 characters — long enough to push every other column out of a table. The drive is already
 * its own column, so only the part below the library root is worth showing.
 */
export function folderLabel(parentPath: string | null | undefined): string {
  if (!parentPath) return '—'

  const root = parentPath.indexOf('/root:')
  const relative = root >= 0 ? parentPath.slice(root + '/root:'.length) : parentPath
  return relative === '' ? '/' : relative
}

const MIME_LABELS: Record<string, string> = {
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'Word document',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'Excel workbook',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation': 'PowerPoint deck',
  'application/pdf': 'PDF',
  'application/msword': 'Word document (legacy)',
  'application/vnd.ms-excel': 'Excel workbook (legacy)',
  'application/vnd.ms-powerpoint': 'PowerPoint deck (legacy)',
  'text/plain': 'Plain text',
  'text/markdown': 'Markdown',
  'text/html': 'HTML',
  'text/csv': 'CSV',
}

/**
 * A label short enough for a table cell. The OpenXML types are ~70 characters and differ only at the
 * end, so truncating them makes Word, Excel, and PowerPoint rows indistinguishable; the full type is
 * still worth showing in a tooltip.
 */
export function mimeTypeLabel(mimeType: string | null | undefined): string {
  if (!mimeType) return '(none recorded)'
  const known = MIME_LABELS[mimeType.toLowerCase()]
  if (known) return known

  const subtype = mimeType.slice(mimeType.indexOf('/') + 1)
  return subtype.length < mimeType.length && subtype.length <= 28 ? subtype : mimeType
}
