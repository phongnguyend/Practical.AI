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
| **Subscriptions** | The Microsoft Graph webhook subscriptions on the application registration. Shows what the worker would create, whether automatic renewal is on, and for each subscription whether its resource, notification URL, and client state match this deployment. Create, edit, renew, and delete from a dialog; deleting takes two clicks. The one on the configured `SharePoint:NotificationUrl` is marked **Default** — its URL is read-only and it cannot be deleted, because the renewal service owns it. Any other subscription can be edited freely as long as its notification URL is not already taken. |
| **Chat** | Conversations with an agent that searches the index when a question needs it. New chat, delete, and a thread with Markdown answers and a collapsible list of the documents each answer came from. Conversations name themselves from the first question and are stored in SQL Server, so they survive a restart. |
| **Feedback** | Every answer someone rated in the chat: the question, the answer, its sources, and a link that opens that conversation. Filter by rating or by text; tiles show how many were liked, disliked, and the liked share. The link jumps straight to that answer in the thread and highlights it, which matters once a conversation is long. |
| **Search** | Full-text, vector, and hybrid over the same request body. **Compare all three** issues them together and reports each one's round trip, plus how much the three agree — distinct chunks returned, how many every strategy found, and how many only one strategy found. |

Searches are kept in the URL (`/search?q=…&mode=compare&top=10`), so a result is a link and the
back button steps through searches. So is the open conversation
(`/chat?conversation=…&message=…`), which is how the Feedback page links to a particular answer.

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

The Chat page uses, and these **write to the database and call Azure OpenAI**:

- `GET|POST /api/chat/conversations`, `DELETE /api/chat/conversations/{id}`
- `GET|POST /api/chat/conversations/{id}/messages`
- `POST /api/chat/messages/{id}/feedback` and `GET /api/chat/feedback`

The Subscriptions page additionally uses, and these **change tenant state**:

- `GET /api/subscriptions`
- `POST /api/subscriptions` — body `{ "days": 28, "notificationUrl": "https://..." }`, both optional
- `PUT /api/subscriptions/{id}` — same body; a changed URL replaces the subscription
- `POST /api/subscriptions/{id}/renew` — optional body `{ "days": 28 }`
- `DELETE /api/subscriptions/{id}` — refused for the default subscription

**Every one of these endpoints is unauthenticated, like the search endpoints. Between them they expose
the whole index and all indexed metadata, and let any caller delete the webhook subscription.** Put
authentication in front of the API before exposing it anywhere but a development machine.
