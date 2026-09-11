import type {
  AgentDefinition,
  ChatConversation,
  ChatFeedback,
  ChatStreamEvent,
  ChatThread,
  ChatTurnResult,
  DeltaStateRow,
  FeedbackPage,
  IndexStateSummary,
  IndexedFileQuery,
  IndexedFileRow,
  PagedResult,
  SearchMode,
  SearchPayload,
  SearchQueryResults,
  SubscriptionOverview,
  SubscriptionView,
  UpdateSubscriptionResult,
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

export function listConversations(signal?: AbortSignal): Promise<ChatConversation[]> {
  return request<ChatConversation[]>('/api/chat/conversations', { signal })
}

export function createConversation(options?: {
  userId?: string | null
  agentId?: string | null
}): Promise<ChatConversation> {
  return request<ChatConversation>('/api/chat/conversations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      title: null,
      userId: options?.userId?.trim() || null,
      agentId: options?.agentId || null,
    }),
  })
}

export function deleteConversation(id: string): Promise<{ deleted: string }> {
  return request<{ deleted: string }>(`/api/chat/conversations/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
}

export function getThread(id: string, signal?: AbortSignal): Promise<ChatThread> {
  return request<ChatThread>(`/api/chat/conversations/${encodeURIComponent(id)}/messages`, { signal })
}

/** Runs one turn and reports text and tool progress as newline-delimited JSON arrives. */
export async function sendChatMessage(
  id: string,
  content: string,
  onEvent: (event: ChatStreamEvent) => void,
  signal?: AbortSignal,
): Promise<ChatTurnResult> {
  let response: Response
  try {
    response = await fetch(
      `${BASE_URL}/api/chat/conversations/${encodeURIComponent(id)}/messages`,
      {
        method: 'POST',
        headers: {
          Accept: 'application/x-ndjson',
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ content }),
        signal,
      },
    )
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause
    throw new ApiError('Could not reach the API. Is SharePointToAzureSearch.Api running?', null)
  }

  if (!response.ok) throw await toError(response)
  if (!response.body) throw new ApiError('The API returned no response stream.', response.status)

  let question: ChatTurnResult['question'] | null = null
  let answer: ChatTurnResult['answer'] | null = null
  let title = ''
  let buffer = ''
  const decoder = new TextDecoder()

  const acceptLine = (line: string) => {
    if (!line.trim()) return
    const event = JSON.parse(line) as ChatStreamEvent
    onEvent(event)

    if (event.type === 'started') {
      question = event.question
      title = event.title
    } else if (event.type === 'completed') {
      answer = event.answer
      title = event.title
    } else if (event.type === 'error') {
      throw new ApiError(event.message, response.status)
    }
  }

  const reader = response.body.getReader()
  while (true) {
    const { value, done } = await reader.read()
    if (done) break

    buffer += decoder.decode(value, { stream: true })
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    for (const line of lines) acceptLine(line)
  }

  buffer += decoder.decode()
  acceptLine(buffer)

  if (!question || !answer) {
    throw new ApiError('The assistant stream ended before the turn completed.', response.status)
  }

  return { question, answer, title }
}

export function listAgents(signal?: AbortSignal): Promise<AgentDefinition[]> {
  return request<AgentDefinition[]>('/api/agents', { signal })
}

export function getAgent(id: string, signal?: AbortSignal): Promise<AgentDefinition> {
  return request<AgentDefinition>(`/api/agents/${encodeURIComponent(id)}`, { signal })
}

export function getDefaultAgentInstructions(signal?: AbortSignal): Promise<{ instructions: string }> {
  return request<{ instructions: string }>('/api/agents/default-instructions', { signal })
}

export function createAgent(name: string, instructions?: string): Promise<AgentDefinition> {
  return request<AgentDefinition>('/api/agents', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, instructions: instructions?.trim() || null }),
  })
}

export function updateAgent(
  id: string,
  name: string,
  instructions: string,
): Promise<AgentDefinition> {
  return request<AgentDefinition>(`/api/agents/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, instructions }),
  })
}

export function listFeedback(
  options: { feedback?: ChatFeedback; search?: string; skip?: number; top?: number },
  signal?: AbortSignal,
): Promise<FeedbackPage> {
  return request<FeedbackPage>(`/api/chat/feedback${query({ ...options })}`, { signal })
}

/** Records a reaction to one answer, or clears it with null. */
export function setMessageFeedback(
  messageId: string,
  feedback: ChatFeedback | null,
): Promise<{ id: string; feedback: ChatFeedback | null }> {
  return request(`/api/chat/messages/${encodeURIComponent(messageId)}/feedback`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ feedback }),
  })
}

export function listSubscriptions(signal?: AbortSignal): Promise<SubscriptionOverview> {
  return request<SubscriptionOverview>('/api/subscriptions', { signal })
}

/**
 * Omitting `days` uses the API's configured `SharePoint:SubscriptionLifetimeDays`, and omitting
 * `notificationUrl` uses its configured `SharePoint:NotificationUrl`.
 */
export function createSubscription(
  days?: number,
  notificationUrl?: string,
): Promise<SubscriptionView> {
  return request<SubscriptionView>('/api/subscriptions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      days: days ?? null,
      notificationUrl: notificationUrl?.trim() || null,
    }),
  })
}

export function renewSubscription(id: string, days?: number): Promise<SubscriptionView> {
  return request<SubscriptionView>(`/api/subscriptions/${encodeURIComponent(id)}/renew`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ days: days ?? null }),
  })
}

/**
 * Changes a subscription's lifetime and, when `notificationUrl` differs, its endpoint. Graph cannot
 * PATCH a URL, so the API replaces the subscription — the result says whether it did.
 */
export function updateSubscription(
  id: string,
  days: number,
  notificationUrl: string,
): Promise<UpdateSubscriptionResult> {
  return request<UpdateSubscriptionResult>(`/api/subscriptions/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ days, notificationUrl: notificationUrl.trim() || null }),
  })
}

export function deleteSubscription(id: string): Promise<{ deleted: string }> {
  return request<{ deleted: string }>(`/api/subscriptions/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
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
