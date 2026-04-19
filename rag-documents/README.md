# Uploading RAG documents

This document explains how to upload RAG documents to the WebhookApi `/api/rag/upload` endpoint.

Endpoint

- POST https://webhook.tailc2bda.ts.net/api/rag/upload

Form fields

- `title` (text) — document title
- `source` (text) — source identifier
- `tags` (text) — comma-separated or repeated tag fields
- `file` (file) — the document to upload (optional if `text` field provided)
- `text` (text) — plain document text (optional if `file` provided)

## Example (Windows / PowerShell using curl.exe)

Run this from your machine to upload `listing-description.md`:

```bash
curl.exe -v -F "title=Listing description" -F "source=listing_description" -F "tags=listing_description,listing_redwood_iloilo" -F "file=@d:\src\WebhookApi\rag-documents\listing-description.md" "https://webhook.tailc2bda.ts.net/api/rag/upload" --insecure
```

Notes

- If the server uses a self-signed certificate, include `--insecure` or disable SSL verification in your client.
- You can supply `tags` as a single comma-separated string or by repeating the `tags` field.
- The endpoint returns a JSON object with a `documentId` on success (HTTP 202 Accepted).

Troubleshooting

- If you see database errors about `MetadataJson` / `jsonb`, ensure the server has the latest code that maps and writes JSON metadata as `jsonb` (see project README for details).
- If you see `Embedding request failed: NotFound` for a model, verify the configured embedding model exists on your AI provider (e.g., run `ollama list` / `ollama pull <model>` or configure OpenAI keys).

Want the server to accept a `metadata` JSON field? Open an issue or request and I can add support to the upload handler to accept and store JSON metadata.

## Uploading category files (house-rules)

You can keep deduplicated rules as separate category Markdown files in the `rag-documents/house-rules` folder for easier maintenance and ingestion. A helper PowerShell script is included at `rag-documents/house-rules/upload-house-rules.ps1` to upload every file in that folder to the `/api/rag/upload` endpoint.

Quick steps

- Edit or add category files under `rag-documents/house-rules` (one `.md` per category).
- From PowerShell run:

```powershell
cd D:\src\WebhookApi\rag-documents\house-rules
.\upload-house-rules.ps1 -Insecure
```

Options

- To point at a different server, pass `-UploadUrl "https://your-server/api/rag/upload"` to the script.
- Remove `-Insecure` if your server has a valid TLS certificate.
- The script uses the file base-name as the document `title` and `house_rules/<basename>` as the `source` tag; it also adds a `house_rules` tag.

Manual single-file upload example (curl)

```bash
curl.exe -v -F "title=Check-In - Redwood" -F "source=house_rules/check_in" -F "tags=house_rules,check_in" -F "file=@D:\src\WebhookApi\rag-documents\house-rules\02-check-in-check-out.md" "https://webhook.tailc2bda.ts.net/api/rag/upload" --insecure
```

Keeping per-category files makes RAG chunking simpler and keeps content maintainable. If you want, the repository now contains a pre-populated set of category files in `rag-documents/house-rules`.
