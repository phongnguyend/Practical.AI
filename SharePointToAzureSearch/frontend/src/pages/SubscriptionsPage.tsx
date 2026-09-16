import { useState, type ReactNode } from 'react'
import {
  Ban,
  Check,
  CircleCheck,
  CircleX,
  Lock,
  Pencil,
  Plus,
  RefreshCw,
  RotateCw,
  ShieldAlert,
  ShieldCheck,
  Trash2,
  TriangleAlert,
  Webhook,
  X,
} from 'lucide-react'
import {
  createSubscription,
  deleteSubscription,
  listSubscriptions,
  renewSubscription,
  setSubscriptionAutoRenew,
  updateSubscription,
} from '../api/client'
import type { SubscriptionStatus, SubscriptionView } from '../api/types'
import { CopyButton, Empty, ErrorBanner, Field, LoadingBar, Modal } from '../components/ui'
import { formatDateTime, formatRelative } from '../lib/format'
import { useAsync } from '../lib/useAsync'

const STATUS_BADGES: Record<SubscriptionStatus, { className: string; label: string }> = {
  Missing: { className: 'badge warning', label: 'Missing from Graph' },
  Active: { className: 'badge good', label: 'Active' },
  ExpiringSoon: { className: 'badge warning', label: 'Expiring soon' },
  Expired: { className: 'badge critical', label: 'Expired' },
}

