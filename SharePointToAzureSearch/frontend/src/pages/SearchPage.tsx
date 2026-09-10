import { useEffect, useMemo, useState, type CSSProperties, type ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import {
  Boxes,
  ChevronLeft,
  ChevronRight,
  Columns3,
  FileText,
  Hash,
  Layers,
  Search as SearchIcon,
  SearchX,
  Sparkles,
  Split,
  Timer,
  Type,
} from 'lucide-react'
import { timedSearch } from '../api/client'
import { FileTypeIcon } from '../components/FileTypeIcon'
import {
  SEARCH_MODES,
  SEARCH_MODE_DESCRIPTIONS,
  SEARCH_MODE_LABELS,
  type SearchMode,
  type SearchPayload,
  type SearchQueryHit,
  type TimedSearch,
} from '../api/types'
import { Empty, ErrorBanner, Field, LoadingBar } from '../components/ui'
import {
  folderLabel,
  formatBytes,
  formatDuration,
  formatNumber,
  formatScore,
} from '../lib/format'
import { useAsync } from '../lib/useAsync'

type Selection = SearchMode | 'compare'

function isSelection(value: string | null): value is Selection {
  return value === 'compare' || SEARCH_MODES.includes(value as SearchMode)
}

/** Keyword matching, nearest-neighbour, and the fusion of the two. */
const MODE_ICONS: Record<SearchMode, typeof Type> = {
  fulltext: Type,
  vector: Sparkles,
  hybrid: Layers,
}

export default function SearchPage() {
  // The executed search lives in the URL, so a result is a link somebody can send on, and the browser's
  // back button steps through searches rather than leaving the page.
  const [params, setParams] = useSearchParams()
  const executed = useMemo(() => {
    const raw = params.get('mode')
    return {
      query: params.get('q') ?? '',
      userId: params.get('userId') ?? '',
      top: clamp(Number(params.get('top') ?? 10), 1, 100),
      skip: Math.max(0, Number(params.get('skip') ?? 0) || 0),
      selection: isSelection(raw) ? raw : ('compare' as Selection),
    }
  }, [params])

  const [query, setQuery] = useState(executed.query)
  const [userId, setUserId] = useState(executed.userId)
  const [top, setTop] = useState(executed.top)
  const [skip, setSkip] = useState(executed.skip)
  const [selection, setSelection] = useState<Selection>(executed.selection)

  // Keeps the form showing the search that is on screen when the URL changes underneath it — a back
  // navigation, or a pasted link.
  useEffect(() => {
    setQuery(executed.query)
    setUserId(executed.userId)
    setTop(executed.top)
    setSkip(executed.skip)
    setSelection(executed.selection)
  }, [executed])

  const run = useAsync(
    async (signal): Promise<TimedSearch[] | null> => {
      if (!executed.query) return null

      const payload: SearchPayload = {
        query: executed.query,
        userId: executed.userId || null,
        top: executed.top,
        skip: executed.skip,
      }
      // The three strategies are independent requests, so a comparison issues them together and the
      // reported time is each one's own round trip rather than the sum.
      const modes = executed.selection === 'compare' ? SEARCH_MODES : [executed.selection]
      return Promise.all(modes.map((mode) => timedSearch(mode, payload, signal)))
    },
    [executed],
  )

  const submit = (nextSkip = skip) => {
    if (!query.trim()) return
    setParams({
      q: query.trim(),
      mode: selection,
      top: String(top),
      skip: String(nextSkip),
      ...(userId.trim() ? { userId: userId.trim() } : {}),
    })
  }

  const terms = useMemo(() => splitTerms(executed.query), [executed.query])
  const results = run.data ?? []
  const comparing = results.length > 1
  const hasRun = executed.query.length > 0

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>
            <SearchIcon size={20} />
            Search
          </h1>
          <p>
            The same request body over the three retrieval strategies. Run one, or run all three at
            once to see where keyword and vector retrieval disagree.
          </p>
        </div>
      </div>

      <div className="card">
        <div className="card-body stack" style={{ gap: 14 }}>
          <div
            className="field-bar"
            style={{ '--bar-columns': 'minmax(240px, 1fr) 240px 80px 80px' } as CSSProperties}
          >
            <Field label="Query">
              <input
                type="search"
                placeholder="quarterly revenue"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') submit(0)
                }}
              />
            </Field>
            <Field
              label="User ID"
              help="Optional — object ID or UPN. Blank searches unfiltered."
            >
              <input
                type="text"
                placeholder="(no permission filter)"
                value={userId}
                onChange={(event) => setUserId(event.target.value)}
              />
            </Field>
            <Field label="Top">
              <input
                type="number"
                min={1}
                max={100}
                value={top}
                onChange={(event) => setTop(clamp(Number(event.target.value), 1, 100))}
              />
            </Field>
            <Field label="Skip">
              <input
                type="number"
                min={0}
                value={skip}
                onChange={(event) => setSkip(Math.max(0, Number(event.target.value)))}
              />
            </Field>
          </div>

          <div className="row spread">
            <div className="segmented" role="group" aria-label="Retrieval strategy">
              {SEARCH_MODES.map((mode) => {
                const Icon = MODE_ICONS[mode]
                return (
                  <button
                    key={mode}
                    aria-pressed={selection === mode}
                    title={SEARCH_MODE_DESCRIPTIONS[mode]}
                    onClick={() => setSelection(mode)}
                  >
                    <Icon size={14} />
                    {SEARCH_MODE_LABELS[mode]}
                  </button>
                )
              })}
              <button
                aria-pressed={selection === 'compare'}
                title="Run all three against the same request and compare"
                onClick={() => setSelection('compare')}
              >
                <Columns3 size={14} />
                Compare all three
              </button>
            </div>
            <div className="row" style={{ gap: 8 }}>
              {hasRun ? (
                <>
                  <button onClick={() => submit(Math.max(0, skip - top))} disabled={skip === 0}>
                    <ChevronLeft size={14} />
                    Previous
                  </button>
                  <button onClick={() => submit(skip + top)}>
                    Next
                    <ChevronRight size={14} />
                  </button>
                </>
              ) : null}
              <button className="primary" onClick={() => submit(0)} disabled={!query.trim()}>
                <SearchIcon size={14} />
                Search
              </button>
            </div>
          </div>
        </div>
      </div>

      <LoadingBar active={hasRun && run.loading} />

      {run.error ? <ErrorBanner message={run.error} onRetry={run.reload} /> : null}

      {!hasRun ? (
        <div className="card">
          <Empty
            title="No search run yet"
            icon={<SearchIcon size={26} strokeWidth={1.5} />}
            detail="Enter a query and pick a strategy. Vector and hybrid embed the query with the same Azure OpenAI deployment used at indexing time."
          />
        </div>
      ) : null}

      {comparing && results.length > 0 ? <OverlapPanel runs={results} /> : null}

      {results.length > 0 ? (
        <div className={comparing ? 'mode-grid' : undefined}>
          {results.map((result) => (
            <ModeResults key={result.mode} result={result} terms={terms} compact={comparing} />
          ))}
        </div>
      ) : null}
    </div>
  )
}

