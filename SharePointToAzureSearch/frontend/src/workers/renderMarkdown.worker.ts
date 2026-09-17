import { marked } from 'marked'

self.onmessage = (event: MessageEvent<string>) => {
  try {
    self.postMessage({ html: marked.parse(event.data, { async: false, gfm: true }) })
  } catch (error) {
    self.postMessage({ error: error instanceof Error ? error.message : String(error) })
  }
}