export default function SubscriptionsPage() {
  const overview = useAsync((signal) => listSubscriptions(signal), [])

  const [subscriptionName, setSubscriptionName] = useState('')
  const [lifetimeDays, setLifetimeDays] = useState(28)
  const [notificationUrl, setNotificationUrl] = useState('')
  const [clientStateInput, setClientStateInput] = useState('')
  // null when the dialog is closed; a subscription when editing one, 'new' when creating.
  const [editing, setEditing] = useState<SubscriptionView | 'new' | null>(null)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const data = overview.data
  const isCreating = editing === 'new'
  const target = editing === 'new' ? null : editing

  // The field starts empty and the configured URL is only the input's greyed hint, so the URL is
  // always typed deliberately rather than inherited from a value nobody looked at.
  const trimmedUrl = notificationUrl.trim()
  const trimmedClientState = clientStateInput.trim()
  const clientStateInvalid = trimmedClientState !== '' && (trimmedClientState.length < 16 || trimmedClientState.length > 128)
  const urlIsHttps = /^https:\/\/.+/i.test(trimmedUrl)
  const isOverride = data !== null && trimmedUrl !== '' && trimmedUrl !== data.expectedNotificationUrl
  // A URL already in use elsewhere is refused by the API; catching it here saves the round trip.
  const urlTaken =
    trimmedUrl !== '' &&
    (data?.items.some(
      (item) =>
        item.id !== target?.id && item.notificationUrl.toLowerCase() === trimmedUrl.toLowerCase(),
    ) ??
      false)
  const nameLocked = target?.isDefault ?? false
  const urlChanged =
    target !== null &&
    target.id !== null &&
    trimmedUrl !== '' &&
    trimmedUrl !== target.notificationUrl

  const openDialog = (subscription: SubscriptionView | 'new') => {
    setSubscriptionName(subscription === 'new' ? '' : subscription.name)
    setLifetimeDays(data?.lifetimeDays ?? 28)
    // Editing shows the subscription's own URL; creating leaves the field empty.
    setNotificationUrl(subscription === 'new' ? '' : subscription.notificationUrl)
    setClientStateInput('')
    setDialogError(null)
    setEditing(subscription)
  }

  /** Every mutation reloads the list afterwards, so what is shown is Graph's state, not an optimistic guess. */
  const run = async (key: string, message: string, action: () => Promise<unknown>) => {
    setBusy(key)
    setActionError(null)
    setNotice(null)
    try {
      await action()
      setNotice(message)
      overview.reload()
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(null)
    }
  }

  /**
   * The dialog keeps its own failures and stays open, because the usual one is a notification URL
   * Microsoft Graph could not reach — fixed by editing the field already in front of the user.
   */
  const save = async () => {
    setBusy('save')
    setDialogError(null)
    setNotice(null)
    try {
      if (target === null) {
        await createSubscription(subscriptionName, lifetimeDays, notificationUrl, clientStateInput)
        setNotice('Subscription created.')
      } else {
        const updateId = target.id ?? target.databaseId
        if (!updateId) {
          throw new Error('This subscription has no database or Microsoft Graph ID.')
        }
        const result = await updateSubscription(
          updateId,
          subscriptionName,
          lifetimeDays,
          notificationUrl,
          clientStateInput,
        )
        setNotice(
          target.id === null
            ? `${result.subscription.name} Graph subscription created — its ID is ${result.subscription.id}.`
            : result.warning ??
            (result.replaced
              ? `The subscription was replaced to apply its changes — its ID is now ${result.subscription.id}.`
              : 'Subscription updated.'),
        )
      }
      setEditing(null)
      overview.reload()
    } catch (cause) {
      setDialogError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>
            <Webhook size={20} />
            Webhook subscriptions
          </h1>
          <p>
            The Microsoft Graph subscriptions that make the drive notify this deployment when something
            changes. A notification is only a signal — the worker still reconciles through the delta
            feed — so removing one leaves the scheduled synchronization as the only trigger.
          </p>
        </div>
        <div className="row" style={{ gap: 8 }}>
          <button onClick={overview.reload}>
            <RefreshCw size={14} />
            Refresh
          </button>
          <button className="primary" disabled={!data} onClick={() => openDialog('new')}>
            <Plus size={14} />
            New subscription
          </button>
        </div>
      </div>

      <LoadingBar active={overview.loading} />

      {overview.error ? <ErrorBanner message={overview.error} onRetry={overview.reload} /> : null}
      {actionError ? <ErrorBanner message={actionError} /> : null}
      {notice ? (
        <div className="banner success" role="status">
          <CircleCheck size={17} color="var(--good)" style={{ marginTop: 2 }} />
          <div style={{ flex: 1 }}>{notice}</div>
        </div>
      ) : null}

      {data && data.items.length > 0 ? (
        <div className="stack">
          <div className="row spread">
            <h2
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 7,
                margin: 0,
                fontSize: 15,
              }}
            >
              <Webhook size={15} />
              All subscriptions
            </h2>
            <span className="badge">{data.items.length}</span>
          </div>
          {data.items.map((item) => (
            <SubscriptionCard
              key={item.id ?? item.databaseId ?? `database:${item.name}`}
              item={item}
              days={data.lifetimeDays}
              renewalCheckHours={data.renewalCheckHours}
              renewalThresholdDays={data.renewalThresholdDays}
              busy={busy}
              onEdit={item.isTracked ? () => openDialog(item) : undefined}
              onAutoRenewToggle={item.databaseId
                ? () => run(
                    `auto-renew:${item.databaseId}`,
                    `Automatic renewal ${item.autoRenewEnabled ? 'disabled' : 'enabled'} for ${item.name}.`,
                    () => setSubscriptionAutoRenew(item.databaseId!, !item.autoRenewEnabled),
                  )
                : undefined}
              onRenew={
                item.id === null
                  ? undefined
                  : () =>
                      run(`renew:${item.id}`, 'Subscription renewed.', () =>
                        renewSubscription(item.id!, data.lifetimeDays),
                      )
              }
              onDelete={
                item.id === null
                  ? undefined
                  : () =>
                      run(`delete:${item.id}`, 'Subscription deleted.', () =>
                        deleteSubscription(item.id!),
                      )
              }
            />
          ))}
        </div>
      ) : data && !overview.loading ? (
        <div className="card">
          <Empty
            title="No subscriptions on this application registration"
            icon={<Webhook size={26} strokeWidth={1.5} />}
            detail="Create one with the button above, or enable automatic renewal for the Default record."
          />
        </div>
      ) : null}

      <Modal
        open={editing !== null}
        title={isCreating ? 'New subscription' : 'Edit subscription'}
        icon={<Webhook size={17} />}
        onClose={() => setEditing(null)}
        footer={
          <>
            <button onClick={() => setEditing(null)} disabled={busy === 'save'}>
              <X size={14} />
              Cancel
            </button>
            <button
              className="primary"
              disabled={busy === 'save' || !subscriptionName.trim() || !urlIsHttps || urlTaken || clientStateInvalid}
              onClick={save}
            >
              {isCreating ? <Plus size={14} /> : <Check size={14} />}
              {busy === 'save' ? 'Saving…' : isCreating ? 'Create' : 'Save changes'}
            </button>
          </>
        }
      >
        <div className="stack" style={{ gap: 14 }}>
          {dialogError ? <ErrorBanner message={dialogError} /> : null}

          <Field
            label="Name"
            help={
              nameLocked
                ? 'The default subscription is always named Default.'
                : 'Saved in the database and included in the generated client state unless you enter a custom value.'
            }
          >
            <input
              autoFocus={!nameLocked}
              type="text"
              value={subscriptionName}
              readOnly={nameLocked}
              onChange={(event) => setSubscriptionName(event.target.value)}
              placeholder="For example: Development tunnel"
            />
          </Field>

          <Field label="Resource">
            <input type="text" className="mono" value={data?.expectedResource ?? ''} readOnly />
          </Field>

          <Field
            label="Notification URL"
            help={
              target?.isDefault
                ? 'Changing this URL also updates the database-backed Default subscription.'
                : 'Where Microsoft Graph posts change notifications. It must be HTTPS, reachable from the internet, and not already used by another subscription.'
            }
          >
            <input
              type="text"
              required
              placeholder={data?.expectedNotificationUrl || 'https://your-host/api/sharepoint/webhook'}
              value={notificationUrl}
              onChange={(event) => setNotificationUrl(event.target.value)}
            />
          </Field>

          <Field
            label="Client state"
            help={isCreating
              ? 'Optional. Leave blank to generate an authenticated value. A custom value must be 16-128 characters.'
              : `Leave blank to keep the ${target?.hasCustomClientState ? 'saved custom' : 'generated'} value. Enter 16-128 characters to replace it.`}
          >
            <input
              type="password"
              autoComplete="new-password"
              maxLength={128}
              value={clientStateInput}
              onChange={(event) => setClientStateInput(event.target.value)}
              placeholder={isCreating ? 'Optional custom client state' : 'Leave blank to keep current value'}
            />
          </Field>

          {clientStateInvalid ? (
            <Hint tone="critical" icon={<CircleX size={13} />}>
              Client state must be 16-128 characters.
            </Hint>
          ) : null}

          <Field label="Lifetime (days)" help="From now, capped at 29 — the most Microsoft Graph allows.">
            <input
              type="number"
              min={1}
              max={29}
              value={lifetimeDays}
              onChange={(event) =>
                setLifetimeDays(Math.min(29, Math.max(1, Number(event.target.value) || 1)))
              }
              style={{ width: 110 }}
            />
          </Field>

          {trimmedUrl && !urlIsHttps ? (
            <Hint tone="critical" icon={<CircleX size={13} />}>
              Microsoft Graph only accepts an absolute HTTPS URL.
            </Hint>
          ) : urlTaken ? (
            <Hint tone="critical" icon={<CircleX size={13} />}>
              Another subscription already uses this notification URL.
            </Hint>
          ) : urlChanged || (target !== null && trimmedClientState !== '') ? (
            <Hint tone="warning" icon={<TriangleAlert size={13} color="var(--warning)" />}>
              Saving this change may replace the Graph subscription. A new one is created first, then the
              old one is removed. The ID will change.
            </Hint>
          ) : isCreating && isOverride ? (
            <Hint tone="warning" icon={<TriangleAlert size={13} color="var(--warning)" />}>
              This differs from the database-backed Default URL, so this subscription will not be
              treated as the Default one.
            </Hint>
          ) : null}
        </div>
      </Modal>
    </div>
  )
}

