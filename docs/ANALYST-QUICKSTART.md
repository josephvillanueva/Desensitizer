# Analyst quick start (one page)

Use this before you send any business document to a cloud AI. The apps run entirely on your computer.

Protect identifying details such as names, companies, contact details and account references. Preserve business rules, conditions, ordinary quantities, versions and numbering. Native DOCX tables receive structured context review; other supported formats review extracted text with format-specific structure checks.

**Start:** open `Desensitizer.exe` (or `AI Desensitizer`: `AI.Desensitizer.exe`) from the portable folder. No installation, no separate .NET.

## Steps

1. **Create a project.** Passphrase of 12 or more characters. Write it down in an approved place: it cannot be recovered.
2. **Add the document** (`.docx`, `.xlsx`, `.pptx`, `.csv`, `.md` or `.txt`; for anything older, save a copy as `.docx`, `.xlsx` or `.pptx` first). It scans automatically. Long documents take a long time. **Stop** keeps finished sections, and **Resume previous check** continues later.
3. **Review every finding** in Mapping review:
   - **Replace**: becomes a token like `[PERSON_001]`.
   - **Keep as-is**: stays in the file. Direct decisions apply by value. Remembered feedback is restricted to eligible ordinary project/system/product labels, not people or companies, and does not train model weights. **Review lists** can limit eligible label feedback to matching sentence/line context and remove it later.
   - **Review**: undecided. Resolve these before export.
   Add anything the tool missed with **Add mapping** (company names, project names, internal systems).
4. **Export desensitized copy.** Wait for the safety check to finish. If it reports remaining values, they move to Review: decide them and export again.
5. **Open the exported copy yourself.** Search it for names, companies, emails, phone numbers, ids and places you know are in the original. Check tables and footers too.
6. **Upload only the exported copy** to the cloud AI.
7. When the AI answer comes back in the same supported file type, open the **same project** and choose **Rehydrate a returned file**. Check the count-only preview before **Restore copy**, or choose **Cancel**. Unknown tokens stay unchanged; incompatible mappings block restoration. Aliases use the saved canonical spelling, which may differ from an original occurrence.

## Never send or share

- The `.dsproj` project, mapping lists, resume checkpoints, logs and diagnostics. They contain the **original confidential text**.
- Anything from the `work\` or `outputs\` folders.
- Do not zip the whole project folder to hand it to someone (see `docs/HANDOFF.md`).

## Know the limits

- Pictures and embedded objects are replaced by placeholders and are **not analyzed**: text inside an image is not checked. Look at them before sending.
- AI findings are suggestions, not calibrated probabilities. Agreement between reviewers is not proof.
- The tools **reduce risk; they do not guarantee anonymity.** Combinations of details (a rare job title in a small city) can still identify someone.
- Older versions of the app cannot open a project after you accept the format upgrade. Keep the `.bak` file until you have reopened the project successfully.

## If something goes wrong

Follow the issue popup's primary recovery action, or choose **Cancel**. **Help me export** explains why a disabled export cannot proceed. **Settings** can replay the spotlight tutorial. See `docs/TROUBLESHOOTING.md`; share the app version or **Copy support info**, never the document or project.
