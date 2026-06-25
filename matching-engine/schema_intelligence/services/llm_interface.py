"""
services/llm_interface.py

Single point where LLM provider is chosen.
Swap providers here without touching any other file.

Literature justification for Ollama + Llama 3.1:
- Privacy motivation: Zhang et al. (ICDE 2023) specifically addresses
  scenarios where source data cannot be shared due to privacy constraints.
  Running inference locally via Ollama ensures schema metadata never
  leaves the organisation's infrastructure.
- Model selection: Touvron et al., "Llama 2: Open Foundation and
  Fine-Tuned Chat Models", arXiv 2023 — foundational paper for the
  Llama model family. Llama 3.1 is the current generation successor.
- Open-source reproducibility: ArcheType (Feuer et al., VLDB 2024)
  specifically emphasises open-source LLMs over closed-source ones
  because "closed-source models are constantly updated, reported
  results cannot be reproduced." Ollama + Llama 3.1 ensures full
  experimental reproducibility — a core thesis requirement.
"""
import logging
import httpx
from ..config import settings

logger = logging.getLogger(__name__)


async def call_llm(prompt: str, max_tokens: int = 1000) -> str:
    # provider = settings.ollama_model.lower()
    provider = settings.llm_provider.lower()  # ← CORRECT

    if provider == "stub":
        logger.warning(
            "LLM_PROVIDER=stub — returning empty list. "
            "Set a real provider in .env to enable LLM classification."
        )
        return "[]"

    elif provider == "openai":
        # pip install openai
        # Set OPENAI_API_KEY in .env
        # from openai import AsyncOpenAI
        # client = AsyncOpenAI(api_key=settings.openai_api_key)
        # response = await client.chat.completions.create(
        #     model="gpt-4o",
        #     messages=[{"role": "user", "content": prompt}],
        #     max_tokens=max_tokens,
        #     temperature=0.0
        # )
        # return response.choices[0].message.content or "[]"
        raise NotImplementedError(
            "Uncomment OpenAI block in llm_interface.py "
            "and set LLM_PROVIDER=openai in .env"
        )

    elif provider == "anthropic":
        # pip install anthropic
        # Set ANTHROPIC_API_KEY in .env
        # import anthropic
        # client = anthropic.AsyncAnthropic(
        #     api_key=settings.anthropic_api_key)
        # message = await client.messages.create(
        #     model="claude-sonnet-4-6",
        #     max_tokens=max_tokens,
        #     messages=[{"role": "user", "content": prompt}]
        # )
        # return message.content[0].text
        raise NotImplementedError(
            "Uncomment Anthropic block in llm_interface.py "
            "and set LLM_PROVIDER=anthropic in .env"
        )

    elif provider == "ollama":
        logger.info(
            "Calling Ollama model '%s' at %s",
            settings.ollama_model,
            settings.ollama_base_url
        )
        try:
            async with httpx.AsyncClient() as client:
                response = await client.post(
                    f"{settings.ollama_base_url}/api/generate",
                    json={
                        "model":  settings.ollama_model,
                        "prompt": prompt,
                        "stream": False,
                        "options": {
                            "num_predict": max_tokens,
                            "temperature": 0
                        }
                    },
                    timeout=180.0
                )
                response.raise_for_status()
                raw = response.json()["response"]
                logger.debug("Ollama raw response: %s", raw[:200])
                return raw

        except httpx.ConnectError:
            logger.error(
                "Cannot connect to Ollama at %s. "
                "Is 'ollama serve' running?",
                settings.ollama_base_url
            )
            return "[]"

        except httpx.TimeoutException:
            logger.error(
                "Ollama timed out after 180s. "
                "Try a smaller model or increase timeout."
            )
            return "[]"

        except Exception as e:
            logger.error("Ollama error: %s", e)
            return "[]"

    else:
        raise ValueError(
            f"Unknown LLM_PROVIDER: '{provider}'. "
            f"Choose: stub | openai | anthropic | ollama"
        )