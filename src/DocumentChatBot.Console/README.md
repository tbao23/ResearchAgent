# DocumentChatBot.Console

Terminal chat loop for the document assistant. Thin host — all corpus loading,
retrieval, and prompting logic lives in `DocumentChatBot.Core`; this project just
wires up configuration and a read/print loop around `CorpusChatEngine`.

See the [repo root README](../../README.md) for corpus setup and prerequisites.

## Run

```
dotnet run --project src/DocumentChatBot.Console
```

Optionally pass a corpus directory to use instead of the one in `appsettings.json`:

```
dotnet run --project src/DocumentChatBot.Console -- "C:\path\to\your\documents"
```

Type a question and press Enter; type `exit` to quit.
