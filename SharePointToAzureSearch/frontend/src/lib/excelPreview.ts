import type { CSSProperties } from 'react'
import type { Border, Cell, Color, Font, Workbook } from 'exceljs'

export type NumberFormatter = (format: string | number, value: unknown, options?: { date1904?: boolean }) => string

const DEFAULT_THEME = [
  '#ffffff', '#000000', '#e7e6e6', '#44546a', '#4472c4', '#ed7d31',
  '#a5a5a5', '#ffc000', '#5b9bd5', '#70ad47', '#0563c1', '#954f72',
]

export function themeColors(workbook: Workbook): string[] {
  const colors = [...DEFAULT_THEME]
  const xml = workbook.model.themes?.[0]
  if (!xml) return colors
  const document = new DOMParser().parseFromString(xml, 'application/xml')
  const scheme = Array.from(document.getElementsByTagName('*')).find((node) => node.localName === 'clrScheme')
  if (!scheme) return colors
  const names = ['lt1', 'dk1', 'lt2', 'dk2', 'accent1', 'accent2', 'accent3', 'accent4', 'accent5', 'accent6', 'hlink', 'folHlink']
  names.forEach((name, index) => {
    const entry = Array.from(scheme.children).find((node) => node.localName === name)
    const value = entry?.firstElementChild?.getAttribute('lastClr') ?? entry?.firstElementChild?.getAttribute('val')
    if (value && /^[0-9a-f]{6}$/i.test(value)) colors[index] = `#${value}`
  })
  return colors
}

function excelColor(color: Partial<Color> | undefined, theme: string[]): string | undefined {
  if (!color) return undefined
  const argb = color.argb
  let hex = argb && /^[0-9a-f]{8}$/i.test(argb) ? `#${argb.slice(2)}` : undefined
  if (!hex && argb && /^[0-9a-f]{6}$/i.test(argb)) hex = `#${argb}`
  if (!hex && Number.isInteger(color.theme)) hex = theme[color.theme!]
  if (!hex) return undefined
  const tint = (color as Partial<Color> & { tint?: number }).tint
  if (typeof tint !== 'number' || !Number.isFinite(tint) || tint === 0) return hex
  const amount = Math.max(-1, Math.min(1, tint))
  const channels = [1, 3, 5].map((start) => {
    const channel = parseInt(hex.slice(start, start + 2), 16)
    return Math.round(amount < 0 ? channel * (1 + amount) : channel * (1 - amount) + 255 * amount)
      .toString(16).padStart(2, '0')
  })
  return `#${channels.join('')}`
}

export function fontStyle(font: Partial<Font> | undefined, theme: string[]): CSSProperties {
  if (!font) return {}
  const decoration = [font.underline && font.underline !== 'none' ? 'underline' : '', font.strike ? 'line-through' : '']
    .filter(Boolean).join(' ')
  return {
    fontFamily: font.name,
    fontSize: typeof font.size === 'number' && font.size > 0 ? `${Math.min(font.size, 72)}pt` : undefined,
    fontWeight: font.bold ? 700 : undefined,
    fontStyle: font.italic ? 'italic' : undefined,
    textDecorationLine: decoration || undefined,
    color: excelColor(font.color, theme),
    verticalAlign: font.vertAlign === 'superscript' ? 'super' : font.vertAlign === 'subscript' ? 'sub' : undefined,
  }
}

function borderCss(border: Partial<Border> | undefined, theme: string[]): string | undefined {
  if (!border?.style) return undefined
  const width = border.style === 'thick' ? 3 : border.style.startsWith('medium') ? 2 : 1
  const style = border.style === 'double' ? 'double'
    : border.style === 'dotted' || border.style === 'hair' ? 'dotted'
      : border.style.toLowerCase().includes('dash') ? 'dashed' : 'solid'
  return `${width}px ${style} ${excelColor(border.color, theme) ?? '#64748b'}`
}

