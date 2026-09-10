import { Link } from 'react-router-dom'
import {
  Boxes,
  CircleCheck,
  Clock,
  Fingerprint,
  FileText,
  GitBranch,
  HardDrive,
  HardDriveDownload,
  History,
  LayoutDashboard,
  PieChart,
  RefreshCw,
  TriangleAlert,
} from 'lucide-react'
import { getSummary, listDeltaState, listIndexedFiles } from '../api/client'
import { FileTypeIcon } from '../components/FileTypeIcon'
import { Empty, ErrorBanner, LoadingBar, StatTile } from '../components/ui'
import {
  formatBytes,
  formatDateTime,
  formatNumber,
  formatRelative,
  mimeTypeLabel,
} from '../lib/format'
import { useAsync } from '../lib/useAsync'

export default function OverviewPage() {
  const summary = useAsync((signal) => getSummary(signal), [])
  const delta = useAsync((signal) => listDeltaState(signal), [])
  const recent = useAsync(
    (signal) => listIndexedFiles({ sort: 'indexedAtUtc', desc: true, top: 8 }, signal),
    [],
  )

  const reloadAll = () => {
    summary.reload()
    delta.reload()
    recent.reload()
  }

  const data = summary.data
  const largestMimeCount = data ? Math.max(1, ...data.byMimeType.map((row) => row.fileCount)) : 1

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>
            <LayoutDashboard size={20} />
            Overview
          </h1>
          <p>
            What the worker has recorded in SQL Server: the files it has indexed and how far its
            reconciliation has got.
          </p>
        </div>
        <button onClick={reloadAll}>
          <RefreshCw size={14} />
          Refresh
        </button>
      </div>

      <LoadingBar active={summary.loading || delta.loading || recent.loading} />

      {summary.error ? <ErrorBanner message={summary.error} onRetry={summary.reload} /> : null}

      {data ? (
        <div className="tiles">
          <StatTile
            label="Indexed files"
            value={formatNumber(data.totalFiles)}
            icon={<FileText size={14} />}
          />
          <StatTile
            label="Chunks in the index"
            value={formatNumber(data.totalChunks)}
            icon={<Boxes size={14} />}
            hint={
              data.totalFiles > 0
                ? `${(data.totalChunks / data.totalFiles).toFixed(1)} per file on average`
                : undefined
            }
          />
          <StatTile
            label="Source size"
            value={formatBytes(data.totalSizeBytes)}
            icon={<HardDrive size={14} />}
          />
          <StatTile
            label="Drives tracked"
            value={formatNumber(data.distinctDrives)}
            icon={<HardDriveDownload size={14} />}
            hint={<Link to="/delta">See checkpoints</Link>}
          />
          <StatTile
            label="Outside current round"
            value={formatNumber(data.filesOutsideCurrentScan)}
            icon={<TriangleAlert size={14} />}
            attention={data.filesOutsideCurrentScan > 0}
            hint="Orphan candidates the next completed sweep removes"
          />
          <StatTile
            label="Index fingerprints"
            value={formatNumber(data.distinctIndexFingerprints)}
            icon={<Fingerprint size={14} />}
            attention={data.distinctIndexFingerprints > 1}
            hint={
              data.distinctIndexFingerprints > 1
                ? 'More than one chunk/embedding setting is in the index'
                : 'One chunking and embedding setting throughout'
            }
          />
        </div>
      ) : null}

      <div className="split">
        <div className="card">
          <div className="card-head">
            <h2>
              <PieChart size={15} />
              Files by content type
            </h2>
            <span className="hint">Top 12</span>
          </div>
          {data && data.byMimeType.length > 0 ? (
            <div className="table-scroll">
              <table>
                <thead>
                  <tr>
                    <th>Content type</th>
                    <th style={{ width: '28%' }}>Share of files</th>
                    <th className="num">Files</th>
                    <th className="num">Chunks</th>
                    <th className="num">Size</th>
                  </tr>
                </thead>
                <tbody>
                  {data.byMimeType.map((row) => (
                    <tr key={row.mimeType ?? '(none)'}>
                      <td>
                        <span className="file-name">
                          <FileTypeIcon mimeType={row.mimeType} size={15} />
                          <span className="truncate" title={row.mimeType ?? undefined}>
                            {mimeTypeLabel(row.mimeType)}
                          </span>
                        </span>
                      </td>
                      <td>
                        <div
                          className="bar-track"
                          role="img"
                          aria-label={`${row.fileCount} files`}
                        >
                          <div
                            className="bar-fill"
                            style={{ width: `${(row.fileCount / largestMimeCount) * 100}%` }}
                          />
                        </div>
                      </td>
                      <td className="num">{formatNumber(row.fileCount)}</td>
                      <td className="num">{formatNumber(row.chunkCount)}</td>
                      <td className="num">{formatBytes(row.sizeBytes)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <Empty
              title="No indexed files yet"
              detail="The table fills once the worker has completed a delta pass."
            />
          )}
        </div>

        <div className="stack">
          {data ? (
            <div className="card">
              <div className="card-head">
                <h2>
                  <Clock size={15} />
                  Indexing window
                </h2>
              </div>
              <div className="card-body">
                <dl className="detail-grid">
                  <dt>Oldest</dt>
                  <dd>
                    {formatDateTime(data.oldestIndexedAtUtc)}
                    <div style={{ color: 'var(--text-muted)', fontSize: 12 }}>
                      {formatRelative(data.oldestIndexedAtUtc)}
                    </div>
                  </dd>
                  <dt>Newest</dt>
                  <dd>
                    {formatDateTime(data.newestIndexedAtUtc)}
                    <div style={{ color: 'var(--text-muted)', fontSize: 12 }}>
                      {formatRelative(data.newestIndexedAtUtc)}
                    </div>
                  </dd>
                </dl>
              </div>
            </div>
          ) : null}

          <div className="card">
            <div className="card-head">
              <h2>
                <History size={15} />
                Recently indexed
              </h2>
              <Link to="/files">All files</Link>
            </div>
            {recent.data && recent.data.items.length > 0 ? (
              <div>
                {recent.data.items.map((file) => (
                  <div className="hit" key={`${file.driveId}:${file.itemId}`}>
                    <div className="file-name top">
                      <FileTypeIcon
                        name={file.name}
                        mimeType={file.mimeType}
                        size={15}
                      />
                      <div className="hit-name">
                        {file.webUrl ? (
                          <a href={file.webUrl} target="_blank" rel="noreferrer">
                            {file.name}
                          </a>
                        ) : (
                          file.name
                        )}
                      </div>
                    </div>
                    <div className="hit-meta" style={{ paddingLeft: 22 }}>
                      <span>{formatRelative(file.indexedAtUtc)}</span>
                      <span>{file.chunkCount} chunks</span>
                      <span>{formatBytes(file.size)}</span>
                    </div>
                  </div>
                ))}
              </div>
            ) : (
              <Empty title="Nothing indexed yet" />
            )}
          </div>

          <div className="card">
            <div className="card-head">
              <h2>
                <GitBranch size={15} />
                Delta checkpoints
              </h2>
              <Link to="/delta">Details</Link>
            </div>
            {delta.error ? (
              <div className="card-body">
                <ErrorBanner message={delta.error} onRetry={delta.reload} />
              </div>
            ) : delta.data && delta.data.length > 0 ? (
              <div>
                {delta.data.map((row) => (
                  <div className="hit" key={row.driveId}>
                    <div className="hit-head">
                      <span className="hit-name mono">{row.driveId}</span>
                    </div>
                    <div className="hit-meta">
                      <span>updated {formatRelative(row.updatedAtUtc)}</span>
                      <span
                        className={row.sweptScanId === row.scanId ? 'badge good' : 'badge warning'}
                      >
                        {row.sweptScanId === row.scanId ? (
                          <>
                            <CircleCheck size={12} />
                            Round swept
                          </>
                        ) : (
                          <>
                            <Clock size={12} />
                            Sweep pending
                          </>
                        )}
                      </span>
                    </div>
                  </div>
                ))}
              </div>
            ) : (
              <Empty title="No checkpoint recorded" detail="The next pass writes one." />
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
