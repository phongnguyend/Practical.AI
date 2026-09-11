import { useEffect, useRef, useState, type ReactNode } from 'react'
import { copyText } from '../lib/clipboard'
import {
  Check,
  ChevronLeft,
  ChevronRight,
  ChevronsLeft,
  Copy,
  Inbox,
  RefreshCw,
  TriangleAlert,
  X,
} from 'lucide-react'

/**
 * A modal built on the native <dialog>, so Escape, the focus trap, and the inert backdrop come from
 * the platform rather than being reimplemented. Clicking the backdrop closes it too, which <dialog>
 * does not do on its own.
 */
export function Modal({
  open,
  title,
  icon,
  className,
  onClose,
  children,
  footer,
}: {
  open: boolean
  title: string
  icon?: ReactNode
  className?: string
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
}) {
  const ref = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return

    if (open && !dialog.open) {
      dialog.showModal()
    } else if (!open && dialog.open) {
      dialog.close()
    }
  }, [open])

  return (
    <dialog
      ref={ref}
      className={className ? `modal ${className}` : 'modal'}
      onCancel={(event) => {
        event.preventDefault()
        onClose()
      }}
      onClick={(event) => {
        // A click that lands on the dialog element itself is a click on the backdrop: the content
        // sits in children that would have been the target otherwise.
        if (event.target === ref.current) onClose()
      }}
    >
      <div className="modal-head">
        <h2>
          {icon}
          {title}
        </h2>
        <button className="ghost icon-only" onClick={onClose} aria-label="Close">
          <X size={15} />
        </button>
      </div>
      <div className="modal-body">{children}</div>
      {footer ? <div className="modal-foot">{footer}</div> : null}
    </dialog>
  )
}

export function StatTile({
  label,
  value,
  hint,
  icon,
  attention = false,
}: {
  label: string
  value: ReactNode
  hint?: ReactNode
  icon?: ReactNode
  attention?: boolean
}) {
  return (
    <div className={attention ? 'tile attention' : 'tile'}>
      <div className="label">
        {icon}
        {label}
      </div>
      <div className="value">{value}</div>
      {hint ? <div className="hint">{hint}</div> : null}
    </div>
  )
}

export function ErrorBanner({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div className="banner" role="alert">
      <TriangleAlert size={17} color="var(--critical)" style={{ marginTop: 2 }} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div className="title">Request failed</div>
        <div>{message}</div>
      </div>
      {onRetry ? (
        <button onClick={onRetry}>
          <RefreshCw size={14} />
          Retry
        </button>
      ) : null}
    </div>
  )
}

export function Empty({
  title,
  detail,
  icon,
}: {
  title: string
  detail?: ReactNode
  icon?: ReactNode
}) {
  return (
    <div className="empty">
      {icon ?? <Inbox size={26} strokeWidth={1.5} />}
      <strong>{title}</strong>
      {detail}
    </div>
  )
}

export function LoadingBar({ active }: { active: boolean }) {
  return <div className="loading-bar" style={{ visibility: active ? 'visible' : 'hidden' }} />
}

export function Field({
  label,
  help,
  className,
  children,
}: {
  label: string
  help?: string
  className?: string
  children: ReactNode
}) {
  return (
    <div className={className ? `field ${className}` : 'field'}>
      <label>{label}</label>
      {children}
      {help ? <span className="help">{help}</span> : null}
    </div>
  )
}

export function CopyButton({ value, label = 'Copy' }: { value: string; label?: string }) {
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (!copied) return
    const timer = setTimeout(() => setCopied(false), 1400)
    return () => clearTimeout(timer)
  }, [copied])

  return (
    <button
      className="ghost"
      title={`Copy ${value}`}
      onClick={() => {
        void copyText(value).then((ok) => setCopied(ok))
      }}
    >
      {copied ? <Check size={13} color="var(--good)" /> : <Copy size={13} />}
      {copied ? 'Copied' : label}
    </button>
  )
}

/**
 * A pager over a total the API reports. It shows the row window rather than a page count, because the
 * table is filtered server-side and the window is what the reader is looking at.
 */
export function Pagination({
  skip,
  top,
  total,
  onSkip,
}: {
  skip: number
  top: number
  total: number
  onSkip: (skip: number) => void
}) {
  const from = total === 0 ? 0 : skip + 1
  const to = Math.min(skip + top, total)

  return (
    <div className="row spread" style={{ padding: '10px 16px' }}>
      <span style={{ color: 'var(--text-secondary)', fontSize: 12 }}>
        {from.toLocaleString()}–{to.toLocaleString()} of {total.toLocaleString()}
      </span>
      <div className="row" style={{ gap: 6 }}>
        <button onClick={() => onSkip(0)} disabled={skip === 0} title="First page">
          <ChevronsLeft size={14} />
          First
        </button>
        <button onClick={() => onSkip(Math.max(0, skip - top))} disabled={skip === 0}>
          <ChevronLeft size={14} />
          Previous
        </button>
        <button onClick={() => onSkip(skip + top)} disabled={to >= total}>
          Next
          <ChevronRight size={14} />
        </button>
      </div>
    </div>
  )
}
