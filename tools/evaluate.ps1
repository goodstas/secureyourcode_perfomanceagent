# Precision/recall of a report against test-assets/ground-truth.json (plan section 5).
# Writes metrics.json into the report folder. No argument: the latest report (<StateRoot>/reports/latest.txt).
#   powershell -ExecutionPolicy Bypass -File tools/evaluate.ps1 [<report folder> | <report.json>]
param([string]$Report)

$project = Join-Path $PSScriptRoot 'evaluate'
if ($Report) {
    dotnet run --project $project --configuration Release -- $Report
} else {
    dotnet run --project $project --configuration Release
}
exit $LASTEXITCODE
