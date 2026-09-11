import { useState } from 'react'
import { Bot, Check, CircleCheck, Eye, Pencil, Plus, RefreshCw, RotateCcw, X } from 'lucide-react'
import {
  createAgent,
  getDefaultAgentInstructions,
  listAgents,
  updateAgent,
} from '../api/client'
import type { AgentDefinition } from '../api/types'
import { Empty, ErrorBanner, Field, LoadingBar, Modal } from '../components/ui'
import { formatDateTime, formatRelative } from '../lib/format'
import { useAsync } from '../lib/useAsync'

type EditorTarget = AgentDefinition | 'new' | null

export default function AgentsPage() {
  const agents = useAsync((signal) => listAgents(signal), [])
  const defaults = useAsync((signal) => getDefaultAgentInstructions(signal), [])
  const [viewing, setViewing] = useState<AgentDefinition | null>(null)
  const [editing, setEditing] = useState<EditorTarget>(null)
  const [name, setName] = useState('')
  const [instructions, setInstructions] = useState('')
  const [busy, setBusy] = useState(false)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const editingDefault =
    editing !== null && editing !== 'new' && editing.name.toLowerCase() === 'default'

  const openCreate = () => {
    setName('')
    setInstructions(defaults.data?.instructions ?? '')
    setDialogError(null)
    setEditing('new')
  }

  const openEdit = (agent: AgentDefinition) => {
    setName(agent.name)
    setInstructions(agent.instructions)
    setDialogError(null)
    setEditing(agent)
  }

  const save = async () => {
    const trimmedName = name.trim()
    const trimmedInstructions = instructions.trim()
    if (!trimmedName || !trimmedInstructions) {
      setDialogError('Name and instructions are required.')
      return
    }

    setBusy(true)
    setDialogError(null)
    setNotice(null)
    try {
      if (editing === 'new') {
        await createAgent(trimmedName, trimmedInstructions)
        setNotice('Agent created.')
      } else if (editing) {
        await updateAgent(editing.id, trimmedName, trimmedInstructions)
        setNotice('Agent updated.')
      }
      setEditing(null)
      agents.reload()
    } catch (cause) {
      setDialogError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>
            <Bot size={20} />
            Agents
          </h1>
          <p>
            Create and maintain reusable agent instructions. New agents start with the SharePoint
            assistant&apos;s built-in instruction template and can then be customized.
          </p>
        </div>
        <div className="row" style={{ gap: 8 }}>
          <button onClick={agents.reload}>
            <RefreshCw size={14} />
            Refresh
          </button>
          <button className="primary" disabled={!defaults.data} onClick={openCreate}>
            <Plus size={14} />
            New agent
          </button>
        </div>
      </div>

      <LoadingBar active={agents.loading || defaults.loading} />
      {agents.error ? <ErrorBanner message={agents.error} onRetry={agents.reload} /> : null}
      {defaults.error ? <ErrorBanner message={defaults.error} onRetry={defaults.reload} /> : null}
      {notice ? (
        <div className="banner success" role="status">
          <CircleCheck size={17} color="var(--good)" />
          <div>{notice}</div>
        </div>
      ) : null}

      {agents.data && agents.data.length > 0 ? (
        <div className="agent-grid">
          {agents.data.map((agent) => (
            <div className="card agent-card" key={agent.id}>
              <div className="card-head">
                <h2>
                  <Bot size={15} />
                  {agent.name}
                </h2>
                <span className="hint" title={formatDateTime(agent.updatedAtUtc)}>
                  Updated {formatRelative(agent.updatedAtUtc)}
                </span>
              </div>
              <div className="card-body stack" style={{ gap: 14 }}>
                <p className="agent-preview">{agent.instructions}</p>
                <div className="row" style={{ justifyContent: 'flex-end', gap: 8 }}>
                  <button onClick={() => setViewing(agent)}>
                    <Eye size={14} />
                    View
                  </button>
                  <button className="primary" onClick={() => openEdit(agent)}>
                    <Pencil size={14} />
                    Edit
                  </button>
                </div>
              </div>
            </div>
          ))}
        </div>
      ) : agents.data && !agents.loading ? (
        <div className="card">
          <Empty
            title="No agents"
            icon={<Bot size={26} strokeWidth={1.5} />}
            detail="Create an agent from the built-in SharePoint assistant instructions."
          />
        </div>
      ) : null}

      <Modal
        open={viewing !== null}
        title={viewing?.name ?? 'Agent instructions'}
        icon={<Eye size={17} />}
        className="agent-view-modal"
        onClose={() => setViewing(null)}
        footer={
          <button onClick={() => setViewing(null)}>
            <X size={14} />
            Close
          </button>
        }
      >
        {viewing ? <pre className="agent-instructions">{viewing.instructions}</pre> : null}
      </Modal>

      <Modal
        open={editing !== null}
        title={editing === 'new' ? 'New agent' : 'Edit agent'}
        icon={<Bot size={17} />}
        className="agent-editor-modal"
        onClose={() => setEditing(null)}
        footer={
          <>
            <button disabled={busy} onClick={() => setEditing(null)}>
              <X size={14} />
              Cancel
            </button>
            <button
              className="primary"
              disabled={busy || !name.trim() || !instructions.trim()}
              onClick={() => void save()}
            >
              {editing === 'new' ? <Plus size={14} /> : <Check size={14} />}
              {busy ? 'Saving…' : editing === 'new' ? 'Create agent' : 'Save changes'}
            </button>
          </>
        }
      >
        <div className="agent-editor-form">
          {dialogError ? <ErrorBanner message={dialogError} /> : null}

          <div className="agent-editor-intro">
            <span className="agent-editor-mark">
              <Bot size={20} />
            </span>
            <div>
              <strong>Define this agent&apos;s behavior</strong>
              <span>
                Give it a recognizable name, then describe its role, tool rules, safety boundaries,
                and preferred response style.
              </span>
            </div>
          </div>

          <div className="agent-editor-name-row">
            <Field
              label="Agent name"
              help={
                editingDefault
                  ? 'The default agent name is fixed.'
                  : 'Must be unique. Maximum 100 characters.'
              }
            >
              <input
                autoFocus={!editingDefault}
                type="text"
                maxLength={100}
                value={name}
                disabled={editingDefault}
                onChange={(event) => setName(event.target.value)}
                placeholder="For example: Contract reviewer"
              />
            </Field>
            <span className="badge accent">
              {editing === 'new' ? 'Built-in template' : 'Custom instructions'}
            </span>
          </div>

          <div className="agent-instructions-field">
            <div className="agent-instructions-label">
              <label htmlFor="agent-instructions">Instructions</label>
              <span>{instructions.length.toLocaleString()} characters</span>
            </div>
            <textarea
              id="agent-instructions"
              className="agent-instructions-editor"
              value={instructions}
              onChange={(event) => setInstructions(event.target.value)}
              spellCheck={false}
            />
            <div className="agent-editor-help">
              <span>Plain text and Markdown are supported. Changes take effect when you save.</span>
              {editing === 'new' && instructions !== defaults.data?.instructions ? (
                <button
                  className="ghost"
                  type="button"
                  onClick={() => setInstructions(defaults.data?.instructions ?? '')}
                >
                  <RotateCcw size={13} />
                  Restore template
                </button>
              ) : null}
            </div>
          </div>
        </div>
      </Modal>
    </div>
  )
}
