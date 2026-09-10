# SharePoint Search Viewer

A React front end for `SharePointToAzureSearch.Api`. It shows the worker's SQL Server state — the
`SharePointIndexedFiles` and `SharePointDeltaState` tables — and runs the three retrieval strategies
against the search index, side by side.

## Pages

| Page | What it shows |
| --- | --- |
| **Overview** | Totals over the indexed-file table: files, chunks, source size, drives, files outside the current reconciliation round, and how many distinct index fingerprints are in play. Plus files by content type, the most recently indexed files, and the delta checkpoints. |
| **Indexed files** | The `SharePointIndexedFiles` table, filterable and sortable, with a detail panel showing every recorded column — ETag, CTag, permissions hash, index fingerprint, scan ID, and the drive and item IDs. |
| **Delta state** | The `SharePointDeltaState` table, one card per drive: the scan ID, whether that round's orphan sweep has run, when the checkpoint was last written, and the delta link itself. |
| **Search** | Full-text, vector, and hybrid over the same request body. **Compare all three** issues them together and reports each one's round trip, plus how much the three agree — distinct chunks returned, how many every strategy found, and how many only one strategy found. |

Searches are kept in the URL (`/search?q=…&mode=compare&top=10`), so a result is a link and the
back button steps through searches.

## Running it

The API must be running first. From `src/SharePointToAzureSearch.Api`:

```bash
dotnet run
```

Then here:

```bash
npm install
npm run dev
```

Open <http://localhost:5173>. The dev server proxies `/api` to `http://localhost:5263`, so the browser
makes same-origin requests and never needs the API's CORS policy.

### Configuration

Copy `.env.example` to `.env` to change either of:

- `VITE_API_PROXY_TARGET` — where `npm run dev` proxies `/api`. Set it when the API is not on 5263;
  the `https` launch profile, for example, is `https://localhost:7104`.
- `VITE_API_BASE_URL` — only for a production build served from a different origin than the API. When
  set, add that origin to `Cors:AllowedOrigins` in the API's configuration.

### Other scripts

```bash
npm run build      # typecheck, then build to dist/
npm run preview    # serve the built output
npm run typecheck
```

## What it needs from the API

Read-only endpoints added alongside the existing search ones:

- `GET /api/state/summary`
- `GET /api/state/indexed-files?search=&driveId=&sort=&desc=&skip=&top=`
- `GET /api/state/indexed-files/{driveId}/{itemId}`
- `GET /api/state/delta`

They read the database at `SqlServer:ConnectionString` and never write to it. A table that does not
exist yet reads as empty, so the viewer works before the worker's first pass.

**These endpoints are unauthenticated, like the search endpoints, and they expose the whole index and
every indexed file's metadata.** Put authentication in front of the API before exposing it anywhere
but a development machine.