function ModeResults({
  result,
  terms,
  compact,
}: {
  result: TimedSearch
  terms: string[]
  compact: boolean
}) {
  const Icon = MODE_ICONS[result.mode]

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h2>
            <Icon size={15} />
            {SEARCH_MODE_LABELS[result.mode]}
          </h2>
          <div className="hint">{SEARCH_MODE_DESCRIPTIONS[result.mode]}</div>
        </div>
        <div className="row" style={{ gap: 6 }}>
          <span className="badge accent">
            <Timer size={12} />
            {formatDuration(result.elapsedMs)}
          </span>
          {result.results ? (
            <span className="badge">
              <FileText size={12} />
              {formatNumber(result.results.items.length)} of{' '}
              {result.results.totalCount === null
                ? '?'
                : formatNumber(result.results.totalCount)}
            </span>
          ) : null}
        </div>
      </div>

      {result.error ? (
        <div className="card-body">
          <ErrorBanner message={result.error} />
        </div>
      ) : result.results && result.results.items.length > 0 ? (
        <div>
          {result.results.items.map((hit, index) => (
            <Hit key={hit.id} hit={hit} rank={index + 1} terms={terms} compact={compact} />
          ))}
        </div>
      ) : (
        <Empty
          title="No matches"
          icon={<SearchX size={26} strokeWidth={1.5} />}
          detail="Nothing in the index matched this query."
        />
      )}
    </div>
  )
}

