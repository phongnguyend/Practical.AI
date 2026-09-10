import { useState, type CSSProperties } from 'react'
import {
  ArrowDown,
  ArrowUp,
  ExternalLink,
  FileText,
  ListFilter,
  RefreshCw,
  SearchX,
  X,
} from 'lucide-react'
import { listIndexedFiles } from '../api/client'
import { FileTypeIcon } from '../components/FileTypeIcon'
import type { IndexedFileRow, SortKey } from '../api/types'
import { CopyButton, Empty, ErrorBanner, Field, LoadingBar, Pagination } from '../components/ui'
import {
  fileExtension,
  formatBytes,
  formatDateTime,
  formatNumber,
  formatRelative,
  folderLabel,
  mimeTypeLabel,
} from '../lib/format'
import { useAsync, useDebounced } from '../lib/useAsync'

// Widths are shares of the table, which is laid out fixed so that a long folder path or content type
// is ellipsised rather than widening the table past its card.
const COLUMNS: { key: SortKey; label: string; width: string; numeric?: boolean }[] = [
  { key: 'name', label: 'Name', width: '21%' },
  { key: 'path', label: 'Folder', width: '16%' },
  { key: 'mimeType', label: 'Content type', width: '14%' },
  { key: 'size', label: 'Size', width: '8%', numeric: true },
  { key: 'chunkCount', label: 'Chunks', width: '10%', numeric: true },
  { key: 'lastModifiedUtc', label: 'Modified', width: '15.5%', numeric: true },
  { key: 'indexedAtUtc', label: 'Indexed', width: '15.5%', numeric: true },
]

const PAGE_SIZES = [10, 25, 50, 100]

