param(
  [Parameter(Mandatory=$true)][string]$Dataset,
  [Parameter(Mandatory=$true)][uri]$IngestionBaseUrl,
  [Parameter(Mandatory=$true)][uri]$SearchBaseUrl,
  [Parameter(Mandatory=$true)][string]$TenantId,
  [Parameter(Mandatory=$true)][string]$OwnerId,
  [int]$ReadyTimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$datasetPath = (Resolve-Path -LiteralPath $Dataset).Path
$datasetDirectory = Split-Path -Parent $datasetPath
$data = Get-Content -Raw $datasetPath | ConvertFrom-Json
if ($data.documents.Count -lt 30 -or $data.documents.Count -gt 50) { throw 'Dataset must contain 30-50 documents.' }
if ($data.questions.Count -lt 1) { throw 'Dataset must contain questions.' }
if ([string]::IsNullOrWhiteSpace([string]$data.provenance.sourceUri)) { throw 'Dataset provenance.sourceUri is required.' }
$documentKeys = @{}
foreach ($document in $data.documents) {
  if ([string]::IsNullOrWhiteSpace([string]$document.key)) { throw 'Every document must have a non-empty key.' }
  if ($documentKeys.ContainsKey([string]$document.key)) { throw "Duplicate document key: $($document.key)." }
  $documentKeys[[string]$document.key] = $true
}
$questionKeys = @{}
foreach ($question in $data.questions) {
  if ([string]::IsNullOrWhiteSpace([string]$question.key)) { throw 'Every question must have a non-empty key.' }
  if ($questionKeys.ContainsKey([string]$question.key)) { throw "Duplicate question key: $($question.key)." }
  $questionKeys[[string]$question.key] = $true
  if (@($question.expected).Count -eq 0) { throw "Question $($question.key) must have at least one expected hit." }
  foreach ($item in $question.expected) {
    if (-not $documentKeys.ContainsKey([string]$item.documentKey)) {
      throw "Question $($question.key) references unknown document key: $($item.documentKey)."
    }
    if ($null -eq $item.sequence -or [int]$item.sequence -lt 0) { throw "Question $($question.key) has an invalid expected sequence." }
  }
}
$headers = @{ 'X-Tenant-Id'=$TenantId; 'X-Owner-Id'=$OwnerId }
$ids = @{}
$http = New-Object System.Net.Http.HttpClient
foreach ($header in $headers.GetEnumerator()) { $http.DefaultRequestHeaders.Add($header.Key, $header.Value) }
foreach ($document in $data.documents) {
  $documentPath = [string]$document.path
  if (-not [IO.Path]::IsPathRooted($documentPath)) { $documentPath = Join-Path $datasetDirectory $documentPath }
  if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf)) { throw "Missing corpus file for $($document.key)." }
  $metadata = $document.metadata | ConvertTo-Json -Compress -Depth 20
  $multipart = New-Object System.Net.Http.MultipartFormDataContent
  $stream = [IO.File]::OpenRead($documentPath)
  $fileContent = New-Object System.Net.Http.StreamContent($stream)
  $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse([string]$document.contentType)
  $multipart.Add($fileContent, 'file', [IO.Path]::GetFileName($documentPath))
  $multipart.Add((New-Object System.Net.Http.StringContent($metadata, [Text.Encoding]::UTF8, 'application/json')), 'metadata')
  try {
    $upload = $http.PostAsync([uri]::new($IngestionBaseUrl, '/documents'), $multipart).GetAwaiter().GetResult()
    $uploadBody = $upload.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $upload.IsSuccessStatusCode) { throw "Upload failed for $($document.key): HTTP $([int]$upload.StatusCode) $uploadBody" }
    $response = $uploadBody | ConvertFrom-Json
  } finally {
    $multipart.Dispose()
    $stream.Dispose()
  }
  $ids[$document.key] = [string]$response.id
}
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
foreach ($entry in $ids.GetEnumerator()) {
  do {
    $status = Invoke-RestMethod -Uri ([uri]::new($IngestionBaseUrl, "/documents/$($entry.Value)")) -Headers $headers
    if ($status.status -eq 'Failed') { throw "Ingestion failed for $($entry.Key): $($status.processingError)" }
    if ($status.status -eq 'Ready') { break }
    if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "Ready timeout for $($entry.Key)." }
    Start-Sleep -Milliseconds 500
  } while ($true)
}
$latencies = [System.Collections.Generic.List[double]]::new()
$details = @()
$recallSum = 0.0
$reciprocalRankSum = 0.0
foreach ($question in $data.questions) {
  $body = @{ query=[string]$question.text; limit=5 } | ConvertTo-Json -Compress
  $timer = [Diagnostics.Stopwatch]::StartNew()
  $response = Invoke-RestMethod -Method Post -Uri ([uri]::new($SearchBaseUrl, '/search')) -Headers $headers -ContentType 'application/json' -Body $body
  $timer.Stop(); $latencies.Add($timer.Elapsed.TotalMilliseconds)
  if ($null -eq $response.results) { throw "Search response for $($question.key) did not contain results." }
  $expected = @{}
  foreach ($item in $question.expected) { $expected["$($ids[$item.documentKey]):$($item.sequence)"] = $true }
  $rank = $null
  $recalled = @{}
  for ($i=0; $i -lt [Math]::Min(5, $response.results.Count); $i++) {
    $key = "$($response.results[$i].citation.documentId):$($response.results[$i].citation.sequence)"
    if ($expected.ContainsKey($key)) {
      $recalled[$key] = $true
      if ($null -eq $rank) { $rank = $i + 1 }
    }
  }
  $questionRecall = $recalled.Count / $expected.Count
  $recallSum += $questionRecall
  if ($null -ne $rank) { $reciprocalRankSum += 1.0 / $rank }
  $top5 = @()
  for ($i=0; $i -lt [Math]::Min(5, $response.results.Count); $i++) {
    $top5 += [ordered]@{ rank=$i+1; documentId=[string]$response.results[$i].citation.documentId; sequence=[int]$response.results[$i].citation.sequence; score=[double]$response.results[$i].score }
  }
  $details += [ordered]@{ questionKey=$question.key; recallAt5=$questionRecall; firstRelevantRank=$rank; latencyMs=[Math]::Round($timer.Elapsed.TotalMilliseconds,3); top5=$top5 }
}
$ordered = @($latencies | Sort-Object)
function Percentile([double[]]$values, [double]$p) { $values[[Math]::Min($values.Count-1,[Math]::Ceiling($p*$values.Count)-1)] }
$result = [ordered]@{
  status='completed'; measuredAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); datasetVersion=[string]$data.version
  provenance=$data.provenance; scope=@{tenantId=$TenantId;ownerId=$OwnerId}; documentCount=$data.documents.Count; questionCount=$data.questions.Count
  recallAt5=$recallSum/$data.questions.Count; mrr=$reciprocalRankSum/$data.questions.Count
  latencyMs=@{ p50=(Percentile $ordered 0.50); p95=(Percentile $ordered 0.95) }; documents=$ids; questions=$details
}
$path = Join-Path $PSScriptRoot ("results-{0}.json" -f [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
$json = (($result | ConvertTo-Json -Depth 20) -replace "`r`n", "`n") + "`n"
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($path, $json, $utf8WithoutBom)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'results.json'), $json, $utf8WithoutBom)
$http.Dispose()
Write-Output $path
