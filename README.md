# Desensitizer: local document preparation for responsible AI use

Two experimental Windows applications that help separate identifying information from business logic before a person chooses to share a document with cloud AI.

This is an AI-governance engineering endeavor, not a compliance certification, automatic data-classification authority or guarantee of anonymization. Human review remains necessary.

## Two local architectures

| Application | Review pipeline |
| --- | --- |
| Desensitizer | Deterministic rules and local detector workers, followed by a Qwen contextual review |
| AI Desensitizer | The same detector layers, followed by sequential Qwen, NuExtract and Gemma reviews with disagreement handling |

Multiple model agreement is an evidence signal, not a calibrated probability. More reviewers cost more time and memory and do not establish superior accuracy for every document.

Two reviewers can support replacement; an eligible direct identifier can also be supported by one reviewer and an independent detector. Category disagreements, analyst decisions and export safeguards still apply. Dedicated structured table-context review currently applies to DOCX. XLSX/PPTX review extracted cell/slide text with format-specific structure checks.

## What it demonstrates

- Token mappings for names, organizations, contact details, addresses and identifying references, while retaining business-rule context, tables and ordinary quantities where supported.
- Local inference through authenticated loopback processes, with process ownership checks and runtime hash manifests.
- Encrypted project mappings and bounded resumable review progress, project write locks and explicit decisions: Replace, Keep or Review.
- Protected-copy checks that block incomplete review, unsafe package content or unresolved findings rather than pretending an export is safe.
- Atomic output replacement and a count-only restoration preview, with unknown-token and conflicting-mapping warnings.
- Reversible, category/context-limited suggestion feedback. Saving feedback does not train model weights.

Supported input formats: DOCX, XLSX, PPTX, CSV, Markdown and text. Images and embedded objects are removed or replaced, not analyzed with OCR. PDF and legacy/macro-enabled Office formats are not supported. Some metadata and hidden-content channels are removed or cause export to be blocked. Inspect both the findings and the protected copy before sharing.

## Build

Requires Windows and the .NET 8 SDK. Build the applications separately:

```powershell
dotnet build Desensitizer/Desensitizer.csproj -c Release
dotnet build AI.Desensitizer/Desensitizer.csproj -c Release
```

Source lives in `Desensitizer`, `AI.Desensitizer` and `Shared`. Python detector sources are in `Desensitizer.Analyzer`. Models, compiled detector executables and inference runtimes are **not included**. Building the UI does not provision them or make inference ready.

## Use your preferred AI

Read **[the model-swapping guide](docs/MODEL-SWAPPING.md)**. It covers supported development overrides, local paths, model-specific adapters and prompt formats, runtime manifests, detector worker contracts, licences, testing and rollback. A GGUF filename change does not make an arbitrary model compatible with every reviewer.

Obtain each model and runtime separately under its publisher's terms, keep a tested baseline, and validate on held-out approved or fictional data. Do not turn integrity checks off or send documents to a new provider merely to make a replacement work.

## Verify

```powershell
powershell -NoProfile -File tools/ci.ps1
```

The deterministic gate covers document transformation, identity rules, UI logic, encrypted storage, runtime safety and fictional adversarial packages. It does not establish live-model accuracy, cloud parity, clean-machine readiness or employer approval. Long-document live evaluation is separate. Repository examples are fictional; confidential fixtures and historic local test outputs are not published.

## Documentation

- [Model swapping and validation](docs/MODEL-SWAPPING.md)
- [Analyst quick start](docs/ANALYST-QUICKSTART.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Building local detector workers](docs/ANALYZER-BUILD.md)
- [Third-party notices](THIRD-PARTY-NOTICES.txt)

## Privacy and distribution boundary

This repository is a reviewed source-only snapshot with fresh history. It excludes confidential documents, original mappings, encrypted projects, user settings, local diagnostics, model weights, compiled programs and the private development history. Some embedded test/reference labels are fictionalized for publication. Do not upload your entire working folder, `work`, `outputs` or project files when reporting a problem.

The original software has no general reuse licence selected yet. Publication makes the source available for inspection; it does not grant a general licence to reuse or redistribute it. Third-party components and models retain their own terms. Contact the repository owner for permissions and policy questions.
