<#
Uploads all Markdown files in the house-rules folder to the WebhookApi RAG upload endpoint.
Usage examples:
  .\upload-house-rules.ps1                                  # uses defaults
  .\upload-house-rules.ps1 -Folder "D:\path\to\folder" -UploadUrl "https://example/api/rag/upload" -Insecure
#>
param(
  [string]$Folder = "d:\src\WebhookApi\rag-documents\house-rules",
  [string]$UploadUrl = "https://webhook.tailc2bda.ts.net/api/rag/upload",
  [switch]$Insecure
)

if (-not (Test-Path $Folder)) {
  Write-Error "Folder not found: $Folder"
  exit 1
}

Get-ChildItem -Path $Folder -Filter *.md -File | ForEach-Object {
  $file = $_.FullName
  $title = $_.BaseName
  $source = "house_rules/$title"
  $tags = "house_rules,$title"

  Write-Host "Uploading: $file -> $UploadUrl (title=$title)"

  $args = @('-v')
  $args += '-F'; $args += "title=$title"
  $args += '-F'; $args += "source=$source"
  $args += '-F'; $args += "tags=$tags"
  $args += '-F'; $args += ("file=@{0}" -f $file)
  $args += $UploadUrl
  if ($Insecure) { $args += '--insecure' }

  & curl.exe @args
  $code = $LASTEXITCODE
  if ($code -ne 0) {
    Write-Warning "curl exited with code $code for file $file"
  } else {
    Write-Host "Uploaded $title (exit $code)"
  }
}
