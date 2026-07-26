# DocumentChatBot.Web

Blazor Server chat UI for the document assistant — a ChatGPT/Claude-style chat page
with a sidebar listing every document in the corpus (with view/download links), so
it's always visible what the assistant's answers are grounded in. Thin host — all
corpus loading, retrieval, and prompting logic lives in `DocumentChatBot.Core`; this
project wires up the corpus engine as a singleton, serves the corpus files, and
renders the chat page.

See the [repo root README](../../README.md) for corpus setup and prerequisites.

## Run

```
dotnet run --project src/DocumentChatBot.Web
```

Then open the URL printed in the console output (defaults to
`http://localhost:5223`). The engine (corpus load + Foundry Local connection) is
built once at startup before the server starts accepting requests.

## What's here

- `Program.cs` — builds `CorpusChatEngine` from configuration, registers it as a
  singleton, and serves `data/corpus/` directly from disk at `/corpus/*` (used by the
  sidebar's view/download links — bypasses the wwwroot/static-web-assets pipeline
  since these are private runtime documents, not client assets).
- `Components/Pages/Chat.razor` — the chat page: corpus sidebar + streaming chat
  transcript, `@rendermode InteractiveServer`.
