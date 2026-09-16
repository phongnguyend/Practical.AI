import { useEffect, useId, useMemo, useRef, useState } from 'react'
import type { RefObject } from 'react'
import type { Workbook } from 'exceljs'
import type { PptxViewer } from '@aiden0z/pptx-renderer'
import { ChevronLeft, ChevronRight, Download, X } from 'lucide-react'
import { ErrorBanner } from './ui'
import { normalizePptxXml } from '../lib/normalizePptx'
import { cellDisplayText, cellStyle, columnWidthPx, fontStyle, rowHeightPx, themeColors, visibleMerges } from '../lib/excelPreview'
import type { NumberFormatter } from '../lib/excelPreview'

type OfficeKind = 'docx' | 'xlsx' | 'pptx'

function officeKind(name: string): OfficeKind {
  return name.split('.').pop()!.toLowerCase() as OfficeKind
}

function isOutsideDialog(dialog: HTMLDialogElement, x: number, y: number) {
  const bounds = dialog.getBoundingClientRect()
  return x < bounds.left || x > bounds.right || y < bounds.top || y > bounds.bottom
}

export function OfficePreview({
  name,
  sourceKey,
  load,
  onClose,
}: {
  name: string
  sourceKey: string
  load: (signal: AbortSignal) => Promise<Blob>
  onClose: () => void
}) {
  const [blob, setBlob] = useState<Blob | null>(null)
  const [url, setUrl] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const dialogRef = useRef<HTMLDialogElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)
  const backdropPointerDown = useRef(false)
  const kind = officeKind(name)

  useEffect(() => {
    const dialog = dialogRef.current
    dialog?.showModal()
    closeRef.current?.focus()
    return () => dialog?.close()
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    setBlob(null)
    setError(null)
    load(controller.signal)
      .then((content) => {
        if (!controller.signal.aborted) setBlob(content)
      })
      .catch((cause) => {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : String(cause))
      })
    return () => controller.abort()
    // sourceKey identifies the file; the load callback is intentionally captured for this request.
  }, [sourceKey])

  useEffect(() => {
    if (!blob) return
    const objectUrl = URL.createObjectURL(blob)
    setUrl(objectUrl)
    return () => {
      URL.revokeObjectURL(objectUrl)
      setUrl(null)
    }
  }, [blob])

  return (
      <dialog
        ref={dialogRef}
        className={`office-dialog office-dialog--${kind}`}
        aria-label={`Preview ${name}`}
        onCancel={(event) => { event.preventDefault(); onClose() }}
        onPointerDown={(event) => {
          backdropPointerDown.current = event.target === event.currentTarget && isOutsideDialog(event.currentTarget, event.clientX, event.clientY)
        }}
        onPointerCancel={() => { backdropPointerDown.current = false }}
        onClick={(event) => {
          const startedOnBackdrop = backdropPointerDown.current
          backdropPointerDown.current = false
          if (startedOnBackdrop && event.target === event.currentTarget && isOutsideDialog(event.currentTarget, event.clientX, event.clientY)) onClose()
        }}
      >
        <div className="office-toolbar">
          <strong title={name}>{name}</strong>
          <div className="row">
            {url ? <a className="button-link" href={url} download={name}><Download size={14} />Download</a> : null}
            <button className="ghost" ref={closeRef} onClick={onClose}><X size={15} />Close</button>
          </div>
        </div>
        <div className="office-content">
          {error ? <ErrorBanner message={error} /> : null}
          {!blob && !error ? <div className="office-message">Downloading file…</div> : null}
          {blob && kind === 'docx' ? <WordPreview blob={blob} /> : null}
          {blob && kind === 'xlsx' ? <ExcelPreview blob={blob} /> : null}
          {blob && kind === 'pptx' ? <PowerPointPreview blob={blob} /> : null}
        </div>
      </dialog>
  )
}

