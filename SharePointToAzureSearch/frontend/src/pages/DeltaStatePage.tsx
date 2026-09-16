import { useState } from 'react'
import {
  CircleCheck,
  Clock,
  Eye,
  EyeOff,
  GitBranch,
  HardDriveDownload,
  RefreshCw,
  RotateCcw,
  Trash2,
} from 'lucide-react'
import { deleteDeltaState, listDeltaState, resetDeltaState } from '../api/client'
import type { DeltaStateRow } from '../api/types'
import { CopyButton, Empty, ErrorBanner, LoadingBar, Modal } from '../components/ui'
import { formatDateTime, formatRelative } from '../lib/format'
import { useAsync } from '../lib/useAsync'

export default function DeltaStatePage() {
  const rows = useAsync((signal) => listDeltaState(signal), [])
  const [confirmation, setConfirmation] = useState<{ kind: 'reset' | 'delete'; row: DeltaStateRow } | null>(null)
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const confirmAction = async () => {
    if (!confirmation || busy) return
    setBusy(true)
    setActionError(null)
    try {
      if (confirmation.kind === 'reset') await resetDeltaState(confirmation.row.driveId)
      else await deleteDeltaState(confirmation.row.driveId)
      setNotice(confirmation.kind === 'reset' ? 'Delta state reset. The next sync will scan the full drive.' : 'Delta state record deleted.')
      setConfirmation(null)
      rows.reload()
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }

  const askToConfirm = (kind: 'reset' | 'delete', row: DeltaStateRow) => {
    setActionError(null)
    setNotice(null)
    setConfirmation({ kind, row })
  }

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>
            <GitBranch size={20} />
            Delta state
          </h1>
          <p>
            The <code>SharePointDeltaState</code> table — one checkpoint per drive. The delta link is
            where the next pass resumes; the scan ID is the reconciliation round it belongs to, and a
            round is only swept once it has walked the whole drive.
          </p>
        </div>
        <button onClick={rows.reload}>
          <RefreshCw size={14} />
          Refresh
        </button>
      </div>

      <LoadingBar active={rows.loading} />

      {rows.error ? <ErrorBanner message={rows.error} onRetry={rows.reload} /> : null}
      {notice ? <div className="banner success" role="status"><CircleCheck size={17} color="var(--good)" />{notice}</div> : null}

      {rows.data && rows.data.length > 0 ? (
        <div className="stack">
          {rows.data.map((row) => (
            <CheckpointCard
              key={row.driveId}
              row={row}
              busy={busy}
              onReset={() => askToConfirm('reset', row)}
              onDelete={() => askToConfirm('delete', row)}
            />
          ))}
        </div>
      ) : rows.loading ? null : (
        <div className="card">
          <Empty
            title="No checkpoint recorded"
            detail="The worker writes one after its first delta page is indexed successfully."
          />
        </div>
      )}

      <Modal
        open={confirmation !== null}
        title={confirmation?.kind === 'reset' ? 'Reset delta state' : 'Delete delta state record'}
        icon={confirmation?.kind === 'reset' ? <RotateCcw size={17} /> : <Trash2 size={17} />}
        onClose={() => { if (!busy) setConfirmation(null) }}
        footer={<>
          <button disabled={busy} onClick={() => setConfirmation(null)}>Cancel</button>
          <button className={confirmation?.kind === 'delete' ? 'danger' : 'primary'} disabled={busy} onClick={() => void confirmAction()}>
            {confirmation?.kind === 'reset' ? <RotateCcw size={14} /> : <Trash2 size={14} />}
            {busy ? 'Working…' : confirmation?.kind === 'reset' ? 'Reset state' : 'Delete record'}
          </button>
        </>}
      >
        <div className="stack">
          <p>
            {confirmation?.kind === 'reset'
              ? 'Clear this drive’s delta link and sweep marker while keeping its record. The next sync will scan the full drive.'
              : 'Remove this drive’s checkpoint record. The next sync will create a new record after scanning the full drive.'}
          </p>
          <div>Drive ID: <code>{confirmation?.row.driveId}</code></div>
          <p className="hint">Run this while synchronization is idle; an active pass may write a new checkpoint afterward.</p>
          {actionError ? <ErrorBanner message={actionError} /> : null}
        </div>
      </Modal>
    </div>
  )
}

function CheckpointCard({ row, busy, onReset, onDelete }: {
  row: DeltaStateRow
  busy: boolean
  onReset: () => void
  onDelete: () => void
}) {
  const [showLink, setShowLink] = useState(false)
  const resetPending = row.deltaLink.length === 0
  const swept = !resetPending && row.sweptScanId === row.scanId

  return (
    <div className="card">
      <div className="card-head">
        <h2 style={{ minWidth: 0 }}>
          <HardDriveDownload size={15} />
          <span className="mono" style={{ overflowWrap: 'anywhere', fontWeight: 600 }}>
            {row.driveId}
          </span>
        </h2>
        <div className="row" style={{ gap: 8 }}>
          <span className={swept ? 'badge good' : 'badge warning'}>
            {swept ? <CircleCheck size={12} /> : resetPending ? <RotateCcw size={12} /> : <Clock size={12} />}
            {swept ? 'Round swept' : resetPending ? 'Reset pending' : 'Sweep pending'}
          </span>
          <span className="hint">updated {formatRelative(row.updatedAtUtc)}</span>
        </div>
      </div>
      <div className="card-body stack" style={{ gap: 14 }}>
        <dl className="detail-grid">
          <dt>Updated</dt>
          <dd>{formatDateTime(row.updatedAtUtc)}</dd>

          <dt title="The reconciliation round the current delta link belongs to">{resetPending ? 'Previous scan ID' : 'Scan ID'}</dt>
          <dd className="mono">{row.scanId}</dd>

          <dt title="The round whose orphan sweep has already run">Swept scan ID</dt>
          <dd className="mono">
            {row.sweptScanId ?? <span style={{ color: 'var(--text-muted)' }}>not yet swept</span>}
          </dd>
        </dl>

        <div>
          <div className="row spread" style={{ marginBottom: 6 }}>
            <span style={{ fontSize: 12, fontWeight: 500, color: 'var(--text-secondary)' }}>
              Delta link
            </span>
            {!resetPending ? <div className="row" style={{ gap: 4 }}>
              <button className="ghost" onClick={() => setShowLink((value) => !value)}>
                {showLink ? <EyeOff size={13} /> : <Eye size={13} />}
                {showLink ? 'Hide' : 'Show'}
              </button>
              <CopyButton value={row.deltaLink} />
            </div> : null}
          </div>
          {resetPending ? (
            <div className="hint">No delta link. The next sync will start a full drive scan.</div>
          ) : showLink ? (
            <div className="delta-link">{row.deltaLink}</div>
          ) : (
            <div style={{ color: 'var(--text-muted)', fontSize: 12 }}>
              {row.deltaLink.length.toLocaleString()} characters — contains the Graph delta token.
            </div>
          )}
        </div>
        <div className="row" style={{ justifyContent: 'flex-end', gap: 8, flexWrap: 'wrap' }}>
          <button disabled={busy} onClick={onReset}><RotateCcw size={14} />Reset</button>
          <button className="danger" disabled={busy} onClick={onDelete}><Trash2 size={14} />Delete</button>
        </div>
      </div>
    </div>
  )
}
