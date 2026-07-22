"""Run a simple LangChain prompt against an OpenAI chat model."""

import argparse
import os

from dotenv import load_dotenv
from langchain_core.prompts import ChatPromptTemplate
from langchain_openai import ChatOpenAI


def create_chain():
    """Create the prompt and model pipeline."""
    prompt = ChatPromptTemplate.from_messages(
        [
            ("system", "You are a concise and helpful AI assistant."),
            ("human", "{question}"),
        ]
    )
    model = ChatOpenAI(model=os.getenv("OPENAI_MODEL", "gpt-5.6-sol"))
    return prompt | model


def main() -> None:
    """Load configuration, invoke the chain, and print its response."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "question",
        nargs="?",
        default="What is LangChain? Answer in one sentence.",
    )
    args = parser.parse_args()

    load_dotenv()
    if not os.getenv("OPENAI_API_KEY"):
        parser.error("OPENAI_API_KEY is missing; copy .env.example to .env and set it")

    response = create_chain().invoke({"question": args.question})
    print(response.content)


if __name__ == "__main__":
    main()
