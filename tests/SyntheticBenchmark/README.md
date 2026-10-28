# Synthetic benchmark

Measures precision, recall and F1 of the detectors and reviewers on fictional documents whose sensitive spans are known exactly. The template-based scores have the limits described below.

## Read this first: what the numbers mean

The synthetic score is NOT real-world accuracy. The documents come from templates written by us, so a detector tuned on them can look better than it is on a real BRD or FDS, and a detector that is good on real text can look worse because our templates are narrower, cleaner and more repetitive. Use the benchmark to compare versions of the same engine and to find systematic weaknesses, never to claim a production accuracy figure. All values are fictional (reserved `.example` domains, `555 01xx` style phone numbers, invented company stems and codenames).

## Modes at a glance

| Mode | Candidates come from | Measures | Cost |
| --- | --- | --- | --- |
| Detector | Deterministic rules and policy only | Rule coverage and false positives of the always-on layer | seconds |
| Oracle | Ground truth | Policy over-filtering, token consistency, export preservation | seconds |
| Full | Workers and local LLMs | End-to-end detection quality | minutes per document, not run by default |

## Layout

- `Common/` generator, Word writer, scorer, report, CLI (shared by every project).
- `Tool/` engine independent tool (plain `net8.0`): `generate`, `score`, `selftest`. Scores findings JSON from any engine.
- `Regular/`, `AI/` runners linked to the two apps. They add the `run` command.
- `Run.ps1` wrapper (mode, app, seeds, entities, explicit model paths for full mode).
- `results/` timestamped output, git-ignored. A new folder is created for every run and an existing folder is never reused.

## What the generator produces

Deterministic for a given `(seed, document index, entity count)`. Each document is a real `.docx` built with the Open XML SDK: headings, document control table, revision history table, requirement paragraphs, a Business Rules table with merged cells (grid span and vertical merge), a contact directory table and a sign-off sheet. About a third of paragraphs and cells are split across two or three runs at arbitrary points, including mid-word.

Sensitive spans (with category, entity id and alias kind):

- People, Philippine and Western, with middle initials, particles (`de la Cruz`, `delos Santos`, `van der Berg`, `von Eisen`, `della Rocca`) and these forms: canonical (`Maria T. Santos`), reversed (`Santos, Maria T.`), initial (`M. Santos`), dropMiddle (`Maria Santos`), username, email, phone, employee id.
- Companies with and without legal suffixes (`Brightmoor Logistics Inc.`, `Brightmoor Logistics`, `Brightmoor`) and a three letter acronym introduced in parentheses.
- Street addresses, company phone numbers, account numbers.
- Internal project and system names (`Project Nightjar`, `Nightjar Gateway`, short form `Nightjar`).

Must-preserve spans (decoys, kind in parentheses): public technologies with versions (tech), document version (version), outline numbers (outline), thresholds such as `Amount exceeds 1000` (threshold), roles such as Approver and Manager (role), generic headings (heading), document type words (doctype), dates (date), requirement ids such as `FR-4002` (reqid), quantities such as `within 30 days` (quantity) and table or revision labels (label).

Neutral spans (for example `Quezon City`) are neither required nor penalized.

Limits of the generator: one language, one writing style, no images, no headers or footers, no tracked changes, no real-world typos or OCR noise, surnames unique within a document so alias groups are unambiguous, and the category of a company inside a sentence is always obvious from a suffix or a following verb. Real documents are harder in every one of these respects.

## Scoring

`Scorer` compares findings (value, category, optional token) with the ground truth per document and aggregates.

- strict: normalized value (whitespace collapsed, upper-case, as the apps do) equal and category class equal. Classes: PROJECT = SYSTEM = PRODUCT; ORGANIZATION = VENDOR = DEPARTMENT; others must match exactly.
- lenient: an overlapping span (one contains the other at a word boundary, length ratio at least 0.5) in the same class. This separates boundary problems (for example a label included in the value) from true misses.
- anyCategory: overlap with the category ignored. This separates category confusion from misses.
- Every strict miss and strict false positive is listed with a reason: `not detected`, `boundary`, `wrong category`, or for false positives `decoy:<kind>` (the finding hit a must-preserve span) or `unlisted`.
- Token consistency (needs a `token` on findings): for each entity with two or more detected forms in one category class, do all forms share one token; per alias kind, how often the alias shares the canonical form's token; and the count of tokens shared by different entities (must be 0). Aliasing of a person across categories (a username versus the name) is not required, because the token prefix differs by category.
- Business-content preservation: every must-preserve span must still be present in the protected output (whitespace and case insensitive). Reported per kind.
- Residual leakage: sensitive spans still present as whole words in the protected output.

