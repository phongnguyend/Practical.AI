export function isPreviewableOfficeFile(name: string): boolean {
  return /\.(docx|xlsx|pptx)$/i.test(name)
}
