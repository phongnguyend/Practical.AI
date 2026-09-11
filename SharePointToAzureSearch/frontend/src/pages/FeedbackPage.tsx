import { useState, type CSSProperties } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import {
  ExternalLink,
  MessageSquare,
  RefreshCw,
  Scale,
  SearchX,
  ThumbsDown,
  ThumbsUp,
  User,
} from 'lucide-react'
import { Link } from 'react-router-dom'
import { listFeedback } from '../api/client'
import type { ChatFeedback, FeedbackEntry } from '../api/types'
import { Empty, ErrorBanner, Field, LoadingBar, Pagination, StatTile } from '../components/ui'
import { FileTypeIcon } from '../components/FileTypeIcon'
import {
  folderLabel,
  formatDateTime,
  formatNumber,
  formatRelative,
  formatTokenUsage,
} from '../lib/format'
import { useAsync, useDebounced } from '../lib/useAsync'

type Filter = 'all' | ChatFeedback

const PAGE_SIZES = [10, 20, 50]

export default function FeedbackPage() {
  const [filter, setFilter] = useState<Filter>('all')
  const [search, setSearch] = useState('')
  const [skip, setSkip] = useState(0)
  const [top, setTop] = useState(20)

  const debouncedSearch = useDebounced(search)

  const page = useAsync(
    (signal) =>
      listFeedback(
        {
          feedback: filter === 'all' ? undefined : filter,
          search: debouncedSearch || undefined,
          skip,
          top,
        },
        signal,
      ),
    [filter, debouncedSearch, skip, top],
  )

  // Any change to what is being listed invalidates the current offset.
  const reset = <T,>(apply: (value: T) => void) => (value: T) => {
    setSkip(0)
    apply(value)
  }

  const data = page.data
  const rated = (data?.liked ?? 0) + (data?.disliked ?? 0)

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>
            <Scale size={20} />
            Feedback
          </h1>
          <p>
            Every answer a reader rated in the chat, newest first — the question that prompted it, what
            the assistant said, and which documents it used. Useful for spotting the questions the
            index does not answer well.
          </p>
        </div>
        <button onClick={page.reload}>
          <RefreshCw size={14} />
          Refresh
        </button>
      </div>

      {data ? (
        <div className="tiles">
          <StatTile label="Rated answers" value={formatNumber(rated)} icon={<Scale size={14} />} />
          <StatTile label="Liked" value={formatNumber(data.liked)} icon={<ThumbsUp size={14} />} />
          <StatTile
            label="Disliked"
            value={formatNumber(data.disliked)}
            icon={<ThumbsDown size={14} />}
            attention={data.disliked > 0}
          />
          <StatTile
            label="Liked share"
            value={rated === 0 ? '—' : `${Math.round((data.liked / rated) * 100)}%`}
            hint={rated === 0 ? 'Nothing rated yet' : `of ${formatNumber(rated)} rated answers`}
          />
        </div>
      ) : null}

      <div className="card">
        <div
          className="card-body field-bar"
          style={{ '--bar-columns': 'minmax(220px, 1fr) auto 90px' } as CSSProperties}
        >
          <Field label="Filter" help="Matches the answer or the conversation title">
            <input
              type="search"
              placeholder="logging"
              value={search}
              onChange={(event) => reset(setSearch)(event.target.value)}
            />
          </Field>
          <Field label="Rating">
            <div className="segmented" role="group" aria-label="Rating">
              <button aria-pressed={filter === 'all'} onClick={() => reset(setFilter)('all')}>
                All
              </button>
              <button aria-pressed={filter === 'Like'} onClick={() => reset(setFilter)('Like')}>
                <ThumbsUp size={13} />
                Liked
              </button>
              <button aria-pressed={filter === 'Dislike'} onClick={() => reset(setFilter)('Dislike')}>
                <ThumbsDown size={13} />
                Disliked
              </button>
            </div>
          </Field>
          <Field label="Rows">
            <select value={top} onChange={(event) => reset(setTop)(Number(event.target.value))}>
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

      {data && data.items.length > 0 ? (
        <>
          <div className="stack" style={{ gap: 12 }}>
            {data.items.map((entry) => (
              <FeedbackCard key={entry.messageId} entry={entry} />
            ))}
          </div>
          <div className="card">
            <Pagination skip={skip} top={top} total={data.totalCount} onSkip={setSkip} />
          </div>
        </>
      ) : page.loading ? null : (
        <div className="card">
          <Empty
            title={debouncedSearch || filter !== 'all' ? 'Nothing matches' : 'No feedback yet'}
            icon={<SearchX size={26} strokeWidth={1.5} />}
            detail={
              debouncedSearch || filter !== 'all'
                ? 'No rated answer matches this filter.'
                : 'Ratings appear here once someone uses the thumbs on a chat answer.'
            }
          />
        </div>
      )}
    </div>
  )
}

function FeedbackCard({ entry }: { entry: FeedbackEntry }) {
  const [expanded, setExpanded] = useState(false)
  const liked = entry.feedback === 'Like'

  return (
    <div className="card">
      <div className="card-head">
        <h2>
          {liked ? (
            <ThumbsUp size={15} color="var(--good)" />
          ) : (
            <ThumbsDown size={15} color="var(--critical)" />
          )}
          <span className={liked ? 'badge good' : 'badge critical'}>
            {liked ? 'Liked' : 'Disliked'}
          </span>
          <Link
            to={`/chat?conversation=${entry.conversationId}&message=${entry.messageId}`}
            className="truncate wide"
            title={`Jump to this answer in "${entry.conversationTitle}"`}
          >
            <MessageSquare size={13} style={{ verticalAlign: -2, marginRight: 5 }} />
            {entry.conversationTitle}
          </Link>
        </h2>
        <span
          className="hint"
          title={formatDateTime(entry.createdAtUtc)}
        >
          {formatRelative(entry.createdAtUtc)}
          {entry.totalTokenCount > 0
            ? ` · ${formatTokenUsage(entry.totalTokenCount, entry.inputTokenCount, entry.outputTokenCount)}`
            : ''}
        </span>
      </div>

      <div className="card-body stack" style={{ gap: 12 }}>
        {entry.question ? (
          <div className="feedback-question">
            <User size={13} />
            <span>{entry.question}</span>
          </div>
        ) : null}

        <div
          className={expanded ? 'markdown' : 'markdown feedback-answer'}
          onClick={() => setExpanded((value) => !value)}
          title={expanded ? 'Click to collapse' : 'Click to expand'}
        >
          <ReactMarkdown remarkPlugins={[remarkGfm]}>{entry.answer}</ReactMarkdown>
        </div>

        {entry.citations.length > 0 ? (
          <details className="chat-citations">
            <summary>
              {entry.citations.length} {entry.citations.length === 1 ? 'source' : 'sources'}
            </summary>
            <ul>
              {entry.citations.map((citation, index) => (
                <li key={`${citation.name}:${citation.chunkNumber}:${index}`}>
                  <FileTypeIcon name={citation.name} size={14} />
                  <span className="chat-citation-name">
                    {citation.webUrl ? (
                      <a href={citation.webUrl} target="_blank" rel="noreferrer">
                        {citation.name}
                        <ExternalLink size={11} style={{ marginLeft: 4, verticalAlign: -1 }} />
                      </a>
                    ) : (
                      citation.name
                    )}
                  </span>
                  <span className="chat-citation-meta" title={citation.path ?? undefined}>
                    {folderLabel(citation.path)} · chunk {citation.chunkNumber}
                  </span>
                </li>
              ))}
            </ul>
          </details>
        ) : null}
      </div>
    </div>
  )
}
