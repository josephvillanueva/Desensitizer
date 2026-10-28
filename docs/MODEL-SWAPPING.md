# Bring your own local model

Desensitizer has two Windows applications. The base app uses one Qwen-style generative reviewer. AI Desensitizer uses three model-specific reviewers (Qwen, NuExtract and Gemma), in addition to local detector workers. This guide explains the current extension points, not a universal model compatibility promise.

## Before changing anything

1. Work in a separate copy of the portable folder, not your only working installation. Back up encrypted projects separately and keep their passphrases private.
2. Obtain weights from the model publisher or a trusted conversion source. Record repository, revision, filename, SHA256, quantization and licence. Accept any required publisher terms yourself. Check your organization's policy before using or distributing weights.
3. Use only fictional documents until you have verified that the runtime stays local and the replacement produces grounded findings. Model changes do not train the weights, certify compliance or guarantee anonymization.
4. Keep the original runtime and model files until validation is complete. Do not rename an arbitrary model to a bundled filename and assume its prompt format is compatible.

## Choose the correct integration path

| What you want to replace | Configuration | Additional work |
| --- | --- | --- |
| Base generative reviewer | `DESENSITIZER_LLM_MODEL` | Compatible GGUF, llama-server support, chat template and exact entity JSON |
| AI app's Qwen reviewer | `AI_DESENSITIZER_QWEN_MODEL` | Same Qwen-style chat protocol and entity schema, or a reviewed adapter change |
| AI app's NuExtract reviewer | `AI_DESENSITIZER_NUEXTRACT_MODEL` | Native extraction template and field schema must match, not a generic chat model |
| AI app's Gemma reviewer | `AI_DESENSITIZER_GEMMA_MODEL` | Gemma completion prompt/template and response parsing must match |
| Shared inference server | `DESENSITIZER_LLM_RUNTIME` | Compatible `llama-server.exe`, endpoints, flags, loopback authentication and process tracking |
| Presidio / GLiNER / Privacy Filter | `DESENSITIZER_ANALYZER_PATH`, `DESENSITIZER_GLINER_PATH`, `DESENSITIZER_PRIVACY_FILTER_PATH` | A compatible worker program and its own model assets, not a GGUF substitution |

These overrides are deliberately ignored unless `DESENSITIZER_DEV=1`. There is currently no end-user model picker. Development mode enables explicit path overrides; it is not a reason to disable integrity checks or expose a server to the network.

## Example: test a compatible base reviewer

Use a PowerShell session dedicated to the test. Set absolute paths to files you have verified. The example paths are placeholders, not download locations.

```powershell
$env:DESENSITIZER_DEV = '1'
$env:DESENSITIZER_LLM_MODEL = 'C:\LocalModels\candidate-model.gguf'
$env:DESENSITIZER_LLM_RUNTIME = 'C:\Desensitizer-Test\model-packs\context-llm\llama-server.exe'
& 'C:\Desensitizer-Test\Desensitizer.exe'
```

For the three-reviewer app, set the matching `AI_DESENSITIZER_*_MODEL` variable instead and launch `AI.Desensitizer.exe`. All three configured reviewer models must be present. Changing one path does not change that reviewer's name, template or parser. Use an adapter for a different model family rather than mislabeling it as NuExtract or Gemma.

Close the test application and this shell afterwards. Do not persist development overrides globally. To clear them in the current shell:

```powershell
Remove-Item Env:DESENSITIZER_DEV -ErrorAction SilentlyContinue
Remove-Item Env:DESENSITIZER_LLM_MODEL -ErrorAction SilentlyContinue
Remove-Item Env:DESENSITIZER_LLM_RUNTIME -ErrorAction SilentlyContinue
Remove-Item Env:AI_DESENSITIZER_QWEN_MODEL -ErrorAction SilentlyContinue
Remove-Item Env:AI_DESENSITIZER_NUEXTRACT_MODEL -ErrorAction SilentlyContinue
Remove-Item Env:AI_DESENSITIZER_GEMMA_MODEL -ErrorAction SilentlyContinue
```

## Preserve the integrity boundary

With a runtime manifest present, worker/model paths must be inside that distribution, listed and hash-matching. An override pointing outside it can be refused even in development mode. Put approved replacement files inside the isolated test distribution and set overrides to those paths.

Only after independently verifying the new files, regenerate the test distribution's manifest:

```powershell
powershell -NoProfile -File tools\New-RuntimeManifest.ps1 -AppFolder 'C:\Desensitizer-Test'
```

Rebuilding the manifest records the files you selected. It does not establish publisher authenticity. Do not remove the manifest, turn checks off, use an untrusted executable, bind to a public interface, or bypass PID/API-key checks to make a replacement work.

## Supporting a different model family in source

Start with `Desensitizer/LocalAiReview.cs` or `AI.Desensitizer/LocalAiReview.cs`. AI reviewer definitions select the name, source label, model path and prompt; name-based branches also select endpoint, native template and parser. Those branches must be changed together. Model path configuration alone cannot make a different family compatible.

Relevant boundaries:

- `Shared/LocalReviewJson.cs`: category schema, literal span grounding, NuExtract extraction fields and Gemma/native templates.
- `Shared/LocalReviewEnvelope.cs`: strict transport-envelope validation and privacy-safe errors.
- `Shared/LlamaServerHost.cs`: local server lifecycle, authenticated loopback and resource controls.
- `Shared/ExportReviewCache.cs`: exact configuration/source identity, bounded retention and owned-resource disposal.
- `AI.Desensitizer/AiConsensus.cs`: category disagreements and alias reconciliation. Multiple models agreeing is not a calibrated confidence probability.

An adapter must keep the allowed categories, exact source substrings and strict schemas. Failed, truncated, ungrounded or unusable output must remain an incomplete review, not an empty successful finding list. Preserve cancellation, section failures, retry bounds, final export checks and private diagnostic handling. Never obey instructions embedded in source documents.

For Python detector replacements, inspect `Desensitizer.Analyzer/*.py` and `Shared/RuntimeWorkerProcess.cs`. Current workers read one UTF-8 JSON request from stdin and return one JSON response before exiting. Implement the expected single/batch response shapes, category/span conventions, error/exit semantics and output bounds. Use `tools/Build-Analyzers.ps1` and staged compatibility checks before installing rebuilt workers. A Hugging Face checkpoint cannot simply replace the worker executable.

## Validate before relying on the result

Run deterministic tests first on Windows with the .NET 8 SDK:

```powershell
powershell -NoProfile -File tools\ci.ps1
```

This does not run a fresh live-model accuracy evaluation. Also compare the candidate and baseline on held-out fictional/approved documents with people, organizations, addresses, contact details, usernames, aliases, Unicode names and business-rule decoys. Include native tables, thresholds, quantities, formulas, dates, versions and outline numbering that must remain readable.

Measure missed identifiers separately from false positives, category errors, boundary errors, unsafe identity merges and preserved business meaning. Record model/runtime hashes, settings, dataset revision, latency and peak RAM. Test malformed JSON, truncation, unavailable models, Stop/resume and repeated export checks. Keep evaluation text and mappings private.

Complete an export and rehydration with the same encrypted project. Inspect the protected output before sharing. Faster inference, model popularity or a passing software test suite alone does not prove equivalent privacy quality. Maintain a rollback copy and only package a tested configuration with required notices.

## Distribution

The public repository is source-only. Model weights, compiled analyzers, inference runtimes, confidential documents, encrypted projects and local test history are not included. Install dependencies and model assets separately under their own terms. A public source snapshot is not an endorsement or licence grant for third-party models.