function WordPreview({ blob }: { blob: Blob }) {
  const container = useRef<HTMLDivElement>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    async function render() {
      try {
        const { renderAsync } = await import('docx-preview')
        if (active && container.current) await renderAsync(blob, container.current)
      } catch (cause) {
        if (active) setError(cause instanceof Error ? cause.message : String(cause))
      }
    }
    void render()
    return () => { active = false; if (container.current) container.current.replaceChildren() }
  }, [blob])

  return <>{error ? <ErrorBanner message={error} /> : null}<div className="office-word" ref={container} /></>
}

function PowerPointPreview({ blob }: { blob: Blob }) {
  const container = useRef<HTMLDivElement>(null)
  const thumbnailList = useRef<HTMLElement>(null)
  const viewer = useRef<PptxViewer | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [slideCount, setSlideCount] = useState(0)
  const [slideIndex, setSlideIndex] = useState(0)
  const [loaded, setLoaded] = useState(false)
  const [navigating, setNavigating] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    let active = true
    async function render() {
      try {
        const { PptxViewer, RECOMMENDED_ZIP_LIMITS } = await import('@aiden0z/pptx-renderer')
        const bytes = await normalizePptxXml(await blob.arrayBuffer(), controller.signal)
        if (!active || !container.current) return
        const previewer = new PptxViewer(container.current, {
          fitMode: 'contain',
          zipLimits: RECOMMENDED_ZIP_LIMITS,
          onSlideChange: (index) => { if (active) setSlideIndex(index) },
        })
        viewer.current = previewer
        await previewer.open(bytes, { renderMode: 'slide', signal: controller.signal })
        if (active) {
          setSlideCount(previewer.slideCount)
          setSlideIndex(previewer.currentSlideIndex)
          setLoaded(true)
        }
      } catch (cause) {
        if (active && !controller.signal.aborted) setError(cause instanceof Error ? cause.message : String(cause))
      }
    }
    void render()
    return () => {
      active = false
      controller.abort()
      viewer.current?.destroy()
      viewer.current = null
    }
  }, [blob])

  const goToSlide = async (index: number) => {
    const previewer = viewer.current
    if (!previewer || navigating || index < 0 || index >= slideCount) return
    setNavigating(true)
    try {
      await previewer.goToSlide(index)
      setSlideIndex(previewer.currentSlideIndex)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setNavigating(false)
    }
  }

  return (
    <div className="office-presentation">
      {error ? <ErrorBanner message={error} /> : null}
      {!error && !loaded ? <div className="office-message">Rendering presentation…</div> : null}
      {!error && loaded && slideCount === 0 ? <div className="office-message">Presentation has no slides.</div> : null}
      <div className="office-presentation-body">
        {loaded && slideCount > 0 && viewer.current ? (
          <nav className="office-thumbnail-list" aria-label="Slides" ref={thumbnailList}>
            {Array.from({ length: slideCount }, (_, index) => (
              <SlideThumbnail
                key={index}
                viewer={viewer.current!}
                index={index}
                selected={index === slideIndex}
                scrollRoot={thumbnailList}
                onClick={() => void goToSlide(index)}
              />
            ))}
          </nav>
        ) : null}
        <div className="office-slide-stage"><div className="office-slides" ref={container} /></div>
      </div>
      {slideCount > 0 ? (
        <div className="office-slide-controls">
          <button disabled={navigating || slideIndex === 0} onClick={() => void goToSlide(slideIndex - 1)}>
            <ChevronLeft size={14} />Previous
          </button>
          <span>Slide {slideIndex + 1} of {slideCount}</span>
          <button disabled={navigating || slideIndex >= slideCount - 1} onClick={() => void goToSlide(slideIndex + 1)}>
            Next<ChevronRight size={14} />
          </button>
        </div>
      ) : null}
    </div>
  )
}

