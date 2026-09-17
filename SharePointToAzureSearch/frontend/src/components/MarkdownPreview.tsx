import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { Download, X } from 'lucide-react'
import DOMPurify from 'dompurify'
import { ErrorBanner } from './ui'

type View = 'plain' | 'rendered'

function isOutsideDialog(dialog: HTMLDialogElement, x: number, y: number) {
  const bounds = dialog.getBoundingClientRect()
  return x < bounds.left || x > bounds.right || y < bounds.top || y > bounds.bottom
}

export function MarkdownPreview({
  name,
  sourceKey,
  load,
  onClose,
}: {
  name: string
  sourceKey: string
  load: (signal: AbortSignal) => Promise<{ markdown: string }>
  onClose: () => void
}) {
  const [markdown, setMarkdown] = useState<string | null>(null)
  const [renderedHtml, setRenderedHtml] = useState<string | null>(null)
  const [downloadUrl, setDownloadUrl] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [renderError, setRenderError] = useState<string | null>(null)
  const [view, setView] = useState<View>('plain')
  const dialogRef = useRef<HTMLDialogElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)
  const plainRef = useRef<HTMLButtonElement>(null)
  const renderedRef = useRef<HTMLButtonElement>(null)
  const backdropPointerDown = useRef(false)
  const tabId = useId()

  useEffect(() => {
    const dialog = dialogRef.current
    dialog?.showModal()
    closeRef.current?.focus()
    return () => dialog?.close()
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    setMarkdown(null)
    setError(null)
    load(controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) setMarkdown(result.markdown)
      })
      .catch((cause) => {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : String(cause))
      })
    return () => controller.abort()
    // sourceKey identifies the file; the load callback is captured for this request.
  }, [sourceKey])

  useEffect(() => {
    if (markdown === null) {
      setDownloadUrl(null)
      return
    }
    const url = URL.createObjectURL(new Blob([markdown], { type: 'text/markdown;charset=utf-8' }))
    setDownloadUrl(url)
    return () => URL.revokeObjectURL(url)
  }, [markdown])

  useEffect(() => {
    setRenderedHtml(null)
    setRenderError(null)
    if (markdown === null) return

    const worker = new Worker(new URL('../workers/renderMarkdown.worker.ts', import.meta.url), { type: 'module' })
    worker.onmessage = (event: MessageEvent<{ html?: string; error?: string }>) => {
      try {
        if (event.data.error) setRenderError(event.data.error)
        else setRenderedHtml(DOMPurify.sanitize(event.data.html ?? ''))
      } catch (cause) {
        setRenderError(cause instanceof Error ? cause.message : String(cause))
      }
      worker.terminate()
    }
    worker.onerror = (event) => {
      setRenderError(event.message || 'Could not render this Markdown file.')
      worker.terminate()
    }
    worker.postMessage(markdown)
    return () => worker.terminate()
  }, [markdown])

  const lastDot = name.lastIndexOf('.')
  const downloadName = `${lastDot > 0 ? name.slice(0, lastDot) : name}.md`

  const selectWithArrow = (event: KeyboardEvent<HTMLButtonElement>, current: View) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
    event.preventDefault()
    const next = current === 'plain' ? 'rendered' : 'plain'
    setView(next)
    const nextRef = next === 'plain' ? plainRef : renderedRef
    nextRef.current?.focus()
  }

  return (
    <dialog
      ref={dialogRef}
      className="office-dialog markdown-dialog"
      aria-label={`View Markdown for ${name}`}
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
          {downloadUrl ? <a className="button-link" href={downloadUrl} download={downloadName}><Download size={14} />Download</a> : null}
          <button className="ghost" ref={closeRef} onClick={onClose}><X size={15} />Close</button>
        </div>
      </div>
      <div className="markdown-preview-tabs" role="tablist" aria-label="Markdown view">
        <button
          ref={plainRef}
          id={`${tabId}-plain`}
          role="tab"
          aria-selected={view === 'plain'}
          aria-controls={`${tabId}-panel`}
          tabIndex={view === 'plain' ? 0 : -1}
          onClick={() => setView('plain')}
          onKeyDown={(event) => selectWithArrow(event, 'plain')}
        >
          Plain text
        </button>
        <button
          ref={renderedRef}
          id={`${tabId}-rendered`}
          role="tab"
          aria-selected={view === 'rendered'}
          aria-controls={`${tabId}-panel`}
          tabIndex={view === 'rendered' ? 0 : -1}
          onClick={() => setView('rendered')}
          onKeyDown={(event) => selectWithArrow(event, 'rendered')}
        >
          Markdown
        </button>
      </div>
      <div
        id={`${tabId}-panel`}
        className={`office-content markdown-preview-content${view === 'plain' && markdown ? ' markdown-preview-content--plain' : ''}`}
        role="tabpanel"
        aria-labelledby={`${tabId}-${view}`}
        tabIndex={0}
      >
        {error ? <ErrorBanner message={error} /> : null}
        {markdown === null && !error ? <div className="office-message">Converting with MarkItDown…</div> : null}
        {markdown !== null && !markdown.trim() ? <div className="office-message">No text was returned.</div> : null}
        {markdown && view === 'plain' ? <textarea className="markdown-preview-source" aria-label="Plain text Markdown" value={markdown} readOnly /> : null}
        {markdown && view === 'rendered' && renderError ? <ErrorBanner message={renderError} /> : null}
        {markdown && view === 'rendered' && renderedHtml === null && !renderError ? <div className="office-message" role="status">Rendering Markdown…</div> : null}
        {markdown && view === 'rendered' && renderedHtml !== null ? <div className="markdown markdown-preview-rendered" dangerouslySetInnerHTML={{ __html: renderedHtml }} /> : null}
      </div>
    </dialog>
  )
}
