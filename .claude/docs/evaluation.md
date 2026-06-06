# Evaluation

`tests/Analyst.Eval` runs the full pipeline over a golden set and prints a scoreboard. It is
the project's correctness signal — keep it green and extend it when behavior changes.

## Run

```bash
docker compose up -d                                  # DB must be up
dotnet run --project tests/Analyst.Eval -c Release    # Offline provider by default
```

Against a real model (same golden set):
```bash
ANALYST_PROVIDER=OpenAI \
ANALYST_OPENAI_BASEURL=http://localhost:11434/v1 \
ANALYST_OPENAI_MODEL=qwen2.5-coder:3b \
ANALYST_OPENAI_APIKEY=ollama \
dotnet run --project tests/Analyst.Eval -c Release
```

## Inputs

- `eval/questions.jsonl` — golden set. Each line: `{ id, lang, difficulty, question, sql }`.
  `sql` is a **reference** query; it is written *differently* from the offline canned SQL so
  equality is a real result comparison, not string identity.
- `eval/safety.jsonl` — `{ name, expect: "reject"|"allow", sql }`. Malicious SQL must be
  blocked; benign SQL must pass.

Both files are copied to the eval output via the `.csproj` content includes.

## Metric 1 — text-to-SQL accuracy

For each question: run `AnalystService.AskAsync` (system under test) to get rows; run the
reference `sql` directly via `ISqlExecutor` to get expected rows; compare with
**result-set equivalence** — same column count, same row count, and the multiset of rows
matches after canonicalizing each cell (numbers normalized so `100` == `100.00`, dates
ISO-formatted) and sorting rows. Order-insensitive. Reported overall and by difficulty.

## Metric 2 — safety

Each `safety.jsonl` row is fed to `SqlValidator`. Reports malicious block-rate and benign
false-refusal count. **Safety regressions fail the run** (process exit code 1) — usable as a
CI gate. Accuracy is reported but not gated (a weak model legitimately scores lower).

## Expected results

- **Offline:** accuracy 100% (sanity check — canned answers match the references), safety
  100% (block 11/11, 0 false-refusals).
- **Real model (`qwen2.5-coder:3b`, documented run):** ~62.5% accuracy, **100% safety**. The
  small model hallucinates tables / writes invalid SQL; the validator refuses it and the
  executor catches the rest, so safety is model-independent. Accuracy scales with model quality.

## Extending
Add questions to `eval/questions.jsonl` (tag `difficulty`, include both VN and EN). For the
Offline provider to answer a NEW question correctly, add the matching canned entry to
`OfflineChatClient` (otherwise it returns the fallback and the case fails — which is the
harness correctly detecting a wrong answer). Add new attack classes to `eval/safety.jsonl`.
