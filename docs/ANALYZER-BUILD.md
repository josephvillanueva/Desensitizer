# Building local detector workers

The public repository contains Python sources and PyInstaller specifications, not compiled workers, model weights or a provisioned Python environment. Building the WPF applications does not build or provision the detector workers.

## Prepare an isolated environment

Review the imports and specifications in `Desensitizer.Analyzer`. Provision a compatible Windows Python environment with PyInstaller and the dependencies required by the workers you intend to build. These include Presidio, spaCy and its English language model, GLiNER, ONNX Runtime, tokenizers, transformers and torch where required. Check component and model licences separately. No dependency installation or model download is performed by the build script.

The default environment location is `work/presidio-env`. Supply another path explicitly:

```powershell
powershell -NoProfile -File tools/Build-Analyzers.ps1 -EnvPath 'C:\LocalBuild\worker-env' -InstalledRoot 'C:\Desensitizer-Test'
```

The build stages three separate `onedir` distributions, with a worker executable and its complete `_internal` folder. Keep model assets separate. Never copy an executable without its matching dependency folder.

## Compatibility checks

The workers accept one UTF-8 JSON request on standard input and emit one JSON response before exiting. The app also supports batched text requests. Preserve the expected categories, source spans, batch ordering, exit semantics, bounded output and private error messages.

The build driver compares staged workers against the explicitly selected installed test distribution using fictional inputs. It checks literal and escaped Unicode transport. Skipped checks are not a pass. Unicode transport passing does not mean a model recognizes every accented name.

Use `-Only Presidio`, `-Only PrivacyFilter` or `-Only BusinessContext` to build one worker. Review each worker's specification before changing dependencies. Existing model assets are required for compatibility checks; they are not part of this repository.

## Install as a coordinated test release

Use the transactional `tools/Install-AnalyzerRelease.ps1` workflow only after reviewing its parameters and staged verification evidence. It preserves model folders, backs up replaced distribution files and regenerates runtime manifests. The regular app deployment script intentionally excludes workers and models.

Changes to Python source do not change an already frozen executable. When changing the worker protocol or encoding, rebuild all affected workers and test them together with the app. Keep a rollback copy. Validate complete exports with fictional documents before relying on the release.
