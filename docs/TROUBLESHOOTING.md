# Troubleshooting

Never send a document, `.dsproj` project, mapping list or diagnostics file to anyone while asking for help. They contain original confidential text. Send the app version (in the footer) and the visible error message only.

## The app does not start

The portable folders are **self-contained builds** (they include the .NET runtime). You do not need to install .NET.

- **Windows says "You must install or update .NET".** The runtime and app files may not match after a partial copy. Keep the old folder as a rollback copy and extract a **complete portable distribution into a new folder**. Do not copy individual app files over an older distribution.
- **Nothing happens, or the window opens and closes.** Check that the whole folder was copied, including `model-packs` and `analyzer`. A path with very long names can break a copy: move the folder closer to the drive root (for example `C:\Tools\Desensitizer`).
- **Blocked by SmartScreen, antivirus or AppLocker.** The executables are not code-signed. Verify the distribution's source and checksums, then ask IT to approve the app and local workers under the organization's policy. Do not turn protection off. Antivirus scanning of large model files can make the first start slow.
- **A "detector failed" message at scan time.** One of the bundled analyzer executables was blocked or removed. Re-check the allow-list items above and the folder contents.

## Scans take hours on a long document

This is expected: the models run on your own CPU, and long documents have many sections that are checked more than once.

- **Stop** keeps every section already finished. Do not close the app with the window's close button mid-scan if you can press Stop first.
- **Resume previous check** (or the resume prompt when you scan the same file again) continues from the saved progress. Failed sections can be retried from the same prompt.
- Plan the scan for a time you do not need the computer. Plug in the power cable and turn off sleep.
- The AI Desensitizer runs three models in turn and takes longer than Desensitizer.

## Memory (RAM) on a 16 GB laptop

- Reviewer requests run sequentially. Automatic residency retains one model on lower-memory or unknown machines and can retain up to two on machines with at least 23 GiB RAM. Retaining models in memory is different from running reviews concurrently. Settings can limit residency. Hardware estimates do not establish work-laptop readiness.
- Close browsers with many tabs, Teams and large Office files before long scans.
- The footer shows the app's RAM use and the computer's used and total RAM. If the computer becomes unresponsive, press Stop and close other programs.
- Do not run both apps at the same time on a 16 GB machine.

## Project problems

- **An export is disabled or blocked.** Use **Help me export** or the issue popup's primary action to find the actual blocker: incomplete deep review, unresolved findings, missing runtime assets or unsupported content. Resolve that issue and retry; a zero Review count alone does not prove the scan finished. **Cancel** returns to the project. Repeating an unchanged export is not a recovery strategy.
- **Resume progress is unavailable.** Checkpoints are bounded and tied to compatible source/settings. Old, incompatible or oversized entries can be evicted. Run a fresh check if no reusable progress remains.
- **Restoration shows unknown tokens or conflicts.** Use the project that produced the protected copy. Unknown tokens stay unchanged. Resolve incompatible mappings before restoring; aliases restore the project's canonical spelling. The preview shows counts without revealing original text.

- **"Older app versions cannot open this project."** The project was upgraded to the new format. Use the current app version. The `.bak` copy beside the project is the pre-upgrade copy: keep it until you have verified the upgraded project.
- **Wrong passphrase.** It cannot be recovered. Passphrases must be 12 or more characters when you create or change one.
- Imported projects can sit outside the managed folder. The catalogue stores the location in protected form: use **Import project** if a project is missing from the list.

## Where things live

| Item | Location |
| --- | --- |
| Settings and managed projects (Desensitizer) | `%LOCALAPPDATA%\Desensitizer` |
| Settings and managed projects (AI Desensitizer) | `%LOCALAPPDATA%\AI Desensitizer` |
| Portable apps | wherever you copied the folder (the author uses the Desktop folders `Desensitizer Portable Latest` and `AI Desensitizer Portable Latest`) |
| Backups of previous installs and app data (developer machine) | `work\backups\` inside the source project folder, created by `tools\Publish-And-Deploy.ps1`; they hold original confidential data and must never be shared |

## Developer note

Install and roll back only with `tools\Publish-And-Deploy.ps1` (it publishes self-contained, backs up every replaced file and the app data, hashes files and smoke-tests the result). Do not copy the exe, dll and runtimeconfig by hand.
