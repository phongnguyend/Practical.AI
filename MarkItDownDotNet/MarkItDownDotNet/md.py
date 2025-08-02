from markitdown import MarkItDown

def convert_to_md(filename: str) -> str:
    markitdown = MarkItDown()
    result = markitdown.convert(filename)
    return result.text_content