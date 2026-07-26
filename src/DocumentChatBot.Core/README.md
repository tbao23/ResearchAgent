# DocumentChatBot.Core

Shared library — corpus loading, search, and the Foundry Local chat engine used by
both hosts (`DocumentChatBot.Console` and `DocumentChatBot.Web`). Not meant to be run
on its own; see the [repo root README](../../README.md) for setup and running.

## What's here

- `DocumentLoader.cs` — reads `.txt`/`.pdf`/`.docx` files and chunks them into
  `DocumentChunk`s, parsing date/doctype/superseded metadata from filenames.
- `TextIndex.cs` — TF-IDF index over the chunks with cosine-similarity search.
- `FoundryLocalBootstrapper.cs` — ensures the Foundry Local service is running and the
  configured model is downloaded/loaded, via the `foundry` CLI.
- `CorpusChatEngine.cs` — orchestrates the above: loads the corpus, builds the index,
  boots Foundry Local, and exposes `Ask(question)` which returns a streaming answer
  plus the source documents it was grounded in. Both hosts call this and only this,
  so retrieval/prompting behavior can't drift between them.
- `AppSettings.cs` — `AiSettings`/`CorpusSettings` POCOs bound from each host's
  `appsettings.json`.
