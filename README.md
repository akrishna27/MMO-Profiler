# MMO-Profiler

A chat profiler for MMOs. It reads incoming chat, asks a local AI model (via [Ollama](https://ollama.com)) for the tone and intent of each message, and builds a local profile of each character: who they are, what they tend to talk about, and when they are usually chatting.

The first target is Final Fantasy XIV as a [Dalamud](https://dalamud.dev) plugin. The core is game-agnostic so other MMOs can be added with a thin adapter.

**Status:** specification only. See [docs/SPEC.md](docs/SPEC.md).

## Privacy

All data stays on your computer. Chat text is sent only to the Ollama server you configure (localhost by default). Storing raw message text is off by default.

## Licence

MIT. See [LICENSE](LICENSE).