export default function IndexedFilesPage() {
  const [search, setSearch] = useState('')
  const [driveId, setDriveId] = useState('')
  const [sort, setSort] = useState<SortKey>('indexedAtUtc')
  const [desc, setDesc] = useState(true)
  const [skip, setSkip] = useState(0)
  const [top, setTop] = useState(25)
  const [selected, setSelected] = useState<IndexedFileRow | null>(null)

  const debouncedSearch = useDebounced(search)
  const debouncedDriveId = useDebounced(driveId)

  const page = useAsync(
    (signal) =>
      listIndexedFiles(
        { search: debouncedSearch, driveId: debouncedDriveId, sort, desc, skip, top },
        signal,
      ),
    [debouncedSearch, debouncedDriveId, sort, desc, skip, top],
  )

  // Any change to what is being listed invalidates the current offset, so filtering returns to page one.
  const filterAnd = <T,>(apply: (value: T) => void) => (value: T) => {
    setSkip(0)
    apply(value)
  }

  const sortBy = (key: SortKey) => {
    if (key === sort) {
      setDesc((value) => !value)
    } else {
      setSort(key)
      setDesc(key === 'indexedAtUtc' || key === 'lastModifiedUtc' || key === 'size' || key === 'chunkCount')
    }
    setSkip(0)
  }

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>
            <FileText size={20} />
            Indexed files
          </h1>
          <p>
            The <code>SharePointIndexedFiles</code> table — what was last written to the search index
            for each file, and the tags the next delta pass compares against.
          </p>
        </div>
        <button onClick={page.reload}>
          <RefreshCw size={14} />
          Refresh
        </button>
      </div>

      <div className="card">
        <div
          className="card-body field-bar"
          style={{ '--bar-columns': 'minmax(220px, 1fr) 220px 90px' } as CSSProperties}
        >
          <Field label="Filter" help="Matches name, folder, URL, content type, or item ID">
            <input
              type="search"
              placeholder="quarterly-report.docx"
              value={search}
              onChange={(event) => filterAnd(setSearch)(event.target.value)}
            />
          </Field>
          <Field label="Drive ID" help="Exact match; blank for all drives">
            <input
              type="text"
              placeholder="all drives"
              value={driveId}
              onChange={(event) => filterAnd(setDriveId)(event.target.value)}
            />
          </Field>
          <Field label="Rows">
            <select
              value={top}
              onChange={(event) => filterAnd(setTop)(Number(event.target.value))}
            >
              {PAGE_SIZES.map((size) => (
                <option key={size} value={size}>
                  {size}
                </option>
              ))}
            </select>
          </Field>
        </div>
      </div>

      <LoadingBar active={page.loading} />

      {page.error ? <ErrorBanner message={page.error} onRetry={page.reload} /> : null}

      {/* The detail panel only claims its column once a row is selected, so the table has the whole
          width while it is being scanned. */}
      <div className={selected ? 'split' : undefined}>
        <div className="card">
          {page.data && page.data.items.length > 0 ? (
            <>
              <div className="card-head">
                <h2>
                  <ListFilter size={15} />
                  {page.data.totalCount.toLocaleString()}{' '}
                  {page.data.totalCount === 1 ? 'row' : 'rows'}
                </h2>
                <span className="hint">Select a row for every recorded column</span>
              </div>
              <div className="table-scroll">
                <table className="table-fixed">
                  <colgroup>
                    {COLUMNS.map((column) => (
                      <col key={column.key} style={{ width: column.width }} />
                    ))}
                  </colgroup>
                  <thead>
                    <tr>
                      {COLUMNS.map((column) => (
                        <th
                          key={column.key}
                          className={`sortable${column.numeric ? ' num' : ''}`}
                          onClick={() => sortBy(column.key)}
                          aria-sort={
                            sort === column.key
                              ? desc
                                ? 'descending'
                                : 'ascending'
                              : 'none'
                          }
                        >
                          {column.label}
                          {sort === column.key ? (
                            <span className="arrow">
                              {desc ? <ArrowDown size={12} /> : <ArrowUp size={12} />}
                            </span>
                          ) : null}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {page.data.items.map((file) => {
                      const key = `${file.driveId}:${file.itemId}`
                      const isSelected =
                        selected?.itemId === file.itemId && selected?.driveId === file.driveId
                      return (
                        <tr
                          key={key}
                          className={isSelected ? 'clickable selected' : 'clickable'}
                          onClick={() => setSelected(isSelected ? null : file)}
                        >
                          <td>
                            <span className="file-name">
                              <FileTypeIcon name={file.name} mimeType={file.mimeType} size={15} />
                              <span className="truncate" title={file.name}>
                                {file.name}
                              </span>
                            </span>
                          </td>
                          <td>
                            <span className="truncate" title={file.parentPath ?? undefined}>
                              {folderLabel(file.parentPath)}
                            </span>
                          </td>
                          <td>
                            <span className="truncate" title={file.mimeType ?? undefined}>
                              {mimeTypeLabel(file.mimeType)}
                            </span>
                          </td>
                          <td className="num">{formatBytes(file.size)}</td>
                          <td className="num">{formatNumber(file.chunkCount)}</td>
                          <td className="num" title={formatDateTime(file.lastModifiedUtc)}>
                            {formatRelative(file.lastModifiedUtc)}
                          </td>
                          <td className="num" title={formatDateTime(file.indexedAtUtc)}>
                            {formatRelative(file.indexedAtUtc)}
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
              <Pagination skip={skip} top={top} total={page.data.totalCount} onSkip={setSkip} />
            </>
          ) : page.loading ? (
            <Empty title="Loading…" />
          ) : (
            <Empty
              title="No rows"
              icon={<SearchX size={26} strokeWidth={1.5} />}
              detail={
                debouncedSearch || debouncedDriveId
                  ? 'No indexed file matches this filter.'
                  : 'The table is empty until the worker completes a delta pass.'
              }
            />
          )}
        </div>

        {selected ? <FileDetail file={selected} onClose={() => setSelected(null)} /> : null}
      </div>
    </div>
  )
}

function FileDetail({ file, onClose }: { file: IndexedFileRow; onClose: () => void }) {
  return (
    <div className="card">
      <div className="card-head">
        <h2>
          <FileText size={15} />
          Row detail
        </h2>
        <button className="ghost" onClick={onClose}>
          <X size={14} />
          Close
        </button>
      </div>
      <div className="card-body">
        <dl className="detail-grid">
          <dt>Name</dt>
          <dd>
            <span className="file-name top">
              <FileTypeIcon name={file.name} mimeType={file.mimeType} size={15} />
              <span>{file.name}</span>
            </span>
          </dd>

          <dt>Folder</dt>
          <dd title={file.parentPath ?? undefined}>{folderLabel(file.parentPath)}</dd>

          <dt>Web URL</dt>
          <dd>
            {file.webUrl ? (
              <a
                href={file.webUrl}
                target="_blank"
                rel="noreferrer"
                style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}
              >
                Open in SharePoint
                <ExternalLink size={13} />
              </a>
            ) : (
              '—'
            )}
          </dd>

          <dt>Content type</dt>
          <dd title={file.mimeType ?? undefined}>{mimeTypeLabel(file.mimeType)}</dd>

          <dt>Extension</dt>
          <dd className="mono">{fileExtension(file.name) ?? '—'}</dd>

          <dt>Size</dt>
          <dd>{formatBytes(file.size)}</dd>

          <dt>Chunks</dt>
          <dd>{formatNumber(file.chunkCount)}</dd>

          <dt>Modified</dt>
          <dd>{formatDateTime(file.lastModifiedUtc)}</dd>

          <dt>Indexed</dt>
          <dd>
            {formatDateTime(file.indexedAtUtc)}{' '}
            <span style={{ color: 'var(--text-muted)' }}>({formatRelative(file.indexedAtUtc)})</span>
          </dd>

        </dl>

        {/* The identifiers are 36-70 characters of unbroken text — too long for the value column of a
            two-column grid in a panel this narrow, so each gets the panel's whole width. */}
        <div className="detail-blocks">
          <DetailBlock
            label="ETag"
            hint="Changes when either the content or the metadata changes"
            value={file.eTag}
          />
          <DetailBlock
            label="CTag"
            hint="Changes only when the content changes"
            value={file.cTag}
          />
          <DetailBlock
            label="Permissions hash"
            hint="Hash of the permission snapshot stored on the chunks"
            value={file.permissionsHash}
          />
          <DetailBlock
            label="Fingerprint"
            hint="The chunking and embedding settings that produced the chunks"
            value={file.indexFingerprint}
          />
          <DetailBlock
            label="Scan ID"
            hint="The reconciliation round this file was last seen in"
            value={file.scanId}
            copyable
          />
          <DetailBlock label="Drive ID" value={file.driveId} copyable />
          <DetailBlock label="Item ID" value={file.itemId} copyable />
        </div>
      </div>
    </div>
  )
}

function DetailBlock({
  label,
  hint,
  value,
  copyable = false,
}: {
  label: string
  hint?: string
  value: string | null
  copyable?: boolean
}) {
  return (
    <div className="detail-block">
      <div className="detail-block-head">
        <span title={hint}>{label}</span>
        {copyable && value ? <CopyButton value={value} /> : null}
      </div>
      <code>{value ?? '—'}</code>
    </div>
  )
}
