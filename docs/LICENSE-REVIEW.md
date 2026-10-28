# Licence review (technical summary)

The licence observations below were recorded on 2026-10-10 by an AI agent from files present on the development laptop and from official web pages. They are historical evidence, not a fresh review of publisher terms. **This is a technical summary, not legal advice.** It does not replace review by your legal or IT team. Anything marked **UNVERIFIED** or **MISSING** was not confirmed in that review and remains unresolved unless a later distribution inventory provides evidence.

Companion files: `THIRD-PARTY-NOTICES.txt` (inventory and obligations), `docs/licenses/` (licence texts), `docs/third-party-components.json` (data table; regenerate the notices with `tools/New-ThirdPartyNotices.ps1`).

## Packaging correction (2026-10-29)

The current `tools/Publish-And-Deploy.ps1` stages `THIRD-PARTY-NOTICES.txt`, the available `docs/licenses/` tree into the portable folder's `licenses/` directory, and `PORTABLE-START-HERE.txt` before the transactional installation plan is built. These files are included in that plan's backup and hash verification. Earlier statements that only the Qwen and llama.cpp notices ship, or that .NET notices are never packaged, describe the older installation inspected on 2026-10-10 and are no longer a description of the release script.

Copying the available notices does not resolve missing licence texts, model provenance, publisher revisions, source-code ownership or employer approval. Check the inventory of the actual release being shared, including notices already present inside worker dependency folders. Do not treat an old `MISSING` entry as proof that a publisher still has no licence file, or treat an unverified component as cleared for redistribution.

The public GitHub project is a sanitized source-only snapshot. It excludes model weights, inference runtimes, compiled analyzers, confidential documents and private development history. Its available third-party notice files do not mean those components or weights are bundled with the source. The application source still has no general reuse licence selected.

## Scope of the question

