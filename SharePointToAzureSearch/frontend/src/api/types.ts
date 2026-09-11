/** Mirrors the records the API returns. Property names match its camelCase JSON. */

export interface AgentDefinition {
  id: string
  name: string
  modelId: string
  instructions: string
  createdAtUtc: string
  updatedAtUtc: string
}

export interface PagedResult<T> {
  totalCount: number
  items: T[]
}

/** A row of the `SharePointIndexedFiles` table. */
export interface IndexedFileRow {
  driveId: string
  itemId: string
  name: string
  parentPath: string | null
  webUrl: string | null
  mimeType: string | null
  size: number | null
  lastModifiedUtc: string | null
  eTag: string | null
  cTag: string | null
  permissionsHash: string
  indexFingerprint: string
  chunkCount: number
  scanId: string
  indexedAtUtc: string
}

/** A row of the `SharePointDeltaState` table. */
export interface DeltaStateRow {
  driveId: string
  deltaLink: string
  scanId: string
  sweptScanId: string | null
  updatedAtUtc: string
}

export interface MimeTypeCount {
  mimeType: string | null
  fileCount: number
  chunkCount: number
  sizeBytes: number | null
}

export interface IndexStateSummary {
  totalFiles: number
  totalChunks: number
  totalSizeBytes: number | null
  distinctDrives: number
  filesOutsideCurrentScan: number
  distinctIndexFingerprints: number
  oldestIndexedAtUtc: string | null
  newestIndexedAtUtc: string | null
  byMimeType: MimeTypeCount[]
}

export type SortKey =
  | 'name'
  | 'path'
  | 'mimeType'
  | 'size'
  | 'lastModifiedUtc'
  | 'chunkCount'
  | 'indexedAtUtc'

export interface IndexedFileQuery {
  search?: string
  driveId?: string
  sort?: SortKey
  desc?: boolean
  skip?: number
  top?: number
}

export interface ChatConversation {
  id: string
  title: string
  /** When set, every search the assistant runs in this conversation is filtered to that user. */
  userId: string | null
  /** The persisted agent whose instructions govern this conversation. Null means the default agent. */
  agentId: string | null
  createdAtUtc: string
  updatedAtUtc: string
  messageCount: number
  inputTokenCount: number
  outputTokenCount: number
  totalTokenCount: number
}

/** A document excerpt the assistant retrieved to answer with. */
export interface ChatCitation {
  name: string
  path: string | null
  webUrl: string | null
  chunkNumber: number
  score: number | null
}

export type ChatFeedback = 'Like' | 'Dislike'

export interface ChatMessage {
  id: string
  conversationId: string
  role: 'User' | 'Assistant'
  content: string
  citations: ChatCitation[]
  inputTokenCount: number
  outputTokenCount: number
  totalTokenCount: number
  modelId: string | null
  /** What the reader thought of the answer, null until they say. */
  feedback: ChatFeedback | null
  createdAtUtc: string
}

/** One rated answer, with the question that prompted it. */
export interface FeedbackEntry {
  messageId: string
  conversationId: string
  conversationTitle: string
  feedback: ChatFeedback
  question: string | null
  answer: string
  citations: ChatCitation[]
  inputTokenCount: number
  outputTokenCount: number
  totalTokenCount: number
  modelId: string | null
  createdAtUtc: string
}

export interface FeedbackPage {
  totalCount: number
  /** Counts for the search term, not the page or the selected rating. */
  liked: number
  disliked: number
  items: FeedbackEntry[]
}

export interface ChatThread {
  conversation: ChatConversation
  messages: ChatMessage[]
}

export interface ChatTurnResult {
  question: ChatMessage
  answer: ChatMessage
  /** The conversation title, which the first question replaces "New chat" with. */
  title: string
}

/** Newline-delimited events emitted while an agent turn is running. */
export type ChatStreamEvent =
  | { type: 'started'; question: ChatMessage; title: string }
  | { type: 'status'; message: string }
  | { type: 'delta'; text: string }
  | { type: 'completed'; answer: ChatMessage; title: string }
  | { type: 'error'; message: string }

export type SubscriptionStatus = 'Active' | 'ExpiringSoon' | 'Expired'

/** A Microsoft Graph webhook subscription. The client state itself is never sent to the browser. */
export interface SubscriptionView {
  id: string
  resource: string
  notificationUrl: string
  expirationUtc: string
  clientStateMatches: boolean
  resourceMatches: boolean
  notificationUrlMatches: boolean
  isManaged: boolean
  /** On the configured SharePoint:NotificationUrl. The worker owns it, so it cannot be deleted here. */
  isDefault: boolean
  status: SubscriptionStatus
}

export interface UpdateSubscriptionResult {
  subscription: SubscriptionView
  /** True when the notification URL changed: Graph can only express that as a new subscription. */
  replaced: boolean
  warning: string | null
}

export interface SubscriptionOverview {
  expectedResource: string
  expectedNotificationUrl: string
  renewalEnabled: boolean
  lifetimeDays: number
  renewalCheckHours: number
  renewalThresholdDays: number
  items: SubscriptionView[]
}

/** The three retrieval strategies the API exposes over one request body. */
export type SearchMode = 'fulltext' | 'vector' | 'hybrid'

export const SEARCH_MODES: SearchMode[] = ['fulltext', 'vector', 'hybrid']

export const SEARCH_MODE_LABELS: Record<SearchMode, string> = {
  fulltext: 'Full-text',
  vector: 'Vector',
  hybrid: 'Hybrid',
}

export const SEARCH_MODE_DESCRIPTIONS: Record<SearchMode, string> = {
  fulltext: 'Keyword search over the searchable fields.',
  vector: 'Pure k-nearest-neighbour search over contentVector.',
  hybrid: 'Keyword and vector in one request, fused by reciprocal rank.',
}

export interface SearchPayload {
  query: string
  userId?: string | null
  top: number
  skip: number
}

export interface SearchQueryHit {
  id: string
  driveId: string
  itemId: string
  name: string
  path: string | null
  webUrl: string | null
  mimeType: string | null
  size: number | null
  lastModifiedUtc: string | null
  chunkNumber: number
  content: string
  score: number | null
}

export interface SearchQueryResults {
  totalCount: number | null
  items: SearchQueryHit[]
}

/** One strategy's outcome, with the wall-clock time the round trip took. */
export interface TimedSearch {
  mode: SearchMode
  results: SearchQueryResults | null
  error: string | null
  elapsedMs: number
}
