import { useState } from 'react'
import {
  CircleCheck,
  Clock,
  Eye,
  EyeOff,
  GitBranch,
  HardDriveDownload,
  RefreshCw,
} from 'lucide-react'
import { listDeltaState } from '../api/client'
import type { DeltaStateRow } from '../api/types'
import { CopyButton, Empty, ErrorBanner, LoadingBar } from '../components/ui'
import { formatDateTime, formatRelative } from '../lib/format'
import { useAsync } from '../lib/useAsync'

export default function DeltaStatePage() {
  const rows = useAsync((signal) => listDeltaState(signal), [])

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

      {rows.data && rows.data.length > 0 ? (
        <div className="stack">
          {rows.data.map((row) => (
            <CheckpointCard key={row.driveId} row={row} />
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
    </div>
  )
}

function CheckpointCard({ row }: { row: DeltaStateRow }) {
  const [showLink, setShowLink] = useState(false)
  const swept = row.sweptScanId === row.scanId

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
            {swept ? <CircleCheck size={12} /> : <Clock size={12} />}
            {swept ? 'Round swept' : 'Sweep pending'}
          </span>
          <span className="hint">updated {formatRelative(row.updatedAtUtc)}</span>
        </div>
      </div>
      <div className="card-body stack" style={{ gap: 14 }}>
        <dl className="detail-grid">
          <dt>Updated</dt>
          <dd>{formatDateTime(row.updatedAtUtc)}</dd>

          <dt title="The reconciliation round the current delta link belongs to">Scan ID</dt>
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
            <div className="row" style={{ gap: 4 }}>
              <button className="ghost" onClick={() => setShowLink((value) => !value)}>
                {showLink ? <EyeOff size={13} /> : <Eye size={13} />}
                {showLink ? 'Hide' : 'Show'}
              </button>
              <CopyButton value={row.deltaLink} />
            </div>
          </div>
          {showLink ? (
            <div className="delta-link">{row.deltaLink}</div>
          ) : (
            <div style={{ color: 'var(--text-muted)', fontSize: 12 }}>
              {row.deltaLink.length.toLocaleString()} characters — contains the Graph delta token.
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
