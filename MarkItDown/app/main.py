from fastapi import FastAPI, UploadFile, File, HTTPException
from fastapi.responses import PlainTextResponse
from markitdown import MarkItDown
import io

app = FastAPI()

md = MarkItDown()

MAX_FILE_SIZE = 25 * 1024 * 1024  # 25MB


@app.post("/convert", response_class=PlainTextResponse)
async def convert(file: UploadFile = File(...)):
    try:
        content = await file.read()

        if len(content) > MAX_FILE_SIZE:
            raise HTTPException(status_code=413, detail="File too large")

        stream = io.BytesIO(content)

        result = md.convert_stream(stream)

        return result.text_content

    finally:
        await file.close()