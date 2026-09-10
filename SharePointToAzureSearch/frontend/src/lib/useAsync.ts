import { useCallback, useEffect, useState } from 'react'

export interface AsyncState<T> {
  data: T | null
  error: string | null
  loading: boolean
  reload: () => void
}

/**
 * Loads a value whenever `deps` change, aborting the request in flight so a fast sequence of filter
 * changes settles on the last one rather than on whichever response happens to arrive last.
 */
export function useAsync<T>(
  load: (signal: AbortSignal) => Promise<T>,
  deps: unknown[],
): AsyncState<T> {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [nonce, setNonce] = useState(0)

  // The caller passes a fresh closure on every render, so the effect is keyed on `deps` instead.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const run = useCallback(load, deps)

  useEffect(() => {
    const controller = new AbortController()
    let active = true

    setLoading(true)
    run(controller.signal)
      .then((value) => {
        if (!active) return
        setData(value)
        setError(null)
      })
      .catch((cause: unknown) => {
        if (!active || (cause instanceof DOMException && cause.name === 'AbortError')) return
        setError(cause instanceof Error ? cause.message : String(cause))
      })
      .finally(() => {
        if (active) setLoading(false)
      })

    return () => {
      active = false
      controller.abort()
    }
  }, [run, nonce])

  return { data, error, loading, reload: () => setNonce((value) => value + 1) }
}

/** Delays a fast-changing value, so typing in a filter box does not issue a request per keystroke. */
export function useDebounced<T>(value: T, delayMs = 300): T {
  const [debounced, setDebounced] = useState(value)

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs)
    return () => clearTimeout(timer)
  }, [value, delayMs])

  return debounced
}
