/** Mirrors the records the API returns. Property names match its camelCase JSON. */

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
