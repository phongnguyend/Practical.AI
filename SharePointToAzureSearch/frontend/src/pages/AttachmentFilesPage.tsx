import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Download, MessageSquare, Paperclip, RefreshCw, RotateCw, SearchX, Trash2, X } from 'lucide-react'
import {
  attachmentFileDownloadUrl,
  deleteOrphanAttachmentFile,
  listAttachmentFiles,
  reindexAttachmentFile,
} from '../api/client'
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

export default function AttachmentFilesPage() {
  const [search, setSearch] = useState('')
  const [skip, setSkip] = useState(0)
  const [working, setWorking] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)
  const debouncedSearch = useDebounced(search)
  const page = useAsync(
    (signal) => listAttachmentFiles({ search: debouncedSearch, skip, top: 25 }, signal),
    [debouncedSearch, skip],
  )

  const reindex = async (id: string) => {
    setWorking(id)
    setActionError(null)
    try {
      const result = await reindexAttachmentFile(id)
      if (result.status === 'Failed') setActionError(result.errorMessage ?? 'Indexing failed.')
      page.reload()
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setWorking(null)
    }
  }

  const removeOrphan = async (id: string) => {
    setWorking(id)
    setActionError(null)
    try {
      await deleteOrphanAttachmentFile(id)
      setConfirmDelete(null)
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
          <h1><Paperclip size={20} />Attachment files</h1>
          <p>Files prepared for chat, their index status, and the conversation each file belongs to.</p>
        </div>
        <button onClick={page.reload}><RefreshCw size={14} />Refresh</button>
      </div>

      <div className="card"><div className="card-body">
        <input
          type="search"
          aria-label="Filter attachment files"
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
                <thead><tr><th>File</th><th>Status</th><th>Conversation</th><th>Size</th><th>Chunks</th><th>Uploaded</th><th>Indexed</th><th>Actions</th></tr></thead>
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
                      <td><span className={`badge ${STATUS_CLASSES[file.status]}`}>{STATUS_LABELS[file.status]}</span></td>
                      <td>
                        {file.conversationId ? (
                          <Link
                            className="conversation-link"
                            to={`/chat?conversation=${encodeURIComponent(file.conversationId)}${file.messageId ? `&message=${encodeURIComponent(file.messageId)}` : ''}`}
                          >
                            <MessageSquare size={13} />
                            {file.conversationTitle ?? 'Open conversation'}
                          </Link>
                        ) : <span className="badge warning">Orphan</span>}
                      </td>
                      <td>{formatBytes(file.sizeBytes)}</td>
                      <td>{file.chunkCount.toLocaleString()}</td>
                      <td title={formatDateTime(file.createdAtUtc)}>{formatRelative(file.createdAtUtc)}</td>
                      <td title={formatDateTime(file.indexedAtUtc)}>{formatRelative(file.indexedAtUtc)}</td>
                      <td>
                        <div className="row" style={{ gap: 6, flexWrap: 'nowrap' }}>
                          <a className="button-link" href={attachmentFileDownloadUrl(file.id)}><Download size={13} />Download</a>
                          <button disabled={working === file.id} onClick={() => void reindex(file.id)}>
                            <RotateCw size={13} />{working === file.id ? 'Indexing…' : 'Reindex'}
                          </button>
                          {file.isOrphan ? (
                            confirmDelete === file.id ? (
                              <>
                                <button className="ghost icon-only" title="Cancel" onClick={() => setConfirmDelete(null)}><X size={13} /></button>
                                <button className="danger" disabled={working === file.id} onClick={() => void removeOrphan(file.id)}>
                                  <Trash2 size={13} />Confirm
                                </button>
                              </>
                            ) : (
                              <button className="danger" onClick={() => setConfirmDelete(file.id)}>
                                <Trash2 size={13} />Delete orphan
                              </button>
                            )
                          ) : null}
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
          <Empty title="No attachment files" icon={<SearchX size={26} strokeWidth={1.5} />} detail="Files attached in chat will appear here." />
        )}
      </div>
    </div>
  )
}
