# DocumentChatBot

A retrieval-augmented chat assistant that answers questions grounded only in a local
corpus of regulatory documents (`.txt`, `.pdf`, `.docx`). It runs entirely on your
machine against [Foundry Local](https://learn.microsoft.com/windows/ai/foundry-local/)
— no cloud model calls.

## Solution layout

```
DocumentChatBot.slnx
data/corpus/                    # your documents live here (gitignored — private data)
src/
  DocumentChatBot.Core/         # corpus loading, TF-IDF search index, Foundry Local
                                 # bootstrap, and CorpusChatEngine (shared by both hosts)
  DocumentChatBot.Console/      # terminal chat loop
  DocumentChatBot.Web/          # Blazor Server chat UI (ChatGPT-style, with a
                                 # corpus sidebar you can view/download files from)
```

Both hosts call the same `CorpusChatEngine` in Core, so retrieval and prompting
behavior is identical whether you're in the terminal or the browser.

## Prerequisites

- .NET SDK (net9.0 for Core/Console, net10.0 for Web — a single modern SDK install
  covers both)
- [Foundry Local](https://learn.microsoft.com/windows/ai/foundry-local/) installed,
  with the `foundry` CLI on your PATH. Both apps will start the service and download/load
  the configured model automatically on first run if it isn't already running — the
  first model download can take a while.

## Setup

1. Drop your source documents into `data/corpus/` at the repo root. Supported types:
   `.txt`, `.pdf`, `.docx`.
2. Optionally name files to carry metadata the assistant surfaces alongside answers:
   `YYYY-MM-DD_doctype_description[_superseded].ext`, e.g.
   `2024-03-01_comment_letter_annuity_rule_superseded.pdf`. Recognized `doctype`
   values: `comment_letter`, `position_paper`, `guidance`, `brief`. All parts are
   optional — an unadorned filename works fine too.
3. Review `appsettings.json` in `src/DocumentChatBot.Console/` and
   `src/DocumentChatBot.Web/` (`Ai` section) if you want to point at a different
   Foundry Local model or change the system prompt.

## Running

Console:

```
dotnet run --project src/DocumentChatBot.Console
```

Web (Blazor Server chat UI):

```
dotnet run --project src/DocumentChatBot.Web
```

Then open the URL printed in the console output (defaults to
`http://localhost:5223`).

## How it works

1. `DocumentLoader` chunks every document in `data/corpus/` and `TextIndex` builds a
   TF-IDF index over the chunks.
2. On each question, `CorpusChatEngine.Ask` retrieves the top-matching chunks and
   builds a prompt that instructs the model to answer only from that retrieved
   context, citing sources.
3. The Web UI shows every corpus document in a sidebar (with view/download links) so
   it's always visible what the assistant's answers are grounded in.
