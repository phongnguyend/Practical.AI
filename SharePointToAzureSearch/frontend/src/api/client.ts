import type {
  DeltaStateRow,
  IndexStateSummary,
  IndexedFileQuery,
  IndexedFileRow,
  PagedResult,
  SearchMode,
  SearchPayload,
  SearchQueryResults,
  TimedSearch,
} from './types'

/** Empty by default, so requests go to the dev server's /api proxy on this same origin. */
const BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '')

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number | null,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

/**
 * The message to show for a failure. Minimal-API validation failures come back as `{ "error": ... }`,
 * and a request that never reached the API (the usual case — it is not running) has no status at all.
 */
async function toError(response: Response): Promise<ApiError> {
  let message = `${response.status} ${response.statusText}`
  try {
    const body = await response.json()
    if (body && typeof body === 'object') {
      const detail = (body as Record<string, unknown>).error ?? (body as Record<string, unknown>).title
      if (typeof detail === 'string' && detail.length > 0) {
        message = detail
      }
    }
  } catch {
    // A non-JSON error body leaves the status line as the message.
  }
  return new ApiError(message, response.status)
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      ...init,
      headers: { Accept: 'application/json', ...init?.headers },
    })
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause
    }
    throw new ApiError('Could not reach the API. Is SharePointToAzureSearch.Api running?', null)
  }

  if (!response.ok) {
    throw await toError(response)
  }
  return (await response.json()) as T
}

function query(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') {
      search.set(key, String(value))
    }
  }
  const text = search.toString()
  return text ? `?${text}` : ''
}

export function getSummary(signal?: AbortSignal): Promise<IndexStateSummary> {
  return request<IndexStateSummary>('/api/state/summary', { signal })
}

export function listIndexedFiles(
  options: IndexedFileQuery,
  signal?: AbortSignal,
): Promise<PagedResult<IndexedFileRow>> {
  return request<PagedResult<IndexedFileRow>>(
    `/api/state/indexed-files${query({ ...options })}`,
    { signal },
  )
}

export function listDeltaState(signal?: AbortSignal): Promise<DeltaStateRow[]> {
  return request<DeltaStateRow[]>('/api/state/delta', { signal })
}

export function search(
  mode: SearchMode,
  payload: SearchPayload,
  signal?: AbortSignal,
): Promise<SearchQueryResults> {
  return request<SearchQueryResults>(`/api/search/${mode}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...payload, userId: payload.userId || null }),
    signal,
  })
}

/**
 * Runs one strategy and keeps its timing and any failure alongside the results, so a comparison run
 * reports the strategy that failed instead of losing the two that succeeded.
 */
export async function timedSearch(
  mode: SearchMode,
  payload: SearchPayload,
  signal?: AbortSignal,
): Promise<TimedSearch> {
  const startedAt = performance.now()
  try {
    const results = await search(mode, payload, signal)
    return { mode, results, error: null, elapsedMs: performance.now() - startedAt }
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause
    }
    return {
      mode,
      results: null,
      error: cause instanceof Error ? cause.message : String(cause),
      elapsedMs: performance.now() - startedAt,
    }
  }
}
