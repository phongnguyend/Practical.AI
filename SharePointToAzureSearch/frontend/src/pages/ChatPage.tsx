import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import {
  Bot,
  Check,
  Copy,
  Cpu,
  ExternalLink,
  GitBranch,
  MessageSquare,
  Plus,
  Paperclip,
  Download,
  SendHorizontal,
  Sparkles,
  ThumbsDown,
  ThumbsUp,
  Trash2,
  User,
  X,
} from 'lucide-react'
import {
  branchConversation,
  createConversation,
  deleteConversation,
  getThread,
  listAgents,
  listConversations,
  sendChatMessage,
  setMessageFeedback,
  uploadFile,
  uploadDownloadUrl,
} from '../api/client'
import type { ChatConversation, ChatFeedback, ChatMessage, ChatMessageAttachment } from '../api/types'
import { Empty, ErrorBanner, Field, LoadingBar, Modal } from '../components/ui'
import { FileTypeIcon } from '../components/FileTypeIcon'
import {
  folderLabel,
  formatDateTime,
  formatMessageTime,
  formatRelative,
  formatScore,
  formatTokenUsage,
} from '../lib/format'
import { copyText } from '../lib/clipboard'
import { useAsync } from '../lib/useAsync'

export default function ChatPage() {
  const conversations = useAsync((signal) => listConversations(signal), [])
  const agents = useAsync((signal) => listAgents(signal), [])
  // The open conversation is in the URL, so a link from elsewhere — the Feedback page — can open the
  // one it is pointing at rather than dropping the reader into whichever is most recent.
  const [params, setParams] = useSearchParams()
  const [activeId, setActiveId] = useState<string | null>(params.get('conversation'))
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [draft, setDraft] = useState('')
  const [sending, setSending] = useState(false)
  const [streamingText, setStreamingText] = useState('')
  const [agentStatus, setAgentStatus] = useState('Thinking…')
  const [error, setError] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)
  const [showNewConversation, setShowNewConversation] = useState(false)
  const [selectedAgentId, setSelectedAgentId] = useState('')
  const [creatingConversation, setCreatingConversation] = useState(false)
  const [attachments, setAttachments] = useState<ChatMessageAttachment[]>([])
  const [uploading, setUploading] = useState(false)
  const threadRef = useRef<HTMLDivElement>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const list = conversations.data ?? []
  const active = list.find((item) => item.id === activeId) ?? null
  const activeAgent = active
    ? (agents.data ?? []).find((item) =>
        active.agentId ? item.id === active.agentId : item.name.toLowerCase() === 'default',
      )
    : null

  const open = (id: string | null) => {
    setActiveId(id)
    setParams(id ? { conversation: id } : {}, { replace: true })
  }

  const requested = params.get('conversation')
  const targetMessageId = params.get('message')
  const jumpedTo = useRef<string | null>(null)
  const [highlighted, setHighlighted] = useState<string | null>(null)
  useEffect(() => {
    if (requested && requested !== activeId) {
      setActiveId(requested)
    }
    // Only a change to the URL should move the selection; selecting in the page writes the URL itself.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requested])

  // Picks a conversation only when none is selected — on first load, and after a delete clears the
  // selection. It must not correct a selection that is merely newer than the list, or creating a
  // conversation would snap straight back to the most recent one already in it.
  useEffect(() => {
    if (activeId === null && list.length > 0) {
      open(list[0].id)
    }
  }, [list, activeId])

  const thread = useAsync(
    async (signal) => (activeId ? getThread(activeId, signal) : null),
    [activeId],
  )

  // Switching conversations empties the thread at once rather than leaving the previous one on
  // screen until the new fetch lands, and the guard keeps an in-flight response for the conversation
  // just left from overwriting the new one.
  useEffect(() => {
    setMessages([])
    setAttachments([])
  }, [activeId])

  useEffect(() => {
    if (thread.data && thread.data.conversation.id === activeId) {
      setMessages(thread.data.messages)
    }
  }, [thread.data, activeId])

  /**
   * A chat is read from the bottom, so that is where every new turn and every conversation opened
   * lands — unless the URL names a message to jump to, which a link from the Feedback page does. The
   * jump happens once per target: later turns in the same conversation scroll to the bottom again.
   */
  useEffect(() => {
    const element = threadRef.current
    if (!element) return

    if (targetMessageId && jumpedTo.current !== targetMessageId) {
      // The thread has not arrived yet; stay put rather than flashing to the bottom first.
      if (messages.length === 0) return

      jumpedTo.current = targetMessageId
      const node = element.querySelector(`[data-message-id="${CSS.escape(targetMessageId)}"]`)
      if (node) {
        node.scrollIntoView({ block: 'center' })
        setHighlighted(targetMessageId)
        return
      }
    }

    element.scrollTop = element.scrollHeight
  }, [messages, sending, targetMessageId])

  // The highlight is a hint, not a state: it fades once the reader has had a chance to see it.
  useEffect(() => {
    if (!highlighted) return
    const timer = setTimeout(() => setHighlighted(null), 2600)
    return () => clearTimeout(timer)
  }, [highlighted])

  const openNewChat = () => {
    setSelectedAgentId('')
    setError(null)
    setShowNewConversation(true)
  }

  const newChat = async () => {
    setError(null)
    setCreatingConversation(true)
    try {
      const created = await createConversation({ agentId: selectedAgentId || null })
      conversations.reload()
      open(created.id)
      setShowNewConversation(false)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setCreatingConversation(false)
    }
  }

  /**
   * The reaction is applied to the thread immediately and rolled back if the write fails, because a
   * thumbs-up that waits on a round trip feels broken.
   */
  const react = async (messageId: string, feedback: ChatFeedback | null) => {
    const previous = messages.find((message) => message.id === messageId)?.feedback ?? null
    setMessages((current) =>
      current.map((message) => (message.id === messageId ? { ...message, feedback } : message)),
    )

    try {
      await setMessageFeedback(messageId, feedback)
    } catch (cause) {
      setMessages((current) =>
        current.map((message) =>
          message.id === messageId ? { ...message, feedback: previous } : message,
        ),
      )
      setError(cause instanceof Error ? cause.message : String(cause))
    }
  }

  const branch = async (messageId: string) => {
    if (!activeId) return

    setError(null)
    try {
      const created = await branchConversation(activeId, messageId)
      conversations.reload()
      open(created.id)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    }
  }

  const remove = async (id: string) => {
    setError(null)
    setConfirmDelete(null)
    try {
      await deleteConversation(id)
      if (id === activeId) open(null)
      conversations.reload()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    }
  }

  const addFiles = async (files: FileList | null) => {
    if (!files?.length) return
    setUploading(true)
    setError(null)
    try {
      for (const file of Array.from(files).slice(0, Math.max(0, 10 - attachments.length))) {
        const uploaded = await uploadFile(file)
        if (uploaded.status !== 'Indexed') {
          throw new Error(uploaded.errorMessage ?? `${uploaded.fileName} could not be indexed.`)
        }
        setAttachments((current) => [...current, {
          id: uploaded.id,
          fileName: uploaded.fileName,
          contentType: uploaded.contentType,
          sizeBytes: uploaded.sizeBytes,
        }])
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setUploading(false)
      if (fileInputRef.current) fileInputRef.current.value = ''
    }
  }

  /**
   * The question is shown immediately with a local id and replaced by the stored one when the turn
   * returns, so the thread does not sit empty while the agent searches.
   */
  const send = async () => {
    const content = draft.trim()
    if (!content || sending) return

    let conversationId = activeId
    setError(null)
    setSending(true)
    setStreamingText('')
    setAgentStatus('Thinking…')
    setDraft('')

    const pending: ChatMessage = {
      id: `pending-${Date.now()}`,
      conversationId: conversationId ?? '',
      role: 'User',
      content,
      citations: [],
      inputTokenCount: 0,
      outputTokenCount: 0,
      totalTokenCount: 0,
      modelId: null,
      feedback: null,
      attachments,
      createdAtUtc: new Date().toISOString(),
    }
    setMessages((current) => [...current, pending])

    try {
      // Typing into an empty page starts a conversation rather than making the user press New chat.
      if (conversationId === null) {
        const created = await createConversation()
        conversationId = created.id
        open(created.id)
      }

      const result = await sendChatMessage(conversationId, content, attachments.map((file) => file.id), (event) => {
        if (event.type === 'started') {
          setMessages((current) => [
            ...current.filter((message) => message.id !== pending.id),
            event.question,
          ])
        } else if (event.type === 'status') {
          setAgentStatus(event.message)
        } else if (event.type === 'delta') {
          setStreamingText((current) => current + event.text)
          setAgentStatus('Writing the answer…')
        }
      })
      setMessages((current) => [
        ...current.filter(
          (message) => message.id !== pending.id && message.id !== result.question.id,
        ),
        result.question,
        result.answer,
      ])
      conversations.reload()
      setAttachments([])
    } catch (cause) {
      setMessages((current) => current.filter((message) => message.id !== pending.id))
      setDraft(content)
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setSending(false)
      setStreamingText('')
      setAgentStatus('Thinking…')
    }
  }

  return (
    <div className="stack" style={{ gap: 14 }}>
      <div className="page-head" style={{ marginBottom: 0 }}>
        <div>
          <h1>
            <Sparkles size={20} />
            Chat
          </h1>
          <p>
            Ask about the indexed documents. The assistant searches the index when a question needs it
            and answers from what it finds, citing the files it used.
          </p>
        </div>
      </div>

      {error ? <ErrorBanner message={error} /> : null}

      <div className="chat-shell">
        <aside className="card chat-sidebar">
          <div className="card-head">
            <h2>
              <MessageSquare size={15} />
              Conversations
            </h2>
            <button className="primary" onClick={openNewChat}>
              <Plus size={14} />
              New
            </button>
          </div>
          <LoadingBar active={conversations.loading} />
          <div className="chat-conversations">
            {list.length === 0 && !conversations.loading ? (
              <Empty
                title="No conversations"
                icon={<MessageSquare size={24} strokeWidth={1.5} />}
                detail="Start one below."
              />
            ) : (
              list.map((item) => (
                <ConversationRow
                  key={item.id}
                  item={item}
                  active={item.id === activeId}
                  confirming={confirmDelete === item.id}
                  onOpen={() => open(item.id)}
                  onAskDelete={() => setConfirmDelete(item.id)}
                  onCancelDelete={() => setConfirmDelete(null)}
                  onDelete={() => remove(item.id)}
                />
              ))
            )}
          </div>
        </aside>

        <section className="card chat-panel">
          <div className="card-head">
            <h2>
              <Bot size={15} />
              {active?.title ?? 'New chat'}
            </h2>
            <div className="row" style={{ gap: 8 }}>
              {active ? (
                <span className="badge accent" title="Agent assigned to this conversation">
                  <Bot size={12} />
                  {activeAgent?.name ?? 'Agent unavailable'}
                </span>
              ) : null}
              {active ? (
                <span className="badge" title="Model ID used by the current agent">
                  <Cpu size={12} />
                  {activeAgent?.modelId ?? (agents.loading ? 'Loading model…' : 'Model unavailable')}
                </span>
              ) : null}
              {active?.userId ? (
                <span className="badge accent" title="Searches are filtered to this user's permissions">
                  as {active.userId}
                </span>
              ) : (
                <span className="hint">Searching the whole index, unfiltered</span>
              )}
            </div>
          </div>

          <div className="chat-thread" ref={threadRef}>
            {messages.length === 0 && !thread.loading ? (
              <Empty
                title="Ask a question"
                icon={<Sparkles size={26} strokeWidth={1.5} />}
                detail="For example: which documents describe the logging requirements?"
              />
            ) : (
              messages.map((message) => (
                <MessageBubble
                  key={message.id}
                  message={message}
                  highlighted={message.id === highlighted}
                  onFeedback={react}
                  onBranch={branch}
                />
              ))
            )}
            {sending ? <StreamingAnswer text={streamingText} status={agentStatus} /> : null}
          </div>

          <div className="chat-composer">
            <input
              ref={fileInputRef}
              className="visually-hidden"
              type="file"
              multiple
              onChange={(event) => void addFiles(event.target.files)}
            />
            <button
              className="chat-composer-action"
              disabled={sending || uploading || attachments.length >= 10}
              onClick={() => fileInputRef.current?.click()}
              title="Attach files"
            >
              <Paperclip size={15} />
              {uploading ? 'Uploading…' : 'Attach'}
            </button>
            <div className="chat-compose-main">
              {attachments.length > 0 ? (
                <div className="chat-attachment-list">
                  {attachments.map((file) => (
                    <span className="chat-attachment-chip" key={file.id}>
                      <FileTypeIcon name={file.fileName} mimeType={file.contentType} size={14} />
                      <span title={file.fileName}>{file.fileName}</span>
                      <button
                        className="ghost icon-only"
                        aria-label={`Remove ${file.fileName}`}
                        onClick={() => setAttachments((current) => current.filter((item) => item.id !== file.id))}
                      ><X size={12} /></button>
                    </span>
                  ))}
                </div>
              ) : null}
              <textarea
                rows={2}
                placeholder="Ask about the indexed documents…   (Enter to send, Shift+Enter for a new line)"
                value={draft}
                disabled={sending}
                onChange={(event) => setDraft(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' && !event.shiftKey) {
                    event.preventDefault()
                    void send()
                  }
                }}
              />
            </div>
            <button
              className="primary chat-composer-action"
              disabled={sending || uploading || draft.trim() === ''}
              onClick={send}
            >
              <SendHorizontal size={15} />
              {sending ? 'Thinking…' : 'Send'}
            </button>
          </div>
        </section>
      </div>

      <Modal
        open={showNewConversation}
        title="New conversation"
        icon={<MessageSquare size={17} />}
        onClose={() => setShowNewConversation(false)}
        footer={
          <>
            <button disabled={creatingConversation} onClick={() => setShowNewConversation(false)}>
              <X size={14} />
              Cancel
            </button>
            <button
              className="primary"
              disabled={creatingConversation}
              onClick={() => void newChat()}
            >
              <Plus size={14} />
              {creatingConversation ? 'Creatingâ€¦' : 'Create conversation'}
            </button>
          </>
        }
      >
        <div className="stack">
          {error ? <ErrorBanner message={error} /> : null}
          {agents.error ? <ErrorBanner message={agents.error} onRetry={agents.reload} /> : null}
          <Field
            label="Agent"
            help="Optional. If none is selected, this conversation uses the Default agent."
          >
            <select
              autoFocus
              value={selectedAgentId}
              disabled={agents.loading || creatingConversation}
              onChange={(event) => setSelectedAgentId(event.target.value)}
            >
              <option value="">Default agent</option>
              {(agents.data ?? [])
                .filter((agent) => agent.name.toLowerCase() !== 'default')
                .map((agent) => (
                  <option key={agent.id} value={agent.id}>
                    {agent.name}
                  </option>
                ))}
            </select>
          </Field>
        </div>
      </Modal>
    </div>
  )
}

function ConversationRow({
  item,
  active,
  confirming,
  onOpen,
  onAskDelete,
  onCancelDelete,
  onDelete,
}: {
  item: ChatConversation
  active: boolean
  confirming: boolean
  onOpen: () => void
  onAskDelete: () => void
  onCancelDelete: () => void
  onDelete: () => void
}) {
  return (
    <div className={active ? 'chat-conversation active' : 'chat-conversation'}>
      <button className="chat-conversation-open" onClick={onOpen} title={item.title}>
        <span className="chat-conversation-title">{item.title}</span>
        <span className="chat-conversation-meta">
          <span className="chat-conversation-meta-line">
            {formatRelative(item.updatedAtUtc)} · {item.messageCount}{' '}
            {item.messageCount === 1 ? 'message' : 'messages'}
          </span>
          <span className="chat-conversation-token-usage">
            {formatTokenUsage(item.totalTokenCount, item.inputTokenCount, item.outputTokenCount)}
          </span>
        </span>
      </button>
      {confirming ? (
        <div className="row" style={{ gap: 4, flexWrap: 'nowrap' }}>
          <button className="ghost" onClick={onCancelDelete}>
            No
          </button>
          <button className="danger" onClick={onDelete}>
            Delete
          </button>
        </div>
      ) : (
        <button className="ghost icon-only chat-conversation-delete" onClick={onAskDelete} title="Delete">
          <Trash2 size={14} />
        </button>
      )}
    </div>
  )
}

function MessageBubble({
  message,
  highlighted,
  onFeedback,
  onBranch,
}: {
  message: ChatMessage
  highlighted: boolean
  onFeedback: (id: string, feedback: ChatFeedback | null) => void
  onBranch: (id: string) => Promise<void>
}) {
  const isUser = message.role === 'User'
  const classes = [
    'chat-message',
    isUser ? 'user' : 'assistant',
    highlighted ? 'highlighted' : '',
  ].join(' ')

  return (
    <div className={classes.trim()} data-message-id={message.id}>
      <div className="chat-avatar">{isUser ? <User size={14} /> : <Bot size={14} />}</div>
      <div className="chat-body">
        {isUser ? (
          <p className="chat-text">{message.content}</p>
        ) : (
          <div className="chat-text markdown">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>{message.content}</ReactMarkdown>
          </div>
        )}
        {message.attachments.length > 0 ? (
          <div className="message-attachments">
            {message.attachments.map((file) => (
              <a href={uploadDownloadUrl(file.id)} key={file.id} title={`Download ${file.fileName}`}>
                <FileTypeIcon name={file.fileName} mimeType={file.contentType} size={14} />
                <span>{file.fileName}</span>
                <Download size={12} />
              </a>
            ))}
          </div>
        ) : null}
        <div className="chat-meta">
          <span className="chat-time" title={formatDateTime(message.createdAtUtc)}>
            {formatMessageTime(message.createdAtUtc)}
          </span>
          {!isUser && message.totalTokenCount > 0 ? (
            <span className="chat-time">
              {formatTokenUsage(
                message.totalTokenCount,
                message.inputTokenCount,
                message.outputTokenCount,
              )}
              {message.modelId ? ` · ${message.modelId}` : ''}
            </span>
          ) : null}
          {!isUser ? (
            <MessageActions message={message} onFeedback={onFeedback} onBranch={onBranch} />
          ) : null}
        </div>
        {message.citations.length > 0 ? <Citations citations={message.citations} /> : null}
      </div>
    </div>
  )
}

/**
 * Copy, and a thumbs up/down that toggles: pressing the reaction already set clears it, so a
 * mis-click is undone the same way it was made.
 */
function MessageActions({
  message,
  onFeedback,
  onBranch,
}: {
  message: ChatMessage
  onFeedback: (id: string, feedback: ChatFeedback | null) => void
  onBranch: (id: string) => Promise<void>
}) {
  const [copied, setCopied] = useState(false)
  const [branching, setBranching] = useState(false)

  useEffect(() => {
    if (!copied) return
    const timer = setTimeout(() => setCopied(false), 1400)
    return () => clearTimeout(timer)
  }, [copied])

  return (
    <div className="chat-actions">
      <button
        className="ghost icon-only"
        title={copied ? 'Copied' : 'Copy the answer'}
        aria-label="Copy the answer"
        onClick={() => {
          void copyText(message.content).then((ok) => setCopied(ok))
        }}
      >
        {copied ? <Check size={13} color="var(--good)" /> : <Copy size={13} />}
      </button>
      <button
        className={message.feedback === 'Like' ? 'ghost icon-only liked' : 'ghost icon-only'}
        title="Good answer"
        aria-label="Good answer"
        aria-pressed={message.feedback === 'Like'}
        onClick={() => onFeedback(message.id, message.feedback === 'Like' ? null : 'Like')}
      >
        <ThumbsUp size={13} />
      </button>
      <button
        className={message.feedback === 'Dislike' ? 'ghost icon-only disliked' : 'ghost icon-only'}
        title="Bad answer"
        aria-label="Bad answer"
        aria-pressed={message.feedback === 'Dislike'}
        onClick={() => onFeedback(message.id, message.feedback === 'Dislike' ? null : 'Dislike')}
      >
        <ThumbsDown size={13} />
      </button>
      <button
        className="ghost icon-only"
        title="Branch in new chat"
        aria-label="Branch in new chat"
        disabled={branching}
        onClick={() => {
          setBranching(true)
          void onBranch(message.id).finally(() => setBranching(false))
        }}
      >
        <GitBranch size={13} />
      </button>
    </div>
  )
}

function Citations({ citations }: { citations: ChatMessage['citations'] }) {
  return (
    <details className="chat-citations">
      <summary>
        {citations.length} {citations.length === 1 ? 'source' : 'sources'}
      </summary>
      <ul>
        {citations.map((citation, index) => (
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
              {folderLabel(citation.path)} · chunk {citation.chunkNumber} · {formatScore(citation.score)}
            </span>
          </li>
        ))}
      </ul>
    </details>
  )
}

function StreamingAnswer({ text, status }: { text: string; status: string }) {
  return (
    <div className="chat-message assistant">
      <div className="chat-avatar">
        <Bot size={14} />
      </div>
      <div className="chat-body">
        {text ? (
          <div className="chat-text markdown">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>{text}</ReactMarkdown>
          </div>
        ) : null}
        <div className="chat-thinking" role="status" aria-live="polite">
          {status}
          <span className="dots" aria-hidden="true">
            <i />
            <i />
            <i />
          </span>
        </div>
      </div>
    </div>
  )
}