function Hit({
  hit,
  rank,
  terms,
  compact,
}: {
  hit: SearchQueryHit
  rank: number
  terms: string[]
  compact: boolean
}) {
  const [expanded, setExpanded] = useState(false)

  return (
    <div className="hit">
      <div className="hit-head">
        <span className="hit-rank">{rank}</span>
        <FileTypeIcon name={hit.name} mimeType={hit.mimeType} size={15} />
        <span className="hit-name">
          {hit.webUrl ? (
            <a href={hit.webUrl} target="_blank" rel="noreferrer">
              {hit.name}
            </a>
          ) : (
            hit.name
          )}
        </span>
      </div>
      <div className="hit-meta">
        <span title="Relevance score">score {formatScore(hit.score)}</span>
        <span>
          <Hash size={11} style={{ verticalAlign: -1 }} /> chunk {hit.chunkNumber}
        </span>
        {!compact && hit.path ? <span title={hit.path}>{folderLabel(hit.path)}</span> : null}
        {!compact ? <span>{formatBytes(hit.size)}</span> : null}
      </div>
      <div
        className={expanded ? 'hit-snippet expanded' : 'hit-snippet'}
        onClick={() => setExpanded((value) => !value)}
        title={expanded ? 'Click to collapse' : 'Click to expand'}
      >
        {highlight(hit.content, terms)}
      </div>
    </div>
  )
}

/**
 * How much the three strategies agree. Overlap is counted on chunk IDs, the unit a strategy actually
 * ranks, and on the distinct files those chunks come from.
 */
function OverlapPanel({ runs }: { runs: TimedSearch[] }) {
  const succeeded = runs.filter((run) => run.results !== null)
  if (succeeded.length < 2) return null

  const chunkModes = new Map<string, Set<SearchMode>>()
  const files = new Set<string>()
  for (const run of succeeded) {
    for (const hit of run.results!.items) {
      const modes = chunkModes.get(hit.id) ?? new Set<SearchMode>()
      modes.add(run.mode)
      chunkModes.set(hit.id, modes)
      files.add(`${hit.driveId}:${hit.itemId}`)
    }
  }

  const inAll = [...chunkModes.values()].filter((modes) => modes.size === succeeded.length).length
  const inOne = [...chunkModes.values()].filter((modes) => modes.size === 1).length

  return (
    <div className="tiles compact">
      <StatCell
        label="Distinct chunks"
        value={formatNumber(chunkModes.size)}
        icon={<Boxes size={14} />}
      />
      <StatCell
        label="Distinct files"
        value={formatNumber(files.size)}
        icon={<FileText size={14} />}
      />
      <StatCell
        label={`Returned by all ${succeeded.length}`}
        value={formatNumber(inAll)}
        icon={<Layers size={14} />}
        hint="Chunks every strategy agreed on"
      />
      <StatCell
        label="Returned by one only"
        value={formatNumber(inOne)}
        icon={<Split size={14} />}
        hint="Where the strategies disagree"
      />
      {succeeded.map((run) => {
        const Icon = MODE_ICONS[run.mode]
        return (
          <StatCell
            key={run.mode}
            label={`${SEARCH_MODE_LABELS[run.mode]} only`}
            value={formatNumber(
              run.results!.items.filter((hit) => chunkModes.get(hit.id)?.size === 1).length,
            )}
            icon={<Icon size={14} />}
            hint={`${formatDuration(run.elapsedMs)} round trip`}
          />
        )
      })}
    </div>
  )
}

function StatCell({
  label,
  value,
  hint,
  icon,
}: {
  label: string
  value: string
  hint?: string
  icon?: ReactNode
}) {
  return (
    <div className="tile">
      <div className="label">
        {icon}
        {label}
      </div>
      <div className="value">{value}</div>
      {hint ? <div className="hint">{hint}</div> : null}
    </div>
  )
}

function clamp(value: number, min: number, max: number): number {
  return Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : min
}

function splitTerms(query: string): string[] {
  return [...new Set(query.toLowerCase().split(/\s+/).filter((term) => term.length > 2))]
}

const REGEX_SPECIALS = /[.*+?^${}()|[\]\\]/g

/**
 * Marks the query's own words in a snippet. This is a reading aid over the returned text, not the
 * service's highlighting — a vector hit can match with none of these words present, which is the
 * point worth seeing.
 */
function highlight(text: string, terms: string[]): ReactNode {
  if (terms.length === 0) return text

  const pattern = new RegExp(`(${terms.map((term) => term.replace(REGEX_SPECIALS, '\\$&')).join('|')})`, 'gi')
  return text.split(pattern).map((part, index) =>
    index % 2 === 1 ? <mark key={index}>{part}</mark> : part,
  )
}
