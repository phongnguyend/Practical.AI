import {
  File,
  FileArchive,
  FileAudio,
  FileCode,
  FileImage,
  FileSpreadsheet,
  FileText,
  FileType2,
  FileVideo,
  Presentation,
} from 'lucide-react'
import { fileExtension } from '../lib/format'

type Kind =
  | 'doc'
  | 'sheet'
  | 'slide'
  | 'pdf'
  | 'text'
  | 'code'
  | 'image'
  | 'video'
  | 'audio'
  | 'archive'
  | 'other'

const KINDS: Record<Kind, { Icon: typeof File; label: string }> = {
  doc: { Icon: FileType2, label: 'Word document' },
  sheet: { Icon: FileSpreadsheet, label: 'Spreadsheet' },
  slide: { Icon: Presentation, label: 'Presentation' },
  pdf: { Icon: FileText, label: 'PDF' },
  text: { Icon: FileText, label: 'Text' },
  code: { Icon: FileCode, label: 'Code or markup' },
  image: { Icon: FileImage, label: 'Image' },
  video: { Icon: FileVideo, label: 'Video' },
  audio: { Icon: FileAudio, label: 'Audio' },
  archive: { Icon: FileArchive, label: 'Archive' },
  other: { Icon: File, label: 'File' },
}

const BY_EXTENSION: Record<string, Kind> = {
  doc: 'doc', docx: 'doc', docm: 'doc', dot: 'doc', odt: 'doc', rtf: 'doc',
  xls: 'sheet', xlsx: 'sheet', xlsm: 'sheet', xlsb: 'sheet', csv: 'sheet', tsv: 'sheet', ods: 'sheet',
  ppt: 'slide', pptx: 'slide', pptm: 'slide', odp: 'slide',
  pdf: 'pdf',
  txt: 'text', md: 'text', markdown: 'text', log: 'text', rst: 'text',
  json: 'code', xml: 'code', html: 'code', htm: 'code', css: 'code', js: 'code', ts: 'code',
  yml: 'code', yaml: 'code', sql: 'code', cs: 'code', py: 'code',
  png: 'image', jpg: 'image', jpeg: 'image', gif: 'image', webp: 'image', svg: 'image',
  bmp: 'image', tif: 'image', tiff: 'image', heic: 'image',
  mp4: 'video', mov: 'video', avi: 'video', mkv: 'video', webm: 'video', wmv: 'video',
  mp3: 'audio', wav: 'audio', m4a: 'audio', flac: 'audio', ogg: 'audio',
  zip: 'archive', '7z': 'archive', rar: 'archive', tar: 'archive', gz: 'archive',
}

/**
 * The kind of file, from its extension where there is one and its content type otherwise. The
 * extension is checked first because SharePoint reports a generic or missing MIME type more often
 * than it reports a wrong name.
 */
function kindOf(name: string | null, mimeType: string | null): Kind {
  const extension = name ? fileExtension(name) : null
  if (extension && extension in BY_EXTENSION) {
    return BY_EXTENSION[extension]
  }

  const mime = mimeType?.toLowerCase() ?? ''
  if (mime.includes('wordprocessingml') || mime === 'application/msword') return 'doc'
  if (mime.includes('spreadsheetml') || mime.includes('ms-excel') || mime === 'text/csv') return 'sheet'
  if (mime.includes('presentationml') || mime.includes('ms-powerpoint')) return 'slide'
  if (mime === 'application/pdf') return 'pdf'
  if (mime.startsWith('image/')) return 'image'
  if (mime.startsWith('video/')) return 'video'
  if (mime.startsWith('audio/')) return 'audio'
  if (mime.includes('zip') || mime.includes('compressed')) return 'archive'
  if (mime === 'application/json' || mime.includes('xml') || mime === 'text/html') return 'code'
  if (mime.startsWith('text/')) return 'text'
  return 'other'
}

/**
 * Colour comes from a class rather than a prop, so the hue is a theme token that swaps with the rest
 * of the palette instead of a hex baked into the markup.
 */
export function FileTypeIcon({
  name = null,
  mimeType = null,
  size = 15,
}: {
  name?: string | null
  mimeType?: string | null
  size?: number
}) {
  const kind = kindOf(name, mimeType)
  const { Icon, label } = KINDS[kind]
  return <Icon size={size} className={`file-icon file-icon-${kind}`} aria-label={label} />
}
