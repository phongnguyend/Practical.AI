import { useState } from 'react'
import { Download, RefreshCw, RotateCw, SearchX, Upload } from 'lucide-react'
import { listUploads, reindexUpload, uploadDownloadUrl } from '../api/client'
import type { UploadIndexStatus } from '../api/types'
import { Empty, ErrorBanner, LoadingBar, Pagination } from '../components/ui'
import { FileTypeIcon } from '../components/FileTypeIcon'
import { formatBytes, formatDateTime, formatRelative } from '../lib/format'
import { useAsync, useDebounced } from '../lib/useAsync'

const STATUS_LABELS: Record<UploadIndexStatus, string> = {
  NotStarted: 'Not started',
  Indexing: 'Indexing',
  Indexed: 'Indexed',
  Failed: 'Failed',
}

const STATUS_CLASSES: Record<UploadIndexStatus, string> = {
  NotStarted: '',
  Indexing: 'warning',
  Indexed: 'good',
  Failed: 'critical',
}

export default function UploadsPage() {
  const [search, setSearch] = useState('')
  const [skip, setSkip] = useState(0)
  const [working, setWorking] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const debouncedSearch = useDebounced(search)
  const page = useAsync(
    (signal) => listUploads({ search: debouncedSearch, skip, top: 25 }, signal),
    [debouncedSearch, skip],
  )

  const reindex = async (id: string) => {
    setWorking(id)
    setActionError(null)
    try {
      const result = await reindexUpload(id)
      if (result.status === 'Failed') setActionError(result.errorMessage ?? 'Indexing failed.')
      page.reload()
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setWorking(null)
    }
  }

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1><Upload size={20} />Uploaded files</h1>
          <p>Files uploaded for chat, their conversion and embedding status, and the resulting chunk count.</p>
        </div>
        <button onClick={page.reload}><RefreshCw size={14} />Refresh</button>
      </div>

      <div className="card"><div className="card-body">
        <input
          type="search"
          aria-label="Filter uploads"
          placeholder="Filter by file name"
          value={search}
          onChange={(event) => { setSearch(event.target.value); setSkip(0) }}
        />
      </div></div>

      <LoadingBar active={page.loading || working !== null} />
      {page.error ? <ErrorBanner message={page.error} onRetry={page.reload} /> : null}
      {actionError ? <ErrorBanner message={actionError} /> : null}

      <div className="card">
        {page.data && page.data.items.length > 0 ? (
          <>
            <div className="table-scroll">
              <table>
                <thead><tr><th>File</th><th>Size</th><th>Status</th><th>Chunks</th><th>Uploaded</th><th>Indexed</th><th>Actions</th></tr></thead>
                <tbody>
                  {page.data.items.map((file) => (
                    <tr key={file.id}>
                      <td>
                        <span className="file-name">
                          <FileTypeIcon name={file.fileName} mimeType={file.contentType} size={15} />
                          <span>{file.fileName}</span>
                        </span>
                        {file.errorMessage ? <div className="upload-error" title={file.errorMessage}>{file.errorMessage}</div> : null}
                      </td>
                      <td>{formatBytes(file.sizeBytes)}</td>
                      <td><span className={`badge ${STATUS_CLASSES[file.status]}`}>{STATUS_LABELS[file.status]}</span></td>
                      <td>{file.chunkCount.toLocaleString()}</td>
                      <td title={formatDateTime(file.createdAtUtc)}>{formatRelative(file.createdAtUtc)}</td>
                      <td title={formatDateTime(file.indexedAtUtc)}>{formatRelative(file.indexedAtUtc)}</td>
                      <td>
                        <div className="row" style={{ gap: 6, flexWrap: 'nowrap' }}>
                          <a className="button-link" href={uploadDownloadUrl(file.id)}><Download size={13} />Download</a>
                          <button disabled={working === file.id} onClick={() => void reindex(file.id)}>
                            <RotateCw size={13} />{working === file.id ? 'Indexing…' : 'Reindex'}
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination skip={skip} top={25} total={page.data.totalCount} onSkip={setSkip} />
          </>
        ) : page.loading ? <Empty title="Loading…" /> : (
          <Empty title="No uploads" icon={<SearchX size={26} strokeWidth={1.5} />} detail="Files attached in chat will appear here." />
        )}
      </div>
    </div>
  )
}
