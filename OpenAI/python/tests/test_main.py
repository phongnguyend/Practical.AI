"""Tests for the command-line example."""

from practical_openai.main import create_chain


def test_create_chain_returns_runnable(monkeypatch) -> None:
    monkeypatch.setenv("OPENAI_API_KEY", "test-api-key")
    chain = create_chain()

    assert hasattr(chain, "invoke")