function SubscriptionCard({
  item,
  days,
  renewalCheckHours,
  renewalThresholdDays,
  busy,
  onEdit,
  onAutoRenewToggle,
  onRenew,
  onDelete,
}: {
  item: SubscriptionView
  days: number
  renewalCheckHours: number
  renewalThresholdDays: number
  busy: string | null
  onEdit?: () => void
  onAutoRenewToggle?: () => void
  onRenew?: () => void
  onDelete?: () => void
}) {
  const [confirming, setConfirming] = useState(false)
  const status = STATUS_BADGES[item.status]
  const anyBusy = busy !== null

  return (
    <div className="card">
      <div className="card-head">
        <h2 style={{ minWidth: 0 }}>
          {item.isTracked ? (
            <ShieldCheck size={15} color="var(--good)" />
          ) : (
            <ShieldAlert size={15} color="var(--text-muted)" />
          )}
          <span style={{ overflowWrap: 'anywhere', fontWeight: 600 }}>
            {item.name}
          </span>
        </h2>
        <div className="row" style={{ gap: 8 }}>
          <span className={status.className}>{status.label}</span>
          <span className={item.isTracked ? 'badge good' : 'badge'}>
            {item.isTracked ? <CircleCheck size={11} /> : <CircleX size={11} />}
            {item.isTracked ? 'Tracked' : 'Untracked'}
          </span>
          {item.isDefault ? (
            <span className="badge accent" title="Managed by the database-backed Default definition">
              <Lock size={11} />
              Default
            </span>
          ) : null}
          {item.expirationUtc ? (
            <span className="hint">expires {formatRelative(item.expirationUtc)}</span>
          ) : null}
        </div>
      </div>

      <div className="card-body stack" style={{ gap: 14 }}>
        <dl className="detail-grid">
          <dt>Name</dt>
          <dd>{item.name}</dd>

          {item.isDefault ? (
            <>
              <dt>Default lifetime</dt>
              <dd>{days} days</dd>
            </>
          ) : null}

          <dt>Auto-renewal</dt>
          <dd>
            <span className={item.autoRenewEnabled ? 'badge good' : 'badge warning'}>
              {item.autoRenewEnabled ? <RotateCw size={12} /> : <Ban size={12} />}
              {item.autoRenewEnabled ? 'Enabled' : 'Disabled'}
            </span>
            {item.autoRenewEnabled ? (
              <span style={{ color: 'var(--text-muted)', marginLeft: 8 }}>
                check interval {renewalCheckHours} h, renewal window {renewalThresholdDays} days
              </span>
            ) : null}
          </dd>

          <dt>Expires</dt>
          <dd>{item.expirationUtc ? formatDateTime(item.expirationUtc) : 'No Graph subscription'}</dd>

          <dt>Graph ID</dt>
          <dd className="mono">{item.id ?? '(not created)'}</dd>

          <dt>Resource</dt>
          <dd>
            <MatchValue value={item.resource} matches={item.resourceMatches} />
          </dd>

          <dt>Notification URL</dt>
          <dd>
            <MatchValue value={item.notificationUrl} matches={item.notificationUrlMatches} />
          </dd>

          <dt title="Whether the client state on the subscription is the one this deployment validates with">
            Client state
          </dt>
          <dd>
            <span className={item.clientStateMatches ? 'badge good' : 'badge'}>
              {item.clientStateMatches ? <CircleCheck size={12} /> : <CircleX size={12} />}
              {item.id === null
                ? 'No Graph subscription'
                : item.clientStateMatches
                  ? item.hasCustomClientState ? 'Matches saved value' : 'Matches configuration'
                  : 'Different or unset'}
            </span>
          </dd>
        </dl>

        <div className="row spread">
          {item.id ? <CopyButton value={item.id} label="Copy ID" /> : <span className="hint">Database record</span>}
          <div className="row" style={{ gap: 8 }}>
            {onAutoRenewToggle ? (
              <button disabled={anyBusy} onClick={onAutoRenewToggle}>
                {item.autoRenewEnabled ? <Ban size={14} /> : <RotateCw size={14} />}
                {busy === `auto-renew:${item.databaseId}`
                  ? 'Saving…'
                  : item.autoRenewEnabled ? 'Disable auto-renew' : 'Enable auto-renew'}
              </button>
            ) : null}
            {onRenew ? (
              <button disabled={anyBusy} onClick={onRenew}>
                <RotateCw size={14} />
                {busy === `renew:${item.id}` ? 'Renewing…' : `Renew ${days} days`}
              </button>
            ) : null}
            <button
              disabled={anyBusy || !onEdit}
              onClick={onEdit}
              title={onEdit ? undefined : 'Untracked subscriptions cannot be updated.'}
            >
              <Pencil size={14} />
              Edit
            </button>
            {item.isDefault ? (
              <button
                disabled
                title="The Default subscription is a reserved database record and cannot be deleted."
              >
                <Lock size={14} />
                Delete
              </button>
            ) : onDelete && confirming ? (
              <>
                <button onClick={() => setConfirming(false)} disabled={anyBusy}>
                  Cancel
                </button>
                <button
                  className="danger"
                  disabled={anyBusy}
                  onClick={() => {
                    setConfirming(false)
                    onDelete()
                  }}
                >
                  <Trash2 size={14} />
                  {busy === `delete:${item.id}` ? 'Deleting…' : 'Confirm delete'}
                </button>
              </>
            ) : onDelete ? (
              <button disabled={anyBusy} onClick={() => setConfirming(true)}>
                <Trash2 size={14} />
                Delete
              </button>
            ) : null}
          </div>
        </div>
      </div>
    </div>
  )
}

/** An inline note under a field: an icon, and text that wraps under itself rather than under the icon. */
function Hint({
  tone,
  icon,
  children,
}: {
  tone: 'critical' | 'warning'
  icon: ReactNode
  children: ReactNode
}) {
  return (
    <div
      style={{
        display: 'flex',
        gap: 7,
        fontSize: 12,
        color: tone === 'critical' ? 'var(--critical)' : 'var(--text-secondary)',
      }}
    >
      <span style={{ display: 'flex', marginTop: 2 }}>{icon}</span>
      <span>{children}</span>
    </div>
  )
}

/** A value shown next to whether it is the one this deployment expects. */
function MatchValue({ value, matches }: { value: string; matches: boolean }) {
  return (
    <span style={{ display: 'flex', alignItems: 'flex-start', gap: 7 }}>
      {matches ? (
        <CircleCheck size={13} color="var(--good)" style={{ marginTop: 4 }} />
      ) : (
        <CircleX size={13} color="var(--text-muted)" style={{ marginTop: 4 }} />
      )}
      <span className="mono">{value || '(none)'}</span>
    </span>
  )
}
