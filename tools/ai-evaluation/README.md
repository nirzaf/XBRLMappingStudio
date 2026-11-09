# Synthetic AI retrieval evaluation

This small offline harness checks ranking behavior against a self-authored,
digest-pinned synthetic fixture. It reports top-k recall, top-one accuracy,
candidate precision and incorrect-candidate rate, no-match abstention, results
by category, Wilson 95% intervals, and the mean number of candidates presented
per answerable case.

```powershell
dotnet tools/ai-evaluation/bin/Release/net10.0/XBRLMappingStudio.AIEvaluation.dll --self-test
dotnet tools/ai-evaluation/bin/Release/net10.0/XBRLMappingStudio.AIEvaluation.dll --output artifacts/ai-evaluation/synthetic-report.json
```

The fixture and profile digests identify exactly what was measured. The report
sets `humanAcceptanceMetricsAvailable` to `false`; candidate precision is not
accepted precision, and candidate count is not measured reviewer effort. The
fixture is intentionally tiny and synthetic. Its results are engineering checks
only, not accounting mapping quality, a blind or held-out evaluation, a
calibrated confidence estimate, or evidence for a production model. AIM-07,
GAT-03, authorized held-out data, adjudication, thresholds, and uncertainty at
useful sample sizes remain open.
