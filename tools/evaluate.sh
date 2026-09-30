#!/usr/bin/env sh
# Precision/recall of a report against test-assets/ground-truth.json (plan section 5).
# Writes metrics.json into the report folder. No argument: the latest report (<StateRoot>/reports/latest.txt).
#   sh tools/evaluate.sh [<report folder> | <report.json>]
exec dotnet run --project "$(dirname "$0")/evaluate" --configuration Release -- "$@"