`selftest` checks the generator and scorer with an oracle engine and with deliberately broken engines, so a wrong scorer is caught.

## How to run

Install the .NET 8 SDK and use `dotnet` or `Run.ps1`.

```powershell
# deterministic detector layers only, no model, no LLM (default); use -Mode Oracle for the policy and identity ceiling
pwsh tests/SyntheticBenchmark/Run.ps1 -App Regular -Mode Detector -Seeds 1,2,3,4,5 -Entities 60
pwsh tests/SyntheticBenchmark/Run.ps1 -App AI      -Mode Detector -Seeds 1,2,3,4,5 -Entities 60

# direct
dotnet run --project tests/SyntheticBenchmark/Regular -c Release -- run --mode detector --seeds 1,2,3,4,5 --entities 60

# engine independent: generate documents and truth, then score any findings JSON
dotnet run --project tests/SyntheticBenchmark/Tool -c Release -- generate --seeds 1,2,3 --entities 60 --out out/docs
dotnet run --project tests/SyntheticBenchmark/Tool -c Release -- score --truth out/docs/truth.json --findings findings.json --protected-dir out/protected --out out/score
dotnet run --project tests/SyntheticBenchmark/Tool -c Release -- selftest
```

`--entities` is per seed; documents hold at most 20 entities each, so 60 entities per seed is three documents and five seeds is 300 entities in 15 documents.

Findings JSON format for `score`:

```json
{ "documents": [ { "id": "s1-d1", "findings": [ { "value": "Maria T. Santos", "category": "PERSON", "token": "[PERSON_001]" } ] } ]
}
```

`token` is optional. The protected directory holds `<id>.docx` or `<id>.txt` per document (the engine's output); omit it to skip preservation and leakage.

## Modes

Detector mode runs `Detector.Find`, `FindingPostProcessor.Complete` (which applies `DocumentPrivacyPolicy`) and the `PresidioDetector.IsEligibleCandidate` filter, i.e. the deterministic layers that run before any worker or model. It then creates mappings (all findings approved, as if the analyst chose Replace for each), runs `ConsolidatePersonAliases` and exports with `DocumentProcessor.Desensitize`, so token consistency and preservation are measured on the real export code.

Oracle mode (`-Mode Oracle`) offers the exact ground truth to the shared layers as if a perfect model had proposed it: `FindingPostProcessor.Complete` (policy filter), mapping creation, `ConsolidatePersonAliases` and the Word export. It is a ceiling for what the deterministic filtering and identity code can preserve, and it shows over-filtering, token inconsistency and export damage independent of any model. Its precision and recall are not detector quality.

Full mode calls `PresidioDetector.Find` (Presidio, GLiNER and Privacy Filter workers when present), `LocalAiReview.Find` with the detector findings (Qwen in the base app; Qwen, NuExtract and Gemma in the AI app) and `FindingPostProcessor.Complete` over the union. `Run.ps1 -Mode Full` sets explicit model paths from a separately provisioned Desktop portable folder. It loads local models, can take minutes per document and is not part of the deterministic CI gate. Start with `-Seeds 1 -Entities 20` (one document).

## Interpreting results

A result folder contains `docs/` (generated `.docx` and `truth.json`), `findings.json`, `protected/` (exported documents), `score.json`, `score.md`, per seed `score-seed*.json` and `run.json` (mode, app, seeds, git commit, command line). Generated synthetic documents and their protected copies contain only fictional data, but the folder is still git-ignored.

Compare runs only with the same seeds, entity count and generator version (`Generator.Version`). If the generator changes, bump the version and record a new baseline.
