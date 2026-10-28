# Public snapshot validation

This source-only snapshot was reviewed and tested on Windows on 29 October 2026.

- Both application builds passed.
- The included deterministic CI gate passed 4,243 reported checks across 21 suites, plus Python unit tests.
- After fictionalizing an additional analyzer test name, the 28 Python tests passed again. A subsequent source comment edit and documentation corrections do not alter app behavior.
- Private-reference regression suites and historical benchmark reports are excluded. Their checks are not counted in this public gate.
- Known private reference identifiers, long reference-document passages, personal machine paths and common credential patterns were checked before publication. This review is not a guarantee that arbitrary future contributions are safe to publish.

The gate uses fictional examples, fake reviewer responses and structural document checks. It does not run a new full live-model accuracy benchmark, confidential-document export, cloud comparison, clean-machine certification or target work-laptop test. Passing software checks do not certify anonymity, policy compliance, preservation of every business rule or model quality on unseen documents.

Models, compiled workers and inference runtimes are separately provisioned assets. Record and test their hashes and configuration when validating a runnable distribution. Follow [MODEL-SWAPPING.md](MODEL-SWAPPING.md) before replacing them.
