# Safe source and portable handoff

This public repository begins with a sanitized source snapshot. It excludes private development history, reference documents, local results, model weights and compiled programs. Some private-reference regression suites are omitted. The public test gate runs only the included suites.

Never upload an entire development or portable folder. Later local work may contain confidential documents, encrypted mapping projects, original-name restoration data, diagnostics and user settings even when Git ignores them. A normal folder ZIP does not respect Git ignore rules.

For source handoff, commit and review the intended files, then use `tools/Make-Handoff.ps1`. Its archive checks are a guard, not a substitute for checking content for private document excerpts or credentials.

```powershell
powershell -NoProfile -File tools/Make-Handoff.ps1 -OutFile 'C:\Handoff\desensitizer-source.zip'
```

For portable handoff, package only a complete tested distribution using `tools/New-PortablePackage.ps1`, with app files and the separately obtained permitted models and runtimes. Verify all archive entries and include required notices. Never include confidential documents, mapping projects, settings or diagnostics. Model licences remain separate from source publication.

The repository owner has not selected a general source reuse licence. Source visibility alone does not grant reuse or redistribution rights. Obtain permission before redistributing.
