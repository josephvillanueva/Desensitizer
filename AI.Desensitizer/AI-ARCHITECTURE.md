# AI Desensitizer architecture

AI Desensitizer is an experimental companion to Desensitizer. Both prepare documents for analysis by tokenizing identifying details while retaining business rules, ordinary quantities and supported table content. They do not make policy decisions about what a business rule should say, certify compliance or guarantee anonymization. Each app has separate settings and a managed project catalogue; an analyst can still explicitly import an accessible encrypted project.

## Current review flow

1. Read supported document text locally, including table and hidden-content channels where supported. Prepare structural context without treating document instructions as trusted commands.
2. Run deterministic rules and local detector workers to propose identifying values.
3. Give overlapping sections to sequential Qwen, NuExtract and Gemma reviewers using their own templates and strict response contracts. Returned values must be grounded in source text. Failed or unusable output leaves review incomplete.
4. Reconcile category evidence and supported entity keys. Conservative alias rules can reuse tokens for supported variants; repeated first names, shared surnames or co-occurrence alone do not prove identity. Document-wide candidate reconciliation is not whole-document understanding or guaranteed coreference resolution.
5. Propose Replace for corroborated eligible findings and leave uncertain findings in Review. Preserve the analyst's explicit decisions. Optional category/context-limited feedback filters future suggestions without training model weights.
6. Store mappings and bounded resumable review progress inside the encrypted project. Completed compatible sections can be reused; changed source or configuration invalidates incompatible progress. Older saved progress can be evicted to keep storage bounded.
7. Build a protected copy, check preservation and package safety, and run the required final checks. Optional automatic residual replacement can retry up to five passes; it does not bypass incomplete review or structural failures.
8. Release the copy only when required checks pass and blocking findings are resolved. The analyst must inspect it before sharing. Rehydration uses the matching project, presents token counts and warnings, leaves unknown tokens unchanged and blocks incompatible mappings.

## Business rules and table context

The intended replacements are names, organizations, contact details, addresses, usernames and identifying references. Generic headings, requirements language, outline numbers, versions and ordinary business quantities should remain readable. Detection can still produce false positives or misses, so Keep, Replace and Review remain analyst decisions.

DOCX reviewers receive bounded row/column context from native Word tables. XLSX and PPTX reviewers receive extracted, labeled cell or slide text; those formats do not currently provide the same `TableReviewContext` adapter. Their export handlers preserve supported workbook/slide structure and validate transformations, while removing unsupported or risky channels. This is not a promise that every formula, layout or business meaning survives unchanged.

Images and embedded objects are removed or replaced rather than read with OCR. Text inside an image is not reviewed and cannot be restored by token rehydration. Keep original documents privately, and inspect the protected result's business-rule and table content.

## Resource strategy

Reviewer requests run sequentially. Model residency is a separate setting: automatic policy keeps at most one model resident on smaller or unknown-memory machines and at most two when reported physical memory is at least 23 GiB, capped by the available model count. An explicit setting can change that cap. Keeping a model resident does not make review requests parallel.

CPU time, model loading and detector workers can still make long reviews slow or memory-intensive. Suitability for a 16 GB work laptop requires testing on that machine and its security policies. The app has not been certified for every such environment. Python detector workers currently handle one request and exit; the tested resident-worker pool abstraction is not wired into their execution protocol.

## Consensus policy

- An eligible direct identifier supported by an independent detector and one AI reviewer can be proposed for Replace.
- Two or three AI reviewers agreeing on an eligible interpretation can be proposed for Replace.
- An AI-only finding supported by one reviewer stays in Review.
- Category disagreements or identity ambiguity can require Review even when other evidence appears strong. Agreement is an evidence signal, not calibrated accuracy.
- Existing Keep as-is decision: preserve unless the analyst changes it.
- Alias matching is conservative. Ambiguous names may need a manual mapping decision.

## Active reviewer slots

- Reviewer A: Qwen3-4B, broad privacy and confidential-name review.
- Reviewer B: NuExtract 2.0 2B, exact structured span extraction.
- Reviewer C: Gemma 3 4B instruction-tuned QAT, conservative privacy review and generic-term filtering.

All three configured reviewers are required for a complete AI-app review. They use an authenticated loopback-only local runtime with owned-process and integrity checks. The base Desensitizer app uses one contextual reviewer after its local detector layers. Three reviewers cost additional time and memory and are not proven more accurate for every document. See `../docs/MODEL-SWAPPING.md` before replacing any reviewer or runtime.
