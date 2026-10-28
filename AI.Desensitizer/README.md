# AI Desensitizer

AI Desensitizer is an experimental local document-preparation tool, forked from Desensitizer. It tokenizes identifying values in `.docx`, `.xlsx`, `.pptx`, `.csv`, `.md` and `.txt` documents before a person chooses to share a protected copy with cloud AI. Three local reviewers provide additional evidence and disagreement handling; superior accuracy is not established for every document. Preserve business rules, ordinary quantities and supported tables. See the repository `README.md` for the common quick start.

## What differs from Desensitizer

- Same local detectors (rules, Presidio, GLiNER, OpenAI Privacy Filter), then **sequential Qwen3-4B, NuExtract 2.0 2B and Gemma 3 4B reviews**.
- **Two agreeing reviewers**, or one reviewer corroborated by an eligible independent direct detector, can support replacement. Unsupported single-reviewer findings stay in Review. Consensus is an agreement signal, **not a calibrated probability** of correctness. Existing analyst decisions and category/disagreement safeguards still apply.
- Document-wide entity keys let strong name variants share one replacement token.
- Optional automatic residual replacement with a five-pass safety limit.
- If a reviewer answers in prose or repeats until cut off, the app retries with a structured request. An incomplete scan keeps export blocked.
- Generic document headings are filtered before they can become mappings.
- Separate default Windows identity and local data: `%LOCALAPPDATA%\AI Desensitizer` (projects, settings). Deliberately imported project files can still be shared, subject to project locking; do not run concurrent writers.
- Runs three language models, so scans take longer and use more memory (about 9 to 11 GB with everything resident is an unverified estimate). On a 16 GB laptop close other heavy programs.

Architecture notes: `AI-ARCHITECTURE.md`.

Rehydration first shows a count-only preview. Unknown tokens stay unchanged, incompatible saved tokens block restoration, and supported aliases use the saved canonical spelling. Context-limited label feedback is reversible project configuration, not model training. Dedicated table-context review currently applies to DOCX; XLSX/PPTX review extracted content and preserve supported structure through format-specific checks.

For replacing reviewers or detector workers, read [the model-swapping guide](../docs/MODEL-SWAPPING.md). Changing a model path does not replace its adapter, schema or prompt template.

## Supported files

`.docx`, `.xlsx`, `.pptx`, `.csv`, `.md` and `.txt`. Old formats (`.doc`, `.pdf`, `.xls`, `.ppt`, `.rtf`, `.odt`) and macro files are refused with advice to save a copy as `.docx`, `.xlsx` or `.pptx`. Excel and PowerPoint files have pictures, charts, embedded objects, external links and alt text removed.

## Licences

Gemma 3 is under the Gemma Terms of Use and Prohibited Use Policy, which are not open-source licences and carry conditions on redistribution. See `docs/LICENSE-REVIEW.md` and `THIRD-PARTY-NOTICES.txt` before sharing a portable folder.

## Build

Requires the .NET 8 SDK on Windows:

```powershell
dotnet build .\Desensitizer.csproj -c Release
```

For a full build, test and deploy use `tools\ci.ps1` and `tools\Publish-And-Deploy.ps1` from the repository root. Large model files are intentionally excluded from Git and must be installed separately in the packaged application.