function SlideThumbnail({ viewer, index, selected, scrollRoot, onClick }: {
  viewer: PptxViewer
  index: number
  selected: boolean
  scrollRoot: RefObject<HTMLElement | null>
  onClick: () => void
}) {
  const button = useRef<HTMLButtonElement>(null)
  const preview = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const target = button.current
    const container = preview.current
    if (!target || !container) return

    let handle: ReturnType<PptxViewer['renderThumbnailToContainer']> = null
    let failed = false
    const observer = new IntersectionObserver(([entry]) => {
      if (entry.isIntersecting && !handle && !failed) {
        try {
          handle = viewer.renderThumbnailToContainer(index, container, { width: container.clientWidth })
          if (!handle) failed = true
          else {
            const currentHandle = handle
            void currentHandle.ready.catch(() => {
              failed = true
              currentHandle.dispose()
              if (handle === currentHandle) handle = null
            })
          }
        } catch {
          failed = true
        }
      } else if (!entry.isIntersecting && handle) {
        handle.dispose()
        handle = null
      }
    }, { root: scrollRoot.current, rootMargin: '120px 0px' })
    observer.observe(target)
    return () => { observer.disconnect(); handle?.dispose() }
  }, [viewer, index, scrollRoot])

  useEffect(() => {
    if (selected) button.current?.scrollIntoView({ block: 'nearest' })
  }, [selected])

  return (
    <button
      ref={button}
      type="button"
      className="office-thumbnail"
      aria-label={`Slide ${index + 1}`}
      aria-current={selected ? 'page' : undefined}
      onClick={onClick}
    >
      <div className="office-thumbnail-preview" ref={preview} style={{ aspectRatio: `${viewer.slideWidth} / ${viewer.slideHeight}` }} />
      <span>{index + 1}</span>
    </button>
  )
}

const ROWS_PER_PAGE = 50
const COLUMNS_PER_PAGE = 26

function columnLabel(index: number): string {
  let label = ''
  while (index > 0) {
    index--
    label = String.fromCharCode(65 + index % 26) + label
    index = Math.floor(index / 26)
  }
  return label
}

