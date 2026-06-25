from pydantic_settings import BaseSettings

class Settings(BaseSettings):
    # llm_provider:      str   = "stub"
    llm_provider:      str   = "ollama"
    scoping_threshold: float = 0.25
    retrieval_top_k:   int   = 3
    host:              str   = "0.0.0.0"
    port:              int   = 5001
    ollama_base_url:   str   = "http://localhost:11434"
    ollama_model:      str   = "llama3.1:latest"

    class Config:
        env_file = ".env"

settings = Settings()