export function cellStyle(cell: Cell, theme: string[]): CSSProperties {
  const fill = cell.fill
  const alignment = cell.alignment
  const horizontal = alignment?.horizontal
  const vertical = alignment?.vertical
  const style: CSSProperties = {
    ...fontStyle(cell.font, theme),
    textAlign: horizontal === 'centerContinuous' ? 'center'
      : horizontal === 'left' || horizontal === 'center' || horizontal === 'right' || horizontal === 'justify' ? horizontal : undefined,
    verticalAlign: vertical === 'middle' ? 'middle' : vertical === 'top' || vertical === 'bottom' ? vertical : undefined,
    whiteSpace: alignment?.wrapText ? 'pre-wrap' : 'nowrap',
    overflowWrap: alignment?.wrapText ? 'anywhere' : undefined,
    borderTop: borderCss(cell.border?.top, theme),
    borderRight: borderCss(cell.border?.right, theme),
    borderBottom: borderCss(cell.border?.bottom, theme),
    borderLeft: borderCss(cell.border?.left, theme),
  }
  if (fill?.type === 'pattern' && fill.pattern === 'solid') {
    style.backgroundColor = excelColor(fill.fgColor, theme) ?? excelColor(fill.bgColor, theme)
  } else if (fill?.type === 'gradient' && fill.gradient === 'angle' && fill.stops?.length) {
    const stops = fill.stops.map((stop) => {
      const color = excelColor(stop.color, theme)
      return color && Number.isFinite(stop.position) ? `${color} ${Math.max(0, Math.min(100, stop.position * 100))}%` : null
    }).filter(Boolean)
    if (stops.length >= 2) style.backgroundImage = `linear-gradient(${fill.degree}deg, ${stops.join(', ')})`
  }
  if (alignment?.indent && Number.isFinite(alignment.indent)) style.paddingLeft = `${8 + Math.min(alignment.indent, 20) * 12}px`
  return style
}

export function columnWidthPx(width?: number): number {
  return width && Number.isFinite(width) ? Math.max(54, Math.min(600, Math.round(width * 7 + 12))) : 112
}

export function rowHeightPx(height?: number): number | undefined {
  return height && Number.isFinite(height) ? Math.max(24, Math.min(400, Math.round(height * 4 / 3))) : undefined
}

export function cellDisplayText(cell: Cell, formatNumber: NumberFormatter, date1904: boolean): string {
  let value: unknown = cell.value
  if (value && typeof value === 'object' && 'result' in value) value = value.result
  if (cell.numFmt && cell.numFmt !== 'General' && (typeof value === 'number' || value instanceof Date)) {
    const serial = value instanceof Date
      ? (value.getTime() - Date.UTC(1899, 11, 30)) / 86400000 - (date1904 ? 1462 : 0)
      : value
    try { return formatNumber(cell.numFmt, serial, { date1904 }) } catch { /* Some workbooks contain unsupported custom formats. */ }
  }
  if (value instanceof Date) return value.toLocaleDateString()
  return cell.text
}

interface VisibleMerge {
  masterRow: number
  masterColumn: number
  rowSpan: number
  colSpan: number
  anchor: boolean
}

function columnNumber(label: string): number {
  return [...label].reduce((number, char) => number * 26 + char.charCodeAt(0) - 64, 0)
}

export function visibleMerges(ranges: string[], rows: number[], columns: number[]): Map<string, VisibleMerge> {
  const cells = new Map<string, VisibleMerge>()
  if (!Array.isArray(ranges) || !rows.length || !columns.length) return cells
  for (const range of ranges) {
    const match = /^([A-Z]+)(\d+):([A-Z]+)(\d+)$/i.exec(range)
    if (!match) continue
    const top = Number(match[2]); const bottom = Number(match[4])
    const left = columnNumber(match[1].toUpperCase()); const right = columnNumber(match[3].toUpperCase())
    const coveredRows = rows.filter((row) => row >= top && row <= bottom)
    const coveredColumns = columns.filter((column) => column >= left && column <= right)
    if (!coveredRows.length || !coveredColumns.length) continue
    const anchorRow = coveredRows[0]; const anchorColumn = coveredColumns[0]
    for (const row of coveredRows) for (const column of coveredColumns) {
      cells.set(`${row}:${column}`, {
        masterRow: top, masterColumn: left,
        rowSpan: coveredRows.length, colSpan: coveredColumns.length,
        anchor: row === anchorRow && column === anchorColumn,
      })
    }
  }
  return cells
}