function ExcelPreview({ blob }: { blob: Blob }) {
  const [workbook, setWorkbook] = useState<Workbook | null>(null)
  const [formatNumber, setFormatNumber] = useState<NumberFormatter | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [sheetIndex, setSheetIndex] = useState(0)
  const [rowPage, setRowPage] = useState(0)
  const [columnPage, setColumnPage] = useState(0)
  const tabId = useId()
  const panelId = useId()
  const theme = useMemo(() => workbook ? themeColors(workbook) : [], [workbook])
  const mergeRanges = useMemo(() => workbook?.worksheets[sheetIndex]?.model.merges ?? [], [workbook, sheetIndex])

  const selectSheet = (index: number) => {
    setSheetIndex(index)
    setRowPage(0)
    setColumnPage(0)
  }

  useEffect(() => {
    let active = true
    async function read() {
      try {
        const [ExcelJS, SSF, bytes] = await Promise.all([import('exceljs'), import('ssf'), blob.arrayBuffer()])
        const next = new ExcelJS.Workbook()
        await next.xlsx.load(bytes as Parameters<typeof next.xlsx.load>[0])
        if (active) {
          setWorkbook(next)
          setFormatNumber(() => SSF.format)
        }
      } catch (cause) {
        if (active) setError(cause instanceof Error ? cause.message : String(cause))
      }
    }
    void read()
    return () => { active = false }
  }, [blob])

  if (error) return <ErrorBanner message={error} />
  if (!workbook || !formatNumber) return <div className="office-message">Reading workbook…</div>
  if (workbook.worksheets.length === 0) return <div className="office-message">Workbook has no sheets.</div>

  const sheet = workbook.worksheets[sheetIndex]
  const rowStart = rowPage * ROWS_PER_PAGE + 1
  const rowEnd = Math.min(sheet.rowCount, rowStart + ROWS_PER_PAGE - 1)
  const columnStart = columnPage * COLUMNS_PER_PAGE + 1
  const columnEnd = Math.min(sheet.columnCount, columnStart + COLUMNS_PER_PAGE - 1)
  const columns = Array.from({ length: Math.max(0, columnEnd - columnStart + 1) }, (_, i) => columnStart + i)
  const rows = Array.from({ length: Math.max(0, rowEnd - rowStart + 1) }, (_, i) => rowStart + i)
  const visibleRows = rows.filter((row) => !sheet.getRow(row).hidden)
  const visibleColumns = columns.filter((column) => !sheet.getColumn(column).hidden)
  const merges = visibleMerges(mergeRanges, visibleRows, visibleColumns)
  const gridWidth = 50 + visibleColumns.reduce((width, column) => width + columnWidthPx(sheet.getColumn(column).width), 0)

  return (
    <div className="office-excel">
      <div className="office-sheet-controls">
        <div className="office-sheet-tabs" role="tablist" aria-label="Worksheets" onKeyDown={(event) => {
          const count = workbook.worksheets.length
          let next: number
          if (event.key === 'ArrowRight') next = (sheetIndex + 1) % count
          else if (event.key === 'ArrowLeft') next = (sheetIndex - 1 + count) % count
          else if (event.key === 'Home') next = 0
          else if (event.key === 'End') next = count - 1
          else return
          event.preventDefault()
          selectSheet(next)
          event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next]?.focus()
        }}>
          {workbook.worksheets.map((item, index) => (
            <button
              key={item.id}
              id={`${tabId}-${index}`}
              className="office-sheet-tab"
              role="tab"
              type="button"
              aria-selected={index === sheetIndex}
              aria-controls={panelId}
              tabIndex={index === sheetIndex ? 0 : -1}
              title={item.name}
              onClick={() => selectSheet(index)}
            >{item.name}</button>
          ))}
        </div>
        <span className="office-sheet-summary">{sheet.rowCount.toLocaleString()} rows · {sheet.columnCount.toLocaleString()} columns</span>
      </div>
      <div className="office-grid-scroll" id={panelId} role="tabpanel" aria-labelledby={`${tabId}-${sheetIndex}`}>
        <table className="office-grid" style={{ width: gridWidth }}>
          <colgroup><col style={{ width: 50 }} />{visibleColumns.map((col) => <col key={col} style={{ width: columnWidthPx(sheet.getColumn(col).width) }} />)}</colgroup>
          <thead><tr><th aria-label="Row number" />{visibleColumns.map((col) => <th key={col}>{columnLabel(col)}</th>)}</tr></thead>
          <tbody>{visibleRows.map((row) => <tr key={row} style={{ height: rowHeightPx(sheet.getRow(row).height) }}>
            <th>{row}</th>
            {visibleColumns.map((col) => {
              const merge = merges.get(`${row}:${col}`)
              if (merge && !merge.anchor) return null
              const cell = sheet.getRow(merge?.masterRow ?? row).getCell(merge?.masterColumn ?? col)
              const text = cellDisplayText(cell, formatNumber, workbook.properties.date1904)
              const value = cell.value
              const content = value && typeof value === 'object' && 'richText' in value && Array.isArray(value.richText)
                ? value.richText.map((run, index) => <span key={index} style={fontStyle(run.font, theme)}>{run.text}</span>)
                : text
              return <td key={col} rowSpan={merge?.rowSpan} colSpan={merge?.colSpan} title={text} style={cellStyle(cell, theme)}>{content}</td>
            })}
          </tr>)}</tbody>
        </table>
        {rows.length === 0 ? <div className="office-message">This sheet is empty.</div> : null}
        {rows.length > 0 && (!visibleRows.length || !visibleColumns.length) ? <div className="office-message">No visible cells in this range.</div> : null}
      </div>
      <div className="office-sheet-controls">
        <div className="row">
          <button disabled={rowPage === 0} onClick={() => setRowPage(rowPage - 1)}><ChevronLeft size={14} />Rows</button>
          <span>{rows.length ? `${rowStart}–${rowEnd}` : '0'} of {sheet.rowCount}</span>
          <button disabled={rowEnd >= sheet.rowCount} onClick={() => setRowPage(rowPage + 1)}>Rows<ChevronRight size={14} /></button>
        </div>
        <div className="row">
          <button disabled={columnPage === 0} onClick={() => setColumnPage(columnPage - 1)}><ChevronLeft size={14} />Columns</button>
          <span>{columns.length ? `${columnLabel(columnStart)}–${columnLabel(columnEnd)}` : '0'} of {sheet.columnCount}</span>
          <button disabled={columnEnd >= sheet.columnCount} onClick={() => setColumnPage(columnPage + 1)}>Columns<ChevronRight size={14} /></button>
        </div>
      </div>
    </div>
  )
}