The question asked was whether the bundled models and libraries appear to allow **internal commercial or business use** (analysts at the owner's employer using the tools on internal documents), and what conditions apply if the portable folders are **redistributed** (given to colleagues, another team, a contractor, or outside the company).

The current apps perform document processing and model inference locally. The analyst separately decides whether to upload a protected copy to a cloud service. This review concerns software and weight licences; it does not settle the analyst's authority to process a document, data-protection obligations, employer policy or the terms of any later cloud service.

## Historical summary table (2026-10-10)

| Component | Licence (as declared) | Internal business use | Redistribution conditions | Status |
| --- | --- | --- | --- | --- |
| Qwen3-4B (GGUF) | Apache-2.0 | Appears permitted | Include licence text and notices | Text obtained and already shipped |
| NuExtract 2.0 2B (GGUF) | MIT (metadata and model card); base Qwen2-VL-2B-Instruct is Apache-2.0 | Appears permitted ("can be used commercially" on the model card) | Keep MIT notice; include Apache text for the base model | Licence text MISSING (no LICENSE file in the repo) |
| **Gemma 3 4B (GGUF)** | **Gemma Terms of Use plus Prohibited Use Policy (custom, not open source)** | Appears permitted if the use does not breach the Prohibited Use Policy | **Strict**: pass the use restrictions on in an enforceable agreement, give every recipient the Terms, ship the required Notice file, mark modified files; Google may terminate and require deletion | Text obtained from official pages |
| GLiNER small v2.1 (weights) | Apache-2.0 (model card) | Appears permitted | Include licence text | Model card only; backbone and exact file UNVERIFIED |
| OpenAI Privacy Filter (ONNX) | Apache-2.0 (model card) | Appears permitted | Include licence text and notices | Model-specific licence file MISSING; metadata only |
| spaCy `en_core_web_sm` 3.8.0 | MIT (package metadata) | Appears permitted | Keep notices, including the WordNet notice | Text obtained; training-data licences are an open question |
| llama.cpp | MIT | Permitted | Keep notice | Text obtained and shipped |
| Presidio Analyzer 2.2.364 | MIT | Permitted | Keep notice | Text obtained |
| spaCy 3.8.16 | MIT | Permitted | Keep notice | Text obtained |
| GLiNER library | Apache-2.0 | Permitted | Include licence text | Text obtained |
| PyInstaller | GPL-2.0-or-later with bootloader exception | Permitted (the exception covers executables it produces) | Keep PyInstaller notice; GPL applies to PyInstaller, not the packaged analyzers | Text obtained; confirm exception wording with legal |
| PyTorch, transformers, onnxruntime, numpy and other Python packages | BSD, MIT, Apache-2.0 (tqdm also MPL-2.0; regex also CNRI-Python) | Permitted | Keep notices; Apache text where applicable | Most texts obtained; onnxruntime, tokenizers, sentencepiece, transformers, Python (PSF) MISSING |
| .NET 8.0.31 runtime (self-contained) | MIT | Permitted | Include the .NET licence and third-party notices with the folder | Text obtained; absent from the older inspected folders. Current release script stages available .NET notices with the licence tree. |
| WPF-UI 4.2.0 | MIT | Permitted | Keep notice (and its bundled third-party notices) | Text obtained |
| Open XML SDK 3.5.1 | MIT | Permitted | Keep notice | Text obtained |

## Things that could block or constrain use

1. **Gemma (AI Desensitizer only).** It is the only component under a non-standard licence. Points to review:
   - **Redistribution** (section 3.1) needs the use restrictions to appear as an enforceable provision in whatever agreement governs the recipient's use, a copy of the Terms, the exact Notice text file (see `docs/licenses/Gemma-NOTICE.txt`), and modified-file notices. Handing a portable folder to a colleague at the same legal entity is probably not a "third party" Distribution, but that is a legal question. Handing it to a contractor, another group company or an external party likely is.
   - **Prohibited Use Policy** includes: "Generating, gathering, processing, or inferring sensitive personal or private information about individuals without obtaining all rights, authorizations, and consents required by applicable laws." This tool uses Gemma to find personal information in business documents in order to remove it. Whether that counts as processing sensitive personal information under the policy, and whether the owner's employer has the needed authorisations for the documents scanned, should be confirmed by legal. It also bars "automated decisions in domains that affect material or individual rights" (the app proposes replacements, the analyst decides, so this appears not to apply, but confirm).
   - **Termination:** Google may terminate for breach and require deletion of all copies. Keep a record of which build contains which Gemma file.
   - The terms apply "regardless of the source" of the weights, so using a third-party GGUF conversion does not avoid them. The GGUF source repository was not recorded (UNVERIFIED).
   - A fallback exists: the Desensitizer base app does not use Gemma. If the legal answer is unfavourable, ship only the base app or replace the third reviewer.
2. **The October 10 review did not obtain model-specific licence files for NuExtract and OpenAI Privacy Filter.** It recorded declarations in metadata (MIT and Apache-2.0). Verify the exact upstream revision before drawing a current conclusion. Keep dated model-card evidence and resolve the missing-text question under your policy.
3. **GLiNER version.** GLiNER v0 and v1 weights are CC BY-NC 4.0 (non-commercial) per the model card. The app is configured for `gliner_small-v2.1` (Apache-2.0). Verify that the shipped `pytorch_model.bin` really is v2.1 by comparing its SHA256 with the upstream repository. Its recorded Hugging Face cache hash starts `1d4e83e4` (UNVERIFIED against upstream).
4. **spaCy model training data.** `en_core_web_sm` is MIT, but its `LICENSES_SOURCES` file lists OntoNotes 5 as "commercial (licensed by Explosion)" and WordNet 3.0. These are normally treated as covered by the publisher's licence, but a strict legal review may want to confirm.
5. **Release-specific notice coverage.** The current release script copies the available notices into each portable folder, as described above. Verify the resulting `THIRD-PARTY-NOTICES.txt` and `licenses/` content, including missing or unverified items, before redistribution. Packaging is not a licence clearance.
6. **The applications' own licence is undecided.** No general reuse licence is chosen for the source code. Publication permits inspection; it does not itself grant a general licence to reuse or redistribute the application. The owner must decide and communicate the applicable terms.

## Open questions for legal and IT

- Does the employer allow use of models under the Gemma Terms of Use? Do Section 3.2 and the Prohibited Use Policy fit the intended use (processing personal data to remove it)?
- Is internal circulation of the portable folders (to other staff, possibly other legal entities) "Distribution" under Gemma?
- Is a standalone licence text for NuExtract and the OpenAI Privacy Filter needed, given the repositories declare the licence only in metadata?
- Does the model-training-data note on `en_core_web_sm` matter to the employer's policy?
- Is the PyInstaller bootloader exception acceptable to the employer's open-source policy?
- Will the employer allow-list the unsigned executables, or should they be signed (backlog item B5)?
- Where is the Gemma, NuExtract and Privacy Filter GGUF or ONNX source repository to be pinned (not recorded today)?

## What the October 10 review did not verify

- Exact upstream repositories and revisions of the Gemma GGUF, NuExtract GGUF, Qwen GGUF and privacy-filter ONNX files.
- Versions of llama.cpp, PyInstaller, transformers and the GLiNER library inside the bundles.
- Whether `libomp.dll` (shipped with llama.cpp) needs its own notice (believed LLVM OpenMP, Apache-2.0 with LLVM exception).
- Whether WPF-UI's use of the Segoe Fluent Icons font is satisfied by the user's own Windows installation.
- That the Gemma pages' text is unchanged since 2026-04-01 (terms) and 2024-02-21 (policy).
- Anything about export control, patents or trademarks.
